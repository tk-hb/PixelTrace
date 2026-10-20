using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.OS;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace PixelTrace.Mobile;

public sealed record PhotoFolder(string Uri, string Name, string Path, string Volume, int Count);
public sealed class MediaLibrary(Context context)
{
    private Context Context { get; } = context;
    public static string ReadPermission
        => OperatingSystem.IsAndroidVersionAtLeast(33) ? global::Android.Manifest.Permission.ReadMediaImages : global::Android.Manifest.Permission.ReadExternalStorage;

    public static bool HasAccess(Context context)
        => HasAllFilesAccess || context.CheckSelfPermission(ReadPermission) == Permission.Granted;
    public static bool HasAllFilesAccess
        => OperatingSystem.IsAndroidVersionAtLeast(30) && global::Android.OS.Environment.IsExternalStorageManager;
    public static bool IsMediaFolder(string uri)
        => uri.StartsWith("mediafolder://", StringComparison.Ordinal);
    private static string VisibleSelection
        => OperatingSystem.IsAndroidVersionAtLeast(30) ? "is_pending=0 AND is_trashed=0" : "is_pending=0";

    private void RequireAccess()
    {
        if (!HasAccess(Context))
        {
            throw new UnauthorizedAccessException("설정에서 모든 파일 접근을 허용하거나 앱의 사진 접근 권한을 허용해 주세요. ‘폴더 직접 선택’으로 추가할 수도 있습니다.");
        }
    }

    public IReadOnlyList<PhotoFolder> Folders(CancellationToken ct)
    {
        RequireAccess();
        using Uri collection = MediaStore.Images.Media.GetContentUri("external")!;
        using CancellationSignal signal = new();
        using CancellationTokenRegistration registration = ct.Register(signal.Cancel);
        using ICursor cursor = Context.ContentResolver!.Query(collection, ["volume_name", "relative_path"], VisibleSelection, null, null, signal)
            ?? throw new IOException("기기의 사진 목록을 읽을 수 없습니다.");
        Dictionary<(string Volume, string Path), int> counts = new();
        while (cursor.MoveToNext())
        {
            ct.ThrowIfCancellationRequested();
            (string, string) key = (cursor.GetString(0)!, cursor.GetString(1) ?? "");
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        RequireAccess();
        return counts.Select((item) => {
            (string? volume, string? path) = item.Key;
            string name = path.TrimEnd('/').Split('/').Last();
            return new PhotoFolder("mediafolder://" + volume + "/?path=" + System.Uri.EscapeDataString(path), name.Length == 0 ? "저장소 최상위" : name, path, volume, item.Value);
        }).OrderBy((f) => f.Path, StringComparer.OrdinalIgnoreCase).ThenBy((f) => f.Volume).ToArray();
    }

    public IEnumerable<Document> Scan(Folder folder, CancellationToken ct)
    {
        RequireAccess();
        using Uri source = Uri.Parse(folder.Uri)!;
        string path = source.GetQueryParameter("path")!;
        using Uri collection = MediaStore.Images.Media.GetContentUri(source.Host!)!;
        using CancellationSignal signal = new();
        using CancellationTokenRegistration registration = ct.Register(signal.Cancel);
        using ICursor cursor = Context.ContentResolver!.Query(collection, ["_id", "_display_name", "_size", "date_modified"], VisibleSelection + (path.Length == 0 ? " AND (relative_path IS NULL OR relative_path=?)" : " AND relative_path=?"), [path], "_id", signal)
            ?? throw new IOException("선택한 사진 폴더를 읽을 수 없습니다.");
        while (cursor.MoveToNext())
        {
            ct.ThrowIfCancellationRequested();
            using Uri uri = ContentUris.WithAppendedId(collection, cursor.GetLong(0))!;
            string name = cursor.GetString(1)!;
            yield return new(folder.Id, uri.ToString()!, name, path + name, cursor.IsNull(2) ? 0 : cursor.GetLong(2), cursor.IsNull(3) ? 0 : cursor.GetLong(3) * 1000);
        }

        RequireAccess();
    }
}
