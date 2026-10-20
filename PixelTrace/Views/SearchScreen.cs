using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Uri = Android.Net.Uri;

namespace PixelTrace.Mobile;

internal sealed record SearchScreenActions(Action OpenLibrary, Action ShowMenu, Action Search, Action PickPhoto, Action PickFolder, Action ChooseSearchMode, Action<ImageItem> ShowImage);
internal sealed class SearchScreen
{
    private Activity Activity { get; }
    private AppState State { get; }
    private SearchScreenActions Actions { get; }
    private CancellationTokenSource Lifetime { get; } = new();
    public FrameLayout Root { get; private set; } = null!;
    public View MenuAnchor
        => menu;
    public View ModeAnchor
        => mode;
    public EditText QueryField
        => query;
    public string Query
        => query.Text ?? "";

    private LinearLayout progressPanel = null!, referencePanel = null!;
    private ImageView referencePicture = null!;
    private TextView referenceName = null!, status = null!, heading = null!, empty = null!, scopeTitle = null!, resultCount = null!;
    private Button referenceClose = null!;
    private SearchReference? displayedReference;
    private ProgressBar progress = null!;
    private EditText query = null!;
    private FrameLayout galleryArea = null!;
    private ScrollView emptyScroll = null!;
    private LinearLayout emptyPanel = null!;
    private GridView grid = null!;
    private ImageGridAdapter adapter = null!;
    private Button search = null!, compare = null!, more = null!, cancel = null!, menu = null!, libraryButton = null!, mode = null!, emptyAdd = null!;
    private IReadOnlyList<ImageItem>? displayed;
    private bool keyboardVisible;
    public SearchScreen(Activity activity, AppState state, SearchScreenActions actions)
    {
        Activity = activity;
        State = state;
        Actions = actions;
        Build();
    }

    public void RefreshCards()
        => adapter.NotifyDataSetChanged();
    public void StopLoading()
    {
        Lifetime.Cancel();
        adapter.StopLoading();
    }

    private void Build()
    {
        Root = new(Activity) {
            FocusableInTouchMode = true
        };
        FrameLayout shell = Root;
        shell.SetBackgroundColor(Ui.Bg);
        LinearLayout root = Ui.Column(Activity);
        root.SetPadding(Ui.Dp(Activity, 16), Ui.Dp(Activity, 8), Ui.Dp(Activity, 16), Ui.Dp(Activity, 8));
        shell.AddView(root, new FrameLayout.LayoutParams(-1, -1));
        LinearLayout header = Ui.Row(Activity);
        LinearLayout titleBlock = Ui.Column(Activity);
        libraryButton = Ui.Icon(Activity, Glyph.Library, "라이브러리 열기", Actions.OpenLibrary);
        header.AddView(libraryButton, Ui.Layout(Activity, 48, 48, right: 8));
        TextView eyebrow = Ui.Text(Activity, AppIdentity.Name, 10, Ui.Accent);
        eyebrow.LetterSpacing = .15f;
        titleBlock.AddView(eyebrow);
        scopeTitle = Ui.Text(Activity, "전체 폴더", 20, bold: true);
        scopeTitle.SetMaxLines(1);
        scopeTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        titleBlock.AddView(scopeTitle, Ui.Layout(Activity, top: 3));
        header.AddView(titleBlock, new LinearLayout.LayoutParams(0, -2, 1));
        menu = Ui.Icon(Activity, Glyph.More, "라이브러리 메뉴", Actions.ShowMenu, true);
        header.AddView(menu, Ui.Layout(Activity, 48, 48, left: 8));
        root.AddView(header, Ui.Layout(Activity, bottom: 12));
        LinearLayout queryField = Ui.Row(Activity);
        queryField.Background = Ui.Surface(Activity, Ui.Field, 12);
        queryField.SetPadding(Ui.Dp(Activity, 16), 0, Ui.Dp(Activity, 12), 0);
        queryField.AddView(Ui.Symbol(Activity, Glyph.Search, Ui.Muted), Ui.Layout(Activity, 20, 20));
        query = Ui.Input(Activity, State.Preferences.Query, "어떤 이미지를 찾고 있나요?");
        query.ImeOptions = ImeAction.Search;
        query.InputType = Android.Text.InputTypes.ClassText;
        query.TextSize = 16;
        query.Background = null;
        query.SetTextColor(Ui.TextColor);
        query.SetHintTextColor(Ui.Muted);
        query.SetPadding(Ui.Dp(Activity, 12), Ui.Dp(Activity, 14), 0, Ui.Dp(Activity, 14));
        queryField.AddView(query, new LinearLayout.LayoutParams(0, -2, 1));
        search = Ui.Button(Activity, "검색", Actions.Search, "primary");
        root.AddView(queryField, Ui.Layout(Activity));
        query.EditorAction += (_, args) =>
        {
            if (args.ActionId == ImeAction.Search)
            {
                args.Handled = true;
                Actions.Search();
            }
        };
        compare = Ui.Button(Activity, "이미지로 비교", Actions.PickPhoto);
        mode = Ui.Select(Activity, State.Preferences.SearchMode == "filename" ? "파일명" : "이미지 내용", Actions.ChooseSearchMode);
        SearchActionsView actions = new(Activity, compare, mode, search);
        root.AddView(actions, Ui.Layout(Activity, top: 8));
        referencePanel = Ui.Row(Activity);
        referencePanel.Background = Ui.Surface(Activity, Ui.Panel, 12);
        referencePanel.SetPadding(Ui.Dp(Activity, 10), Ui.Dp(Activity, 10), Ui.Dp(Activity, 6), Ui.Dp(Activity, 10));
        referencePicture = new(Activity);
        referencePicture.SetScaleType(ImageView.ScaleType.CenterCrop);
        referencePicture.Background = Ui.Surface(Activity, Ui.Field, 8, false);
        referencePicture.ClipToOutline = true;
        referencePanel.AddView(referencePicture, Ui.Layout(Activity, 50, 50, right: 10));
        LinearLayout referenceText = Ui.Column(Activity);
        referenceText.AddView(Ui.Text(Activity, "비교 기준 이미지", 10, Ui.Accent));
        referenceName = Ui.Text(Activity, "선택한 이미지", 12, bold: true);
        referenceName.SetSingleLine(true);
        referenceName.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        referenceText.AddView(referenceName, Ui.Layout(Activity, top: 3, bottom: 3));
        referenceText.AddView(Ui.Text(Activity, "선택한 폴더에서 비슷한 이미지를 찾습니다.", 10, Ui.Muted));
        referencePanel.AddView(referenceText, new LinearLayout.LayoutParams(0, -2, 1));
        referenceClose = Ui.Icon(Activity, Glyph.Close, "비교 기준 이미지 지우기", async () => await State.BrowseAsync());
        referencePanel.AddView(referenceClose, Ui.Layout(Activity, 48, 48));
        root.AddView(referencePanel, Ui.Layout(Activity, top: 14));
        progressPanel = Ui.Column(Activity);
        progressPanel.SetPadding(Ui.Dp(Activity, 14), Ui.Dp(Activity, 10), Ui.Dp(Activity, 14), Ui.Dp(Activity, 12));
        progressPanel.Background = Ui.Surface(Activity, Ui.Panel, 12);
        LinearLayout progressTop = Ui.Row(Activity);
        status = Ui.Text(Activity, "", 12, Ui.Muted);
        status.SetMaxLines(2);
        status.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        progressTop.AddView(status, new LinearLayout.LayoutParams(0, -2, 1));
        cancel = Ui.Button(Activity, "취소", State.Cancel, "primary");
        cancel.Background = Ui.Surface(Activity, Color.ParseColor("#643449"), 10);
        cancel.SetTextColor(Color.ParseColor("#ffe0e7"));
        cancel.TextSize = 14;
        progressTop.AddView(cancel, Ui.Layout(Activity, 88, 48, left: 8));
        progressPanel.AddView(progressTop);
        progress = new ProgressBar(Activity, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(Ui.Accent)
        };
        progressPanel.AddView(progress, Ui.Layout(Activity, height: 5, top: 6));
        root.AddView(progressPanel, Ui.Layout(Activity, top: 12));
        LinearLayout resultsHeader = Ui.Row(Activity);
        heading = Ui.Text(Activity, "이미지 목록", 16, bold: true);
        heading.SetSingleLine(true);
        heading.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        resultsHeader.AddView(heading, new LinearLayout.LayoutParams(0, -2, 1));
        resultCount = Ui.Text(Activity, "0개", 12, Ui.Muted);
        resultsHeader.AddView(resultCount, Ui.Layout(Activity, left: 8, right: 4));
        resultsHeader.AddView(Ui.Button(Activity, "전체 목록", async () => {
            if (!State.Operation.Busy)
            {
                await State.BrowseAsync();
            }
        }, "quiet"), Ui.Layout(Activity, 82));
        root.AddView(resultsHeader, Ui.Layout(Activity, top: 8, bottom: 8));
        galleryArea = new FrameLayout(Activity);
        root.AddView(galleryArea, new LinearLayout.LayoutParams(-1, 0, 1));
        grid = new GridView(Activity) {
            NumColumns = -1,
            StretchMode = StretchMode.StretchColumnWidth
        };
        grid.SetColumnWidth(Ui.Dp(Activity, 145));
        grid.SetHorizontalSpacing(Ui.Dp(Activity, 11));
        grid.SetVerticalSpacing(Ui.Dp(Activity, 11));
        grid.Selector = new ColorDrawable(Color.Transparent);
        adapter = new(Activity, State.Preferences, State.Thumbnails);
        grid.Adapter = adapter;
        grid.ItemClick += (_, args) => Actions.ShowImage(adapter[args.Position]);
        galleryArea.AddView(grid, new FrameLayout.LayoutParams(-1, -1));
        emptyPanel = Ui.Column(Activity);
        emptyPanel.SetGravity(GravityFlags.Center);
        emptyPanel.SetPadding(Ui.Dp(Activity, 15), Ui.Dp(Activity, 36), Ui.Dp(Activity, 15), Ui.Dp(Activity, 36));
        GradientDrawable emptyBackground = Ui.Surface(Activity, Ui.Bg, 18);
        emptyBackground.SetStroke(Ui.Dp(Activity, 1), Ui.Border, Ui.Dp(Activity, 5), Ui.Dp(Activity, 5));
        emptyPanel.Background = emptyBackground;
        emptyPanel.AddView(Ui.Symbol(Activity, Glyph.Image), Ui.Layout(Activity, 44, 44, bottom: 14));
        TextView emptyTitle = Ui.Text(Activity, "이미지를 모아, 더 쉽게 찾으세요.", 17, bold: true);
        emptyTitle.Gravity = GravityFlags.Center;
        emptyPanel.AddView(emptyTitle);
        empty = Ui.Text(Activity, "", 12, Ui.Muted);
        empty.Gravity = GravityFlags.Center;
        empty.SetLineSpacing(Ui.Dp(Activity, 4), 1);
        emptyPanel.AddView(empty, Ui.Layout(Activity, top: 9, bottom: 20));
        emptyAdd = Ui.Button(Activity, "폴더 추가", Actions.PickFolder, "primary");
        emptyPanel.AddView(emptyAdd, Ui.Layout(Activity, 124));
        emptyScroll = new ScrollView(Activity);
        emptyScroll.AddView(emptyPanel);
        galleryArea.AddView(emptyScroll, new FrameLayout.LayoutParams(-1, -1));
        more = Ui.Button(Activity, "이미지 더 보기", async () => await State.BrowseAsync(true));
        root.AddView(more, Ui.Layout(Activity, top: 8));
        shell.SetOnApplyWindowInsetsListener(new InsetsListener((visible) => {
            keyboardVisible = visible;
            header.Visibility = actions.Visibility = visible ? ViewStates.Gone : ViewStates.Visible;
            referencePanel.Visibility = !visible && State.Reference is not null ? ViewStates.Visible : ViewStates.Gone;
        }));
        shell.RequestFocus();
    }

    private sealed class InsetsListener(Action<bool> keyboardChanged) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private Action<bool> KeyboardChanged { get; } = keyboardChanged;

        public WindowInsets OnApplyWindowInsets(View? view, WindowInsets? insets)
        {
            Insets bars = OperatingSystem.IsAndroidVersionAtLeast(30) ? insets!.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout())! : insets!.SystemWindowInsets!;
            int bottom = bars.Bottom;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                bottom = Math.Max(bottom, insets!.GetInsets(WindowInsets.Type.Ime())!.Bottom);
                KeyboardChanged(insets.IsVisible(WindowInsets.Type.Ime()));
            }

            view!.SetPadding(bars.Left, bars.Top, bars.Right, bottom);
            return insets;
        }
    }

    public void Refresh()
    {
        OperationState op = State.Operation;
        progressPanel.Visibility = op.Busy ? ViewStates.Visible : ViewStates.Gone;
        status.Text = op.Text;
        progress.Indeterminate = op.Total <= 0;
        if (0 < op.Total)
        {
            progress.Max = op.Total;
            progress.Progress = op.Done;
        }

        search.Enabled = compare.Enabled = libraryButton.Enabled = mode.Enabled = emptyAdd.Enabled = query.Enabled = more.Enabled = !op.Busy;
        menu.Enabled = true;
        referenceClose.Enabled = !op.Busy;
        cancel.Enabled = op.CanCancel;
        referencePanel.Visibility = State.Reference is null || keyboardVisible ? ViewStates.Gone : ViewStates.Visible;
        referenceName.Text = State.Preferences.HideNames ? "선택한 이미지" : State.Reference?.Name;
        if (displayedReference != State.Reference)
        {
            displayedReference = State.Reference;
            referencePicture.SetImageDrawable(null);
            if (State.Reference is SearchReference reference)
            {
                _ = LoadReferenceAsync(reference);
            }
        }

        grid.SetColumnWidth(Ui.Dp(Activity, State.Preferences.CardSize switch { "small" => 115, "large" => 225, _ => 145 }));
        heading.Text = State.ResultTitle;
        resultCount.Text = $"{State.Results.Count:N0}개";
        if (!ReferenceEquals(displayed, State.Results))
        {
            displayed = State.Results;
            adapter.SetRows(displayed);
        }

        emptyScroll.Visibility = State.Results.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        mode.Text = State.Preferences.SearchMode == "filename" ? "파일명" : "이미지 내용";
        query.Hint = State.Preferences.SearchMode == "filename" ? "파일명으로 찾기" : "어떤 이미지를 찾고 있나요?";
        grid.Visibility = State.Results.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
        empty.Text = State.Preferences.NsfwFilter == "hide" && State.Snapshot.Folders.Any((folder) => 0 < folder.Count)
            ? "표시할 이미지가 없습니다. 검색 범위와 NSFW 필터 설정을 확인해 주세요."
            : State.ResultTitle == "이미지 목록" ? State.Snapshot.Folders.Count == 0 ? "폴더 추가에서 기기의 사진 폴더를 선택해 주세요.\n색인을 갱신하면 검색할 수 있습니다." : "폴더가 등록되어 있습니다.\n⋯ → 색인 갱신을 실행하면 이미지가 표시됩니다." : "검색 결과가 없습니다. 검색어나 검색 범위를 바꿔 보세요.";
        more.Visibility = State.More ? ViewStates.Visible : ViewStates.Gone;
        if (!op.Busy)
        {
            RefreshScopes();
        }
    }

    private void RefreshScopes()
    {
        IReadOnlyList<Folder> folders = State.Snapshot.Folders;
        IReadOnlyList<FolderGroup> groups = State.Snapshot.Groups;
        string selected = State.Preferences.Scope;
        scopeTitle.Text = selected.StartsWith("folder:") ? folders.FirstOrDefault((f) => "folder:" + f.Id == selected)?.Name ?? "전체 폴더" : selected.StartsWith("group:") ? groups.FirstOrDefault((g) => "group:" + g.Id == selected)?.Name ?? "전체 폴더" : "전체 폴더";
    }

    private async Task LoadReferenceAsync(SearchReference reference)
    {
        try
        {
            using Bitmap? bitmap = await Task.Run(() => {
                if (reference.Thumbnail is not null && File.Exists(reference.Thumbnail))
                {
                    return BitmapFactory.DecodeFile(reference.Thumbnail);
                }

                using BitmapFactory.Options options = new() {
                    InJustDecodeBounds = true
                };
                using (Stream bounds = Activity.ContentResolver!.OpenInputStream(Uri.Parse(reference.Uri)!)!)
                {
                    BitmapFactory.DecodeStream(bounds, null, options);
                }

                int sample = 1;
                while (160 < Math.Max(options.OutWidth, options.OutHeight) / sample)
                {
                    sample *= 2;
                }

                options.InJustDecodeBounds = false;
                options.InSampleSize = sample;
                using Stream stream = Activity.ContentResolver!.OpenInputStream(Uri.Parse(reference.Uri)!)!;
                return BitmapFactory.DecodeStream(stream, null, options);
            }, Lifetime.Token);
            if (!Lifetime.IsCancellationRequested && !Activity.IsDestroyed && State.Reference == reference && bitmap is not null)
            {
                if (State.Preferences.MasksImage(reference.NsfwScore))
                {
                    using Bitmap blurred = await Task.Run(() => ImagePrivacy.Blur(bitmap), Lifetime.Token);
                    if (!Lifetime.IsCancellationRequested && !Activity.IsDestroyed && State.Reference == reference)
                    {
                        referencePicture.SetImageBitmap(blurred);
                    }
                }
                else
                {
                    referencePicture.SetImageBitmap(bitmap);
                }
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (Exception error)when (error is IOException or Java.Lang.SecurityException or Java.IO.IOException)
        {
            global::Android.Util.Log.Warn(AppIdentity.LogTag, "비교 이미지 미리 보기: " + error.Message);
        }
    }
}
