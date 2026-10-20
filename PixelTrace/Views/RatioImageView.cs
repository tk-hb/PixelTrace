using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace PixelTrace.Mobile;

public sealed class RatioImageView(Context context) : ImageView(context)
{
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec);
        SetMeasuredDimension(width, ResolveSize(width * 3 / 4, heightMeasureSpec));
    }
}
