using Microsoft.ML.OnnxRuntime;

namespace PixelTrace.Mobile;

internal static class OnnxRuntimeSupport
{
    private static object Gate { get; } = new();

    private static bool registered;
    private static string LibraryDirectory
        => global::Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!;

    public static string? UnavailableReason(string backend)
    {
        if (backend is not ("cpu" or "gpu"))
        {
            return "설정에서 사용할 CPU/GPU 엔진을 선택해 주세요.";
        }

        string[] libraries = backend == "gpu" ? ["libonnxruntime.so", "libtokenizers_proto.so", "libonnxruntime_providers_webgpu.so"] : ["libonnxruntime.so", "libtokenizers_proto.so"];
        return libraries.All((name) => File.Exists(Path.Combine(LibraryDirectory, name))) ? null : "이 앱에서 선택한 ONNX 엔진을 사용할 수 없습니다.";
    }

    public static SessionOptions CreateOptions(string backend, bool textModel)
    {
        if (UnavailableReason(backend)is { } reason)
        {
            throw new NotSupportedException(reason);
        }

        SessionOptions options = new() {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
            EnableCpuMemArena = false,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };
        try
        {
            if (backend == "gpu")
            {
                OrtEnv environment = OrtEnv.Instance();
                lock (Gate)
                {
                    if (!registered)
                    {
                        environment.RegisterExecutionProviderLibrary("pixeltrace_webgpu", Path.Combine(LibraryDirectory, "libonnxruntime_providers_webgpu.so"));
                        registered = true;
                    }
                }

                OrtEpDevice device = environment.GetEpDevices().FirstOrDefault((device) => device.EpName == "WebGpuExecutionProvider")
                    ?? throw new NotSupportedException("이 기기에서 ONNX WebGPU 장치를 찾지 못했습니다. 실행 장치를 자동 변경하지 않습니다.");
                Dictionary<string, string> settings = new() {
                    ["powerPreference"] = "high-performance",
                    ["enableGraphCapture"] = "0",
                    ["storageBufferCacheMode"] = "disabled"
                };
                if (textModel)
                {
                    // The FP32 vocabulary table exceeds common Android GPU binding
                    // limits. Keep this lookup on CPU without changing its precision.
                    settings["forceCpuNodeNames"] = "/model/embed_tokens/Gather";
                }

                options.AppendExecutionProvider(environment, [device], settings);
            }

            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }
}
