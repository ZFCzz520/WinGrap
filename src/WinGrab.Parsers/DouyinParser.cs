using System.Text.Json;
using System.Text.RegularExpressions;
using WinGrab.Contracts;
using WinGrab.Core;
namespace WinGrab.Parsers;
public sealed class DouyinParser : IPlatformParser
{
    const string MobileAgent="Mozilla/5.0 (Linux; Android 10; Pixel 3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";
    public string Platform => "douyin";
    public bool CanParse(Uri url) => Links.Platform(url) == Platform;
    public async Task<ParsedMedia> ParseAsync(ParseContext context, CancellationToken ct)
    {
        string id = Regex.Match(context.Url.AbsolutePath, @"/(?:video|note)/(\d+)").Groups[1].Value;
        if (id.Length == 0) throw new GrabException("WG-1001", "请提供抖音单条视频或图文分享链接");
        // Desktop share pages redirect to a JS challenge. The public mobile SSR page
        // supplies the actual work data without signatures or browser credentials.
        var url = new Uri($"https://www.iesdouyin.com/share/{(context.Url.AbsolutePath.Contains("/note/")?"note":"video")}/{id}/?from_ssr=1");
        var session=new System.Net.CookieContainer();
        int contentAttempts=0;
        for(int i=0;i<5;i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent",MobileAgent);
            req.Headers.TryAddWithoutValidation("Referer","https://www.iesdouyin.com/");
            string cookie=session.GetCookieHeader(url);
            if (url.Host.Equals(context.Url.Host,StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(context.Cookie))
                cookie=context.Cookie+(cookie.Length>0?"; "+cookie:"");
            if(cookie.Length>0)req.Headers.TryAddWithoutValidation("Cookie",cookie);
            using var response = await context.Http.SendAsync(req, ct);
            if(response.Headers.TryGetValues("Set-Cookie",out var setCookies))
                foreach(var value in setCookies)
                    try{session.SetCookies(url,value);}catch(System.Net.CookieException){}
            if((int)response.StatusCode is >=300 and <400&&response.Headers.Location is {} location)
            {
                var next=location.IsAbsoluteUri?location:new Uri(url,location);
                if(next.Scheme!="https"||Links.Platform(next)!="douyin")throw Restricted();
                url=next;continue;
            }
            if(response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.TooManyRequests)
                throw Restricted();
            response.EnsureSuccessStatusCode();
            var html=await response.Content.ReadAsStringAsync(ct);
            try{return FromHtml(html,id);}
            catch(GrabException ex)when(ex.Code=="WG-1003"&&++contentAttempts<3)
            {
                // Some public pages first establish an anonymous ttwid session.
                // Keep it only for this parse; never persist it or send it to CDN hosts.
            }
        }
        throw Restricted();
    }
    static GrabException Restricted()=>new("WG-1003","抖音暂未允许访问此作品，请稍后重试，或在官方客户端确认作品仍为公开内容。");
    public static ParsedMedia FromHtml(string html, string id)
    {
        var candidates = Regex.Matches(html, "<script[^>]*id=[\"'](?:RENDER_DATA|__UNIVERSAL_DATA_FOR_REHYDRATION__)[\"'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToList();
        var router = Regex.Match(html, @"(?:window\.)?_ROUTER_DATA\s*=\s*(\{.*?\})\s*;?\s*</script>", RegexOptions.Singleline);
        if (router.Success) candidates.Add(router.Groups[1].Value);
        foreach (var candidate in candidates)
        {
            try
            {
                string json = candidate.TrimStart().StartsWith('%') ? Uri.UnescapeDataString(candidate) : candidate;
                using var doc = ParseJson(json);
                var detail = Find(doc.RootElement, id);
                if (detail.ValueKind == JsonValueKind.Undefined) continue;
                if (detail.At("is_private").Num() != 0 || detail.At("status").At("is_private").ValueKind == JsonValueKind.True || detail.At("mix_info").At("is_paid").ValueKind == JsonValueKind.True)
                    throw new GrabException("WG-1005", "私密或付费内容不在支持范围内");
                var images = detail.At("images").Arr().Select(i => i.FirstUrl()).Where(s => s.StartsWith("http")).ToList();
                var video = detail.At("video");
                // Preserve the platform's supplied playback URL; never substitute a no-watermark endpoint.
                var play = video.At("play_addr").FirstUrl(); if (play.Length == 0) play = video.At("playAddr").FirstUrl();
                var videos = play.Length == 0 ? new List<MediaTrack>() : [new MediaTrack("original", "平台原画质", play, true)];
                if (images.Count == 0 && videos.Count == 0) continue;
                return new("douyin", id, detail.At("desc").Str() is { Length: > 0 } title ? title : "抖音作品_" + id,
                    detail.At("author").At("nickname").Str(), images.FirstOrDefault() ?? video.At("cover").FirstUrl(), video.At("duration").Num() / 1000d,
                    images.Count > 0 ? MediaKind.Gallery : MediaKind.Video, videos, null, images,
                    new() { ["Referer"] = "https://www.iesdouyin.com/", ["User-Agent"] = MobileAgent });
            }
            catch (JsonException) { }
        }
        throw new GrabException("WG-1003", "抖音页面未提供可用媒体，可能有访问限制或页面已变化。请使用官方客户端缓存，或稍后重试。");
    }
    static JsonDocument ParseJson(string json)
    {
        try{return JsonDocument.Parse(json);}
        catch(JsonException){return JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(json));}
    }
    static JsonElement Find(JsonElement node, string id, int depth = 0)
    {
        if (depth > 40) return default;
        if (node.ValueKind == JsonValueKind.Object)
        {
            string nodeId = node.At("aweme_id").Str(); if (nodeId.Length == 0) nodeId = node.At("awemeId").Str();
            if (nodeId == id && (node.At("video").ValueKind == JsonValueKind.Object || node.At("images").ValueKind == JsonValueKind.Array)) return node;
            foreach (var p in node.EnumerateObject()) { var found = Find(p.Value, id, depth + 1); if (found.ValueKind != JsonValueKind.Undefined) return found; }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var n in node.EnumerateArray()) { var found = Find(n, id, depth + 1); if (found.ValueKind != JsonValueKind.Undefined) return found; }
        return default;
    }
}
