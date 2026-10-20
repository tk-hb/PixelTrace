using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Path = System.IO.Path;

namespace PixelTrace.Mobile;

internal sealed class ImageViewerDialog(Activity activity, AppState state)
{
    private Activity Activity { get; } = activity;
    private AppState State { get; } = state;

    public void Show(ImageItem image)
    {
        Dialog dialog = new(Activity, global::Android.Resource.Style.ThemeMaterialNoActionBarFullscreen);
        LinearLayout panel = Ui.Column(Activity);
        panel.SetBackgroundColor(Color.Black);
        panel.SetOnApplyWindowInsetsListener(new InsetsListener());
        LinearLayout toolbar = Ui.Row(Activity);
        toolbar.SetPadding(Ui.Dp(Activity, 12), Ui.Dp(Activity, 8), Ui.Dp(Activity, 8), Ui.Dp(Activity, 8));
        TextView title = Ui.Text(Activity, State.Preferences.HideNames ? "이미지 확대" : image.Name, 14);
        title.SetSingleLine(true);
        title.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        toolbar.AddView(title, new LinearLayout.LayoutParams(0, -2, 1));
        toolbar.AddView(Ui.Icon(Activity, Glyph.Close, "확대 보기 닫기", dialog.Dismiss), Ui.Layout(Activity, 48, 48));
        panel.AddView(toolbar);
        ZoomImageView picture = new(Activity) { ContentDescription = "핀치 또는 더블 탭으로 확대·축소" };
        panel.AddView(picture, new LinearLayout.LayoutParams(-1, 0, 1));
        TextView status = Ui.Text(Activity, "이미지를 불러오는 중…", 12, Ui.Muted);
        status.Gravity = GravityFlags.Center;
        panel.AddView(status, Ui.Layout(Activity, top: 8, bottom: 8));
        panel.AddView(Ui.Button(Activity, "배율 초기화", picture.ResetZoom), Ui.Layout(Activity, top: 8, bottom: 8));
        dialog.SetContentView(panel);
        dialog.Show();
        dialog.Window!.SetLayout(-1, -1);
        CancellationTokenSource lifetime = new();
        bool masked = State.Preferences.MasksImage(image.NsfwScore);
        Bitmap? fullImage = null;
        Button? reveal = null;
        if (masked)
        {
            reveal = Ui.Button(Activity, "블러 해제하고 보기", () => {
                if (fullImage is not null)
                {
                    picture.SetPicture(fullImage);
                    reveal!.Visibility = ViewStates.Gone;
                }
            });
            reveal.Enabled = false;
            panel.AddView(reveal, Ui.Layout(Activity, bottom: 8));
        }

        Task load = LoadAsync();
        dialog.DismissEvent += async (_, _) => {
            lifetime.Cancel();
            await load;
            picture.SetImageDrawable(null);
            fullImage?.Dispose();
            picture.Dispose();
            lifetime.Dispose();
        };

        async Task LoadAsync()
        {
            string temporary = Path.Combine(Activity.CacheDir!.AbsolutePath, "viewer-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                fullImage = await Task.Run(async () => {
                    DocumentLibrary documents = new(Activity.ContentResolver!);
                    await documents.CopyAsync(image.Uri, temporary, lifetime.Token);
                    return ImagePreparation.ReadImage(temporary, lifetime.Token, (bitmap, _, _) => bitmap.Copy(Bitmap.Config.Argb8888!, false)!, 4096);
                }, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (Activity.IsDestroyed || !dialog.IsShowing)
                {
                    return;
                }

                if (masked)
                {
                    using Bitmap blurred = await Task.Run(() => ImagePrivacy.Blur(fullImage), lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    picture.SetPicture(blurred);
                    reveal!.Enabled = true;
                }
                else
                {
                    picture.SetPicture(fullImage);
                }

                status.Text = "핀치 확대·축소 · 드래그 이동 · 더블 탭 확대/초기화";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                global::Android.Util.Log.Warn(AppIdentity.LogTag, "확대 이미지 읽기 실패: " + error.Message);
                if (!Activity.IsDestroyed && dialog.IsShowing)
                {
                    status.Text = "사진을 읽지 못했습니다. 폴더 접근 권한을 확인해 주세요.";
                }
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException error)
                {
                    global::Android.Util.Log.Warn(AppIdentity.LogTag, "확대 이미지 캐시 정리 실패: " + error.Message);
                }
            }
        }
    }

    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View? view, WindowInsets? insets)
        {
            Insets bars = OperatingSystem.IsAndroidVersionAtLeast(30) ? insets!.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout())! : insets!.SystemWindowInsets!;
            view!.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets;
        }
    }
}
