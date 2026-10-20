using Android.Content;
using Uri = Android.Net.Uri;
using PixelTrace.Exchange;

namespace PixelTrace.Mobile;

public sealed class AppState
{
    private object Gate { get; } = new();

    private CancellationTokenSource? active;
    private Func<Task<bool>>? pendingWork;
    private TaskCompletionSource<bool>? workCompletion;
    private OperationState completedOperation = new(false, "완료");
    private Context Context { get; }
    private long lastReport;
    private bool collectedFiles;
    private LibraryFiles Files { get; }
    private ContentResolver Resolver { get; }
    private NsfwAnalysis ContentAnalysis { get; }
    private ArchiveTransfer ArchiveTransfer { get; }
    private Preferences WorkPreferences { get; }
    public ThumbnailCache Thumbnails { get; }
    public Preferences Preferences { get; }
    public Catalog Catalog { get; }
    public ModelCache Models { get; }
    public LibraryEngine Library { get; }
    public OperationState Operation { get; private set; } = new(false, "사진 폴더를 추가해 주세요.");
    public LibrarySnapshot Snapshot { get; private set; } = new([], []);
    public IReadOnlyList<ImageItem> Results { get; private set; } = [];
    public string ResultTitle { get; private set; } = "이미지 목록";
    public bool More { get; private set; }
    public SearchReference? Reference { get; private set; }
    public string Data { get; }

    public event Action? Changed;
    public AppState(Context context)
    {
        Context = context.ApplicationContext!;
        Data = context.FilesDir!.AbsolutePath;
        Preferences = new(context);
        WorkPreferences = new(context);
        Catalog = new(context, WorkPreferences);
        Models = new(Path.Combine(Data, "models"), WorkPreferences);
        Library = new(context, Catalog, Models, WorkPreferences, Data);
        Files = new(Data, Catalog);
        Resolver = context.ContentResolver!;
        Thumbnails = new(context, Path.Combine(Data, "thumbnails"));
        ContentAnalysis = new(context, Catalog, WorkPreferences, Data);
        ArchiveTransfer = new(context, Catalog, Data);
    }

    public void Report(string text, int done = 0, int total = 0)
    {
        lock (Gate)
        {
            if (active is null || active.IsCancellationRequested)
            {
                return;
            }

            Operation = Operation with
            {
                Busy = true,
                Text = text,
                Done = done,
                Total = total
            };
            long now = Environment.TickCount64;
            if (now - lastReport < 100)
            {
                return;
            }

            lastReport = now;
        }

        Changed?.Invoke();
    }

    public void Cancel()
    {
        lock (Gate)
        {
            if (active is null || !Operation.CanCancel)
            {
                return;
            }

            active.Cancel();
            Operation = Operation with
            {
                Text = "취소 요청됨 · 진행 중인 처리를 정리하고 있습니다.",
                CanCancel = false
            };
        }

        Changed?.Invoke();
    }

    internal Task<bool> ExecuteWorkAsync()
    {
        Func<Task<bool>>? work;
        lock (Gate)
        {
            work = pendingWork;
            pendingWork = null;
        }
        return work is null ? Task.FromResult(false) : work();
    }

    internal void CompleteWork(bool success, Exception? error = null)
    {
        TaskCompletionSource<bool>? completion;
        lock (Gate)
        {
            completion = workCompletion;
            if (completion is null) { return; }
            workCompletion = null;
            pendingWork = null;
            Operation = error is null ? completedOperation : new(false, "작업 실패", Error: error.Message);
            WorkPreferences.UseSnapshot(null);
            active = null;
        }

        try { Changed?.Invoke(); }
        finally { completion.TrySetResult(success && error is null); }
    }

    public async Task<bool> RunAsync(string title, Func<CancellationToken, Task> action, bool collectFiles = false, bool refreshResults = false)
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (active is not null)
            {
                return false;
            }

            active = cancellation;
            WorkPreferences.UseSnapshot(Preferences.Capture());
            lastReport = 0;
            Operation = new(true, title);
            completedOperation = new(false, "완료");
            workCompletion = completion;
            pendingWork = () => ExecuteAsync(action, cancellation, collectFiles, refreshResults);
        }

        try
        {
            Changed?.Invoke();
            Context.StartForegroundService(new Intent(Context, typeof(WorkService)));
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Error(AppIdentity.LogTag, error.ToString());
            CompleteWork(false, error);
        }
        return await completion.Task;
    }

    private async Task<bool> ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancellation, bool collectFiles, bool refreshResults)
    {
        OperationState completed = new(false, "완료");
        bool success = false;
        try
        {
            Changed?.Invoke();
            await Task.Run(async () => {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!collectedFiles)
                {
                    Files.Collect();
                    collectedFiles = true;
                }

                await action(cancellation.Token);
            });
            cancellation.Token.ThrowIfCancellationRequested();
            success = true;
        }
        catch (Exception error)when (cancellation.IsCancellationRequested && error is OperationCanceledException or global::Android.OS.OperationCanceledException)
        {
            completed = new(false, "중단했습니다. 저장한 색인은 유지됩니다.");
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Error(AppIdentity.LogTag, error.ToString());
            completed = new(false, "작업 실패", Error: error.Message);
        }
        finally
        {
            try
            {
                // All producers/native calls have finished before cleanup. Keep
                // ownership until DB counts and immutable sidebar data are ready.
                LibrarySnapshot snapshot = await Task.Run(() => {
                    if (collectFiles)
                    {
                        Files.Collect();
                    }

                    if (refreshResults)
                    {
                        BrowseCore(false, CancellationToken.None);
                    }

                    return ReadSnapshot();
                });
                Snapshot = snapshot;
            }
            catch (Exception error)
            {
                global::Android.Util.Log.Error(AppIdentity.LogTag, error.ToString());
                if (completed.Error is null)
                {
                    completed = new(false, "라이브러리 갱신 실패", Error: error.Message);
                }

                success = false;
            }

            lock (Gate)
            {
                completedOperation = completed;
            }
        }

        return success;
    }

    public Task<bool> BrowseAsync(bool append = false)
        => RunAsync("이미지 목록을 불러오는 중…", async (ct) => {
        await ContentAnalysis.AnalyzePendingAsync(Report, ct);
        BrowseCore(append, ct);
    });
    public Task<bool> ExportArchiveAsync(string destination)
        => RunAsync("교환 파일을 내보내는 중…", async (ct) => {
        using Uri uri = Uri.Parse(destination)!;
        await using Stream output = Resolver.OpenOutputStream(uri, "wt")
            ?? throw new IOException("저장할 파일을 열 수 없습니다.");
        await ArchiveTransfer.ExportAsync(output, ct);
    });
    public async Task<ArchiveImportResult?> ImportArchiveAsync(string archive, IReadOnlyDictionary<string, long> mappings)
    {
        ArchiveImportResult? result = null;
        bool saved = await RunAsync("교환 파일을 가져오는 중…", async (ct) => {
            result = await ArchiveTransfer.ImportAsync(archive, mappings, Report, ct);
            Preferences.Scope = "all";
        }, collectFiles: true, refreshResults: true);
        return saved ? result : null;
    }
    public Task<bool> EditLibraryAsync(string title, Action<Catalog> edit)
        => RunAsync(title, (ct) => {
        ct.ThrowIfCancellationRequested();
        edit(Catalog);
        BrowseCore(false, ct);
        return Task.CompletedTask;
    }, collectFiles: true);
    private LibrarySnapshot ReadSnapshot()
        => new(Catalog.Folders(), Catalog.Groups());
    public Task<bool> SaveSettingsAsync(SettingsValues settings)
    {
        EmbeddingEngines.RequireAvailable(settings.EngineId);
        lock (Gate)
        {
            if (active is null && (Preferences.EngineId != settings.EngineId || Preferences.SoftTokens != settings.SoftTokens))
            {
                Library.Unload();
            }

            Preferences.Apply(settings);
            if (active is null)
            {
                Results = [];
                Reference = null;
                More = false;
            }
        }

        Changed?.Invoke();
        return Task.FromResult(true);
    }
    public Task<bool> AnalyzeContentAsync()
        => RunAsync("NSFW 판별 중…", (ct) => ContentAnalysis.AnalyzePendingAsync(Report, ct), refreshResults: true);
    private void BrowseCore(bool append, CancellationToken ct)
    {
        LibrarySnapshot snapshot = ReadSnapshot();
        string scope = Preferences.Scope;
        if ((scope.StartsWith("folder:") && !snapshot.Folders.Any((f) => scope == "folder:" + f.Id)) || (scope.StartsWith("group:") && !snapshot.Groups.Any((g) => scope == "group:" + g.Id)))
        {
            scope = "all";
        }

        IReadOnlyList<ImageItem> rows = Catalog.Browse(scope, append ? Results.Count : 0, 81);
        ct.ThrowIfCancellationRequested();
        Preferences.Scope = scope;
        Snapshot = snapshot;
        More = 80 < rows.Count;
        Results = append ? Results.Concat(rows.Take(80)).ToArray() : rows.Take(80).ToArray();
        ResultTitle = "이미지 목록";
        Reference = null;
    }

    public void ShowResults(IReadOnlyList<ImageItem> rows, string title, SearchReference? reference = null)
    {
        lock (Gate)
        {
            active?.Token.ThrowIfCancellationRequested();
            Results = rows;
            ResultTitle = title;
            Reference = reference;
            More = false;
        }

        Changed?.Invoke();
    }
}
