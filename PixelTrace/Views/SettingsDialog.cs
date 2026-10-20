using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace PixelTrace.Mobile;

internal sealed class SettingsDialog(Activity activity, AppState state, Action saved)
{
    private Activity Activity { get; } = activity;
    private AppState State { get; } = state;
    private Action Saved { get; } = saved;
    private DialogPresenter Dialogs { get; } = new(activity, state.Preferences);
    private Button? storageAccess;
    private TextView? storageAccessStatus;

    public void Show()
    {
        LinearLayout panel = Ui.Column(Activity);
        panel.SetPadding(Ui.Dp(Activity, 20), 0, Ui.Dp(Activity, 20), 0);
        panel.AddView(Ui.Text(Activity, "작업 중에도 저장할 수 있으며, 변경한 설정은 다음 작업부터 적용됩니다.", 11, Ui.Muted), Ui.Layout(Activity, bottom: 12));
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            FieldLabel(panel, "저장소 접근");
            storageAccessStatus = Ui.Text(Activity, "", 12, Ui.Muted);
            panel.AddView(storageAccessStatus, Ui.Layout(Activity, bottom: 8));
            storageAccess = Ui.Button(Activity, "모든 파일 접근 허용", OpenStorageAccess);
            panel.AddView(storageAccess, Ui.Layout(Activity, bottom: 16));
            RefreshStorageAccess();
        }
        LinearLayout modelCard = Ui.Column(Activity);
        modelCard.SetPadding(Ui.Dp(Activity, 14), Ui.Dp(Activity, 14), Ui.Dp(Activity, 14), Ui.Dp(Activity, 14));
        modelCard.Background = Ui.Surface(Activity, Ui.Field, 12);
        modelCard.AddView(Ui.Text(Activity, "EmbeddingGemma 2", 14, bold: true));
        modelCard.AddView(Ui.Text(Activity, State.Models.Precision, 11, Ui.Muted), Ui.Layout(Activity, top: 8));
        string? configurationReason = EmbeddingModels.UnavailableReason(State.Preferences.Precision, State.Preferences.Backend);
        string cacheStatus = configurationReason is not null ? "선택한 설정으로 모델을 사용할 수 없습니다." : State.Models.Ready ? "다운로드 완료" : "다운로드 필요 · " + State.Models.DownloadSize;
        modelCard.AddView(Ui.Text(Activity, cacheStatus + "\n" + State.Library.ModelStatus, 11, Ui.Muted), Ui.Layout(Activity, top: 8));
        panel.AddView(modelCard);
        FieldLabel(panel, "추론 엔진");
        string nextEngine = State.Preferences.EngineId;
        int nextTokens = State.Preferences.SoftTokens;
        string nextCardSize = State.Preferences.CardSize;
        string nextNsfwFilter = State.Preferences.NsfwFilter;
        IReadOnlyList<EmbeddingEngineOption> engines = EmbeddingEngines.Options;
        TextView engineHint = Ui.Text(Activity, EmbeddingEngines.Find(nextEngine)?.Description ?? "사용할 엔진을 선택해 주세요.", 11, Ui.Muted);
        panel.AddView(SelectField("추론 엔진", engines.Select((option) => option.Label).ToArray(), engines.ToList().FindIndex((option) => option.Id == nextEngine), (index) => {
            nextEngine = engines[index].Id;
            engineHint.Text = engines[index].Description;
        }, (index) => EmbeddingModels.UnavailableReason(engines[index].Precision, engines[index].Backend)is null), Ui.Layout(Activity));
        panel.AddView(engineHint, Ui.Layout(Activity, top: 8));
        EditText batchSize = NumberField(panel, "요청 묶음 크기 (장)", State.Preferences.BatchSize);
        EditText preparationConcurrency = NumberField(panel, "사진 준비 동시성", State.Preferences.PreparationConcurrency);
        panel.AddView(Ui.Text(Activity, "사진은 차례대로 분석하고, 준비 작업은 병렬로 처리합니다. 작업 수를 높이면 메모리 사용량이 늘어납니다.", 11, Ui.Muted), Ui.Layout(Activity, top: 8));
        FieldLabel(panel, "표시 설정");
        panel.AddView(SelectField("이미지 카드 크기", ["작게", "보통", "크게"], nextCardSize == "small" ? 0 : nextCardSize == "large" ? 2 : 1, (index) => nextCardSize = new[] { "small", "normal", "large" }[index]), Ui.Layout(Activity, bottom: 8));
        CheckBox names = new(Activity) {
            Text = "파일명 숨기기",
            Checked = State.Preferences.HideNames
        };
        panel.AddView(names);
        CheckBox paths = new(Activity) {
            Text = "경로 숨기기",
            Checked = State.Preferences.HidePaths
        };
        panel.AddView(paths);
        FieldLabel(panel, "NSFW 필터");
        panel.AddView(SelectField("NSFW 필터", ["끄기", "숨김", "블러"], nextNsfwFilter == "hide" ? 1 : nextNsfwFilter == "blur" ? 2 : 0, (index) => nextNsfwFilter = new[] { "off", "hide", "blur" }[index]), Ui.Layout(Activity));
        EditText nsfwThreshold = NumberField(panel, "NSFW 판별 기준 (1~99%)", State.Preferences.NsfwThreshold);
        EditText nsfwConcurrency = NumberField(panel, "NSFW 동시 처리 수", State.Preferences.NsfwConcurrency);
        panel.AddView(Ui.Text(Activity, "동시에 판별하는 사진 수입니다. 값을 높이면 메모리 사용량이 늘어납니다.", 11, Ui.Muted), Ui.Layout(Activity, top: 8));
        panel.AddView(Ui.Text(Activity, "기준을 낮추면 더 많은 사진을 필터링합니다. 미판별 사진에도 숨김·블러가 적용됩니다. 처음 사용 시 판별 모델 약 22 MB를 다운로드하며 사진은 기기에서 분석합니다.", 11, Ui.Muted), Ui.Layout(Activity, top: 8));
        FieldLabel(panel, "이미지 해상도");
        panel.AddView(SelectField("이미지 해상도", ["70 토큰 · 메모리 절약", "140 토큰 · 기본"], nextTokens == 70 ? 0 : 1, (index) => nextTokens = index == 0 ? 70 : 140), Ui.Layout(Activity));
        EditText limit = NumberField(panel, "검색 결과 개수 (1~1000)", State.Preferences.ResultLimit);
        panel.AddView(Ui.Text(Activity, "검색·색인 시 모델을 자동으로 로딩합니다. 모델이 없으면 처음 한 번 다운로드합니다.", 11, Ui.Muted), Ui.Layout(Activity, top: 18, bottom: 12));
        ScrollView scroll = new(Activity);
        scroll.AddView(panel);
        AlertDialog dialog = new AlertDialog.Builder(Activity).SetTitle("설정")!.SetView(scroll)!.SetPositiveButton("저장", (_, _) => {
        })!.SetNegativeButton("취소", (_, _) => {
        })!.Create()!;
        dialog.DismissEvent += (_, _) => {
            storageAccess = null;
            storageAccessStatus = null;
        };
        dialog.Show();
        Ui.FitDialog(dialog);
        Button save = dialog.GetButton((int)DialogButtonType.Positive)!;
        save.Click += async (_, _) =>
        {
            if (!int.TryParse(batchSize.Text, out int batch) || batch < 1)
            {
                Dialogs.Alert("설정", "요청 묶음 크기는 1 이상의 정수로 입력해 주세요.");
                return;
            }

            if (!int.TryParse(preparationConcurrency.Text, out int concurrency) || concurrency < 1)
            {
                Dialogs.Alert("설정", "사진 준비 동시성은 1 이상의 정수로 입력해 주세요.");
                return;
            }

            if (!int.TryParse(limit.Text, out int count) || count is < 1 or > 1000)
            {
                Dialogs.Alert("설정", "검색 결과 개수는 1~1000으로 입력해 주세요.");
                return;
            }

            if (!int.TryParse(nsfwThreshold.Text, out int threshold) || threshold is < 1 or > 99)
            {
                Dialogs.Alert("설정", "NSFW 판별 기준은 1~99로 입력해 주세요.");
                return;
            }

            if (!int.TryParse(nsfwConcurrency.Text, out int nsfwWorkers) || nsfwWorkers < 1)
            {
                Dialogs.Alert("설정", "NSFW 동시 처리 수는 1 이상의 정수로 입력해 주세요.");
                return;
            }

            EmbeddingEngineOption? selectedEngine = EmbeddingEngines.Find(nextEngine);
            if (selectedEngine is null || EmbeddingModels.UnavailableReason(selectedEngine.Precision, selectedEngine.Backend)is not null)
            {
                Dialogs.Alert("추론 엔진", "사용 가능한 엔진을 선택해 주세요.");
                return;
            }

            save.Enabled = false;
            SettingsValues values = new(nextEngine, nextTokens, batch, concurrency, count, nextCardSize, names.Checked, paths.Checked, nextNsfwFilter, threshold, nsfwWorkers);
            bool saved = await State.SaveSettingsAsync(values);
            if (!saved)
            {
                save.Enabled = true;
                return;
            }

            Saved();
            dialog.Dismiss();
            if (!State.Operation.Busy)
            {
                if (nextNsfwFilter != "off") { await State.AnalyzeContentAsync(); }
                else { await State.BrowseAsync(); }
            }
        };
    }

    public void RefreshStorageAccess()
    {
        if (storageAccess is null || storageAccessStatus is null) { return; }
        bool allowed = MediaLibrary.HasAllFilesAccess;
        storageAccessStatus.Text = allowed ? "모든 파일 접근이 허용되어 있습니다." : "모든 파일 접근이 허용되지 않았습니다.";
        storageAccess.Text = allowed ? "접근 권한 관리" : "모든 파일 접근 허용";
    }

    private void OpenStorageAccess()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) { return; }
        try
        {
            using global::Android.Net.Uri uri = global::Android.Net.Uri.Parse("package:" + Activity.PackageName)!;
            using Intent intent = new(global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, uri);
            Activity.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Dialogs.Alert("저장소 접근", "기기 설정의 특별한 앱 접근 → 모든 파일 접근에서 PixelTrace를 허용해 주세요.");
        }
    }

    private EditText NumberField(LinearLayout panel, string title, int value)
    {
        FieldLabel(panel, title);
        EditText field = Ui.Input(Activity, value.ToString());
        field.ContentDescription = title;
        field.InputType = Android.Text.InputTypes.ClassNumber;
        field.SetSelectAllOnFocus(true);
        panel.AddView(field, Ui.Layout(Activity));
        return field;
    }

    private void FieldLabel(LinearLayout panel, string text)
        => panel.AddView(Ui.Text(Activity, text, 12, Ui.Muted), Ui.Layout(Activity, top: 18, bottom: 9));
    private Button SelectField(string title, string[] labels, int selected, Action<int> change, Func<int, bool>? enabled = null)
    {
        Button field = Ui.Select(Activity, selected < 0 ? "엔진을 선택하세요" : labels[selected], () => {
        });
        field.Gravity = GravityFlags.CenterVertical | GravityFlags.Left;
        field.Click += (_, _) =>
        {
            LinearLayout options = Ui.Column(Activity);
            options.SetPadding(Ui.Dp(Activity, 16), 0, Ui.Dp(Activity, 16), Ui.Dp(Activity, 12));
            ScrollView scroll = new(Activity);
            scroll.AddView(options);
            AlertDialog dialog = new AlertDialog.Builder(Activity).SetTitle(title)!.SetView(scroll)!.Create()!;
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                Button option = Ui.Button(Activity, (selected == i ? "✓  " : "    ") + labels[i], () => {
                    selected = index;
                    field.Text = labels[index];
                    change(index);
                    dialog.Dismiss();
                });
                option.Gravity = GravityFlags.CenterVertical | GravityFlags.Left;
                option.SetSingleLine(false);
                option.Ellipsize = null;
                option.Enabled = enabled is null || enabled(i);
                option.Alpha = option.Enabled ? 1 : .4f;
                options.AddView(option, Ui.Layout(Activity, bottom: 6));
            }

            dialog.Show();
            Ui.FitDialog(dialog);
        };
        return field;
    }
}
