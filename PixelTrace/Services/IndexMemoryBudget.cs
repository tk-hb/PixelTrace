using Android.App;
using Android.Content;

namespace PixelTrace.Mobile;

internal sealed class IndexMemoryBudget
{
    private const long MiB = 1024 * 1024;
    public long InputBytes { get; }
    public int PreparationConcurrency { get; }

    public IndexMemoryBudget(Context context, int requestedConcurrency)
    {
        ActivityManager manager = (ActivityManager)context.GetSystemService(Context.ActivityService)!;
        long heapBytes = manager.MemoryClass * MiB;
        // Leave room for bitmaps, the runtime, and JNI copies. This budgets input
        // buffers; it does not estimate the model's native/GPU allocations.
        InputBytes = Math.Max(16 * MiB, heapBytes / 8);
        PreparationConcurrency = Math.Min(requestedConcurrency, Math.Max(1, (int)(heapBytes / (128 * MiB))));
    }
}
