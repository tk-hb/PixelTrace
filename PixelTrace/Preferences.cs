namespace PixelTrace.Mobile;

public sealed class Preferences(global::Android.Content.Context context)
{
    private global::Android.Content.ISharedPreferences Values { get; } = context.GetSharedPreferences("preferences", global::Android.Content.FileCreationMode.Private)!;
    private SettingsValues? snapshot;
    internal SettingsValues Capture()
        => new(EngineId, SoftTokens, BatchSize, PreparationConcurrency, ResultLimit, CardSize, HideNames, HidePaths, NsfwFilter, NsfwThreshold, NsfwConcurrency);
    internal void UseSnapshot(SettingsValues? settings)
        => snapshot = settings;

    internal void Apply(SettingsValues settings)
    {
        EmbeddingEngineOption engine = EmbeddingEngines.RequireAvailable(settings.EngineId);
        Values.Edit()!.PutString("engine", engine.Id)!.PutInt("tokens", settings.SoftTokens)!.PutInt("batch_size", settings.BatchSize)!.PutInt("preparation_concurrency", settings.PreparationConcurrency)!.PutInt("limit", settings.ResultLimit)!.PutString("card_size", settings.CardSize)!.PutBoolean("hide_names", settings.HideNames)!.PutBoolean("hide_paths", settings.HidePaths)!.PutString("nsfw_filter", settings.NsfwFilter)!.PutInt("nsfw_threshold", settings.NsfwThreshold)!.PutInt("nsfw_concurrency", settings.NsfwConcurrency)!.Apply();
    }

    public string EngineId
        => snapshot?.EngineId ?? Values.GetString("engine", "litert-gpu")!;
    public string NsfwFilter
        => snapshot?.NsfwFilter ?? Values.GetString("nsfw_filter", "off")!;
    public int NsfwThreshold
        => snapshot?.NsfwThreshold ?? Values.GetInt("nsfw_threshold", 50);
    public int NsfwConcurrency
        => snapshot?.NsfwConcurrency ?? Values.GetInt("nsfw_concurrency", 2);
    public bool MasksImage(float? score)
        => NsfwFilter != "off" && (!score.HasValue || NsfwThreshold <= score.Value * 100);

    public string Precision
        => EmbeddingEngines.Find(EngineId)?.Precision ?? "unselected";
    public int SoftTokens
    {
        get
            => snapshot?.SoftTokens ?? (Values.GetInt("tokens", 140) == 70 ? 70 : 140);
        set
            => Values.Edit()!.PutInt("tokens", value)!.Apply();
    }
    public int BatchSize
    {
        get
            => snapshot?.BatchSize ?? Values.GetInt("batch_size", 1);
        set
            => Values.Edit()!.PutInt("batch_size", value)!.Apply();
    }
    public int PreparationConcurrency
    {
        get
            => snapshot?.PreparationConcurrency ?? Values.GetInt("preparation_concurrency", 2);
        set
            => Values.Edit()!.PutInt("preparation_concurrency", value)!.Apply();
    }
    public string Backend
        => EmbeddingEngines.Find(EngineId)?.Backend ?? "unselected";
    public string SearchMode
    {
        get
            => Values.GetString("search_mode", "content")!;
        set
            => Values.Edit()!.PutString("search_mode", value)!.Apply();
    }
    public int ResultLimit
    {
        get
            => snapshot?.ResultLimit ?? Values.GetInt("limit", 100);
        set
            => Values.Edit()!.PutInt("limit", value)!.Apply();
    }
    public string CardSize
    {
        get
            => snapshot?.CardSize ?? Values.GetString("card_size", "normal")!;
        set
            => Values.Edit()!.PutString("card_size", value)!.Apply();
    }
    public bool HideNames
    {
        get
            => snapshot?.HideNames ?? Values.GetBoolean("hide_names", false);
        set
            => Values.Edit()!.PutBoolean("hide_names", value)!.Apply();
    }
    public bool HidePaths
    {
        get
            => snapshot?.HidePaths ?? Values.GetBoolean("hide_paths", false);
        set
            => Values.Edit()!.PutBoolean("hide_paths", value)!.Apply();
    }
    public string Scope
    {
        get
            => Values.GetString("scope", "all")!;
        set
            => Values.Edit()!.PutString("scope", value)!.Apply();
    }
    public string Query
    {
        get
            => Values.GetString("query", "")!;
        set
            => Values.Edit()!.PutString("query", value)!.Apply();
    }
}
