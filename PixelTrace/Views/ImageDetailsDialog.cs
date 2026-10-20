using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Uri = Android.Net.Uri;

namespace PixelTrace.Mobile;

internal sealed class ImageDetailsDialog(Activity activity, AppState state)
{
    private Activity Activity { get; } = activity;
    private AppState State { get; } = state;
    private DialogPresenter Dialogs { get; } = new(activity, state.Preferences);

    public void Show(ImageItem image)
    {
        LinearLayout panel = Ui.Column(Activity);
        panel.SetPadding(Ui.Dp(Activity, 20), Ui.Dp(Activity, 16), Ui.Dp(Activity, 20), Ui.Dp(Activity, 20));
        Dialog dialog = new(Activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        LinearLayout header = Ui.Row(Activity);
        header.AddView(Ui.Text(Activity, "이미지 상세", 17, bold: true), new LinearLayout.LayoutParams(0, -2, 1));
        header.AddView(Ui.Icon(Activity, Glyph.Close, "이미지 상세 닫기", dialog.Dismiss), Ui.Layout(Activity, 48, 48));
        panel.AddView(header, Ui.Layout(Activity, bottom: 12));
        ScrollView scroll = new(Activity);
        LinearLayout content = Ui.Column(Activity);
        ImageView picture = new(Activity);
        picture.SetScaleType(ImageView.ScaleType.FitCenter);
        picture.Background = Ui.Surface(Activity, Color.ParseColor("#100f15"), 12, false);
        picture.ClipToOutline = true;
        picture.ContentDescription = "이미지 확대 보기";
        picture.Click += (_, _) => new ImageViewerDialog(Activity, State).Show(image);
        content.AddView(picture, Ui.Layout(Activity, height: 260, bottom: 16));
        if (!State.Preferences.HideNames)
        {
            TextView name = Ui.Text(Activity, image.Name, 17, bold: true);
            name.SetMaxLines(3);
            name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            content.AddView(name, Ui.Layout(Activity, bottom: 9));
        }

        content.AddView(Ui.Text(Activity, $"{image.Width} × {image.Height}" + (image.Score.HasValue ? $" · 유사도 {image.Score:P1}" : ""), 12, Ui.Muted));
        if (!State.Preferences.HidePaths && !State.Preferences.HideNames)
        {
            content.AddView(Ui.Text(Activity, image.Path, 11, Ui.Muted), Ui.Layout(Activity, top: 8));
        }

        scroll.AddView(content);
        panel.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        panel.AddView(Ui.Button(Activity, "확대 보기", () => new ImageViewerDialog(Activity, State).Show(image)), Ui.Layout(Activity, top: 16));
        panel.AddView(Ui.Button(Activity, "원본 열기", () => {
            try
            {
                Activity.StartActivity(new Intent(Intent.ActionView).SetDataAndType(Uri.Parse(image.Uri), "image/*")!.AddFlags(ActivityFlags.GrantReadUriPermission));
            }
            catch (ActivityNotFoundException)
            {
                Dialogs.Alert("원본", "사진을 열 앱이 없습니다.");
            }
            catch (Java.Lang.SecurityException)
            {
                Dialogs.Alert("사진 접근 권한", "폴더 추가에서 사진 접근 권한을 허용하거나 해당 폴더를 다시 선택해 주세요.");
            }
        }), Ui.Layout(Activity, top: 8));
        Button similar = Ui.Button(Activity, "비슷한 이미지 찾기", async () => {
            if (State.Operation.Busy) { return; }
            dialog.Dismiss();
            SearchReference reference = new(image.Uri, image.Name, image.Thumbnail, image.NsfwScore);
            await State.RunAsync("유사 이미지 검색 중…", async (ct) => State.ShowResults(await State.Library.SimilarAsync(image.Uri, State.Preferences.Scope, State.Report, ct), "유사 이미지", reference), collectFiles: true);
        }, "primary");
        similar.Enabled = !State.Operation.Busy;
        panel.AddView(similar, Ui.Layout(Activity, top: 8));
        void RefreshActions()
            => Activity.RunOnUiThread(() => {
                if (!Activity.IsDestroyed && dialog.IsShowing)
                {
                    similar.Enabled = !State.Operation.Busy;
                }
            });
        State.Changed += RefreshActions;
        dialog.DismissEvent += (_, _) => State.Changed -= RefreshActions;
        dialog.SetContentView(panel);
        dialog.Show();
        RefreshActions();
        dialog.Window!.SetBackgroundDrawable(Ui.Surface(Activity, Ui.Panel, 16));
        dialog.Window.SetLayout(Math.Min(Activity.Resources!.DisplayMetrics!.WidthPixels - Ui.Dp(Activity, 32), Ui.Dp(Activity, 680)), (int)(Activity.Resources.DisplayMetrics.HeightPixels * .88));
        _ = LoadPreviewAsync(dialog, picture, image);
    }

    private async Task LoadPreviewAsync(Dialog dialog, ImageView picture, ImageItem image)
    {
        try
        {
            using Bitmap? bitmap = await Task.Run(async () => {
                Bitmap? original = await State.Thumbnails.LoadAsync(image, CancellationToken.None);
                if (original is not null && State.Preferences.MasksImage(image.NsfwScore))
                {
                    using (original)
                    {
                        return ImagePrivacy.Blur(original);
                    }
                }

                return original;
            });
            if (!Activity.IsDestroyed && dialog.IsShowing && bitmap is not null)
            {
                picture.SetImageBitmap(bitmap);
            }
        }
        catch (Exception error)when (error is InvalidDataException or IOException or UnauthorizedAccessException or Java.IO.IOException or Java.Lang.RuntimeException)
        {
            global::Android.Util.Log.Warn(AppIdentity.LogTag, "이미지 미리 보기를 읽지 못했습니다.");
        }
    }
}
