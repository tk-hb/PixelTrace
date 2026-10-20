namespace PixelTrace.Mobile;

public sealed record Folder(long Id, string Uri, string Name, long? GroupId, int Count);
public sealed record FolderGroup(long Id, string Name);
public sealed record Document(long FolderId, string Uri, string Name, string RelativePath, long Size, long Modified);
public sealed record ImageItem(long Id, long FolderId, string Uri, string Name, string Path, int Width, int Height, string Thumbnail, float? Score = null, float? NsfwScore = null);
public sealed record ImageEntry(ImageItem Image, long Size, long Modified, string Hash, string Profile, float[] Vector);
public sealed record PreparedImage(byte[] Content, int Width, int Height, string Thumbnail);
public sealed record OperationState(bool Busy, string Text, int Done = 0, int Total = 0, string? Error = null, bool CanCancel = true);
public sealed record SearchReference(string Uri, string Name, string? Thumbnail = null, float? NsfwScore = null);
public sealed record LibrarySnapshot(IReadOnlyList<Folder> Folders, IReadOnlyList<FolderGroup> Groups);
public sealed record SettingsValues(string EngineId, int SoftTokens, int BatchSize, int PreparationConcurrency, int ResultLimit, string CardSize, bool HideNames, bool HidePaths, string NsfwFilter, int NsfwThreshold, int NsfwConcurrency = 2);
