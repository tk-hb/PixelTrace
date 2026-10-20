using Android.Content;
using Android.Database;
using Android.Database.Sqlite;
using PixelTrace.Exchange;

namespace PixelTrace.Mobile;

public sealed class Catalog(Context context, Preferences? preferences = null) : SQLiteOpenHelper(context, FileName, null, 1)
{
    private const string FileName = "library.sqlite3";
    private Preferences Preferences { get; } = preferences ?? new(context);
    private VectorSearchIndex SearchIndex { get; } = new(Path.Combine(context.FilesDir!.AbsolutePath, "search-index"));
    private const string RatingSql = "COALESCE((SELECT r.score FROM content_ratings r WHERE r.hash=images.hash AND r.model='" + NsfwClassifier.Profile + "'),(SELECT r.score FROM content_ratings r WHERE r.hash=images.hash AND r.model='" + NsfwModel.PortableProfile + "'))";

    public override void OnConfigure(SQLiteDatabase? db)
        => db!.SetForeignKeyConstraintsEnabled(true);
    public override void OnOpen(SQLiteDatabase? db)
    {
        base.OnOpen(db);
        db!.ExecSQL("CREATE TABLE IF NOT EXISTS content_ratings(hash TEXT NOT NULL, model TEXT NOT NULL, score REAL NOT NULL CHECK(score>=0 AND score<=1), PRIMARY KEY(hash,model))");
        db.ExecSQL("CREATE TABLE IF NOT EXISTS search_state(id INTEGER PRIMARY KEY CHECK(id=1), database_id TEXT NOT NULL, revision INTEGER NOT NULL)");
        db.ExecSQL("INSERT OR IGNORE INTO search_state VALUES(1,'" + Guid.NewGuid().ToString("N") + "',0)");
        db.ExecSQL("CREATE TABLE IF NOT EXISTS search_changes(image_id INTEGER PRIMARY KEY, revision INTEGER NOT NULL)");
        db.ExecSQL("CREATE INDEX IF NOT EXISTS search_changes_revision ON search_changes(revision)");
        foreach ((string name, string operation, string key) in new[] { ("insert", "INSERT", "NEW.id"), ("update", "UPDATE OF vector", "NEW.id"), ("delete", "DELETE", "OLD.id") })
        {
            db.ExecSQL($"CREATE TRIGGER IF NOT EXISTS search_images_{name} AFTER {operation} ON images BEGIN UPDATE search_state SET revision=revision+1 WHERE id=1; INSERT OR REPLACE INTO search_changes VALUES({key},(SELECT revision FROM search_state WHERE id=1)); END");
        }
    }
    public override void OnCreate(SQLiteDatabase? db)
    {
        SQLiteDatabase database = db!;
        database.ExecSQL("CREATE TABLE groups_table(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE)");
        database.ExecSQL("CREATE TABLE folders(id INTEGER PRIMARY KEY, uri TEXT UNIQUE NOT NULL, name TEXT NOT NULL, group_id INTEGER REFERENCES groups_table(id) ON DELETE SET NULL)");
        database.ExecSQL("CREATE TABLE images(id INTEGER PRIMARY KEY, folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE, uri TEXT NOT NULL, name TEXT NOT NULL, path TEXT NOT NULL, size INTEGER, modified INTEGER, hash TEXT NOT NULL, profile TEXT NOT NULL, width INTEGER, height INTEGER, thumbnail TEXT NOT NULL, vector BLOB NOT NULL, scan TEXT NOT NULL, UNIQUE(folder_id,uri))");
        database.ExecSQL("CREATE INDEX images_folder ON images(folder_id)");
        database.ExecSQL("CREATE TABLE errors(folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE, uri TEXT NOT NULL, message TEXT NOT NULL, PRIMARY KEY(folder_id,uri))");
    }

    public override void OnUpgrade(SQLiteDatabase? db, int oldVersion, int newVersion)
        => throw new NotSupportedException("지원하지 않는 DB 버전입니다.");
    public IReadOnlyList<Folder> Folders()
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT f.id,f.uri,f.name,f.group_id,(SELECT COUNT(*) FROM images i WHERE i.folder_id=f.id) FROM folders f ORDER BY f.id", null);
        List<Folder> result = new();
        while (cursor!.MoveToNext())
        {
            result.Add(new(cursor.GetLong(0), cursor.GetString(1)!, cursor.GetString(2)!, cursor.IsNull(3) ? null : cursor.GetLong(3), cursor.GetInt(4)));
        }

        return result;
    }

    public IReadOnlyList<FolderGroup> Groups()
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT id,name FROM groups_table ORDER BY name", null);
        List<FolderGroup> result = new();
        while (cursor!.MoveToNext())
        {
            result.Add(new(cursor.GetLong(0), cursor.GetString(1)!));
        }

        return result;
    }

    public void AddFolder(string uri, string name)
    {
        using ContentValues values = new();
        values.Put("uri", uri);
        values.Put("name", name);
        WritableDatabase!.InsertWithOnConflict("folders", null, values, Conflict.Ignore);
    }

    public void RenameFolder(long id, string name)
    {
        using ContentValues values = new();
        values.Put("name", name);
        WritableDatabase!.Update("folders", values, "id=?", [id.ToString()]);
    }

    public void GroupFolder(long id, long? group)
    {
        using ContentValues values = new();
        if (group.HasValue)
        {
            values.Put("group_id", group.Value);
        }
        else
        {
            values.PutNull("group_id");
        }

        WritableDatabase!.Update("folders", values, "id=?", [id.ToString()]);
    }

    public void RemoveFolder(long id)
        => WritableDatabase!.Delete("folders", "id=?", [id.ToString()]);
    public void AddGroup(string name)
    {
        using ContentValues v = new();
        v.Put("name", name);
        WritableDatabase!.InsertOrThrow("groups_table", null, v);
    }

    public void RenameGroup(long id, string name)
    {
        using ContentValues v = new();
        v.Put("name", name);
        WritableDatabase!.Update("groups_table", v, "id=?", [id.ToString()]);
    }

    public void RemoveGroup(long id)
        => WritableDatabase!.Delete("groups_table", "id=?", [id.ToString()]);
    public ImageEntry? Find(string uri, long? folderId = null)
    {
        using ICursor c = ReadableDatabase!.RawQuery("SELECT id,folder_id,uri,name,path,width,height,thumbnail,size,modified,hash,profile,vector," + RatingSql + " FROM images WHERE uri=?" + (folderId.HasValue ? " AND folder_id=?" : ""), folderId.HasValue ? [uri, folderId.Value.ToString()] : [uri]);
        return c!.MoveToFirst() ? new(ReadImage(c, 13), c.GetLong(8), c.GetLong(9), c.GetString(10)!, c.GetString(11)!, EmbeddingVector.Decode(c.GetBlob(12)!)) : null;
    }

    public void Touch(long id, string scan)
    {
        using ContentValues v = new();
        v.Put("scan", scan);
        WritableDatabase!.Update("images", v, "id=?", [id.ToString()]);
    }

    public void RefreshMetadata(ImageEntry previous, Document document, string scan)
    {
        using ContentValues values = new();
        values.Put("name", document.Name);
        values.Put("path", document.RelativePath);
        values.Put("size", document.Size);
        values.Put("modified", document.Modified);
        values.Put("scan", scan);
        SQLiteDatabase db = WritableDatabase!;
        db.BeginTransaction();
        try
        {
            db.Update("images", values, "id=?", [previous.Image.Id.ToString()]);
            db.Delete("errors", "folder_id=? AND uri=?", [document.FolderId.ToString(), document.Uri]);
            db.SetTransactionSuccessful();
        }
        finally
        {
            db.EndTransaction();
        }
    }

    public HashSet<string> ReferencedThumbnails()
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT DISTINCT thumbnail FROM images", null);
        HashSet<string> paths = new(StringComparer.Ordinal);
        while (cursor.MoveToNext())
        {
            paths.Add(cursor.GetString(0)!);
        }

        return paths;
    }

    public void Save(Document doc, PreparedImage image, string hash, string profile, float[] vector, string scan)
    {
        byte[] bytes = EmbeddingVector.Encode(vector);
        using ContentValues values = new();
        values.Put("folder_id", doc.FolderId);
        values.Put("uri", doc.Uri);
        values.Put("name", doc.Name);
        values.Put("path", doc.RelativePath);
        values.Put("size", doc.Size);
        values.Put("modified", doc.Modified);
        values.Put("hash", hash);
        values.Put("profile", profile);
        values.Put("width", image.Width);
        values.Put("height", image.Height);
        values.Put("thumbnail", image.Thumbnail);
        values.Put("vector", bytes);
        values.Put("scan", scan);
        SQLiteDatabase db = WritableDatabase!;
        db.BeginTransaction();
        try
        {
            // Update rather than REPLACE so list IDs remain stable across re-indexing.
            if (db.Update("images", values, "folder_id=? AND uri=?", [doc.FolderId.ToString(), doc.Uri]) == 0)
            {
                db.InsertOrThrow("images", null, values);
            }

            db.Delete("errors", "folder_id=? AND uri=?", [doc.FolderId.ToString(), doc.Uri]);
            db.SetTransactionSuccessful();
        }
        finally
        {
            db.EndTransaction();
        }
    }

    public void Error(Document doc, string message)
    {
        using ContentValues values = new();
        values.Put("folder_id", doc.FolderId);
        values.Put("uri", doc.Uri);
        values.Put("message", message);
        WritableDatabase!.InsertWithOnConflict("errors", null, values, Conflict.Replace);
    }

    public string[] Errors()
    {
        using ICursor c = ReadableDatabase!.RawQuery("SELECT message FROM errors ORDER BY folder_id LIMIT 100", null);
        List<string> rows = new();
        while (c!.MoveToNext())
        {
            rows.Add(c.GetString(0)!);
        }

        return rows.ToArray();
    }

    public void Prune(long folder, string scan)
        => WritableDatabase!.Delete("images", "folder_id=? AND scan<>?", [folder.ToString(), scan]);
    private (string Where, string[] Args) Scope(string scope, bool filter = true)
    {
        List<string> clauses = new();
        List<string> args = new();
        if (scope.StartsWith("folder:") && long.TryParse(scope[7..], out long folder))
        {
            clauses.Add("folder_id=?");
            args.Add(folder.ToString());
        }
        else if (scope.StartsWith("group:") && long.TryParse(scope[6..], out long group))
        {
            clauses.Add("folder_id IN (SELECT id FROM folders WHERE group_id=?)");
            args.Add(group.ToString());
        }

        if (filter && Preferences.NsfwFilter == "hide")
        {
            clauses.Add(RatingSql + " < ?");
            args.Add((Preferences.NsfwThreshold / 100d).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses), args.ToArray());
    }

    public IReadOnlyList<ImageItem> Browse(string scope, int offset, int count)
    {
        (string? where, string[]? args) = Scope(scope);
        using ICursor c = ReadableDatabase!.RawQuery("SELECT id,folder_id,uri,name,path,width,height,thumbnail," + RatingSql + " FROM images" + where + " ORDER BY id DESC LIMIT ? OFFSET ?", [..args, count.ToString(), offset.ToString()]);
        List<ImageItem> rows = new();
        while (c!.MoveToNext())
        {
            rows.Add(ReadImage(c));
        }

        return rows;
    }

    public bool HasIndex(string scope)
    {
        (string? where, string[]? args) = Scope(scope, false);
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT 1 FROM images" + where + " LIMIT 1", args);
        return cursor!.MoveToFirst();
    }

    public IReadOnlyList<ImageItem> SearchNames(string query, string scope, int limit, CancellationToken ct)
    {
        (string? where, string[]? args) = Scope(scope);
        using ICursor c = ReadableDatabase!.RawQuery("SELECT id,folder_id,uri,name,path,width,height,thumbnail," + RatingSql + " FROM images" + where + (where.Length == 0 ? " WHERE" : " AND") + " instr(lower(name),lower(?))>0 ORDER BY id DESC LIMIT ?", [..args, query, limit.ToString()]);
        List<ImageItem> rows = new();
        while (c!.MoveToNext())
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(ReadImage(c));
        }

        return rows;
    }

    public IReadOnlyList<ImageItem> Search(float[] query, string scope, int limit, CancellationToken ct, string? exclude = null, Action<string, int, int>? report = null)
    {
        (string where, string[] args) = Scope(scope);
        if (exclude is not null)
        {
            where += (where.Length == 0 ? " WHERE" : " AND") + " uri<>?";
            args = [..args, exclude];
        }
        int count;
        using (ICursor cursor = ReadableDatabase!.RawQuery("SELECT COUNT(*) FROM images" + where, args))
        {
            count = cursor.MoveToFirst() ? cursor.GetInt(0) : 0;
        }
        PriorityQueue<ulong, float> best = new();
        if (count < VectorSearchIndex.Threshold)
        {
            Rank("SELECT id,vector FROM images" + where, args);
        }
        else
        {
            HashSet<ulong> allowed = new(count);
            using (ICursor cursor = ReadableDatabase!.RawQuery("SELECT id FROM images" + where, args))
            {
                while (cursor.MoveToNext())
                {
                    ct.ThrowIfCancellationRequested();
                    allowed.Add((ulong)cursor.GetLong(0));
                }
            }
            ulong[] candidates = SearchIndex.Search(query, limit, allowed, ReadSearchRevision, () => SearchVectors(ct), (revision) => SearchChanges(revision, ct), CheckpointSearch, (done) => report?.Invoke("검색 준비 중…", done, 0), ct);
            report?.Invoke("검색 중…", 0, 0);
            foreach (ulong[] chunk in candidates.Chunk(256))
            {
                Rank("SELECT id,vector FROM images WHERE id IN (" + string.Join(',', chunk.Select((_) => "?")) + ")", chunk.Select((key) => key.ToString()).ToArray());
            }
        }
        Dictionary<ulong, float> scores = best.UnorderedItems.ToDictionary((entry) => entry.Element, (entry) => entry.Priority);
        List<ImageItem> results = new(scores.Count);
        foreach (ulong[] chunk in scores.Keys.Chunk(256))
        {
            using ICursor cursor = ReadableDatabase!.RawQuery("SELECT id,folder_id,uri,name,path,width,height,thumbnail," + RatingSql + " FROM images WHERE id IN (" + string.Join(',', chunk.Select((_) => "?")) + ")", chunk.Select((key) => key.ToString()).ToArray());
            while (cursor.MoveToNext())
            {
                ct.ThrowIfCancellationRequested();
                results.Add(ReadImage(cursor) with { Score = scores[(ulong)cursor.GetLong(0)] });
            }
        }
        return results.OrderByDescending((image) => image.Score).ThenBy((image) => image.Id).ToArray();

        void Rank(string sql, string[] parameters)
        {
            using ICursor cursor = ReadableDatabase!.RawQuery(sql, parameters);
            while (cursor.MoveToNext())
            {
                ct.ThrowIfCancellationRequested();
                float[] vector = EmbeddingVector.Decode(cursor.GetBlob(1)!);
                double dot = 0;
                for (int index = 0; index < query.Length; index++) { dot += (double)query[index] * vector[index]; }
                float score = (float)Math.Clamp(dot, -1, 1);
                ulong id = (ulong)cursor.GetLong(0);
                if (best.Count < limit) { best.Enqueue(id, score); }
                else if (best.TryPeek(out _, out float minimum) && minimum < score) { best.DequeueEnqueue(id, score); }
            }
        }
    }

    private SearchRevision ReadSearchRevision()
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT database_id,revision FROM search_state WHERE id=1", null);
        if (!cursor.MoveToFirst()) { throw new InvalidDataException("검색 정보를 읽을 수 없습니다."); }
        return new(cursor.GetString(0)!, cursor.GetLong(1));
    }

    private IEnumerable<SearchVector> SearchVectors(CancellationToken ct)
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT id,vector FROM images ORDER BY id", null);
        while (cursor.MoveToNext())
        {
            ct.ThrowIfCancellationRequested();
            yield return new((ulong)cursor.GetLong(0), EmbeddingVector.Decode(cursor.GetBlob(1)!));
        }
    }

    private IEnumerable<SearchVector> SearchChanges(long revision, CancellationToken ct)
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT c.image_id,i.vector FROM search_changes c LEFT JOIN images i ON i.id=c.image_id WHERE c.revision>? ORDER BY c.image_id", [revision.ToString()]);
        while (cursor.MoveToNext())
        {
            ct.ThrowIfCancellationRequested();
            yield return new((ulong)cursor.GetLong(0), cursor.IsNull(1) ? null : EmbeddingVector.Decode(cursor.GetBlob(1)!));
        }
    }

    private void CheckpointSearch(long revision)
        => WritableDatabase!.Delete("search_changes", "revision<=?", [revision.ToString()]);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { SearchIndex.Dispose(); }
        base.Dispose(disposing);
    }

    private static ImageItem ReadImage(ICursor c, int ratingColumn = 8)
        => new(c.GetLong(0), c.GetLong(1), c.GetString(2)!, c.GetString(3)!, c.GetString(4)!, c.GetInt(5), c.GetInt(6), c.GetString(7)!, NsfwScore: c.IsNull(ratingColumn) ? null : c.GetFloat(ratingColumn));

    internal IReadOnlyList<(ImageItem Image, string Hash)> PendingRatings(long afterId, int limit)
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT id,folder_id,uri,name,path,width,height,thumbnail," + RatingSql + ",hash FROM images WHERE id>? AND " + RatingSql + " IS NULL ORDER BY id LIMIT ?", [afterId.ToString(), limit.ToString()]);
        List<(ImageItem Image, string Hash)> rows = new();
        while (cursor.MoveToNext())
        {
            rows.Add((ReadImage(cursor), cursor.GetString(9)!));
        }

        return rows;
    }

    internal int PendingRatingCount()
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT COUNT(*) FROM images WHERE " + RatingSql + " IS NULL", null);
        return cursor.MoveToFirst() ? cursor.GetInt(0) : 0;
    }

    internal void SaveRating(string hash, float score)
    {
        using ContentValues values = new();
        values.Put("hash", hash);
        values.Put("model", NsfwClassifier.Profile);
        values.Put("score", score);
        WritableDatabase!.InsertWithOnConflict("content_ratings", null, values, Conflict.Replace);
    }

    internal IEnumerable<ArchiveImage> ExportEntries(Folder folder, CancellationToken ct)
    {
        using ICursor cursor = ReadableDatabase!.RawQuery("SELECT hash,width,height,vector," + RatingSql + ",path FROM images WHERE folder_id=? ORDER BY id", [folder.Id.ToString()]);
        while (cursor.MoveToNext())
        {
            ct.ThrowIfCancellationRequested();
            yield return new(folder.Id.ToString(), DocumentLibrary.ArchivePath(folder, cursor.GetString(5)!), cursor.GetString(0)!.ToLowerInvariant(), cursor.GetInt(1), cursor.GetInt(2), cursor.GetBlob(3)!, cursor.IsNull(4) ? null : cursor.GetFloat(4));
        }
    }

    internal void MergeArchive(ArchiveManifest manifest, IReadOnlyDictionary<string, long> mappings, IReadOnlyList<(Document Document, ArchiveImage Image)> rows, string thumbnails, CancellationToken ct)
    {
        SQLiteDatabase database = WritableDatabase!;
        database.BeginTransaction();
        try
        {
            Dictionary<string, long> groups = new();
            Dictionary<string, long> existingGroups = Groups().ToDictionary((group) => group.Name, (group) => group.Id, StringComparer.Ordinal);
            foreach (ArchiveGroup group in manifest.Groups.Where((group) => manifest.Folders.Any((folder) => mappings.ContainsKey(folder.Id) && folder.GroupId == group.Id)))
            {
                if (!existingGroups.TryGetValue(group.Name, out long id))
                {
                    AddGroup(group.Name);
                    id = Groups().Single((entry) => entry.Name == group.Name).Id;
                    existingGroups[group.Name] = id;
                }

                groups[group.Id] = id;
            }

            foreach (ArchiveFolder folder in manifest.Folders.Where((folder) => mappings.ContainsKey(folder.Id)))
            {
                RenameFolder(mappings[folder.Id], folder.Name);
                GroupFolder(mappings[folder.Id], folder.GroupId is null ? null : groups[folder.GroupId]);
            }

            foreach ((Document document, ArchiveImage image) in rows)
            {
                ct.ThrowIfCancellationRequested();
                string hash = image.Hash.ToLowerInvariant();
                Save(document, new([], image.Width, image.Height, Path.Combine(thumbnails, hash + ".jpg")), hash, LibraryArchive.EmbeddingProfile, VectorData.Decode(image.Vector), "import");
                if (image.NsfwScore.HasValue)
                {
                    using ContentValues values = new();
                    values.Put("hash", hash);
                    values.Put("model", NsfwModel.PortableProfile);
                    values.Put("score", image.NsfwScore.Value);
                    database.InsertWithOnConflict("content_ratings", null, values, Conflict.Replace);
                }
            }

            ct.ThrowIfCancellationRequested();
            database.SetTransactionSuccessful();
        }
        finally
        {
            database.EndTransaction();
        }
    }
}
