using Android.Content;
using PixelTrace.Exchange;
using System.Runtime.CompilerServices;

namespace PixelTrace.Mobile;

internal sealed class ArchiveTransfer(Context context, Catalog catalog, string data)
{
    private Catalog Catalog { get; } = catalog;
    private string Data { get; } = data;
    private DocumentLibrary Documents { get; } = new(context.ContentResolver!);
    private MediaLibrary Media { get; } = new(context);

    public Task ExportAsync(Stream destination, CancellationToken ct)
    {
        ArchiveManifest manifest = LibraryArchive.CreateManifest(Catalog.Groups().Select((group) => new ArchiveGroup(group.Id.ToString(), group.Name)).ToArray(), Catalog.Folders().Select((folder) => new ArchiveFolder(folder.Id.ToString(), folder.Name, folder.GroupId?.ToString())).ToArray());
        return LibraryArchive.WriteAsync(destination, manifest, ExportImagesAsync(ct), ct);
    }

    private async IAsyncEnumerable<ArchiveImage> ExportImagesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (Folder folder in Catalog.Folders())
        {
            foreach (ArchiveImage image in Catalog.ExportEntries(folder, ct))
            {
                yield return image;
            }

            await Task.Yield();
        }
    }

    public async Task<ArchiveImportResult> ImportAsync(string archive, IReadOnlyDictionary<string, long> mappings, Action<string, int, int> report, CancellationToken ct)
    {
        ArchiveManifest manifest = await LibraryArchive.ReadManifestAsync(archive, ct);
        Dictionary<long, Folder> targets = Catalog.Folders().ToDictionary((folder) => folder.Id);
        if (mappings.Count == 0 || mappings.Values.Distinct().Count() != mappings.Count || mappings.Any((mapping) => !targets.ContainsKey(mapping.Value) || !manifest.Folders.Any((folder) => folder.Id == mapping.Key)))
        {
            throw new ArgumentException("가져올 폴더마다 서로 다른 대상 폴더를 지정해 주세요.");
        }

        Dictionary<string, Dictionary<string, Document>> files = new(StringComparer.Ordinal);
        int scanned = 0;
        long reported = Environment.TickCount64;
        report("대상 사진 연결 중…", 0, 0);
        foreach ((string source, long target) in mappings)
        {
            Folder folder = targets[target];
            Dictionary<string, Document> paths = new(StringComparer.Ordinal);
            IEnumerable<Document> documents = MediaLibrary.IsMediaFolder(folder.Uri) ? Media.Scan(folder, ct) : Documents.Scan(folder, ct);
            foreach (Document document in documents)
            {
                string path = DocumentLibrary.ArchivePath(folder, document.RelativePath);
                if (!paths.TryAdd(path, document))
                {
                    throw new InvalidDataException("같은 상대 경로의 사진이 여러 개 있습니다. 다른 원본 폴더를 지정해 주세요.");
                }
                scanned++;
                if (250 <= Environment.TickCount64 - reported)
                {
                    report($"대상 사진 연결 중 · {scanned:N0}개 확인", scanned, 0);
                    reported = Environment.TickCount64;
                }
            }

            files[source] = paths;
        }

        List<(Document Document, ArchiveImage Image)> imported = new();
        HashSet<(long Folder, string Uri)> used = new();
        HashSet<string> known = manifest.Folders.Select((folder) => folder.Id).ToHashSet(StringComparer.Ordinal);
        int unmatched = 0;
        await foreach (ArchiveImage image in LibraryArchive.ReadImagesAsync(archive, ct))
        {
            if (!known.Contains(image.FolderId))
            {
                throw new InvalidDataException("이미지의 원본 폴더 정보가 없습니다.");
            }

            if (!files.TryGetValue(image.FolderId, out Dictionary<string, Document>? paths)) { continue; }
            if (!paths.TryGetValue(image.RelativePath, out Document? document) || !used.Add((document.FolderId, document.Uri)))
            {
                unmatched++;
                continue;
            }

            imported.Add((document, image));
        }

        ct.ThrowIfCancellationRequested();
        report("연결한 임베딩을 저장하는 중…", 0, imported.Count);
        Catalog.MergeArchive(manifest, mappings, imported, Path.Combine(Data, "thumbnails"), ct);
        return new(imported.Count, unmatched);
    }
}
