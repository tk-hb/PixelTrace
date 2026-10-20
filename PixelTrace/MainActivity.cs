using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Uri = Android.Net.Uri;

namespace PixelTrace.Mobile;

[Activity(Name = AppIdentity.ActivityClass, Label = AppIdentity.Name, MainLauncher = true, Exported = true, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private const int FolderPicker = 11, PhotoPicker = 12, ArchiveExportPicker = 15, ArchiveImportPicker = 16;
    private AppState state = null!;
    private DialogPresenter dialogs = null!;
    private SettingsDialog settings = null!;
    private LibraryDrawer library = null!;
    private ImageDetailsDialog details = null!;
    private SearchScreen screen = null!;
    private string? displayedError;
    private BackHandler? backHandler;
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            Window!.SetDecorFitsSystemWindows(false);
        }

        state = App.State;
        dialogs = new(this, state.Preferences);
        settings = new(this, state, () => {
            screen.RefreshCards();
            Refresh();
        });
        library = new(this, state, PickFolder, settings.Show);
        details = new(this, state);
        screen = new(this, state, new(library.Open, ShowMenu, Search, PickPhoto, PickFolder, ChooseSearchMode, details.Show));
        library.Attach(screen.Root);
        SetContentView(screen.Root);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            backHandler = new(() => {
                if (library.IsOpen)
                {
                    library.Close();
                }
                else
                {
                    MoveTaskToBack(true);
                }
            });
            OnBackInvokedDispatcher!.RegisterOnBackInvokedCallback(0, backHandler);
        }

        state.Changed += OnStateChanged;
        Refresh();
        if (savedInstanceState is null && OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 21);
        }
        if (!state.Operation.Busy && state.Results.Count == 0 && state.ResultTitle == "이미지 목록")
        {
            await state.BrowseAsync();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        settings.RefreshStorageAccess();
    }

    protected override void OnPause()
    {
        state.Preferences.Query = screen.Query;
        base.OnPause();
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && library.IsOpen)
        {
            library.Close();
            return true;
        }

        return base.OnKeyUp(keyCode, e);
    }

    protected override void OnDestroy()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && backHandler is not null)
        {
            OnBackInvokedDispatcher!.UnregisterOnBackInvokedCallback(backHandler);
            backHandler.Dispose();
            backHandler = null;
        }

        state.Changed -= OnStateChanged;
        screen.StopLoading();
        base.OnDestroy();
    }

    private sealed class BackHandler(Action invoked) : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked()
            => invoked();
    }

    private void OnStateChanged()
        => RunOnUiThread(() => {
        if (!IsDestroyed)
        {
            Refresh();
        }
    });
    private void Refresh()
    {
        screen.Refresh();
        library.Refresh();
        OperationState operation = state.Operation;
        if (operation.Error is not null && displayedError != operation.Error)
        {
            displayedError = operation.Error;
            dialogs.Error("작업을 완료하지 못했습니다", operation.Error);
        }

        if (operation.Busy)
        {
            displayedError = null;
        }
    }

    private async void Search()
    {
        if (state.Operation.Busy)
        {
            return;
        }

        string value = screen.Query.Trim();
        state.Preferences.Query = value;
        ((InputMethodManager)GetSystemService(InputMethodService)!).HideSoftInputFromWindow(screen.QueryField.WindowToken, HideSoftInputFlags.None);
        if (value.Length == 0)
        {
            await state.BrowseAsync();
            return;
        }

        string title = state.Preferences.SearchMode == "filename" ? "파일명 검색" : "문장 검색";
        await state.RunAsync(title + " 중…", async (ct) => state.ShowResults(await state.Library.SearchAsync(value, state.Preferences.Scope, state.Report, ct), title));
    }

    private void ShowMenu()
    {
        PopupMenu popup = new(this, screen.MenuAnchor);
        string[] labels = ["폴더 추가", "폴더 관리", "그룹 관리", "색인 갱신", "설정", "파일 오류", "모델 메모리 해제", "라이브러리 내보내기", "라이브러리 가져오기"];
        for (int i = 0; i < labels.Length; i++)
        {
            popup.Menu!.Add(0, i, i, labels[i]);
        }

        popup.MenuItemClick += async (_, args) =>
        {
            if (state.Operation.Busy && args.Item!.ItemId != 4)
            {
                return;
            }

            switch (args.Item!.ItemId)
            {
                case 0:
                    PickFolder();
                    break;
                case 1:
                    library.ManageFolders();
                    break;
                case 2:
                    library.ManageGroups();
                    break;
                case 3:
                    StartIndexing();
                    break;
                case 4:
                    settings.Show();
                    break;
                case 5:
                    string[] errors = [];
                    bool loaded = await state.RunAsync("파일 오류를 불러오는 중…", (_) => {
                        errors = state.Catalog.Errors();
                        return Task.CompletedTask;
                    });
                    if (loaded)
                    {
                        dialogs.Alert("파일 오류", errors.Length == 0 ? "기록된 파일 오류가 없습니다." : state.Preferences.HideNames || state.Preferences.HidePaths ? $"파일 처리 오류 {errors.Length}건 · 파일명/경로 숨김 설정이 적용되어 상세 내용을 표시하지 않습니다." : string.Join("\n\n", errors));
                    }

                    break;
                case 6:
                    await state.RunAsync("모델 메모리를 해제하는 중…", (_) => {
                        state.Library.Unload();
                        return Task.CompletedTask;
                    });
                    break;
                case 7:
                    StartActivityForResult(new Intent(Intent.ActionCreateDocument).SetType("application/cbor").AddCategory(Intent.CategoryOpenable)!.PutExtra(Intent.ExtraTitle, $"PixelTrace-{DateTime.Now:yyyyMMdd-HHmmss}.cbor"), ArchiveExportPicker);
                    break;
                case 8:
                    StartActivityForResult(new Intent(Intent.ActionOpenDocument).SetType("*/*").AddCategory(Intent.CategoryOpenable)!.AddFlags(ActivityFlags.GrantReadUriPermission), ArchiveImportPicker);
                    break;
            }
        };
        popup.Show();
    }

    private void ChooseSearchMode()
    {
        PopupMenu popup = new(this, screen.ModeAnchor);
        IMenu choices = popup.Menu!;
        choices.Add(0, 0, 0, "이미지 내용");
        choices.Add(0, 1, 1, "파일명");
        popup.MenuItemClick += (_, args) =>
        {
            if (state.Operation.Busy)
            {
                return;
            }

            state.Preferences.SearchMode = args.Item!.ItemId == 1 ? "filename" : "content";
            screen.Refresh();
        };
        popup.Show();
    }

    private void StartIndexing(long[]? folderIds = null)
    {
        if (state.Operation.Busy)
        {
            return;
        }

        _ = state.RunAsync("모델 준비 중…", (ct) => state.Library.IndexAsync(state.Report, ct, folderIds), collectFiles: true, refreshResults: true);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 22)
        {
            if (MediaLibrary.HasAccess(this))
            {
                screen.RefreshCards();
                ShowPhotoFolders();
            }
            else
            {
                new AlertDialog.Builder(this).SetTitle("사진 접근 권한")!.SetMessage("사진이 있는 폴더 목록을 보려면 ‘전체 사진 허용’이 필요합니다. 앱 권한에서 변경하거나 폴더를 직접 선택해 주세요.")!.SetPositiveButton("앱 권한 열기", (_, _) => StartActivity(new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings, Uri.Parse("package:" + PackageName))))!.SetNeutralButton("폴더 직접 선택", (_, _) => PickSystemFolder())!.SetNegativeButton("닫기", (_, _) => {
                })!.Show();
            }

            return;
        }
    }

    private void PickFolder()
        => new AlertDialog.Builder(this).SetTitle("폴더 추가")!.SetItems(["사진이 있는 폴더 목록", "폴더 직접 선택"], (_, args) => {
        if (args.Which == 1)
        {
            PickSystemFolder();
        }
        else if (MediaLibrary.HasAccess(this))
        {
            ShowPhotoFolders();
        }
        else if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            RequestPermissions([MediaLibrary.ReadPermission, Manifest.Permission.ReadMediaVisualUserSelected], 22);
        }
        else
        {
            RequestPermissions([MediaLibrary.ReadPermission], 22);
        }
    })!.SetNegativeButton("취소", (_, _) => {
    })!.Show();
    private void PickSystemFolder()
    {
        try
        {
            StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree).AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission), FolderPicker);
        }
        catch (ActivityNotFoundException)
        {
            dialogs.Alert("폴더 선택", "기기의 파일 선택기를 열 수 없습니다. ‘사진이 있는 폴더 목록’을 사용해 주세요.");
        }
    }

    private async void ShowPhotoFolders()
    {
        IReadOnlyList<PhotoFolder> folders = [];
        bool loaded = await state.RunAsync("기기의 사진 폴더를 찾고 있습니다…", (ct) => {
            folders = new MediaLibrary(this).Folders(ct);
            return Task.CompletedTask;
        });
        if (!loaded || IsDestroyed)
        {
            return;
        }

        if (folders.Count == 0)
        {
            dialogs.Alert("사진이 있는 폴더 목록", "기기 사진 목록에 등록된 이미지가 없습니다. ‘폴더 직접 선택’으로 추가해 주세요.");
            return;
        }

        bool[] selected = new bool[folders.Count];
        string[] labels = folders.Select((f) => $"{f.Name} · {f.Count:N0}개" + (state.Preferences.HidePaths ? "" : $"\n{f.Path} · {f.Volume}")).ToArray();
        new AlertDialog.Builder(this).SetTitle("사진이 있는 폴더 목록 · 여러 개 선택 가능")!.SetMultiChoiceItems(labels, selected, (_, args) => selected[args.Which] = args.IsChecked)!.SetPositiveButton("선택 폴더 추가", async (_, _) => {
            (string Uri, string Name)[] chosen = folders.Where((_, index) => selected[index]).Select((f) => (f.Uri, f.Name)).ToArray();
            if (0 < chosen.Length)
            {
                await RegisterFoldersAsync(chosen);
            }
        })!.SetNegativeButton("취소", (_, _) => {
        })!.Show();
    }

    private async Task RegisterFoldersAsync(IReadOnlyList<(string Uri, string Name)> folders)
    {
        long[] folderIds = [];
        bool saved = await state.EditLibraryAsync("폴더 등록 중…", (catalog) => {
            foreach ((string Uri, string Name) folder in folders)
            {
                catalog.AddFolder(folder.Uri, folder.Name);
            }

            Dictionary<string, Folder> registered = catalog.Folders().ToDictionary((folder) => folder.Uri);
            folderIds = folders.Select((folder) => registered[folder.Uri].Id).Distinct().ToArray();
            state.Preferences.Scope = folderIds.Length == 1 ? "folder:" + folderIds[0] : "all";
        });
        if (!saved || IsDestroyed)
        {
            return;
        }

        library.Close();
        StartIndexing(folderIds);
    }

    private void PickPhoto()
        => StartActivityForResult(new Intent(Intent.ActionOpenDocument).SetType("image/*").AddCategory(Intent.CategoryOpenable)!.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission), PhotoPicker);
    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data is null)
        {
            return;
        }

        try
        {
            if (requestCode == ArchiveExportPicker)
            {
                bool exported = await state.ExportArchiveAsync(data.Data.ToString()!);
                if (exported && !IsDestroyed)
                {
                    Toast.MakeText(this, "교환 파일 내보내기 완료", ToastLength.Long)!.Show();
                }
                return;
            }

            if (requestCode == ArchiveImportPicker)
            {
                await new ArchiveImportDialog(this, state).ShowAsync(data.Data.ToString()!);
                return;
            }

            if (requestCode == FolderPicker)
            {
                ContentResolver!.TakePersistableUriPermission(data.Data, data.Flags & ActivityFlags.GrantReadUriPermission);
                string name = await Task.Run(() => new DocumentLibrary(ContentResolver).FolderName(data.Data));
                await RegisterFoldersAsync([(data.Data.ToString()!, name)]);
            }
            else if (requestCode == PhotoPicker)
            {
                string uri = data.Data.ToString()!;
                string name = await Task.Run(() => {
                    using global::Android.Database.ICursor? cursor = ContentResolver!.Query(data.Data, [IOpenableColumns.DisplayName!], null, null, null);
                    return cursor?.MoveToFirst() == true ? cursor.GetString(0) ?? "선택한 이미지" : "선택한 이미지";
                });
                SearchReference reference = new(uri, name);
                await state.RunAsync("유사 이미지 검색 중…", async (ct) => state.ShowResults(await state.Library.SimilarAsync(uri, state.Preferences.Scope, state.Report, ct), "유사 이미지", reference), collectFiles: true);
            }
        }
        catch (Exception error)
        {
            dialogs.Error("사진 접근 실패", error.Message);
        }
    }
}
