using System.Text.Json;
using System.Text.RegularExpressions;
using WinGrab.Contracts;
using WinGrab.Core;

namespace WinGrab.Parsers;
public sealed class BilibiliParser : IPlatformParser
{
    public string Platform => "bilibili";
    public bool CanParse(Uri url) => Links.Platform(url) == Platform;
    internal static Dictionary<string,string> Headers => new() { ["Referer"] = "https://www.bilibili.com/", ["User-Agent"] = "Mozilla/5.0 WinGrab/1.0" };
    async Task<JsonElement> Get(ParseContext c, string endpoint, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/" + endpoint);
        foreach (var h in Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (!string.IsNullOrWhiteSpace(c.Cookie)) request.Headers.TryAddWithoutValidation("Cookie", c.Cookie);
        using var response = await c.Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.At("code").Num() != 0)
        {
            long code = json.RootElement.At("code").Num();
            throw new GrabException(code is -101 or -403 ? "WG-1004" : "WG-1005", code is -101 or -403 ? "需要有效登录状态，或平台限制了访问" : "内容不存在或不在支持范围内");
        }
        return json.RootElement.At("data").Clone();
    }
    public async Task<ParsedMedia> ParseAsync(ParseContext c, CancellationToken ct)
    {
        if (c.Url.Host == "t.bilibili.com" || c.Url.AbsolutePath.Contains("/opus/")) return await Dynamic(c, ct);
        var id = Regex.Match(c.Url.AbsolutePath, @"(?:/video/)?(BV[0-9A-Za-z]{10}|av\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
        if (id.Length == 0) throw new GrabException("WG-1001", "请粘贴 B站视频或合集内的视频链接，解析后可选集；也支持动态图文");
        string query = id.StartsWith("av", StringComparison.OrdinalIgnoreCase) ? "aid=" + id[2..] : "bvid=" + id;
        var data = await Get(c, "x/web-interface/view?" + query, ct);
        if (data.At("rights").At("pay").Num() != 0 || data.At("is_upower_exclusive").ValueKind == JsonValueKind.True)
            throw new GrabException("WG-1005", "付费或专享内容不在支持范围内");
        var pages = data.At("pages").Arr();
        string canonicalId=data.At("bvid").Str();if(canonicalId.Length==0)canonicalId=id;
        int page = int.TryParse(Links.Query(c.Url, "p"), out int p) ? p : 1;
        if (page < 1 || page > pages.Length) throw new GrabException("WG-1001", "分P不存在，请检查链接中的 p 参数");
        var selected = pages[page - 1];
        var playback = await Get(c, $"x/player/playurl?{query}&cid={selected.At("cid").Num()}&qn=127&fnver=0&fnval=4048&fourk=1", ct);
        var videos = new List<MediaTrack>();
        foreach (var v in playback.At("dash").At("video").Arr())
        {
            int qn = (int)v.At("id").Num();
            string codec = v.At("codecid").Num() switch { 12 => "HEVC", 13 => "AV1", _ => "H.264" };
            string url = v.At("baseUrl").Str(); if (url.Length == 0) url = v.At("base_url").Str();
            if (url.Length > 0) videos.Add(new(qn + "-" + codec, Quality(qn) + " · " + codec, url, false, codec, (int)v.At("height").Num()));
        }
        var audioNode = playback.At("dash").At("audio").Arr().OrderByDescending(a => a.At("bandwidth").Num()).FirstOrDefault();
        string audioUrl = audioNode.At("baseUrl").Str(); if (audioUrl.Length == 0) audioUrl = audioNode.At("base_url").Str();
        MediaTrack? audio = audioUrl.Length > 0 ? new("audio", "音频", audioUrl) : null;
        if (videos.Count == 0)
        {
            var durl = playback.At("durl").Arr();
            if (durl.Length == 1 && durl[0].At("url").Str().Length > 0) videos.Add(new(playback.At("quality").Num()+"-progressive", Quality((int)playback.At("quality").Num()), durl[0].At("url").Str(), true));
        }
        if (videos.Count == 0) throw new GrabException("WG-1004", "未返回可用视频，请检查登录状态或稍后重试");
        return new(Platform, canonicalId + "-p" + page, data.At("title").Str() + (pages.Length > 1 ? " · P" + page + " " + selected.At("part").Str() : ""), data.At("owner").At("name").Str(),
            data.At("pic").Str(), selected.At("duration").Num(), MediaKind.Video, videos.OrderByDescending(v => v.Height).ToList(), audio, [], Headers)
            {Qualities=BuildQualities(playback,videos),Collection=BuildCollection(data,canonicalId,pages),ArchiveId=data.At("aid").Num()};
    }
    static MediaCollection? BuildCollection(JsonElement data,string id,JsonElement[] pages)
    {
        if(pages.Length>1)
            return new(data.At("title").Str(),"分P",pages.Select((p,i)=>new MediaEpisode(id+"-p"+(i+1),i+1,p.At("part").Str(),$"https://www.bilibili.com/video/{id}?p={i+1}",p.At("duration").Num(),true,data.At("aid").Num())).ToList());
        var season=data.At("ugc_season");var episodes=new List<MediaEpisode>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var section in season.At("sections").Arr())foreach(var episode in section.At("episodes").Arr())
        {
            var archive=episode.At("arc");string bvid=episode.At("bvid").Str();if(bvid.Length==0)bvid=archive.At("bvid").Str();
            long aid=episode.At("aid").Num();if(aid<=0)aid=archive.At("aid").Num();
            if(!Regex.IsMatch(bvid,@"^BV[0-9A-Za-z]{10}$"))
            {if(aid<=0)continue;bvid=aid==data.At("aid").Num()?id:"av"+aid;}
            if(!seen.Add(bvid))continue;
            string title=episode.At("title").Str();if(title.Length==0)title=archive.At("title").Str();
            double duration=archive.At("duration").Num();if(duration==0)duration=episode.At("duration").Num();
            bool allowed=archive.At("rights").At("pay").Num()==0&&archive.At("is_upower_exclusive").ValueKind!=JsonValueKind.True;
            episodes.Add(new(bvid+"-p1",episodes.Count+1,title,$"https://www.bilibili.com/video/{bvid}?p=1",duration,allowed,aid));
        }
        return episodes.Count>1?new(season.At("title").Str(),"合集",episodes):null;
    }
    static List<QualityChoice> BuildQualities(JsonElement playback,List<MediaTrack> videos)
    {
        var formats=playback.At("support_formats").Arr();
        var ids=playback.At("accept_quality").Arr().Select(q=>(int)q.Num()).Concat(formats.Select(f=>(int)f.At("quality").Num()))
            .Concat(videos.Select(v=>int.TryParse(v.Id.Split('-')[0],out int n)?n:(int)playback.At("quality").Num())).Where(n=>n>0).Distinct().OrderByDescending(n=>n);
        var result=new List<QualityChoice>();
        foreach(int qn in ids)
        {
            var format=formats.FirstOrDefault(f=>f.At("quality").Num()==qn);
            var track=videos.Where(v=>v.Id.StartsWith(qn+"-",StringComparison.Ordinal)||(v.Id=="progressive"&&playback.At("quality").Num()==qn)).OrderBy(v=>v.Codec=="H.264"?0:v.Codec=="HEVC"?1:2).FirstOrDefault();
            string name=Quality(qn);if(name=="原始清晰度")name=format.At("new_description").Str();if(name.Length==0)name="清晰度 "+qn;
            bool vip=format.At("need_vip").ValueKind==JsonValueKind.True||format.At("need_vip").Num()==1||qn>80||qn==74;
            bool login=format.At("need_login").ValueKind==JsonValueKind.True||format.At("need_login").Num()==1||qn>=64;
            result.Add(new(qn,name,track?.Id,track==null&&login,track==null&&vip,track?.Codec??""));
        }
        return result;
    }
    async Task<ParsedMedia> Dynamic(ParseContext c, CancellationToken ct)
    {
        string id = Regex.Match(c.Url.AbsolutePath, @"\d+").Value;
        if (id.Length == 0) throw new GrabException("WG-1001", "动态链接缺少 ID");
        var data = await Get(c, "x/polymer/web-dynamic/v1/detail?id=" + id, ct);
        var item = data.At("item"); var modules = item.At("modules");
        var dynamic = modules.At("module_dynamic"); var major = dynamic.At("major");
        var archive = major.At("archive");
        if (archive.At("bvid").Str().Length > 0) return await ParseAsync(c with { Url = new Uri("https://www.bilibili.com/video/" + archive.At("bvid").Str()) }, ct);
        var images = major.At("draw").At("items").Arr().Select(i => i.At("src").Str()).Where(s => s.StartsWith("http")).ToList();
        if (images.Count == 0) throw new GrabException("WG-1003", "动态未返回公开图片，可能需要登录或平台页面已变化");
        var title = dynamic.At("desc").At("text").Str();
        return new(Platform, id, string.IsNullOrWhiteSpace(title) ? "动态图集_" + id : title, modules.At("module_author").At("name").Str(), images[0], 0, MediaKind.Gallery, [], null, images, Headers);
    }
    static string Quality(int qn) => qn switch {127=>"8K",126=>"杜比视界",125=>"HDR",120=>"4K",116=>"1080P60",112=>"1080P+",100=>"智能修复",80=>"1080P",74=>"720P60",64=>"720P",32=>"480P",16=>"360P",_=>"原始清晰度"};
}
