using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace PixelTrace.Mobile;

internal sealed class LibraryDrawer(Activity activity, AppState state, Action pickFolder, Action settings)
{
    private Activity Activity { get; } = activity;
    private AppState State { get; } = state;
    private Action PickFolder { get; } = pickFolder;
    private Action Settings { get; } = settings;
    private DialogPresenter Dialogs { get; } = new(activity, state.Preferences);

    private FrameLayout libraryOverlay = null!;
    private LinearLayout libraryList = null!;
    public bool IsOpen
        => libraryOverlay.Visibility == ViewStates.Visible;

    public void Refresh()
    {
        if (libraryOverlay.Visibility == ViewStates.Visible && !State.Operation.Busy)
        {
            Open();
        }
    }

    public void Attach(FrameLayout shell)
    {
        libraryOverlay = new FrameLayout(Activity) {
            Visibility = ViewStates.Gone
        };
        View shade = new(Activity);
        shade.SetBackgroundColor(Color.ParseColor("#99000000"));
        shade.Click += (_, _) => Close();
        libraryOverlay.AddView(shade, new FrameLayout.LayoutParams(-1, -1));
        LinearLayout drawer = Ui.Column(Activity);
        drawer.SetPadding(Ui.Dp(Activity, 16), Ui.Dp(Activity, 24), Ui.Dp(Activity, 16), Ui.Dp(Activity, 18));
        drawer.SetBackgroundColor(Ui.Panel);
        drawer.Clickable = true;
        LinearLayout brand = Ui.Row(Activity);
        TextView mark = Ui.Text(Activity, AppIdentity.Monogram, 14, Ui.TextColor, true);
        mark.Gravity = GravityFlags.Center;
        GradientDrawable markBackground = new(GradientDrawable.Orientation.TlBr, [Color.ParseColor("#beaaff").ToArgb(), Ui.Primary.ToArgb()]);
        markBackground.SetCornerRadius(Ui.Dp(Activity, 11));
        mark.Background = markBackground;
        mark.SetTextColor(Color.ParseColor("#181220"));
        brand.AddView(mark, Ui.Layout(Activity, 36, 36, right: 10));
        LinearLayout brandText = Ui.Column(Activity);
        brandText.AddView(Ui.Text(Activity, AppIdentity.Name, 16, bold: true));
        brandText.AddView(Ui.Text(Activity, "내 기기의 이미지 검색", 10, Ui.Muted), Ui.Layout(Activity, top: 3));
        brand.AddView(brandText, new LinearLayout.LayoutParams(0, -2, 1));
        brand.AddView(Ui.Icon(Activity, Glyph.Close, "라이브러리 닫기", Close), Ui.Layout(Activity, 48, 48));
        drawer.AddView(brand, Ui.Layout(Activity, bottom: 16));
        LinearLayout labelRow = Ui.Row(Activity);
        labelRow.AddView(Ui.Text(Activity, "라이브러리", 12, Ui.Muted), new LinearLayout.LayoutParams(0, -2, 1));
        labelRow.AddView(Ui.Icon(Activity, Glyph.Plus, "새 그룹", () => Dialogs.Input("새 그룹", "", (value) => State.EditLibraryAsync("라이브러리 변경 중…", (catalog) => catalog.AddGroup(value)))), Ui.Layout(Activity, 48, 48));
        drawer.AddView(labelRow, Ui.Layout(Activity, bottom: 8));
        ScrollView scroll = new(Activity) {
            FillViewport = false
        };
        libraryList = Ui.Column(Activity);
        scroll.AddView(libraryList);
        drawer.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        drawer.AddView(Ui.Button(Activity, "＋ 폴더 추가", () => {
            Close();
            PickFolder();
        }), Ui.Layout(Activity, top: 14, bottom: 18));
        View divider = new(Activity);
        divider.SetBackgroundColor(Ui.Border);
        drawer.AddView(divider, Ui.Layout(Activity, height: 1, bottom: 16));
        drawer.AddView(Ui.Text(Activity, "●  내 기기의 라이브러리", 11, Ui.Muted), Ui.Layout(Activity, bottom: 12));
        LinearLayout tools = Ui.Row(Activity);
        tools.AddView(Ui.Button(Activity, "폴더 관리", () => {
            Close();
            ManageFolders();
        }), new LinearLayout.LayoutParams(0, -2, 1));
        tools.AddView(Ui.Button(Activity, "설정", () => {
            Close();
            Settings();
        }, "quiet"), Ui.Layout(Activity, 66, left: 8));
        drawer.AddView(tools);
        int width = Math.Min(Ui.Dp(Activity, 300), (int)(Activity.Resources!.DisplayMetrics!.WidthPixels * .86));
        libraryOverlay.AddView(drawer, new FrameLayout.LayoutParams(width, -1));
        libraryOverlay.LayoutChange += (_, _) =>
        {
            int nextWidth = Math.Min(Ui.Dp(Activity, 360), (int)(libraryOverlay.Width * .9));
            if (0 < nextWidth && drawer.LayoutParameters!.Width != nextWidth)
            {
                drawer.LayoutParameters = new FrameLayout.LayoutParams(nextWidth, -1);
            }
        };
        shell.AddView(libraryOverlay, new FrameLayout.LayoutParams(-1, -1));
    }

    public void Open()
    {
        if (State.Operation.Busy)
        {
            return;
        }

        libraryList.RemoveAllViews();
        IReadOnlyList<Folder> folders = State.Snapshot.Folders;
        AddLibraryRow("전체 폴더", "all", folders.Sum((f) => f.Count), Glyph.Grid);
        foreach (Folder? folder in folders.Where((f) => f.GroupId is null))
        {
            AddLibraryRow(folder.Name, "folder:" + folder.Id, folder.Count, Glyph.Folder, manage: () => ManageFolder(folder));
        }

        foreach (FolderGroup group in State.Snapshot.Groups)
        {
            libraryList.AddView(new View(Activity), Ui.Layout(Activity, height: 12));
            AddLibraryRow(group.Name, "group:" + group.Id, folders.Where((f) => f.GroupId == group.Id).Sum((f) => f.Count), Glyph.Folder, manage: () => ManageGroup(group));
            foreach (Folder? folder in folders.Where((f) => f.GroupId == group.Id))
            {
                AddLibraryRow(folder.Name, "folder:" + folder.Id, folder.Count, Glyph.Folder, true, () => ManageFolder(folder));
            }
        }

        libraryOverlay.Visibility = ViewStates.Visible;
    }

    private void AddLibraryRow(string name, string key, int count, Glyph glyph, bool indent = false, Action? manage = null)
    {
        LinearLayout row = Ui.Row(Activity);
        bool selected = State.Preferences.Scope == key;
        row.Background = Ui.Surface(Activity, selected ? Color.ParseColor("#18b3a0ff") : Color.Transparent, 8, false);
        row.SetPadding(Ui.Dp(Activity, indent ? 23 : 10), 0, Ui.Dp(Activity, 4), 0);
        row.AddView(Ui.Symbol(Activity, glyph, Ui.Accent), Ui.Layout(Activity, 18, 18, right: 10));
        TextView label = Ui.Text(Activity, name, key.StartsWith("group:") ? 12 : 13, selected ? Color.ParseColor("#d5c7ff") : Ui.TextColor);
        label.SetMaxLines(1);
        label.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        row.AddView(label, new LinearLayout.LayoutParams(0, -2, 1));
        row.AddView(Ui.Text(Activity, count.ToString("N0"), 11, Ui.Muted));
        if (manage is not null)
        {
            row.AddView(Ui.Icon(Activity, Glyph.More, name + " 메뉴", () => {
                Close();
                manage();
            }), Ui.Layout(Activity, 48, 48));
        }

        row.Click += async (_, _) =>
        {
            if (State.Operation.Busy)
            {
                return;
            }

            State.Preferences.Scope = key;
            Close();
            await State.BrowseAsync();
        };
        row.SetMinimumHeight(Ui.Dp(Activity, 52));
        libraryList.AddView(row, Ui.Layout(Activity, bottom: 4));
    }

    public void Close()
        => libraryOverlay.Visibility = ViewStates.Gone;
    public void ManageFolders()
    {
        IReadOnlyList<Folder> folders = State.Snapshot.Folders;
        if (folders.Count == 0)
        {
            PickFolder();
            return;
        }

        new AlertDialog.Builder(Activity).SetTitle("폴더 관리")!.SetItems(folders.Select((f) => f.Name).ToArray(), (_, args) => ManageFolder(folders[args.Which]))!.Show();
    }

    private void ManageFolder(Folder folder)
    {
        new AlertDialog.Builder(Activity).SetTitle(folder.Name)!.SetItems(["표시 이름 변경", "그룹 변경", "등록 해제"], (_, choice) => {
            if (choice.Which == 0)
            {
                Dialogs.Input("표시 이름", folder.Name, (value) => State.EditLibraryAsync("라이브러리 변경 중…", (catalog) => catalog.RenameFolder(folder.Id, value)));
            }
            else if (choice.Which == 1)
            {
                IReadOnlyList<FolderGroup> groups = State.Snapshot.Groups;
                new AlertDialog.Builder(Activity).SetTitle("그룹 선택")!.SetItems(new[] { "그룹 없음" }.Concat(groups.Select((g) => g.Name)).ToArray(), async (_, group) => {
                    await State.EditLibraryAsync("라이브러리 변경 중…", (catalog) => catalog.GroupFolder(folder.Id, group.Which == 0 ? null : groups[group.Which - 1].Id));
                })!.Show();
            }
            else
            {
                Dialogs.Confirm("폴더 등록 해제", "기기의 사진은 삭제하지 않습니다. 이 폴더의 색인만 제거합니다.", async () => {
                    await State.EditLibraryAsync("라이브러리 변경 중…", (catalog) => catalog.RemoveFolder(folder.Id));
                });
            }
        })!.Show();
    }

    public void ManageGroups()
    {
        IReadOnlyList<FolderGroup> groups = State.Snapshot.Groups;
        new AlertDialog.Builder(Activity).SetTitle("그룹 관리")!.SetItems(new[] { "+ 새 그룹" }.Concat(groups.Select((g) => g.Name)).ToArray(), (_, args) => {
            if (args.Which == 0)
            {
                Dialogs.Input("새 그룹", "", (value) => State.EditLibraryAsync("라이브러리 변경 중…", (catalog) => catalog.AddGroup(value)));
            }
            else
            {
                ManageGroup(groups[args.Which - 1]);
            }
        })!.Show();
    }

    private void ManageGroup(FolderGroup group)
    {
        new AlertDialog.Builder(Activity).SetTitle(group.Name)!.SetItems(["이름 변경", "그룹 삭제"], (_, choice) => {
            if (choice.Which == 0)
            {
                Dialogs.Input("그룹 이름", group.Name, (value) => State.EditLibraryAsync("라이브러리 변경 중…", (catalog) => catalog.RenameGroup(group.Id, value)));
            }
            else
            {
                Dialogs.Confirm("그룹 삭제", "폴더와 사진은 유지됩니다.", async () => {
                    await State.EditLibraryAsync("라이브러리 변경 중…", (catalog) => catalog.RemoveGroup(group.Id));
                });
            }
        })!.Show();
    }
}
