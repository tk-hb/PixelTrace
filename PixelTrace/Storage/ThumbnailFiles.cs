namespace PixelTrace.Mobile;

internal static class ThumbnailFiles
{
    // UI previews run outside AppState's operation slot. All thumbnail writers
    // share this gate with collection, including their temporary file lifetime.
    private static object Gate { get; } = new();

    public static string Ensure(string directory, string hash, Action<string> save)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, hash + ".jpg");
            if (!File.Exists(path))
            {
                save(path);
            }

            return path;
        }
    }

    public static void Collect(string directory, HashSet<string> referenced)
    {
        lock (Gate)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (!referenced.Contains(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
