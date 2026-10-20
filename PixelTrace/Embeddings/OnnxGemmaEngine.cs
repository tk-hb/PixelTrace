using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.HuggingFace.Tokenizer;

namespace PixelTrace.Mobile;

internal sealed class OnnxGemmaEngine : IDisposable
{
    private InferenceSession? text;
    private InferenceSession? vision;
    private Tokenizer? tokenizer;
    private int SoftTokens { get; }
    private string EngineName { get; }

    public OnnxGemmaEngine(string directory, string backend, int softTokens, string precision)
    {
        SoftTokens = softTokens;
        EngineName = "ONNX " + backend.ToUpperInvariant() + " " + precision.ToUpperInvariant();
        string stage = "실행 장치";
        try
        {
            using SessionOptions textOptions = OnnxRuntimeSupport.CreateOptions(backend, true);
            using SessionOptions visionOptions = OnnxRuntimeSupport.CreateOptions(backend, false);
            string suffix = precision == "fp16" ? "_fp16" : "";
            stage = "문장 모델";
            text = new(Path.Combine(directory, "onnx", "model" + suffix + ".onnx"), textOptions);
            stage = "이미지 모델";
            vision = new(Path.Combine(directory, "onnx", "vision_encoder" + suffix + ".onnx"), visionOptions);
            stage = "토크나이저";
            tokenizer = Tokenizer.FromFile(Path.Combine(directory, "tokenizer.json"));
        }
        catch (Exception error)
        {
            Dispose();
            throw new InvalidOperationException(EngineName + " · " + stage + " 로딩 실패 · " + error.Message, error);
        }
    }

    public float[] EncodeText(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long[] ids = tokenizer!.Encode(query, true).First().Ids.Select((id) => (long)id).ToArray();
        if (8192 < ids.Length)
        {
            throw new ArgumentException("검색 문장은 8192 토큰 이내로 입력해 주세요.");
        }

        return RunText(ids, [], ct);
    }

    public float[] EncodeImage(byte[] content, CancellationToken ct)
    {
        OnnxImagePatches patches = OnnxImagePatches.Create(content, SoftTokens, ct);
        int count = patches.Tokens * 9;
        NamedOnnxValue[] inputs = [NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(patches.Pixels, [1, count, OnnxImagePatches.PatchDimension])), NamedOnnxValue.CreateFromTensor("pixel_position_ids", new DenseTensor<long>(patches.Positions, [1, count, 2]))];
        float[] features;
        using (RunOptions run = new())
        using (CancellationTokenRegistration cancellation = ct.Register(() => run.Terminate = true))
        using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = Run(vision!, inputs, "image_features", run, ct, "이미지 모델"))
        {
            features = output.First().AsTensor<float>().ToArray();
        }

        if (features.Length != patches.Tokens * 512)
        {
            throw new InvalidDataException("ONNX 이미지 특징의 크기가 일치하지 않습니다.");
        }

        long[] tokens = new long[patches.Tokens + 4];
        tokens[0] = 2;
        tokens[1] = 255999;
        Array.Fill(tokens, 258880L, 2, patches.Tokens);
        tokens[^2] = 258882;
        tokens[^1] = 1;
        return RunText(tokens, features, ct);
    }

    private float[] RunText(long[] ids, float[] features, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NamedOnnxValue[] inputs = [NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])), NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length])), NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(features, [features.Length / 512, 512])), NamedOnnxValue.CreateFromTensor("video_features", new DenseTensor<float>(Array.Empty<float>(), [0, 512])), NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(Array.Empty<float>(), [0, 512]))];
        using RunOptions run = new();
        using CancellationTokenRegistration cancellation = ct.Register(() => run.Terminate = true);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = Run(text!, inputs, "sentence_embedding", run, ct, "문장 모델");
        return output.First().AsTensor<float>().ToArray();
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(InferenceSession session, NamedOnnxValue[] inputs, string output, RunOptions run, CancellationToken ct, string stage)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result = session.Run(inputs, [output], run);
            if (ct.IsCancellationRequested)
            {
                result.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            return result;
        }
        catch (OnnxRuntimeException)when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OnnxRuntimeException error)
        {
            throw new InvalidOperationException(EngineName + " · " + stage + " 추론 실패 · " + error.Message, error);
        }
    }

    public void Dispose()
    {
        tokenizer?.Dispose();
        tokenizer = null;
        vision?.Dispose();
        vision = null;
        text?.Dispose();
        text = null;
    }
}
