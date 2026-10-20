namespace PixelTrace.Mobile;

internal sealed record EmbeddingEngineOption(string Id, string Label, string Backend, string Precision, string Description);
internal static class EmbeddingEngines
{
    public static IReadOnlyList<EmbeddingEngineOption> Options { get; } = [new("llama-gpu-bf16", "llama.cpp · Vulkan GPU · BF16 모델", "gpu", "bf16", "텍스트·이미지 BF16 가중치 · 다운로드 약 1.54 GB · 연산 형식은 GPU/커널에 따름"), new("llama-cpu-bf16", "llama.cpp · CPU · BF16 모델", "cpu", "bf16", "텍스트·이미지 BF16 가중치 · 다운로드 약 1.54 GB"), new("litert-gpu", "LiteRT-LM · GPU", "gpu", "quantized", "텍스트 INT4 · 이미지 INT8"), new("litert-cpu", "LiteRT-LM · CPU", "cpu", "quantized", "텍스트 INT4 · 이미지 INT8"), new("onnx-gpu-fp16", "ONNX · GPU · FP16", "gpu", "fp16", "FP16 변환 모델 · 다운로드 약 910 MB"), new("onnx-gpu-fp32", "ONNX · GPU · FP32", "gpu", "fp32", "FP32 모델 · 다운로드 약 1.8 GB"), new("onnx-cpu-fp32", "ONNX · CPU · FP32", "cpu", "fp32", "FP32 모델 · 다운로드 약 1.8 GB")];

    public static EmbeddingEngineOption? Find(string id)
        => Options.FirstOrDefault((option) => option.Id == id);
    public static EmbeddingEngineOption RequireAvailable(string id)
    {
        EmbeddingEngineOption option = Find(id)
            ?? throw new NotSupportedException("설정에서 사용할 엔진을 선택해 주세요.");
        EmbeddingModels.RequireSupported(option.Precision, option.Backend);
        return option;
    }
}
