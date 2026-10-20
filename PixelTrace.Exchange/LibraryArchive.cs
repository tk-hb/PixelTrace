using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Runtime.CompilerServices;

namespace PixelTrace.Exchange;

public static class LibraryArchive
{
    public const string Format = "pixeltrace-library";
    public const string EmbeddingProfile = "portable-v1";
    public static ArchiveManifest CreateManifest(IReadOnlyList<ArchiveGroup> groups, IReadOnlyList<ArchiveFolder> folders)
        => new(Format, 3, groups, folders);

    private const int MaximumMetadata = 8 * 1024 * 1024;
    private const int MaximumRecord = 64 * 1024;

    public static async Task WriteAsync(Stream destination, ArchiveManifest manifest, IAsyncEnumerable<ArchiveImage> images, CancellationToken ct)
    {
        ValidateManifest(manifest);
        Dictionary<string, int> groups = manifest.Groups.Select((group, index) => (group.Id, index)).ToDictionary((row) => row.Id, (row) => row.index);
        Dictionary<string, int> folders = manifest.Folders.Select((folder, index) => (folder.Id, index)).ToDictionary((row) => row.Id, (row) => row.index);
        CborWriter header = new();
        header.WriteStartArray(4);
        header.WriteTextString(LibraryArchive.Format);
        header.WriteInt32(3);
        header.WriteStartArray(manifest.Groups.Count);
        foreach (ArchiveGroup group in manifest.Groups)
        {
            header.WriteTextString(group.Name);
        }
        header.WriteEndArray();
        header.WriteStartArray(manifest.Folders.Count);
        foreach (ArchiveFolder folder in manifest.Folders)
        {
            header.WriteStartArray(2);
            header.WriteTextString(folder.Name);
            if (folder.GroupId is null) { header.WriteNull(); }
            else { header.WriteInt32(groups[folder.GroupId]); }
            header.WriteEndArray();
        }
        header.WriteEndArray();
        header.WriteEndArray();
        byte[] metadata = header.Encode();
        if (MaximumMetadata < metadata.Length) { throw new InvalidDataException("폴더 정보가 너무 큽니다."); }

        using MemoryStream buffer = new(128 * 1024);
        buffer.WriteByte(0x9f);
        Append(buffer, metadata);
        await foreach (ArchiveImage image in images.WithCancellation(ct))
        {
            if (!folders.TryGetValue(image.FolderId, out int folder)) { throw new InvalidDataException("이미지의 원본 폴더 정보가 없습니다."); }
            VectorData.Validate(image.Vector);
            byte[] hash = Convert.FromHexString(image.Hash);
            if (hash.Length != 32 || image.Width <= 0 || image.Height <= 0) { throw new InvalidDataException("이미지 정보가 올바르지 않습니다."); }
            float? score = image.NsfwScore;
            ValidateScore(score);
            CborWriter record = new();
            record.WriteStartArray(7);
            record.WriteInt32(folder);
            record.WriteTextString(NormalizeRelativePath(image.RelativePath));
            record.WriteByteString(hash);
            record.WriteInt32(image.Width);
            record.WriteInt32(image.Height);
            record.WriteByteString(image.Vector);
            if (score.HasValue) { record.WriteSingle(score.Value); }
            else { record.WriteNull(); }
            record.WriteEndArray();
            byte[] encoded = record.Encode();
            if (MaximumRecord < encoded.Length) { throw new InvalidDataException("이미지의 상대 경로가 너무 깁니다."); }
            Append(buffer, encoded);
            if (128 * 1024 <= buffer.Length)
            {
                await destination.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct);
                buffer.SetLength(0);
            }
        }

        buffer.WriteByte(0xff);
        await destination.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct);
        await destination.FlushAsync(ct);
    }

    private static void Append(Stream destination, byte[] payload)
    {
        CborWriter frame = new();
        frame.WriteByteString(payload);
        destination.Write(frame.Encode());
    }

    public static async Task<ArchiveManifest> ReadManifestAsync(string path, CancellationToken ct)
    {
        await using Stream stream = Open(path);
        byte[] data = await ReadFrameAsync(stream, MaximumMetadata, ct) ?? throw new InvalidDataException("폴더 정보가 없습니다.");
        return ParseManifest(data);
    }

    public static async IAsyncEnumerable<ArchiveImage> ReadImagesAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using Stream stream = Open(path);
        byte[] metadata = await ReadFrameAsync(stream, MaximumMetadata, ct) ?? throw new InvalidDataException("폴더 정보가 없습니다.");
        ArchiveManifest manifest = ParseManifest(metadata);
        byte[]? data;
        while ((data = await ReadFrameAsync(stream, MaximumRecord, ct)) is not null)
        {
            yield return ParseImage(data, manifest.Folders.Count);
        }
    }

    private static Stream Open(string path)
    {
        Stream stream = new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true), 128 * 1024);
        try
        {
            if (4L * 1024 * 1024 * 1024 < stream.Length || stream.ReadByte() != 0x9f)
            {
                throw new InvalidDataException("지원하지 않는 PixelTrace 교환 파일입니다.");
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, int maximum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int marker = stream.ReadByte();
        if (marker == 0xff)
        {
            if (stream.ReadByte() != -1) { throw new InvalidDataException("교환 파일 끝에 알 수 없는 데이터가 있습니다."); }
            return null;
        }
        if (marker < 0) { throw new InvalidDataException("교환 파일이 끝까지 저장되지 않았습니다."); }
        if ((marker & 0xe0) != 0x40) { throw new InvalidDataException("교환 파일의 레코드 형식이 올바르지 않습니다."); }
        int size = marker & 31;
        int extra = size switch { 24 => 1, 25 => 2, 26 => 4, < 24 => 0, _ => throw new InvalidDataException("레코드 길이가 올바르지 않습니다.") };
        uint length = (uint)size;
        if (0 < extra)
        {
            byte[] encoded = new byte[extra];
            await stream.ReadExactlyAsync(encoded, ct);
            length = extra switch { 1 => encoded[0], 2 => BinaryPrimitives.ReadUInt16BigEndian(encoded), _ => BinaryPrimitives.ReadUInt32BigEndian(encoded) };
        }
        if (length == 0 || maximum < length) { throw new InvalidDataException("교환 파일의 레코드 크기가 올바르지 않습니다."); }
        byte[] data = new byte[(int)length];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    private static ArchiveManifest ParseManifest(byte[] data)
    {
        try
        {
            CborReader reader = new(data);
            RequireArray(reader, 4);
            if (reader.ReadTextString() != LibraryArchive.Format || reader.ReadInt32() != 3) { throw new InvalidDataException("상대 경로를 포함한 교환 파일이 필요합니다. 원본 앱에서 다시 내보내 주세요."); }
            int groupCount = reader.ReadStartArray() ?? throw new InvalidDataException("그룹 목록이 올바르지 않습니다.");
            List<ArchiveGroup> groups = new();
            for (int index = 0; index < groupCount; index++)
            {
                groups.Add(new(index.ToString(), reader.ReadTextString()));
            }
            reader.ReadEndArray();
            int folderCount = reader.ReadStartArray() ?? throw new InvalidDataException("폴더 목록이 올바르지 않습니다.");
            List<ArchiveFolder> folders = new();
            for (int index = 0; index < folderCount; index++)
            {
                RequireArray(reader, 2);
                string name = reader.ReadTextString();
                string? group = null;
                if (reader.PeekState() == CborReaderState.Null) { reader.ReadNull(); }
                else { group = reader.ReadInt32().ToString(); }
                reader.ReadEndArray();
                folders.Add(new(index.ToString(), name, group));
            }
            reader.ReadEndArray();
            reader.ReadEndArray();
            RequireEnd(reader);
            ArchiveManifest manifest = LibraryArchive.CreateManifest(groups, folders);
            ValidateManifest(manifest);
            return manifest;
        }
        catch (CborContentException error)
        {
            throw new InvalidDataException("교환 파일의 폴더 정보가 손상되었습니다.", error);
        }
    }

    private static ArchiveImage ParseImage(byte[] data, int folders)
    {
        try
        {
            CborReader reader = new(data);
            RequireArray(reader, 7);
            int folder = reader.ReadInt32();
            string relativePath = NormalizeRelativePath(reader.ReadTextString());
            byte[] hash = reader.ReadByteString();
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            byte[] vector = reader.ReadByteString();
            float? score = null;
            if (reader.PeekState() == CborReaderState.Null) { reader.ReadNull(); }
            else { score = reader.ReadSingle(); }
            reader.ReadEndArray();
            RequireEnd(reader);
            if (folder < 0 || folders <= folder || hash.Length != 32 || width <= 0 || height <= 0) { throw new InvalidDataException("이미지 정보가 올바르지 않습니다."); }
            VectorData.Validate(vector);
            ValidateScore(score);
            return new(folder.ToString(), relativePath, Convert.ToHexStringLower(hash), width, height, vector, score);
        }
        catch (CborContentException error)
        {
            throw new InvalidDataException("교환 파일의 이미지 정보가 손상되었습니다.", error);
        }
    }

    public static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/'))
        {
            throw new InvalidDataException("교환 파일에 절대 경로가 있습니다. 원본 앱에서 다시 내보내 주세요.");
        }
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Where((part) => part != ".").ToArray();
        if (parts.Length == 0 || parts.Any((part) => part == ".." || part.Contains('\0')))
        {
            throw new InvalidDataException("교환 파일의 이미지 경로가 비어 있거나 원본 폴더를 벗어납니다.");
        }
        return string.Join('/', parts);
    }

    private static void RequireArray(CborReader reader, int length)
    {
        if (reader.ReadStartArray() != length) { throw new InvalidDataException("교환 파일의 필드 구성이 올바르지 않습니다."); }
    }

    private static void RequireEnd(CborReader reader)
    {
        if (reader.BytesRemaining != 0) { throw new InvalidDataException("교환 파일에 알 수 없는 필드가 있습니다."); }
    }

    private static void ValidateScore(float? score)
    {
        if (score.HasValue && (!float.IsFinite(score.Value) || score.Value is < 0 or > 1)) { throw new InvalidDataException("NSFW 점수가 올바르지 않습니다."); }
    }

    private static void ValidateManifest(ArchiveManifest manifest)
    {
        HashSet<string> groups = new(StringComparer.Ordinal), names = new(StringComparer.Ordinal);
        foreach (ArchiveGroup group in manifest.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || !groups.Add(group.Id) || string.IsNullOrWhiteSpace(group.Name) || 200 < group.Name.Length || !names.Add(group.Name)) { throw new InvalidDataException("그룹 정보가 올바르지 않습니다."); }
        }
        HashSet<string> folders = new(StringComparer.Ordinal);
        foreach (ArchiveFolder folder in manifest.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Id) || !folders.Add(folder.Id) || string.IsNullOrWhiteSpace(folder.Name) || 1024 < folder.Name.Length || (folder.GroupId is not null && !groups.Contains(folder.GroupId))) { throw new InvalidDataException("폴더 정보가 올바르지 않습니다."); }
        }
    }
}
