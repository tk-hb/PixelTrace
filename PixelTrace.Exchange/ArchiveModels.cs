namespace PixelTrace.Exchange;

public sealed record ArchiveGroup(string Id, string Name);
public sealed record ArchiveFolder(string Id, string Name, string? GroupId);
public sealed record ArchiveManifest(string Format, int Version, IReadOnlyList<ArchiveGroup> Groups, IReadOnlyList<ArchiveFolder> Folders);
public sealed record ArchiveImage(string FolderId, string RelativePath, string Hash, int Width, int Height, byte[] Vector, float? NsfwScore);
public sealed record ArchiveImportResult(int Imported, int Unmatched);
