using Android.Graphics;
using Android.Media;
using Orientation = Android.Media.Orientation;
using Stream = System.IO.Stream;

namespace PixelTrace.Mobile;

public sealed class ImagePreparation(string thumbnails)
{
    private string Thumbnails { get; } = thumbnails;

    public PreparedImage Prepare(string file, string hash, CancellationToken ct)
        => PrepareCore(file, hash, null, ct);
    internal PreparedImage PrepareToFile(string file, string hash, string destination, CancellationToken ct)
        => PrepareCore(file, hash, destination, ct);
    private PreparedImage PrepareCore(string file, string hash, string? destination, CancellationToken ct)
        => ReadImage(file, ct, (image, width, height) => {
        string thumbnail = EnsureThumbnail(image, hash);
        ct.ThrowIfCancellationRequested();
        using Stream encoded = destination is null ? new MemoryStream() : File.Create(destination);
        using Bitmap rgbImage = Resize(image, image.Width, image.Height);
        if (!rgbImage.Compress(Bitmap.CompressFormat.Png!, 100, encoded))
        {
            throw new IOException("이미지를 준비하지 못했습니다.");
        }

        ct.ThrowIfCancellationRequested();
        return new PreparedImage(destination is null ? ((MemoryStream)encoded).ToArray() : [], width, height, thumbnail);
    });
    internal string RebuildThumbnail(string file, string hash, CancellationToken ct)
        => ReadImage(file, ct, (image, _, _) => EnsureThumbnail(image, hash));
    private string EnsureThumbnail(Bitmap image, string hash)
        => ThumbnailFiles.Ensure(Thumbnails, hash, (path) => SaveThumbnail(image, path));
    internal static T ReadImage<T>(string file, CancellationToken ct, Func<Bitmap, int, int, T> read, int maximumSide = 2048)
    {
        ct.ThrowIfCancellationRequested();
        using BitmapFactory.Options bounds = new() {
            InJustDecodeBounds = true
        };
        BitmapFactory.DecodeFile(file, bounds)?.Dispose();
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
        {
            throw new InvalidDataException("지원하지 않거나 손상된 이미지입니다.");
        }

        int sample = 1;
        while (maximumSide < Math.Max(bounds.OutWidth, bounds.OutHeight) / sample)
        {
            sample *= 2;
        }

        using BitmapFactory.Options decode = new() {
            InSampleSize = sample,
            InPreferredConfig = Bitmap.Config.Argb8888
        };
        using Bitmap original = BitmapFactory.DecodeFile(file, decode)
            ?? throw new InvalidDataException("이미지를 읽지 못했습니다.");
        using Matrix matrix = new();
        Orientation orientation;
        using (ExifInterface exif = new(file))
        {
            orientation = (Orientation)exif.GetAttributeInt(ExifInterface.TagOrientation, (int)Orientation.Normal);
            switch (orientation)
            {
                case Orientation.FlipHorizontal:
                    matrix.SetScale(-1, 1);
                    break;
                case Orientation.Rotate180:
                    matrix.SetRotate(180);
                    break;
                case Orientation.FlipVertical:
                    matrix.SetScale(1, -1);
                    break;
                case Orientation.Transpose:
                    matrix.SetRotate(90);
                    matrix.PostScale(-1, 1);
                    break;
                case Orientation.Rotate90:
                    matrix.SetRotate(90);
                    break;
                case Orientation.Transverse:
                    matrix.SetRotate(-90);
                    matrix.PostScale(-1, 1);
                    break;
                case Orientation.Rotate270:
                    matrix.SetRotate(-90);
                    break;
            }
        }

        // CreateBitmap may return the original object when the matrix is identity.
        Bitmap? rotated = matrix.IsIdentity ? null : Bitmap.CreateBitmap(original, 0, 0, original.Width, original.Height, matrix, true);
        try
        {
            Bitmap image = rotated ?? original;
            bool swaps = orientation is Orientation.Transpose or Orientation.Rotate90 or Orientation.Transverse or Orientation.Rotate270;
            int width = swaps ? bounds.OutHeight : bounds.OutWidth;
            int height = swaps ? bounds.OutWidth : bounds.OutHeight;
            ct.ThrowIfCancellationRequested();
            T result = read(image, width, height);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            rotated?.Dispose();
        }
    }

    private static void SaveThumbnail(Bitmap image, string path)
    {
        double ratio = Math.Min(1, 320d / Math.Max(image.Width, image.Height));
        using Bitmap small = Resize(image, Math.Max(1, (int)(image.Width * ratio)), Math.Max(1, (int)(image.Height * ratio)));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = File.Create(temporary))
            {
                if (!small.Compress(Bitmap.CompressFormat.Jpeg!, 85, output))
                {
                    throw new IOException("썸네일을 저장하지 못했습니다.");
                }
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        Bitmap result = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using Canvas canvas = new(result);
        using Paint paint = new() {
            FilterBitmap = true
        };
        using Rect bounds = new(0, 0, width, height);
        canvas.DrawColor(Color.Black);
        canvas.DrawBitmap(source, null, bounds, paint);
        return result;
    }
}
