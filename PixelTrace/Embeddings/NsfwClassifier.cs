using Android.Graphics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Path = System.IO.Path;
using PixelTrace.Exchange;

namespace PixelTrace.Mobile;

internal sealed class NsfwClassifier : IDisposable
{
    private const string Repository = NsfwModel.Repository;
    private const string Revision = NsfwModel.Revision;
    internal const string Profile = NsfwModel.AndroidProfile;
    private const int Side = 384;
    private static EmbeddingModelAsset Asset { get; } = new("model.onnx", NsfwModel.Bytes, NsfwModel.Sha256, "nsfw");
    private InferenceSession Session { get; }

    private NsfwClassifier(string path)
    {
        using SessionOptions options = OnnxRuntimeSupport.CreateOptions("cpu", false);
        options.IntraOpNumThreads = 1;
        Session = new(path, options);
    }

    public static async Task<NsfwClassifier> LoadAsync(string data, Action<string, int, int> report, CancellationToken ct)
    {
        string directory = Path.Combine(data, "models", "nsfw", Revision);
        await ModelCache.DownloadAsync(Asset, Repository, Revision, directory, (text, done, total) => report("NSFW · " + text, done, total), ct);
        ct.ThrowIfCancellationRequested();
        report("NSFW 판별 모델 로딩 중…", 0, 0);
        return new(Path.Combine(directory, Asset.Name));
    }

    public float Score(string file, CancellationToken ct)
    {
        float[] pixels = ImagePreparation.ReadImage(file, ct, (image, _, _) => PreparePixels(image, ct));
        NamedOnnxValue input = NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(pixels, [1, 3, Side, Side]));
        using RunOptions run = new();
        using CancellationTokenRegistration cancellation = ct.Register(() => run.Terminate = true);
        try
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result = Session.Run([input], ["logits"], run);
            ct.ThrowIfCancellationRequested();
            float[] logits = result.First().AsTensor<float>().ToArray();
            if (logits.Length != 2 || !float.IsFinite(logits[0]) || !float.IsFinite(logits[1]))
            {
                throw new InvalidOperationException("NSFW 모델의 판별 결과가 올바르지 않습니다.");
            }

            double maximum = Math.Max(logits[0], logits[1]);
            double nsfw = Math.Exp(logits[0] - maximum);
            return (float)(nsfw / (nsfw + Math.Exp(logits[1] - maximum)));
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static float[] PreparePixels(Bitmap image, CancellationToken ct)
    {
        int width = image.Width, height = image.Height;
        int[] source = new int[checked(width * height)];
        image.GetPixels(source, 0, width, 0, 0, width, height);
        float[] output = new float[3 * Side * Side];
        (int[] indices, float[] weights) xMap = ResamplingMap(width);
        (int[] indices, float[] weights) yMap = ResamplingMap(height);
        for (int y = 0; y < Side; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < Side; x++)
            {
                float red = 0, green = 0, blue = 0;
                for (int j = 0; j < 4; j++)
                {
                    int row = yMap.indices[y * 4 + j] * width;
                    for (int i = 0; i < 4; i++)
                    {
                        int color = source[row + xMap.indices[x * 4 + i]];
                        float weight = yMap.weights[y * 4 + j] * xMap.weights[x * 4 + i] * ((uint)color >> 24) / 255f;
                        red += ((color >> 16) & 255) * weight;
                        green += ((color >> 8) & 255) * weight;
                        blue += (color & 255) * weight;
                    }
                }

                int offset = y * Side + x;
                output[offset] = Math.Clamp(red, 0, 255) / 127.5f - 1;
                output[Side * Side + offset] = Math.Clamp(green, 0, 255) / 127.5f - 1;
                output[2 * Side * Side + offset] = Math.Clamp(blue, 0, 255) / 127.5f - 1;
            }
        }

        return output;
    }

    private static (int[] Indices, float[] Weights) ResamplingMap(int length)
    {
        int[] indices = new int[Side * 4];
        float[] weights = new float[Side * 4];
        for (int position = 0; position < Side; position++)
        {
            float coordinate = (position + .5f) * length / Side - .5f;
            int first = (int)Math.Floor(coordinate) - 1;
            for (int tap = 0; tap < 4; tap++)
            {
                int pixel = first + tap;
                indices[position * 4 + tap] = Math.Clamp(pixel, 0, length - 1);
                float distance = Math.Abs(coordinate - pixel);
                weights[position * 4 + tap] = distance <= 1 ? 1.5f * distance * distance * distance - 2.5f * distance * distance + 1 : distance < 2 ? -.5f * distance * distance * distance + 2.5f * distance * distance - 4 * distance + 2 : 0;
            }
        }

        return (indices, weights);
    }

    public void Dispose()
        => Session.Dispose();
}
