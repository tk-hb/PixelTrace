using Android.Content;
using Android.Graphics;
using Path = System.IO.Path;

namespace PixelTrace.Mobile;

public sealed class ThumbnailCache(Context context, string thumbnails)
{
    private string CacheDirectory { get; } = context.CacheDir!.AbsolutePath;
    private DocumentLibrary Documents { get; } = new(context.ContentResolver!);
    private ImagePreparation Preprocessor { get; } = new(thumbnails);

    // Runs on a background thread and rebuilds missing thumbnails from the URI
    // without loading a model or computing an embedding.
    public async Task<Bitmap?> LoadAsync(ImageItem image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(image.Thumbnail))
        {
            return BitmapFactory.DecodeFile(image.Thumbnail);
        }

        string temporary = Path.Combine(CacheDirectory, "preview-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await Documents.CopyAsync(image.Uri, temporary, ct);
            string hash = Path.GetFileNameWithoutExtension(image.Thumbnail);
            string preview = Preprocessor.RebuildThumbnail(temporary, hash, ct);
            return BitmapFactory.DecodeFile(preview);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
