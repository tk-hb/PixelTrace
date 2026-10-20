using Android.Content;

namespace PixelTrace.Mobile;

public sealed class LibraryEngine(Context context, Catalog catalog, ModelCache cache, Preferences preferences, string data)
{
    private Context Context { get; } = context;
    private Catalog Catalog { get; } = catalog;
    private ModelCache Cache { get; } = cache;
    private Preferences Preferences { get; } = preferences;
    private string Data { get; } = data;
    private DocumentLibrary Documents { get; } = new(context.ContentResolver!);
    private ImagePreparation Preprocessor { get; } = new(Path.Combine(data, "thumbnails"));
    private GemmaInference Model { get; } = new(cache, preferences, Path.Combine(data, "compiled-models"));
    private NsfwAnalysis ContentAnalysis { get; } = new(context, catalog, preferences, data);
    private string Profile
        => Cache.Profile;
    public string ModelStatus
        => Model.Status;

    public void Unload()
        => Model.Unload();
    public async Task PrepareModelAsync(Action<string, int, int> report, CancellationToken ct)
    {
        await Cache.EnsureAsync(report, ct);
        if (!Model.IsLoaded)
        {
            report("모델 로딩 중 · " + EmbeddingEngines.Find(Preferences.EngineId)!.Label, 0, 0);
            Model.Load(ct);
        }
    }

    public async Task IndexAsync(Action<string, int, int> report, CancellationToken ct, IReadOnlyCollection<long>? folderIds = null)
    {
        await new LibraryIndexer(Context, Catalog, Preferences, Data, Preprocessor, Model, PrepareModelAsync).RunAsync(Profile, report, ct, folderIds);
        await ContentAnalysis.AnalyzePendingAsync(report, ct);
    }
    public async Task<IReadOnlyList<ImageItem>> SearchAsync(string query, string scope, Action<string, int, int> report, CancellationToken ct)
    {
        await ContentAnalysis.AnalyzePendingAsync(report, ct);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Catalog.Browse(scope, 0, Preferences.ResultLimit);
        }

        if (Preferences.SearchMode == "filename")
        {
            return Catalog.SearchNames(query, scope, Preferences.ResultLimit, ct);
        }

        RequireIndex(scope);
        await PrepareModelAsync(report, ct);
        report("검색 중…", 0, 0);
        float[] vector = Model.EncodeText(query, ct);
        return Catalog.Search(vector, scope, Preferences.ResultLimit, ct, report: report);
    }

    public async Task<IReadOnlyList<ImageItem>> SimilarAsync(string uri, string scope, Action<string, int, int> report, CancellationToken ct)
    {
        RequireIndex(scope);
        await ContentAnalysis.AnalyzePendingAsync(report, ct);
        ImageEntry? cached = Catalog.Find(uri);
        float[] vector;
        if (cached is not null && EmbeddingModels.IsCompatibleProfile(cached.Profile, Profile))
        {
            vector = cached.Vector;
        }
        else
        {
            await PrepareModelAsync(report, ct);
            report("유사 이미지 검색 중…", 0, 0);
            string temporary = Path.Combine(Data, "compare-image.tmp");
            try
            {
                await Documents.CopyAsync(uri, temporary, ct);
                PreparedImage image = Preprocessor.Prepare(temporary, Guid.NewGuid().ToString("N"), ct);
                try
                {
                    vector = Model.EncodeImage(image, ct);
                }
                finally
                {
                    File.Delete(image.Thumbnail);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return Catalog.Search(vector, scope, Preferences.ResultLimit, ct, uri, report);
    }

    private void RequireIndex(string scope)
    {
        if (!Catalog.HasIndex(scope))
        {
            throw new InvalidOperationException("선택한 범위에 색인이 없습니다. ⋯ → 색인 갱신을 먼저 실행해 주세요.");
        }
    }
}
