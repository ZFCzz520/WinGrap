using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using WinGrab.Contracts;
using WinGrab.Core;
namespace WinGrab.Infrastructure;

public sealed class ParserService
{
    readonly List<IPlatformParser> parsers;
    readonly HttpClient http;
    readonly Store store;
    readonly Dictionary<string, (DateTime Time, ParsedMedia Media, string CredentialStamp)> cache = [];
    public void ClearCache()=>cache.Clear();
    string CredentialStamp(string platform)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Credentials.Read(store.Root,platform)??"")));
    public ParserService(HttpClient http, Store store, IEnumerable<IPlatformParser> builtin)
    {
        this.http = http; this.store = store; parsers = builtin.ToList();
        string directory = Path.Combine(AppContext.BaseDirectory, "parsers");
        if (Directory.Exists(directory)) foreach (var dll in Directory.GetFiles(directory, "*.dll"))
        {
            try { foreach (var t in Assembly.LoadFrom(dll).GetTypes().Where(t => !t.IsAbstract && typeof(IPlatformParser).IsAssignableFrom(t)))
                if (Activator.CreateInstance(t) is IPlatformParser p && p.ContractVersion == 1) { parsers.RemoveAll(x => x.Platform == p.Platform); parsers.Add(p); } }
            catch { store.Log(null, "PLUGIN", "插件加载失败"); }
        }
    }
    public async Task<ParsedMedia> Parse(string text, CancellationToken ct, bool refresh = false)
    {
        string raw = Links.Extract(text) ?? throw new GrabException("WG-1001", "没有找到支持的链接，请粘贴分享文字或 BV / av 号");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || Links.Platform(url) is null) throw new GrabException("WG-1001", "仅支持 B站和抖音作品链接；B站分P和视频合集可解析后选集");
        string stamp=CredentialStamp(Links.Platform(url)!);
        if (!refresh && cache.TryGetValue(raw, out var hit) && hit.CredentialStamp==stamp && DateTime.Now - hit.Time < TimeSpan.FromMinutes(10)) return hit.Media;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        for (int i = 0; i < 5; i++)
        {
            if (url.Host is not ("b23.tv" or "v.douyin.com")) break;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } location)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (Links.Platform(url) is null) throw new GrabException("WG-1001", "分享链接跳转到了不支持的站点");
            }
            else break;
        }
        string platform = Links.Platform(url)!;
        if (!await Enabled(platform, timeout.Token)) throw new GrabException("WG-4001", "此平台解析器已停用，请稍后查看更新");
        var parser = parsers.FirstOrDefault(p => p.CanParse(url)) ?? throw new GrabException("WG-1001", "没有可用解析器");
        var credential=Credentials.Read(store.Root,platform);
        stamp=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential??"")));
        var media = await parser.ParseAsync(new(url, http, credential), timeout.Token);
        if (media.Videos.Count == 0 && media.Images.Count == 0) throw new GrabException("WG-1003", "未返回可下载内容");
        if (cache.Count > 100) cache.Clear(); cache[raw] = (DateTime.Now, media, stamp); return media;
    }
    async Task<bool> Enabled(string platform, CancellationToken ct)
    {
        string path = Path.Combine(store.Root, "parser-flags.json");
        if (Uri.TryCreate(store.Settings.ParserFlagsUrl, UriKind.Absolute, out var url) && url.Scheme == "https" && (!File.Exists(path) || DateTime.Now - File.GetLastWriteTime(path) > TimeSpan.FromHours(24)))
        {
            try { var json = await http.GetStringAsync(url, ct); using var validate = JsonDocument.Parse(json); File.WriteAllText(path, json); }
            catch { store.Log(null, "FLAGS", "远程平台开关更新失败，使用本地缓存"); }
        }
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            foreach (var p in doc.RootElement.GetProperty("parsers").EnumerateArray())
                if (p.GetProperty("platform").GetString() == platform) return p.GetProperty("enabled").GetBoolean();
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { }
        return true;
    }
    public static HttpClient CreateClient(Settings settings)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false };
        if (!string.IsNullOrWhiteSpace(settings.Proxy)) handler.Proxy = new WebProxy(settings.Proxy);
        return new HttpClient(new PoliteHandler(handler)) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
// Per-host request starts are spaced by >= 500ms. Download bytes are not buffered here.
sealed class PoliteHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> last = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string host = request.RequestUri!.Host; var gate = locks.GetOrAdd(host, _ => new(1));
        await gate.WaitAsync(ct);
        try { if (last.TryGetValue(host, out var t)) { var wait = TimeSpan.FromMilliseconds(500) - (DateTime.UtcNow - t); if (wait > TimeSpan.Zero) await Task.Delay(wait, ct); } last[host] = DateTime.UtcNow; }
        finally { gate.Release(); }
        return await base.SendAsync(request, ct);
    }
}
