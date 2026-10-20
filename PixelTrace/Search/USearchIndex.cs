using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PixelTrace.Mobile;

internal sealed partial class USearchIndex : IDisposable
{
    private const string Library = "usearch_c";
    private const int Float32 = 1;
    private IndexHandle Handle { get; }

    public USearchIndex(int capacity)
    {
        Options options = new() { MetricKind = 2, ScalarKind = Float32, Dimensions = EmbeddingVector.Dimensions, Connectivity = 16, ExpansionAdd = 128, ExpansionSearch = 128 };
        nint pointer = Create(ref options, out nint error);
        Handle = new(pointer);
        try
        {
            Check(error);
            if (Handle.IsInvalid) { throw new InvalidOperationException("검색을 준비하지 못했습니다."); }
            Reserve(Handle, (nuint)capacity, out error);
            Check(error);
        }
        catch
        {
            Handle.Dispose();
            throw;
        }
    }

    public static USearchIndex OpenView(string path)
    {
        Metadata(path, out Options options, out nint error);
        Check(error, invalidFile: true);
        if (options.ScalarKind != Float32 || options.Dimensions != EmbeddingVector.Dimensions || options.MetricKind != 2)
        {
            throw new InvalidDataException("검색 색인의 형식이 일치하지 않습니다.");
        }
        USearchIndex index = new(0);
        try
        {
            View(index.Handle, path, out error);
            Check(error, invalidFile: true);
            return index;
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    public unsafe void Add(ulong key, float[] vector)
    {
        if (vector.Length != EmbeddingVector.Dimensions) { throw new ArgumentException("이미지 색인의 크기가 일치하지 않습니다.", nameof(vector)); }
        fixed (float* values = vector)
        {
            AddVector(Handle, key, values, Float32, out nint error);
            Check(error);
        }
    }

    public void Save(string path)
    {
        SaveFile(Handle, path, out nint error);
        Check(error);
    }

    public unsafe IEnumerable<(ulong Key, float Distance)> Search(float[] query, int count, HashSet<ulong> allowed, HashSet<ulong>? excluded, CancellationToken ct)
    {
        if (query.Length != EmbeddingVector.Dimensions) { throw new ArgumentException("검색 벡터의 크기가 일치하지 않습니다.", nameof(query)); }
        ct.ThrowIfCancellationRequested();
        ulong[] keys = new ulong[count];
        float[] distances = new float[count];
        GCHandle filter = GCHandle.Alloc(new SearchFilter(allowed, excluded, ct));
        nuint found;
        try
        {
            fixed (float* values = query)
            fixed (ulong* resultKeys = keys)
            fixed (float* resultDistances = distances)
            {
                found = FilteredSearch(Handle, values, Float32, (nuint)count, &Accept, GCHandle.ToIntPtr(filter), resultKeys, resultDistances, out nint error);
                Check(error);
            }
        }
        finally
        {
            filter.Free();
        }
        ct.ThrowIfCancellationRequested();
        return Enumerable.Range(0, checked((int)found)).Select((index) => (keys[index], distances[index])).ToArray();
    }

    private sealed record SearchFilter(HashSet<ulong> Allowed, HashSet<ulong>? Excluded, CancellationToken Cancellation);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Accept(ulong key, nint state)
    {
        SearchFilter filter = (SearchFilter)GCHandle.FromIntPtr(state).Target!;
        return !filter.Cancellation.IsCancellationRequested && filter.Allowed.Contains(key) && filter.Excluded?.Contains(key) != true ? 1 : 0;
    }

    private static void Check(nint error, bool invalidFile = false)
    {
        if (error == 0) { return; }
        string detail = Marshal.PtrToStringUTF8(error) ?? "USearch failed";
        global::Android.Util.Log.Error(AppIdentity.LogTag, "USearch: " + detail);
        if (invalidFile) { throw new InvalidDataException("검색 색인을 읽을 수 없습니다."); }
        throw new InvalidOperationException("검색을 준비하거나 실행하지 못했습니다. 다시 시도해 주세요.");
    }

    public void Dispose()
        => Handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct Options
    {
        public int MetricKind;
        public nint Metric;
        public int ScalarKind;
        public nuint Dimensions;
        public nuint Connectivity;
        public nuint ExpansionAdd;
        public nuint ExpansionSearch;
        public byte Multi;
    }

    private sealed class IndexHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public IndexHandle(nint pointer) : base(true)
            => SetHandle(pointer);
        protected override bool ReleaseHandle()
        {
            Free(handle, out nint error);
            return error == 0;
        }
    }

    [LibraryImport(Library, EntryPoint = "usearch_init")]
    private static partial nint Create(ref Options options, out nint error);
    [LibraryImport(Library, EntryPoint = "usearch_free")]
    private static partial void Free(nint index, out nint error);
    [LibraryImport(Library, EntryPoint = "usearch_reserve")]
    private static partial void Reserve(IndexHandle index, nuint capacity, out nint error);
    [LibraryImport(Library, EntryPoint = "usearch_add")]
    private static unsafe partial void AddVector(IndexHandle index, ulong key, float* vector, int kind, out nint error);
    [LibraryImport(Library, EntryPoint = "usearch_save", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void SaveFile(IndexHandle index, string path, out nint error);
    [LibraryImport(Library, EntryPoint = "usearch_view", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void View(IndexHandle index, string path, out nint error);
    [LibraryImport(Library, EntryPoint = "usearch_metadata", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void Metadata(string path, out Options options, out nint error);
    [LibraryImport(Library, EntryPoint = "usearch_filtered_search")]
    private static unsafe partial nuint FilteredSearch(IndexHandle index, float* query, int kind, nuint count, delegate* unmanaged[Cdecl]<ulong, nint, int> filter, nint state, ulong* keys, float* distances, out nint error);
}
