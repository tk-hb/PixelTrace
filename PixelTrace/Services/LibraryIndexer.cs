using Android.Content;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace PixelTrace.Mobile;

internal sealed class LibraryIndexer(Context context, Catalog catalog, Preferences preferences, string data, ImagePreparation preprocessor, GemmaInference model, Func<Action<string, int, int>, CancellationToken, Task> prepareModel)
{
    private Context Context { get; } = context;
    private Catalog Catalog { get; } = catalog;
    private Preferences Preferences { get; } = preferences;
    private string WorkDirectory { get; } = Path.Combine(data, "index-work");
    private DocumentLibrary Documents { get; } = new(context.ContentResolver!);
    private MediaLibrary Media { get; } = new(context);
    private ImagePreparation Preprocessor { get; } = preprocessor;
    private GemmaInference Model { get; } = model;
    private Func<Action<string, int, int>, CancellationToken, Task> PrepareModel { get; } = prepareModel;

    private sealed record IndexItem(Document Document, ImageEntry? Previous, string Hash, PreparedImage? Image, string? ContentFile = null, string? Error = null);
    public async Task RunAsync(string profile, Action<string, int, int> report, CancellationToken ct, IReadOnlyCollection<long>? folderIds = null)
    {
        IReadOnlyList<Folder> folders = Catalog.Folders().Where((folder) => folderIds is null || folderIds.Contains(folder.Id)).ToArray();
        if (folders.Count == 0)
        {
            throw new InvalidOperationException("먼저 사진 폴더를 추가해 주세요.");
        }

        await PrepareModel(report, ct);
        Directory.CreateDirectory(WorkDirectory);
        IndexMemoryBudget memory = new(Context, Preferences.PreparationConcurrency);
        int requestedBatch = Preferences.BatchSize;
        int done = 0, reused = 0;
        foreach (Folder folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            string scan = Guid.NewGuid().ToString("N");
            report($"{folder.Name} · 사진을 찾고 있습니다…", done, 0);
            using CancellationTokenSource pipeline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Channel<IndexItem> queue = Channel.CreateBounded<IndexItem>(new BoundedChannelOptions(memory.PreparationConcurrency) { SingleReader = true });
            Task prepare = ProduceAsync(folder, profile, queue.Writer, memory.PreparationConcurrency, pipeline.Token);
            List<IndexItem> batch = new();
            long inputBytes = 0;
            void FlushBatch()
            {
                if (batch.Count == 0)
                {
                    return;
                }

                ct.ThrowIfCancellationRequested();
                global::Android.Util.Log.Info(AppIdentity.LogTag, $"색인 · 전달 {batch.Count}/{requestedBatch}장 · 준비 동시성 {memory.PreparationConcurrency}/{Preferences.PreparationConcurrency}");
                report($"{folder.Name} · 사진 분석 중 · {done:N0}개 완료", done, 0);
                PreparedImage[] images = batch.Select((item) => item.Image!with { Content = File.ReadAllBytes(item.ContentFile!) }).ToArray();
                float[][] vectors = Model.EncodeImages(images, ct);
                for (int i = 0; i < batch.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    IndexItem item = batch[i];
                    Catalog.Save(item.Document, item.Image!, item.Hash, profile, vectors[i], scan);
                    File.Delete(item.ContentFile!);
                    done++;
                }

                batch.Clear();
                inputBytes = 0;
                report($"{folder.Name} · 처리 {done:N0} · 재사용 {reused:N0}", done, 0);
            }

            try
            {
                await foreach (IndexItem item in queue.Reader.ReadAllAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.Error is not null)
                    {
                        if (item.Previous is not null)
                        {
                            Catalog.Touch(item.Previous.Image.Id, scan);
                        }

                        Catalog.Error(item.Document, item.Error);
                        continue;
                    }

                    if (item.Image is null)
                    {
                        Catalog.RefreshMetadata(item.Previous!, item.Document, scan);
                        reused++;
                        done++;
                        report($"{folder.Name} · 처리 {done:N0} · 재사용 {reused:N0}", done, 0);
                        continue;
                    }

                    long bytes = new FileInfo(item.ContentFile!).Length;
                    if (batch.Count != 0 && memory.InputBytes < inputBytes + bytes)
                    {
                        FlushBatch();
                    }

                    batch.Add(item);
                    inputBytes += bytes;
                    if (requestedBatch <= batch.Count || memory.InputBytes <= inputBytes)
                    {
                        FlushBatch();
                    }
                }

                FlushBatch();
            }
            finally
            {
                pipeline.Cancel();
                // Await all writers before AppState can collect staged inputs.
                await prepare;
            }

            ct.ThrowIfCancellationRequested();
            Catalog.Prune(folder.Id, scan);
        }

        report($"색인 완료 · {done:N0}개", done, done);
    }

    private async Task ProduceAsync(Folder folder, string profile, ChannelWriter<IndexItem> writer, int concurrency, CancellationToken ct)
    {
        try
        {
            IEnumerable<Document> source = MediaLibrary.IsMediaFolder(folder.Uri) ? Media.Scan(folder, ct) : Documents.Scan(folder, ct);
            await Parallel.ForEachAsync(source, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct }, async (document, token) => {
                IndexItem item = await PrepareAsync(document, profile, token);
                await writer.WriteAsync(item, token);
            });
            writer.TryComplete();
        }
        catch (Exception error)
        {
            // Preserve producer failures for the consumer; cancellation isn't a file error.
            writer.TryComplete(error);
        }
    }

    private async Task<IndexItem> PrepareAsync(Document document, string profile, CancellationToken ct)
    {
        ImageEntry? previous = Catalog.Find(document.Uri, document.FolderId);
        // Preview availability does not affect the validity of a stored embedding.
        // Missing previews are rebuilt by ThumbnailCache when displayed.
        if (previous is not null && EmbeddingModels.IsCompatibleProfile(previous.Profile, profile) && 0 < document.Modified && previous.Modified == document.Modified && previous.Size == document.Size)
        {
            return new(document, previous, previous.Hash, null);
        }

        string temporary = Path.Combine(WorkDirectory, Guid.NewGuid().ToString("N") + ".source");
        string content = Path.Combine(WorkDirectory, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await Documents.CopyAsync(document.Uri, temporary, ct);
            string hash;
            await using (FileStream input = File.OpenRead(temporary))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            }

            if (previous is not null && previous.Hash == hash && EmbeddingModels.IsCompatibleProfile(previous.Profile, profile))
            {
                return new(document, previous, hash, null);
            }

            PreparedImage image = Preprocessor.PrepareToFile(temporary, hash, content, ct);
            return new(document, previous, hash, image, content);
        }
        catch (Exception error)when (error is InvalidDataException or IOException or UnauthorizedAccessException or Java.IO.IOException or Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            ct.ThrowIfCancellationRequested();
            return new(document, previous, "", null, Error: error.Message);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
