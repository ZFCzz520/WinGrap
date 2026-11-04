namespace WinGrab.Contracts;

public enum MediaKind { Video, Gallery }
public record MediaTrack(string Id, string Label, string Url, bool HasAudio = false, string Codec = "H.264", int Height = 0);
public record ParsedMedia(string Platform, string ContentId, string Title, string Author, string? CoverUrl,
    double Duration, MediaKind Kind, List<MediaTrack> Videos, MediaTrack? Audio, List<string> Images,
    Dictionary<string, string> Headers)
{
    public List<QualityChoice> Qualities {get;init;}=[];
    public MediaCollection? Collection {get;init;}
    public long ArchiveId {get;init;}
}
public record MediaEpisode(string Id,int Index,string Title,string Url,double Duration,bool Available=true,long ArchiveId=0)
{
    public string Label=>$"{Index:000}  {Title}";
    public string DurationLabel=>TimeSpan.FromSeconds(Math.Max(0,Duration)).ToString(Duration>=3600?@"h\:mm\:ss":@"mm\:ss");
}
public record MediaCollection(string Title,string Kind,List<MediaEpisode> Episodes);
public record QualityChoice(int Quality,string Name,string? StreamId,bool RequiresLogin=false,bool RequiresMembership=false,string Codec="")
{
    public bool Available=>!string.IsNullOrWhiteSpace(StreamId);
    public string Label=>Name+(Available?(Codec.Length>0?" · "+Codec:""):RequiresMembership?" · 需登录 / 大会员":RequiresLogin?" · 需登录":" · 暂不可用");
}
public record ParseContext(Uri Url, HttpClient Http, string? Cookie);
public interface IPlatformParser
{
    string Platform { get; }
    int ContractVersion => 1;
    bool CanParse(Uri url);
    Task<ParsedMedia> ParseAsync(ParseContext context, CancellationToken ct);
}
public class GrabException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
