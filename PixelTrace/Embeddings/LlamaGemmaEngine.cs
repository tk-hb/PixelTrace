using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace PixelTrace.Mobile;

internal sealed partial class LlamaGemmaEngine : IDisposable
{
    internal const string Revision = "c0fd78626405c6d9b934efef0ec7291fcc20e3d6";
    private EngineHandle Handle { get; }
    private string EngineName { get; }

    internal static string? UnavailableReason(string backend)
    {
        if (backend is not ("cpu" or "gpu"))
        {
            return "llama.cpp의 CPU 또는 Vulkan GPU 엔진을 선택해 주세요.";
        }

        string directory = global::Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!;
        return File.Exists(Path.Combine(directory, "libpixeltrace_llama.so")) ? null : "llama.cpp 네이티브 라이브러리가 포함되지 않은 앱입니다.";
    }

    public LlamaGemmaEngine(string directory, int softTokens, string backend)
    {
        EngineName = "llama.cpp BF16 " + backend.ToUpperInvariant();
        if (backend is not ("cpu" or "gpu"))
        {
            throw new NotSupportedException("llama.cpp의 CPU 또는 Vulkan GPU 엔진을 선택해 주세요.");
        }

        try
        {
            if (Marshal.PtrToStringUTF8(NativeRevision()) != Revision)
            {
                throw new NotSupportedException("llama.cpp 네이티브 라이브러리의 버전이 앱과 다릅니다.");
            }

            nint pointer = Create(Path.Combine(directory, EmbeddingModels.LlamaModelFile), Path.Combine(directory, EmbeddingModels.LlamaProjectorFile), softTokens, backend == "gpu" ? 1 : 0);
            if (pointer == 0)
            {
                throw NativeError(EngineName + " 모델 로딩 실패");
            }

            Handle = new(pointer);
        }
        catch (Exception error)when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new NotSupportedException("llama.cpp 네이티브 라이브러리를 불러오지 못했습니다. 설치한 APK와 기기의 네이티브 라이브러리 호환성을 확인해 주세요.", error);
        }
    }

    public float[] EncodeText(string query, CancellationToken ct)
        => Encode(0, Encoding.UTF8.GetBytes(query), ct);
    public float[] EncodeImage(byte[] content, CancellationToken ct)
        => Encode(1, content, ct);
    private unsafe float[] Encode(int kind, byte[] input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        float[] output = new float[EmbeddingVector.Dimensions];
        fixed (byte* source = input)
        fixed (float* target = output)
        {
            if (EncodeNative(Handle, kind, source, (nuint)input.Length, target, output.Length) != 0)
            {
                ct.ThrowIfCancellationRequested();
                throw NativeError(EngineName + (kind == 0 ? " 텍스트 추론 실패" : " 이미지 추론 실패"));
            }
        }

        // Vulkan inference has no safe per-call interrupt here. Cancellation is
        // observed before/after the call; never dispose a live GPU context.
        ct.ThrowIfCancellationRequested();
        return output;
    }

    private static InvalidOperationException NativeError(string operation)
        => new(operation + " · " + (Marshal.PtrToStringUTF8(LastError()) ?? "네이티브 오류"));
    public void Dispose()
        => Handle.Dispose();

    private sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public EngineHandle(nint pointer) : base(true)
            => SetHandle(pointer);
        protected override bool ReleaseHandle()
        {
            Free(handle);
            return true;
        }
    }

    [LibraryImport("pixeltrace_llama", EntryPoint = "pt_llama_create_v2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Create(string model, string projector, int softTokens, int backend);
    [LibraryImport("pixeltrace_llama", EntryPoint = "pt_llama_free")]
    private static partial void Free(nint handle);
    [LibraryImport("pixeltrace_llama", EntryPoint = "pt_llama_encode")]
    private static unsafe partial int EncodeNative(EngineHandle handle, int kind, byte* input, nuint length, float* output, int dimensions);
    [LibraryImport("pixeltrace_llama", EntryPoint = "pt_llama_error")]
    private static partial nint LastError();
    [LibraryImport("pixeltrace_llama", EntryPoint = "pt_llama_revision")]
    private static partial nint NativeRevision();
}
