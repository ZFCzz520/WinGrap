using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinGrab.Contracts;
namespace WinGrab.Infrastructure;

public sealed class BandwidthLimiter(Func<int> limitMB)
{
    readonly SemaphoreSlim gate = new(1);
    long next;
    public async Task Wait(int bytes, CancellationToken ct)
    {
        int limit = limitMB(); if (limit <= 0) return;
        await gate.WaitAsync(ct);
        try
        {
            long now = Stopwatch.GetTimestamp(); long start = Math.Max(now, next);
            next = start + (long)(bytes / (limit * 1048576d) * Stopwatch.Frequency);
            double wait = (next - now) / (double)Stopwatch.Frequency;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
        }
        finally { gate.Release(); }
    }
}
public record DownloadProgress(long Done, long Total, double BytesPerSecond);
record Manifest(long Length, string Validator, int Segments);
public sealed class SegmentDownloader(HttpClient http, BandwidthLimiter limiter)
{
    public async Task Download(string url, Dictionary<string,string> headers, string work, string output, int segments, Action<DownloadProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(work); segments = Math.Clamp(segments, 1, 8);
        using var probe = await Send(url, headers, 0, 0, null, ct);
        Check(probe);
        bool ranges = probe.StatusCode == HttpStatusCode.PartialContent && probe.Content.Headers.ContentRange is { From: 0, To: 0, Length: > 0 };
        long length = ranges ? probe.Content.Headers.ContentRange!.Length!.Value : probe.Content.Headers.ContentLength ?? -1;
        string validator = probe.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : probe.Content.Headers.LastModified?.ToString("R") ?? "";
        // Without a validator, independent requests cannot prove they refer to the same resource.
        if (validator.Length == 0) ranges = false;
        if (!ranges || length < segments) segments = 1;
        var manifest = new Manifest(length, validator, segments);
        string manifestPath = Path.Combine(work, "manifest.json");
        Manifest? previous = null;
        try { previous = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, ct)); } catch (Exception e) when (e is IOException or JsonException) { }
        if (previous != manifest || validator.Length == 0 || !ranges)
            foreach (var f in Directory.GetFiles(work, "*.part")) File.Delete(f);
        Store.AtomicJson(manifestPath, manifest);
        long done = 0; var watch = Stopwatch.StartNew(); long lastReport = 0;
        void Report(int count)
        {
            Interlocked.Add(ref done, count); long ms = watch.ElapsedMilliseconds;
            long prev = Interlocked.Read(ref lastReport);
            if (ms - prev > 200 && Interlocked.CompareExchange(ref lastReport, ms, prev) == prev)
                progress(new(Interlocked.Read(ref done), length, Interlocked.Read(ref done) / Math.Max(watch.Elapsed.TotalSeconds, .1)));
        }
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Part(int index)
        {
            var token = siblings.Token;
            long start = ranges ? length * index / segments : 0;
            long end = ranges ? length * (index + 1) / segments - 1 : -1;
            string path = Path.Combine(work, index + ".part");
            long existing = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (ranges && existing > end - start + 1) { File.Delete(path); existing = 0; }
            Interlocked.Add(ref done, existing);
            if (ranges && existing == end - start + 1) return;
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                long offset = ranges && File.Exists(path) ? new FileInfo(path).Length : 0;
                try
                {
                    using var response = await Send(url, headers, ranges ? start + offset : null, ranges ? end : null, ranges ? validator : null, token);
                    Check(response);
                    if (ranges && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != start + offset || response.Content.Headers.ContentRange?.To != end || response.Content.Headers.ContentRange?.Length != length))
                        throw new GrabException("WG-2001", "服务器分片响应不一致，请重试下载");
                    if (ranges && validator.StartsWith('"') && response.Headers.ETag?.ToString() is { } current && current != validator)
                        throw new GrabException("WG-2001", "文件在下载期间变化，请重新下载");
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var file = new FileStream(path, ranges ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true);
                    byte[] buffer = new byte[65536];
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(30));
                        int n = await input.ReadAsync(buffer, idle.Token); if (n == 0) break;
                        if (ranges && file.Length + n > end - start + 1) throw new GrabException("WG-2001", "分片超出预期长度");
                        await limiter.Wait(n, token); await file.WriteAsync(buffer.AsMemory(0,n), token); Report(n);
                    }
                    if (ranges && file.Length != end - start + 1 || !ranges && length > 0 && file.Length != length) throw new HttpRequestException("下载长度不完整");
                    break;
                }
                catch (Exception e) when (!token.IsCancellationRequested && attempt < 3 && e is HttpRequestException or IOException or OperationCanceledException)
                { if (!ranges && File.Exists(path)) { Interlocked.Add(ref done, -new FileInfo(path).Length); File.Delete(path); } await Task.Delay(TimeSpan.FromSeconds(1 << attempt), token); }
            }
        }
        Exception? failure = null;
        async Task GuardedPart(int index)
        {
            try { await Part(index); }
            catch (Exception ex) { if (ex is not OperationCanceledException) Interlocked.CompareExchange(ref failure,ex,null); siblings.Cancel();throw; }
        }
        try { await Task.WhenAll(Enumerable.Range(0, segments).Select(GuardedPart)); }
        catch { if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); throw; }
        await using (var destination = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            for (int i = 0; i < segments; i++) { await using var part = File.OpenRead(Path.Combine(work, i + ".part")); await part.CopyToAsync(destination, ct); }
        progress(new(length > 0 ? length : done, length, done / Math.Max(watch.Elapsed.TotalSeconds, .1)));
    }
    async Task<HttpResponseMessage> Send(string url, Dictionary<string,string> headers, long? from, long? to, string? validator, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (int i = 0; i < 6; i++)
        {
            if (uri.Scheme is not ("http" or "https")) throw new GrabException("WG-1003", "媒体地址协议不受支持");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var h in headers.Where(h => !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) request.Headers.TryAddWithoutValidation(h.Key,h.Value);
            if (from != null) request.Headers.Range = new RangeHeaderValue(from, to);
            if (!string.IsNullOrEmpty(validator)) request.Headers.TryAddWithoutValidation("If-Range", validator);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            { uri = next.IsAbsoluteUri ? next : new Uri(uri, next); response.Dispose(); continue; }
            return response;
        }
        throw new GrabException("WG-2001", "媒体地址重定向过多");
    }
    static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Gone) throw new GrabException("WG-2003", "下载链接已失效，请重试获取新地址");
        response.EnsureSuccessStatusCode();
    }
}
