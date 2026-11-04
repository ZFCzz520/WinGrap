using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WinGrab.Contracts;

namespace WinGrab.Core;

public static partial class Links
{
    public static string? Extract(string text)
    {
        var match = Regex.Match(text ?? "", "https?://[^\\s\\u4e00-\\u9fff，。！？、；：\"'（）【】<>]+", RegexOptions.IgnoreCase);
        if (match.Success) return match.Value.TrimEnd('.', ',', ')', ']', ';');
        var id = Regex.Match(text ?? "", @"\b(BV[0-9A-Za-z]{10}|av\d+)\b", RegexOptions.IgnoreCase);
        return id.Success ? "https://www.bilibili.com/video/" + id.Value : null;
    }
    public static bool Host(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    public static string? Platform(Uri url) => url.Scheme is not ("http" or "https") ? null :
        Host(url.Host, "bilibili.com") || url.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase) ? "bilibili" :
        Host(url.Host, "douyin.com") || Host(url.Host, "iesdouyin.com") ? "douyin" : null;
    public static string Query(Uri uri, string key) => uri.Query.TrimStart('?').Split('&').Select(s => s.Split('=', 2)).FirstOrDefault(s => s[0] == key)?.ElementAtOrDefault(1) ?? "";
}
public static class Names
{
    public static string Safe(string name)
    {
        var s = Regex.Replace(name, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '.');
        if (s.Length == 0) s = "untitled";
        if (Regex.IsMatch(s.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) s = "_" + s;
        if (s.Length > 120) s = s[..120].TrimEnd(' ', '.');
        return s;
    }
    public static string Render(string template, ParsedMedia media, string quality) => Safe(template.Replace("{title}", media.Title).Replace("{author}", media.Author)
        .Replace("{platform}", media.Platform).Replace("{quality}", quality).Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd")));
    public static string Reserve(string directory, string name, string extension)
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < 10000; i++)
        {
            var path = Path.Combine(directory, name + (i == 0 ? "" : $" ({i})") + extension);
            if (path.Length > 248) throw new GrabException("WG-2004", "保存路径过长，请选择更短的目录");
            try { using var f = new FileStream(path, FileMode.CreateNew); return path; }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("同名文件过多");
    }
}
public enum JobState { New, Queued, Downloading, Paused, Merging, Completed, Failed, Cancelled }
public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public class Job : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public ParsedMedia? Media { get; set; }
    public int TrackIndex { get; set; }
    public string TrackId {get;set;}="";
    public string Format { get; set; } = "mp4";
    public string OutputPath { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public JobState State { get; set; }
    public string Error { get; set; } = "";
    public string Title => Media?.Title ?? Url;
    public string PlatformLabel => Media?.Platform == "douyin" ? "抖音 · 实验性" : "哔哩哔哩";
    public string OutputLabel=>Format switch{"cover"=>"仅封面","m4a"=>"仅音频 M4A","mp3"=>"仅音频 MP3","zip"=>"图集 ZIP",_=>"视频 MP4"};
    public string StateLabel => State switch { JobState.New => "待添加", JobState.Queued => "等待中", JobState.Downloading => "下载中", JobState.Paused => "已暂停", JobState.Merging => "正在合并", JobState.Completed => "已完成", JobState.Failed => "下载失败", _ => "已取消" };
    public double Percent { get; private set; }
    public string Detail { get; private set; } = "等待下载";
    public void Report(double percent, string detail) { Percent = Math.Clamp(percent, 0, 100); Detail = detail; Changed(nameof(Percent)); Changed(nameof(Detail)); }
    public void Transition(JobState target)
    {
        bool allowed = State switch {
            JobState.New => target == JobState.Queued,
            JobState.Queued => target is JobState.Downloading or JobState.Paused or JobState.Cancelled or JobState.Failed,
            JobState.Downloading => target is JobState.Merging or JobState.Paused or JobState.Completed or JobState.Failed or JobState.Cancelled,
            JobState.Merging => target is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.Paused,
            JobState.Paused or JobState.Failed => target is JobState.Queued or JobState.Cancelled,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Invalid transition: {State} -> {target}");
        State = target; Changed(nameof(State)); Changed(nameof(StateLabel)); Changed(nameof(Error));
    }
}
public class Settings
{
    public string DownloadRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinGrab");
    public string NameTemplate { get; set; } = "{date}_{platform}_{title}_{quality}";
    public int MaxConcurrentTasks { get; set; } = 2;
    public int SegmentsPerTask { get; set; } = 4;
    public int SpeedLimitMB { get; set; }
    public bool AutoClipboard { get; set; } = true;
    public bool AcceptedTerms { get; set; }
    public string FfmpegPath { get; set; } = "";
    public string Proxy { get; set; } = "";
    public string ParserFlagsUrl { get; set; } = "";
}
