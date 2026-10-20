namespace PixelTrace.Mobile;

internal sealed class LibraryFiles(string data, Catalog catalog)
{
    private string Data { get; } = data;
    private Catalog Catalog { get; } = catalog;
    public string WorkDirectory { get; } = Path.Combine(data, "index-work");

    // Called only while AppState owns the single operation slot. Prepared files
    // cannot be in use by an indexing producer or a comparison at this point.
    public void Collect()
    {
        try
        {
            DeleteFiles(WorkDirectory);
            foreach (string file in Directory.EnumerateFiles(Data, "index-*.tmp").Concat(Directory.EnumerateFiles(Data, "compare-image.tmp")).Concat(Directory.EnumerateFiles(Data, "nsfw-image.tmp")))
            {
                File.Delete(file);
            }

            string thumbnails = Path.Combine(Data, "thumbnails");
            ThumbnailFiles.Collect(thumbnails, Catalog.ReferencedThumbnails());
        }
        catch (Exception error)when (error is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not turn a successfully saved index into a failed job.
            global::Android.Util.Log.Warn(AppIdentity.LogTag, "사용하지 않는 캐시 파일 정리 실패: " + error.Message);
        }
    }

    private static void DeleteFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }
    }
}
