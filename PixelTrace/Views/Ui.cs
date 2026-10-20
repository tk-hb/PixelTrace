using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace PixelTrace.Mobile;

public static class Ui
{
    public static Color Bg { get; } = Color.ParseColor("#15141b");
    public static Color Panel { get; } = Color.ParseColor("#1c1b24");
    public static Color Field { get; } = Color.ParseColor("#25242e");
    public static Color Border { get; } = Color.ParseColor("#34323f");
    public static Color Muted { get; } = Color.ParseColor("#a7a3b6");
    public static Color TextColor { get; } = Color.ParseColor("#f4f2f8");
    public static Color Accent { get; } = Color.ParseColor("#b3a0ff");
    public static Color Primary { get; } = Color.ParseColor("#8066dc");

    public static int Dp(Context c, float value)
        => (int)(c.Resources!.DisplayMetrics!.Density * value + .5f);
    public static LinearLayout Column(Context context)
        => new(context) {
        Orientation = Android.Widget.Orientation.Vertical
    };
    public static LinearLayout Row(Context context)
    {
        LinearLayout row = new(context) {
            Orientation = Android.Widget.Orientation.Horizontal
        };
        row.SetGravity(GravityFlags.CenterVertical);
        return row;
    }

    public static GradientDrawable Surface(Context c, Color color, int radius = 10, bool border = true)
    {
        GradientDrawable drawable = new();
        drawable.SetColor(color);
        drawable.SetCornerRadius(Dp(c, radius));
        if (border)
        {
            drawable.SetStroke(Dp(c, 1), Border);
        }

        return drawable;
    }

    public static TextView Text(Context c, string text, float size, Color? color = null, bool bold = false)
    {
        TextView view = new(c) {
            Text = text,
            TextSize = size,
            Typeface = Typeface.Create("sans-serif", bold ? TypefaceStyle.Bold : TypefaceStyle.Normal)
        };
        view.SetTextColor(color ?? TextColor);
        view.SetIncludeFontPadding(false);
        return view;
    }

    public static EditText Input(Context c, string text = "", string? hint = null)
    {
        EditText input = new(c) {
            Text = text,
            Hint = hint,
            TextSize = 14,
            Gravity = GravityFlags.CenterVertical
        };
        input.SetSingleLine(true);
        input.SetTextColor(TextColor);
        input.SetHintTextColor(Muted);
        input.SetMinHeight(Dp(c, 52));
        input.SetPadding(Dp(c, 14), Dp(c, 12), Dp(c, 14), Dp(c, 12));
        input.Background = InputBackground(c);
        return input;
    }

    private static StateListDrawable InputBackground(Context context)
    {
        GradientDrawable focused = Surface(context, Field, 10);
        focused.SetStroke(Dp(context, 2), Accent);
        StateListDrawable states = new();
        states.AddState([global::Android.Resource.Attribute.StateFocused], focused);
        states.AddState([], Surface(context, Field, 10));
        return states;
    }

    public static void FitDialog(global::Android.App.AlertDialog dialog)
    {
        Window? window = dialog.Window;
        if (window is null)
        {
            return;
        }

        int available = dialog.Context!.Resources!.DisplayMetrics!.WidthPixels;
        window.SetLayout(Math.Min(Dp(dialog.Context, 560), available - Dp(dialog.Context, 32)), ViewGroup.LayoutParams.WrapContent);
        window.SetSoftInputMode(SoftInput.AdjustResize);
    }

    public static Button Button(Context c, string text, Action action, string kind = "secondary")
    {
        Button button = new(c) {
            Text = text
        };
        Style(c, button, kind);
        button.Click += (_, _) => action();
        return button;
    }

    public static void Style(Context c, Button button, string kind = "secondary")
    {
        button.SetAllCaps(false);
        button.TextSize = 12;
        button.StateListAnimator = null;
        button.SetSingleLine(true);
        button.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        button.SetMinHeight(Dp(c, 48));
        button.SetMinWidth(0);
        button.SetPadding(Dp(c, 12), Dp(c, 10), Dp(c, 12), Dp(c, 10));
        button.Typeface = Typeface.Create("sans-serif", kind == "primary" ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        button.SetTextColor(kind == "quiet" ? Muted : TextColor);
        GradientDrawable background = Surface(c, kind == "primary" ? Primary : kind == "quiet" ? Color.Transparent : Field, 10, kind == "secondary");
        button.Background = new RippleDrawable(ColorStateList.ValueOf(Color.ParseColor("#337f6ca8")), background, Surface(c, Color.White, 10, false));
    }

    public static Button Select(Context c, string text, Action action)
    {
        Button button = new(c) {
            Text = text
        };
        Style(c, button);
        button.Gravity = GravityFlags.CenterVertical | GravityFlags.Left;
        button.SetPadding(Dp(c, 12), Dp(c, 10), Dp(c, 12), Dp(c, 10));
        button.SetCompoundDrawablesRelativeWithIntrinsicBounds(null, null, GlyphIcons.Create(c, Glyph.Chevron, Muted), null);
        button.CompoundDrawablePadding = Dp(c, 8);
        button.Click += (_, _) => action();
        return button;
    }

    public static Button Icon(Context c, Glyph glyph, string description, Action action, bool border = false)
    {
        Button button = new(c) {
            ContentDescription = description
        };
        Style(c, button, border ? "secondary" : "quiet");
        button.SetPadding(Dp(c, 12), 0, Dp(c, 12), 0);
        button.SetCompoundDrawablesRelativeWithIntrinsicBounds(GlyphIcons.Create(c, glyph, TextColor), null, null, null);
        button.CompoundDrawableTintList = new ColorStateList([[-global::Android.Resource.Attribute.StateEnabled], []], [Muted.ToArgb(), TextColor.ToArgb()]);
        button.Click += (_, _) => action();
        return button;
    }

    public static ImageView Symbol(Context c, Glyph glyph, Color? color = null)
    {
        ImageView view = new(c);
        view.SetImageDrawable(GlyphIcons.Create(c, glyph, color ?? Accent));
        return view;
    }

    public static LinearLayout.LayoutParams Layout(Context c, int width = -1, int height = -2, int top = 0, int bottom = 0, int left = 0, int right = 0)
        => new(width < 0 ? width : Dp(c, width), height < 0 ? height : Dp(c, height)) {
        TopMargin = Dp(c, top),
        BottomMargin = Dp(c, bottom),
        LeftMargin = Dp(c, left),
        RightMargin = Dp(c, right)
    };
}
