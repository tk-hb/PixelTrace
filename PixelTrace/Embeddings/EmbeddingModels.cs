namespace PixelTrace.Mobile;

internal sealed record EmbeddingModelAsset(string Name, long Bytes, string Hash, string CacheKey);
internal static class EmbeddingModels
{
    public const string Repository = "litert-community/embeddinggemma-2-text-vision-440m-litert-lm";
    public const string Revision = "e301f74d5551b0c2641bd5cb4652a76239d5c5f8";
    public const string FileName = "embeddinggemma-2-text-vision-440m.litertlm";
    public const string Fingerprint = Revision + ":litertlm-0.18.0:int4-text-int8-vision:768:android-rgb-v1";
    public static EmbeddingModelAsset Default { get; } = new(FileName, 387710976, "92dcbea108899e5d6e30d919b0744f90d9967e80c67a4ab5503ac16d54f62eb0", "litertlm-text-vision");

    public const string OnnxRepository = "onnx-community/embeddinggemma-2-ONNX";
    public const string OnnxRevision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0";
    public static IReadOnlyList<EmbeddingModelAsset> OnnxAssets { get; } = [new("onnx/model.onnx", 421421, "bc47de15f81208a5c99e5ab10f746d5e33b51ea228b7dc0bef9c133a94f1c1c3", "fp32-onnx"), new("onnx/model.onnx_data", 1084170240, "9fd452bfc6916e5a92f080f8dada35598ca4512b21c65897d4c5974d5f9630a2", "fp32-onnx"), new("onnx/vision_encoder.onnx", 121253, "a3fb44f8b5b152cfee6afe16e2cd69e774b7e1d3b822d1db1190ddf7b08b575e", "fp32-onnx"), new("onnx/vision_encoder.onnx_data", 671026176, "ca8d28c99feb8129618f141978a27adae25090e0dadb68a30c235e191c01e37e", "fp32-onnx"), new("tokenizer.json", 32170510, "4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4", "fp32-onnx")];
    public static IReadOnlyList<EmbeddingModelAsset> OnnxFp16Assets { get; } = [new("onnx/model_fp16.onnx", 425780, "a49e227d7e0e5f4ee606d79879d084264366367bc6318f344ab179e35d4fd6a4", "fp16-onnx"), new("onnx/model_fp16.onnx_data", 542085120, "b9dbe09415d77c8686ba6e216a244086f4ada1608238c13f2bfaa587ba627207", "fp16-onnx"), new("onnx/vision_encoder_fp16.onnx", 112253, "3e2b6d5648a3ae61b572cc3ec0a23b13ebfda3b63e3c9c343c593636392a879c", "fp16-onnx"), new("onnx/vision_encoder_fp16.onnx_data", 335513088, "9f374070808b8b96252f7d04c9c4eae34df60283473fde5a3fbf4d9205495e19", "fp16-onnx"), new("tokenizer.json", 32170510, "4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4", "fp16-onnx")];

    public const string LlamaRepository = "ggml-org/embeddinggemma-2-GGUF";
    public const string LlamaRevision = "bfcd298762cc34d0357ece5ebdd31791a3a374d8";
    public const string LlamaModelFile = "embeddinggemma-2-BF16.gguf";
    public const string LlamaProjectorFile = "mmproj-embeddinggemma-2-BF16.gguf";
    public static IReadOnlyList<EmbeddingModelAsset> LlamaAssets { get; } = [new(LlamaModelFile, 557950176, "68bae29d62fb8c7d23e98d21fd4662753ddd636e6b62b8a70dfe059a9844f216", "bf16-llama-vulkan"), new(LlamaProjectorFile, 982074784, "d2033b3cd0223cfb2e7a2b50dc80a664c78c816d375f5b017ec3ef80ee0bc766", "bf16-llama-vulkan")];

    public static bool IsOnnx(string precision)
        => precision is "fp16" or "fp32";
    public static string Label(string precision)
        => precision switch
    {
        "quantized" => "텍스트 INT4 · 이미지 INT8",
        "fp16" => "FP16",
        "fp32" => "FP32",
        "bf16" => "BF16 모델 · llama.cpp Vulkan",
        _ => "엔진 선택 필요"
    };
    public static string? UnavailableReason(string precision, string backend)
    {
        if (precision == "bf16")
        {
            return LlamaGemmaEngine.UnavailableReason(backend);
        }

        if (precision == "fp16" && backend != "gpu")
        {
            return "FP16 변환 모델은 ONNX GPU 엔진에서 사용합니다.";
        }

        if (IsOnnx(precision))
        {
            return OnnxRuntimeSupport.UnavailableReason(backend);
        }

        if (precision != "quantized")
        {
            return "선택한 엔진의 정밀도를 사용할 수 없습니다.";
        }

        return backend is "cpu" or "gpu" ? null : "설정에서 사용할 CPU/GPU 엔진을 선택해 주세요.";
    }

    public static void RequireSupported(string precision, string backend)
    {
        if (UnavailableReason(precision, backend)is { } reason)
        {
            throw new NotSupportedException(reason);
        }
    }

    public static string Profile(Preferences preferences)
    {
        RequireSupported(preferences.Precision, preferences.Backend);
        return ModelFingerprint(preferences.Precision) + ":" + preferences.SoftTokens;
    }

    private static string ModelFingerprint(string precision)
        => precision == "bf16" ? LlamaRevision + ":llama-" + LlamaGemmaEngine.Revision + ":bf16-weights:768:mtmd-mixed-mean-v1" : IsOnnx(precision) ? OnnxRevision + ":onnx-1.26.0:" + precision + ":768:android-patches-v1" : Fingerprint;
    public static bool IsCompatibleProfile(string stored, string current)
        => stored == PixelTrace.Exchange.LibraryArchive.EmbeddingProfile || stored == current;
}
