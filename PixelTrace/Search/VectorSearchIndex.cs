namespace PixelTrace.Mobile;

internal readonly record struct SearchRevision(string Database, long Revision);
internal readonly record struct SearchVector(ulong Key, float[]? Vector);

internal sealed class VectorSearchIndex(string directory) : IDisposable
{
    internal const int Threshold = 50_000;
    private const int ShardSize = 50_000;
    private const int DeltaLimit = 5_000;
    private const string Format = "pixeltrace-usearch-f32-v1";
    private string DirectoryPath { get; } = directory;
    private string ManifestPath => Path.Combine(DirectoryPath, "current.index");
    private readonly object gate = new();
    private readonly List<(Shard File, USearchIndex Index)> shards = new();
    private Manifest? manifest;
    private USearchIndex? delta;
    private HashSet<ulong> changed = new();
    private long deltaRevision = -1;
    private sealed record Shard(string Name, ulong First, ulong Last);
    private sealed record Manifest(SearchRevision Source, Shard[] Files);

    public ulong[] Search(float[] query, int limit, HashSet<ulong> allowed, Func<SearchRevision> revision, Func<IEnumerable<SearchVector>> vectors, Func<long, IEnumerable<SearchVector>> changes, Action<long> checkpoint, Action<int>? progress, CancellationToken ct)
    {
        lock (gate)
        {
            SearchRevision source = revision();
            if (manifest is null) { Load(source); }
            if (manifest is null || manifest.Source.Database != source.Database || manifest.Source.Revision > source.Revision)
            {
                Rebuild(source, revision, vectors, checkpoint, progress, ct);
            }

            if (deltaRevision != source.Revision)
            {
                SearchVector[] pending = changes(manifest!.Source.Revision).Take(DeltaLimit).ToArray();
                if (pending.Length >= DeltaLimit)
                {
                    Rebuild(source, revision, vectors, checkpoint, progress, ct);
                    pending = [];
                }
                ReplaceDelta(pending, source.Revision, ct);
            }

            int candidates = Math.Min(allowed.Count, checked(limit + Math.Max(64, limit)));
            PriorityQueue<ulong, float> best = new();
            foreach ((Shard file, USearchIndex index) in shards)
            {
                int searchable = allowed.Count((key) => file.First <= key && key <= file.Last && !changed.Contains(key));
                if (searchable == 0) { continue; }
                foreach ((ulong key, float distance) in index.Search(query, Math.Min(candidates, searchable), allowed, changed, ct))
                {
                    AddCandidate(best, key, -distance, candidates);
                }
            }
            if (delta is not null)
            {
                int searchable = changed.Count(allowed.Contains);
                if (searchable > 0)
                {
                    foreach ((ulong key, float distance) in delta.Search(query, Math.Min(candidates, searchable), allowed, null, ct))
                    {
                        AddCandidate(best, key, -distance, candidates);
                    }
                }
            }
            return best.UnorderedItems.Select((entry) => entry.Element).ToArray();
        }
    }

    private static void AddCandidate(PriorityQueue<ulong, float> best, ulong key, float score, int limit)
    {
        if (best.Count < limit) { best.Enqueue(key, score); }
        else if (best.TryPeek(out _, out float minimum) && minimum < score) { best.DequeueEnqueue(key, score); }
    }

    private void ReplaceDelta(SearchVector[] changes, long revision, CancellationToken ct)
    {
        USearchIndex? replacement = null;
        try
        {
            if (changes.Any((entry) => entry.Vector is not null))
            {
                replacement = new(changes.Length);
                foreach (SearchVector entry in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Vector is not null) { replacement.Add(entry.Key, entry.Vector); }
                }
            }
            delta?.Dispose();
            delta = replacement;
            replacement = null;
            changed = changes.Select((entry) => entry.Key).ToHashSet();
            deltaRevision = revision;
        }
        finally { replacement?.Dispose(); }
    }

    private void Load(SearchRevision source)
    {
        if (!File.Exists(ManifestPath)) { return; }
        try
        {
            using BinaryReader reader = new(File.OpenRead(ManifestPath));
            if (reader.ReadString() != Format) { return; }
            SearchRevision saved = new(reader.ReadString(), reader.ReadInt64());
            if (saved.Database != source.Database || saved.Revision > source.Revision) { return; }
            int count = reader.ReadInt32();
            if (count <= 0 || count > 10_000) { return; }
            Shard[] files = new Shard[count];
            for (int index = 0; index < count; index++)
            {
                string name = reader.ReadString();
                if (name != Path.GetFileName(name) || !name.StartsWith("part-", StringComparison.Ordinal) || !name.EndsWith(".usearch", StringComparison.Ordinal)) { return; }
                ulong first = reader.ReadUInt64(), last = reader.ReadUInt64();
                if (first > last) { return; }
                files[index] = new(name, first, last);
                if (!File.Exists(Path.Combine(DirectoryPath, name))) { return; }
            }
            OpenShards(files);
            manifest = new(saved, files);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            CloseIndexes();
            global::Android.Util.Log.Warn(AppIdentity.LogTag, "검색 색인을 다시 준비합니다: " + error.Message);
        }
    }

    private void Rebuild(SearchRevision source, Func<SearchRevision> revision, Func<IEnumerable<SearchVector>> vectors, Action<long> checkpoint, Action<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(DirectoryPath);
        List<Shard> files = new();
        USearchIndex? building = null;
        int inShard = 0, done = 0;
        ulong first = 0, last = 0;
        bool published = false;
        string temporary = ManifestPath + ".tmp";
        try
        {
            foreach (SearchVector entry in vectors())
            {
                ct.ThrowIfCancellationRequested();
                building ??= new(ShardSize);
                if (inShard == 0) { first = entry.Key; }
                last = entry.Key;
                building.Add(entry.Key, entry.Vector!);
                inShard++;
                done++;
                if (done % 100 == 0) { progress?.Invoke(done); }
                if (inShard == ShardSize) { SaveShard(); }
            }
            if (building is not null) { SaveShard(); }
            ct.ThrowIfCancellationRequested();
            if (revision() != source) { throw new IOException("검색 준비 중 색인이 변경되었습니다. 다시 검색해 주세요."); }
            using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true);
                writer.Write(Format);
                writer.Write(source.Database);
                writer.Write(source.Revision);
                writer.Write(files.Count);
                foreach (Shard file in files) { writer.Write(file.Name); writer.Write(file.First); writer.Write(file.Last); }
                writer.Flush();
                output.Flush(true);
            }
            File.Move(temporary, ManifestPath, true);
            published = true;
            CloseIndexes();
            manifest = null;
            OpenShards(files);
            manifest = new(source, files.ToArray());
            checkpoint(source.Revision);
            HashSet<string> retained = files.Select((file) => file.Name).ToHashSet(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "part-*.usearch"))
            {
                if (!retained.Contains(Path.GetFileName(path))) { File.Delete(path); }
            }
        }
        finally
        {
            building?.Dispose();
            File.Delete(temporary);
            if (!published)
            {
                foreach (Shard file in files) { File.Delete(Path.Combine(DirectoryPath, file.Name)); }
            }
        }

        void SaveShard()
        {
            string name = "part-" + Guid.NewGuid().ToString("N") + ".usearch";
            files.Add(new(name, first, last));
            building!.Save(Path.Combine(DirectoryPath, name));
            building.Dispose();
            building = null;
            inShard = 0;
        }
    }

    private void OpenShards(IEnumerable<Shard> files)
    {
        try
        {
            foreach (Shard file in files) { shards.Add((file, USearchIndex.OpenView(Path.Combine(DirectoryPath, file.Name)))); }
        }
        catch { CloseIndexes(); throw; }
    }

    private void CloseIndexes()
    {
        foreach ((Shard File, USearchIndex Index) shard in shards) { shard.Index.Dispose(); }
        shards.Clear();
        delta?.Dispose();
        delta = null;
        changed.Clear();
        deltaRevision = -1;
    }

    public void Dispose()
    {
        lock (gate) { CloseIndexes(); manifest = null; }
    }
}
