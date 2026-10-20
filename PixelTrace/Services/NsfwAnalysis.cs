using Android.Content;
using System.Security.Cryptography;

namespace PixelTrace.Mobile;

internal sealed class NsfwAnalysis(Context context, Catalog catalog, Preferences preferences, string data)
{
    private Catalog Catalog { get; } = catalog;
    private Preferences Preferences { get; } = preferences;
    private string Data { get; } = data;
    private DocumentLibrary Documents { get; } = new(context.ContentResolver!);

    public async Task AnalyzePendingAsync(Action<string, int, int> report, CancellationToken ct)
    {
        if (Preferences.NsfwFilter == "off") { return; }
        int concurrency = Preferences.NsfwConcurrency;
        int total = Catalog.PendingRatingCount();
        if (total == 0) { return; }

        using NsfwClassifier classifier = await NsfwClassifier.LoadAsync(Data, report, ct);
        string temporaryPrefix = Path.Combine(Data, "nsfw-" + Guid.NewGuid().ToString("N") + "-");
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = concurrency, CancellationToken = ct };
        object completion = new();
        long after = 0, reported = Environment.TickCount64;
        int done = 0, failed = 0;
        report("NSFW 판별 중…", 0, total);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<(ImageItem Image, string Hash)> rows = Catalog.PendingRatings(after, Math.Max(64, concurrency));
            if (rows.Count == 0) { break; }

            await Parallel.ForEachAsync(rows, parallel, async (row, token) => {
                (ImageItem image, string hash) = row;
                string temporary = temporaryPrefix + image.Id + ".tmp";
                float? score = null;
                try
                {
                    await Documents.CopyAsync(image.Uri, temporary, token);
                    await using (FileStream input = File.OpenRead(temporary))
                    {
                        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
                        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("색인 이후 사진 내용이 변경되었습니다.");
                        }
                    }

                    score = classifier.Score(temporary, token);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Java.IO.IOException or Java.Lang.SecurityException)
                {
                    global::Android.Util.Log.Warn(AppIdentity.LogTag, "NSFW 이미지 판별 실패: " + error.Message);
                }
                finally
                {
                    File.Delete(temporary);
                }

                token.ThrowIfCancellationRequested();
                lock (completion)
                {
                    if (score.HasValue)
                    {
                        Catalog.SaveRating(hash, score.Value);
                    }
                    else
                    {
                        failed++;
                    }

                    done++;
                    if (250 <= Environment.TickCount64 - reported || done == total)
                    {
                        report($"NSFW 판별 중 · {done:N0}/{total:N0}개", done, total);
                        reported = Environment.TickCount64;
                    }
                }
            });
            after = rows[^1].Image.Id;
        }

        report(failed == 0 ? $"NSFW 판별 완료 · {done:N0}개" : $"NSFW 판별 완료 · {done:N0}개 · 미판별 {failed:N0}개", done, done);
    }
}
