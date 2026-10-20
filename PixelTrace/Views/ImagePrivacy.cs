using Android.Graphics;

namespace PixelTrace.Mobile;

internal static class ImagePrivacy
{
    public static Bitmap Blur(Bitmap source)
    {
        float ratio = Math.Min(1, 12f / Math.Max(source.Width, source.Height));
        using Bitmap small = Resize(source, Math.Max(1, (int)(source.Width * ratio)), Math.Max(1, (int)(source.Height * ratio)));
        return Resize(small, source.Width, source.Height);
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        Bitmap bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using Canvas canvas = new(bitmap);
        using Paint paint = new() { FilterBitmap = true };
        using Rect bounds = new(0, 0, width, height);
        canvas.DrawBitmap(source, null, bounds, paint);
        return bitmap;
    }
}
