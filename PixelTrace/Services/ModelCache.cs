using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PixelTrace.Mobile;

public sealed class ModelCache(string root, Preferences preferences)
{
    private string Root { get; } = root;
    private Preferences Preferences { get; } = preferences;

    public const string Revision = EmbeddingModels.Revision;
    public const string Repository = EmbeddingModels.Repository;
    public const string FileName = EmbeddingModels.FileName;
    public const string Fingerprint = EmbeddingModels.Fingerprint;
    public string Precision
        => EmbeddingModels.Label(Preferences.Precision);
    public string Profile
        => EmbeddingModels.Profile(Preferences);
    private IReadOnlyList<EmbeddingModelAsset> Assets
        => Preferences.Precision switch
    {
        "fp16" => EmbeddingModels.OnnxFp16Assets,
        "fp32" => EmbeddingModels.OnnxAssets,
        "bf16" => EmbeddingModels.LlamaAssets,
        _ => [EmbeddingModels.Default]
    };
    private string ActiveRevision
        => Preferences.Precision == "bf16" ? EmbeddingModels.LlamaRevision : EmbeddingModels.IsOnnx(Preferences.Precision) ? EmbeddingModels.OnnxRevision : Revision;
    public string DownloadSize
        => $"약 {Assets.Sum((asset) => asset.Bytes) / 1000000d:N0} MB";
    public string DirectoryPath
        => Path.Combine(Root, ActiveRevision, Assets[0].CacheKey);
    public string ModelPath
        => Path.Combine(DirectoryPath, Assets[0].Name);
    public bool Ready
        => EmbeddingModels.UnavailableReason(Preferences.Precision, Preferences.Backend)is null && Assets.All((asset) => IsComplete(Path.Combine(DirectoryPath, asset.Name), asset.Bytes));

    private static bool IsComplete(string path, long bytes)
        => File.Exists(path) && new FileInfo(path).Length == bytes;
    public async Task EnsureAsync(Action<string, int, int> report, CancellationToken ct)
    {
        EmbeddingModels.RequireSupported(Preferences.Precision, Preferences.Backend);
        if (Ready)
        {
            return;
        }

        string repository = Preferences.Precision == "bf16" ? EmbeddingModels.LlamaRepository : EmbeddingModels.IsOnnx(Preferences.Precision) ? EmbeddingModels.OnnxRepository : Repository;
        foreach (EmbeddingModelAsset asset in Assets)
        {
            await DownloadAsync(asset, repository, ActiveRevision, DirectoryPath, report, ct);
        }
    }

    internal static async Task DownloadAsync(EmbeddingModelAsset asset, string repository, string revision, string directory, Action<string, int, int> report, CancellationToken ct)
    {
        using HttpClient client = new() {
            Timeout = Timeout.InfiniteTimeSpan
        };
        ct.ThrowIfCancellationRequested();
        string target = Path.Combine(directory, asset.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && new FileInfo(target).Length == asset.Bytes)
        {
            return;
        }

        string partial = target + ".partial";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (asset.Bytes < offset)
        {
            File.Delete(partial);
            offset = 0;
        }

        if (offset != asset.Bytes)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://huggingface.co/{repository}/resolve/{revision}/{asset.Name}");
            if (0 < offset && offset < asset.Bytes)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                offset = 0;
            }
            else if (response.Content.Headers.ContentRange?.From != offset)
            {
                throw new IOException("이어받기 위치가 다릅니다.");
            }

            await using Stream input = await response.Content.ReadAsStreamAsync(ct);
            await using FileStream output = new(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            byte[] buffer = new byte[1024 * 1024];
            long last = 0L;
            int read;
            while (0 < (read = await input.ReadAsync(buffer, ct)))
            {
                if (asset.Bytes - offset < read)
                {
                    throw new IOException("서버에서 모델의 예상 크기를 초과한 데이터를 받았습니다.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                offset += read;
                if (500 < Environment.TickCount64 - last)
                {
                    report($"모델 다운로드 중 · {offset / 1048576:N0}/{asset.Bytes / 1048576:N0} MiB", (int)(offset / 1024), (int)(asset.Bytes / 1024));
                    last = Environment.TickCount64;
                }
            }
        }

        if (new FileInfo(partial).Length != asset.Bytes)
        {
            throw new IOException("모델 다운로드가 끝나지 않았습니다. 다시 실행하면 이어받습니다.");
        }

        report("다운로드한 모델 확인 중…", 0, 0);
        await using (FileStream input = File.OpenRead(partial))
        {
            string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            if (actual != asset.Hash)
            {
                File.Delete(partial);
                throw new IOException("모델 파일 검증에 실패했습니다. 다시 받아 주세요.");
            }
        }

        File.Move(partial, target, true);
    }
}
