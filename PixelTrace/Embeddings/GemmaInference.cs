using PixelTrace.Embedding;

namespace PixelTrace.Mobile;

public sealed class GemmaInference(ModelCache models, Preferences preferences, string cache) : IDisposable
{
    private ModelCache Models { get; } = models;
    private Preferences Preferences { get; } = preferences;
    private string Cache { get; } = cache;

    private LocalEmbedder? engine;
    private OnnxGemmaEngine? onnx;
    private LlamaGemmaEngine? llama;
    private string? loadedProfile;
    private string? loadedEngineId;
    private string Profile
        => Models.Profile;
    internal bool IsLoaded
        => loadedProfile is not null && loadedProfile == Profile && loadedEngineId == Preferences.EngineId;
    public string Status
        => engine is null && onnx is null && llama is null ? "사용 준비 전" : $"사용 준비됨 · {EmbeddingEngines.Find(Preferences.EngineId)?.Label}";

    public void Load(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EmbeddingModels.RequireSupported(Preferences.Precision, Preferences.Backend);
        if (IsLoaded)
        {
            return;
        }

        if (!Models.Ready)
        {
            throw new InvalidOperationException("모델 파일 준비가 완료되지 않았습니다.");
        }

        Unload();
        Directory.CreateDirectory(Cache);
        try
        {
            if (Preferences.Precision == "bf16")
            {
                llama = new(Models.DirectoryPath, Preferences.SoftTokens, Preferences.Backend);
            }
            else if (EmbeddingModels.IsOnnx(Preferences.Precision))
            {
                onnx = new(Models.DirectoryPath, Preferences.Backend, Preferences.SoftTokens, Preferences.Precision);
            }
            else
            {
                engine = new LocalEmbedder(Models.ModelPath, Cache, Preferences.Backend, Preferences.SoftTokens);
            }

            loadedProfile = Profile;
            loadedEngineId = Preferences.EngineId;
            ct.ThrowIfCancellationRequested();
            global::Android.Util.Log.Info(AppIdentity.LogTag, $"모델 준비 완료 · {loadedProfile} · {Models.Precision}");
        }
        catch
        {
            Unload();
            throw;
        }
    }

    public float[] EncodeText(string query, CancellationToken ct)
    {
        Load(ct);
        string prompt = "task: search result | query: " + query;
        float[]? vector = llama is not null ? llama.EncodeText(prompt, ct) : onnx is not null ? onnx.EncodeText(prompt, ct) : engine!.EmbedText(prompt);
        ct.ThrowIfCancellationRequested();
        return EmbeddingVector.Normalize(vector!);
    }

    public float[] EncodeImage(PreparedImage image, CancellationToken ct)
    {
        Load(ct);
        float[]? vector = llama is not null ? llama.EncodeImage(image.Content, ct) : onnx is not null ? onnx.EncodeImage(image.Content, ct) : engine!.EmbedImage(image.Content);
        // This SDK has no per-call cancellation API. Finish the current native call,
        // then cancel before saving its result or starting another image.
        ct.ThrowIfCancellationRequested();
        return EmbeddingVector.Normalize(vector!);
    }

    public float[][] EncodeImages(IReadOnlyList<PreparedImage> images, CancellationToken ct)
    {
        Load(ct);
        if (onnx is not null || llama is not null)
        {
            // Keep native activation memory bounded on mobile. The indexing
            // pipeline still prepares/stages the requested group concurrently.
            return images.Select((image) => EncodeImage(image, ct)).ToArray();
        }

        float[] output = engine!.EmbedImages(images.Select((image) => image.Content).ToArray())!;
        ct.ThrowIfCancellationRequested();
        if (output.Length != checked(images.Count * EmbeddingVector.Dimensions))
        {
            throw new InvalidDataException("이미지 수와 임베딩 결과 수가 일치하지 않습니다.");
        }

        float[][] vectors = new float[images.Count][];
        for (int i = 0; i < images.Count; i++)
        {
            vectors[i] = EmbeddingVector.Normalize(output.AsSpan(i * EmbeddingVector.Dimensions, EmbeddingVector.Dimensions).ToArray());
        }

        return vectors;
    }

    public void Unload()
    {
        llama?.Dispose();
        llama = null;
        onnx?.Dispose();
        onnx = null;
        LocalEmbedder? previous = engine;
        engine = null;
        loadedProfile = null;
        loadedEngineId = null;
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Close();
        }
        finally
        {
            previous.Dispose();
        }
    }

    public void Dispose()
        => Unload();
}
