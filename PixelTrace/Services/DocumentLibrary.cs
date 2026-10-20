using Android.Content;
using Android.Database;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace PixelTrace.Mobile;

public sealed class DocumentLibrary(ContentResolver resolver)
{
    private ContentResolver Resolver { get; } = resolver;

    internal static string ArchivePath(Folder folder, string path)
    {
        if (MediaLibrary.IsMediaFolder(folder.Uri))
        {
            using Uri source = Uri.Parse(folder.Uri)!;
            string root = source.GetQueryParameter("path")!;
            if (!path.StartsWith(root, StringComparison.Ordinal)) { throw new InvalidDataException("사진의 원본 폴더 경로가 올바르지 않습니다."); }
            return PixelTrace.Exchange.LibraryArchive.NormalizeRelativePath(path[root.Length..]);
        }

        string prefix = folder.Name + "/";
        if (path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return PixelTrace.Exchange.LibraryArchive.NormalizeRelativePath(path[prefix.Length..]);
        }

        // 표시 이름을 바꾸기 전에 수집한 경로는 이전 폴더 이름을 제거합니다.
        int separator = path.IndexOf('/');
        if (separator < 0) { throw new InvalidDataException("사진의 원본 폴더 경로가 올바르지 않습니다."); }
        return PixelTrace.Exchange.LibraryArchive.NormalizeRelativePath(path[(separator + 1)..]);
    }

    public string FolderName(Uri tree)
    {
        using Uri? doc = DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree));
        using ICursor? cursor = Resolver.Query(doc!, ["_display_name"], null, null, null);
        return cursor is not null && cursor.MoveToFirst() ? cursor.GetString(0)! : "사진 폴더";
    }

    public IEnumerable<Document> Scan(Folder folder, CancellationToken ct)
    {
        using Uri tree = Uri.Parse(folder.Uri)!;
        Stack<(string Id, string Path)> stack = new();
        stack.Push((DocumentsContract.GetTreeDocumentId(tree)!, folder.Name));
        while (stack.TryPop(out (string Id, string Path) directory))
        {
            ct.ThrowIfCancellationRequested();
            using Uri child = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, directory.Id)!;
            using ICursor cursor = Resolver.Query(child, ["document_id", "_display_name", "mime_type", "_size", "last_modified"], null, null, null)
                ?? throw new IOException("폴더 내용을 읽을 수 없습니다. 폴더를 다시 선택해 주세요.");
            while (cursor.MoveToNext())
            {
                ct.ThrowIfCancellationRequested();
                string id = cursor.GetString(0)!;
                string name = cursor.GetString(1)!;
                string mime = cursor.GetString(2)!;
                string relative = directory.Path + "/" + name;
                if (mime == "vnd.android.document/directory")
                {
                    stack.Push((id, relative));
                    continue;
                }

                if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using Uri uri = DocumentsContract.BuildDocumentUriUsingTree(tree, id)!;
                yield return new(folder.Id, uri.ToString()!, name, relative, cursor.IsNull(3) ? 0 : cursor.GetLong(3), cursor.IsNull(4) ? 0 : cursor.GetLong(4));
            }
        }
    }

    public async Task<string> CopyAsync(string uri, string path, CancellationToken ct)
    {
        using Uri parsed = Uri.Parse(uri)!;
        await using Stream input = Resolver.OpenInputStream(parsed)
            ?? throw new IOException("사진을 열 수 없습니다.");
        await using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await input.CopyToAsync(output, ct);
        return path;
    }

}
