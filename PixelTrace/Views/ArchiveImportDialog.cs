using Android.App;
using Android.Content;
using Android.Widget;
using PixelTrace.Exchange;

namespace PixelTrace.Mobile;

internal sealed class ArchiveImportDialog(Activity activity, AppState state)
{
    private Activity Activity { get; } = activity;
    private AppState State { get; } = state;

    public async Task ShowAsync(string uri)
    {
        string temporary = Path.Combine(Activity.CacheDir!.AbsolutePath, "library-import-" + Guid.NewGuid().ToString("N") + ".cbor");
        ArchiveManifest? manifest = null;
        bool opened = await State.RunAsync("교환 파일을 확인하는 중…", async (ct) => {
            await new DocumentLibrary(Activity.ContentResolver!).CopyAsync(uri, temporary, ct);
            manifest = await LibraryArchive.ReadManifestAsync(temporary, ct);
        });
        if (!opened || Activity.IsDestroyed)
        {
            File.Delete(temporary);
            return;
        }

        IReadOnlyList<Folder> targets = State.Catalog.Folders();
        if (targets.Count == 0)
        {
            File.Delete(temporary);
            new DialogPresenter(Activity, State.Preferences).Alert("가져오기", "먼저 이 기기에서 원본 사진이 있는 폴더를 등록해 주세요.");
            return;
        }

        LinearLayout panel = Ui.Column(Activity);
        panel.SetPadding(Ui.Dp(Activity, 20), 0, Ui.Dp(Activity, 20), Ui.Dp(Activity, 16));
        panel.AddView(Ui.Text(Activity, "내보낸 폴더마다 이 기기의 원본 폴더를 지정하세요. 같은 상대 경로의 사진에 저장된 임베딩을 연결합니다. 하위 폴더와 파일명을 그대로 유지해 주세요.", 12, Ui.Muted));
        Dictionary<string, long> mappings = new();
        foreach (ArchiveFolder folder in manifest!.Folders)
        {
            panel.AddView(Ui.Text(Activity, folder.Name, 13, bold: true), Ui.Layout(Activity, top: 16, bottom: 8));
            Button? choice = null;
            choice = Ui.Select(Activity, "가져오지 않음", () => {
                string[] labels = ["가져오지 않음", ..targets.Select((target) => target.Name)];
                new AlertDialog.Builder(Activity).SetTitle(folder.Name)!.SetItems(labels, (_, args) => {
                    if (args.Which == 0)
                    {
                        mappings.Remove(folder.Id);
                    }
                    else
                    {
                        mappings[folder.Id] = targets[args.Which - 1].Id;
                    }

                    choice!.Text = labels[args.Which];
                })!.Show();
            });
            panel.AddView(choice, Ui.Layout(Activity));
        }

        ScrollView scroll = new(Activity);
        scroll.AddView(panel);
        AlertDialog dialog = new AlertDialog.Builder(Activity).SetTitle("교환 파일 가져오기")!.SetView(scroll)!.SetPositiveButton("가져오기", (_, _) => { })!.SetNegativeButton("취소", (_, _) => { })!.Create()!;
        bool importing = false;
        dialog.DismissEvent += (_, _) => {
            if (!importing)
            {
                File.Delete(temporary);
            }
        };
        dialog.Show();
        Ui.FitDialog(dialog);
        Button save = dialog.GetButton((int)DialogButtonType.Positive)!;
        save.Click += async (_, _) => {
            if (mappings.Count == 0 || mappings.Values.Distinct().Count() != mappings.Count)
            {
                new DialogPresenter(Activity, State.Preferences).Alert("폴더 연결", "가져올 폴더마다 서로 다른 대상 폴더를 지정해 주세요.");
                return;
            }

            importing = true;
            save.Enabled = false;
            dialog.Dismiss();
            try
            {
                ArchiveImportResult? result = await State.ImportArchiveAsync(temporary, mappings);
                if (result is not null && !Activity.IsDestroyed)
                {
                    new DialogPresenter(Activity, State.Preferences).Alert("가져오기 완료", $"{result.Imported:N0}개 연결 · 원본 미발견 {result.Unmatched:N0}개");
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        };
    }
}
