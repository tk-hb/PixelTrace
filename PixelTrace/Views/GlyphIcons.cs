using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;

namespace PixelTrace.Mobile;

public enum Glyph
{
    Search,
    Library,
    Compare,
    More,
    Close,
    Plus,
    Folder,
    Grid,
    Chevron,
    Image
}

internal static class GlyphIcons
{
    public static Drawable Create(Context context, Glyph glyph, Color color)
    {
        string name = "ic_" + (glyph == Glyph.Compare ? "image" : glyph.ToString().ToLowerInvariant());
        int resource = context.Resources!.GetIdentifier(name, "drawable", context.PackageName);
        Drawable drawable = context.GetDrawable(resource)!.Mutate();
        drawable.SetTint(color.ToArgb());
        return drawable;
    }
}
