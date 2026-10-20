using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace PixelTrace.Mobile;

public sealed class ImageGridAdapter(Activity activity, Preferences preferences, ThumbnailCache thumbnails) : BaseAdapter<ImageItem>
{
    private Activity Activity { get; } = activity;
    private Preferences Preferences { get; } = preferences;
    private ThumbnailCache Thumbnails { get; } = thumbnails;

    private IReadOnlyList<ImageItem> rows = [];
    private SemaphoreSlim Decoding { get; } = new(3, 3);
    private CancellationTokenSource Lifetime { get; } = new();

    public void StopLoading()
        => Lifetime.Cancel();
    public void SetRows(IReadOnlyList<ImageItem> value)
    {
        rows = value;
        NotifyDataSetChanged();
    }

    public override int Count
        => rows.Count;

    public override ImageItem this[int position]
        => rows[position];
    public override long GetItemId(int position)
        => rows[position].Id;
    private sealed class Holder(ImageView picture, ImageView placeholder, TextView name, TextView info, TextView score) : Java.Lang.Object
    {
        public ImageView Picture { get; } = picture;
        public ImageView Placeholder { get; } = placeholder;
        public TextView Name { get; } = name;
        public TextView Info { get; } = info;
        public TextView Score { get; } = score;

        public int Version;
    }

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        ImageItem item = rows[position];
        LinearLayout? card = convertView as LinearLayout;
        Holder holder;
        if (card is null)
        {
            card = new(Activity) {
                Orientation = Android.Widget.Orientation.Vertical,
                ClipToOutline = true
            };
            card.Background = Ui.Surface(Activity, Ui.Panel, 14);
            card.LayoutParameters = new AbsListView.LayoutParams(-1, -2);
            FrameLayout media = new(Activity);
            RatioImageView picture = new(Activity);
            picture.SetScaleType(ImageView.ScaleType.CenterCrop);
            picture.SetBackgroundColor(Color.ParseColor("#100f15"));
            media.AddView(picture, new FrameLayout.LayoutParams(-1, -2));
            ImageView placeholder = Ui.Symbol(Activity, Glyph.Image, Ui.Muted);
            placeholder.ImportantForAccessibility = ImportantForAccessibility.No;
            media.AddView(placeholder, new FrameLayout.LayoutParams(Dp(28), Dp(28), GravityFlags.Center));
            TextView score = Ui.Text(Activity, "", 10, Ui.TextColor, true);
            score.SetSingleLine(true);
            score.Gravity = GravityFlags.Center;
            score.SetPadding(Dp(7), Dp(5), Dp(7), Dp(5));
            score.Background = Ui.Surface(Activity, Color.ParseColor("#D0181720"), 7, false);
            media.AddView(score, new FrameLayout.LayoutParams(-2, -2, GravityFlags.Top | GravityFlags.End) { TopMargin = Dp(8), RightMargin = Dp(8) });
            card.AddView(media, new LinearLayout.LayoutParams(-1, -2));
            LinearLayout body = new(Activity) {
                Orientation = Android.Widget.Orientation.Vertical
            };
            body.SetPadding(Dp(11), Dp(11), Dp(11), Dp(12));
            card.AddView(body);
            TextView name = Ui.Text(Activity, "", 11);
            name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            name.SetLines(1);
            body.AddView(name);
            TextView info = Ui.Text(Activity, "", 10, Ui.Muted);
            info.SetSingleLine(true);
            info.SetLines(1);
            info.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            body.AddView(info, Ui.Layout(Activity, top: 7));
            holder = new(picture, placeholder, name, info, score);
            card.Tag = holder;
        }
        else
        {
            holder = (Holder)card.Tag!;
        }

        holder.Name.Visibility = Preferences.HideNames ? ViewStates.Gone : ViewStates.Visible;
        holder.Name.Text = Preferences.HideNames ? "" : item.Name;
        holder.Info.Text = $"{item.Width} × {item.Height}";
        bool masked = Preferences.MasksImage(item.NsfwScore);
        holder.Score.Text = masked ? item.NsfwScore.HasValue ? "NSFW" : "미판별" : item.Score.HasValue ? $"{item.Score:P0}" : "";
        holder.Score.Visibility = masked || item.Score.HasValue ? ViewStates.Visible : ViewStates.Gone;
        LinearLayout.LayoutParams infoLayout = (LinearLayout.LayoutParams)holder.Info.LayoutParameters!;
        infoLayout.TopMargin = Preferences.HideNames ? 0 : Dp(7);
        holder.Info.LayoutParameters = infoLayout;
        holder.Picture.ContentDescription = Preferences.HideNames ? "이미지" : item.Name;
        holder.Picture.SetImageDrawable(null);
        holder.Placeholder.Visibility = ViewStates.Visible;
        int version = ++holder.Version;
        _ = LoadAsync(holder, item, version);
        return card;
    }

    private async Task LoadAsync(Holder holder, ImageItem image, int version)
    {
        try
        {
            await Decoding.WaitAsync(Lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Bitmap? bitmap = null;
        try
        {
            if (Lifetime.IsCancellationRequested || Activity.IsDestroyed || holder.Version != version)
            {
                return;
            }

            bitmap = await Task.Run(() => Thumbnails.LoadAsync(image, Lifetime.Token), Lifetime.Token);
            if (bitmap is not null && Preferences.MasksImage(image.NsfwScore))
            {
                Bitmap blurred = await Task.Run(() => ImagePrivacy.Blur(bitmap), Lifetime.Token);
                bitmap.Dispose();
                bitmap = blurred;
            }
        }
        catch (OperationCanceledException)
        {
            bitmap?.Dispose();
            return;
        }
        catch (Exception error)when (error is InvalidDataException or IOException or UnauthorizedAccessException or Java.IO.IOException or Java.Lang.RuntimeException)
        {
            bitmap?.Dispose();
            bitmap = null;
            global::Android.Util.Log.Warn(AppIdentity.LogTag, "썸네일을 읽지 못했습니다.");
        }
        finally
        {
            Decoding.Release();
        }

        if (bitmap is null)
        {
            return;
        }

        Activity.RunOnUiThread(() => {
            if (Lifetime.IsCancellationRequested || Activity.IsDestroyed || holder.Version != version)
            {
                bitmap.Dispose();
                return;
            }

            holder.Picture.SetImageBitmap(bitmap);
            holder.Placeholder.Visibility = ViewStates.Gone;
            // The Java ImageView retains its bitmap; release only the managed JNI reference.
            bitmap.Dispose();
        });
    }

    private int Dp(int value)
        => (int)(Activity.Resources!.DisplayMetrics!.Density * value);
}
