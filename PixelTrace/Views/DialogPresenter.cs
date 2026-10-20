using Android.App;
using Android.Content;
using Android.Widget;

namespace PixelTrace.Mobile;

internal sealed class DialogPresenter(Activity activity, Preferences preferences)
{
    private Activity Activity { get; } = activity;
    private Preferences Preferences { get; } = preferences;

    public void Alert(string title, string message)
    {
        if (Activity.IsDestroyed || Activity.IsFinishing)
        {
            return;
        }

        new AlertDialog.Builder(Activity).SetTitle(title)!.SetMessage(message)!.SetPositiveButton("확인", (_, _) => {
        })!.Show();
    }

    public void Error(string title, string message)
        => Alert(title, Preferences.HideNames || Preferences.HidePaths ? "파일명/경로 숨김 설정이 적용되어 오류 상세 내용을 표시하지 않습니다. 폴더 접근 권한과 모델 준비 상태를 확인해 주세요." : message);
    public void Input(string title, string current, Func<string, Task<bool>> save)
    {
        EditText input = Ui.Input(Activity, current);
        LinearLayout content = Ui.Column(Activity);
        int padding = Ui.Dp(Activity, 20);
        content.SetPadding(padding, 0, padding, 0);
        content.AddView(input, Ui.Layout(Activity));
        AlertDialog dialog = new AlertDialog.Builder(Activity).SetTitle(title)!.SetView(content)!.SetPositiveButton("저장", (_, _) => {
        })!.SetNegativeButton("취소", (_, _) => {
        })!.Create()!;
        dialog.Show();
        Ui.FitDialog(dialog);
        Button button = dialog.GetButton((int)DialogButtonType.Positive)!;
        button.Click += async (_, _) =>
        {
            string value = input.Text?.Trim() ?? "";
            if (value.Length is < 1 or > 200)
            {
                input.Error = "1~200자로 입력해 주세요.";
                return;
            }

            button.Enabled = false;
            try
            {
                if (await save(value))
                {
                    dialog.Dismiss();
                }
                else
                {
                    button.Enabled = true;
                }
            }
            catch (Exception error)
            {
                Error(title, error.Message);
                button.Enabled = true;
            }
        };
    }

    public void Confirm(string title, string message, Func<Task> action)
        => new AlertDialog.Builder(Activity).SetTitle(title)!.SetMessage(message)!.SetPositiveButton("확인", async (_, _) => {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            Error(title, error.Message);
        }
    })!.SetNegativeButton("취소", (_, _) => {
    })!.Show();
}
