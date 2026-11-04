using System.Diagnostics;
using System.IO.Compression;
using WinGrab.Contracts;
using WinGrab.Core;
namespace WinGrab.Infrastructure;
public sealed class MediaWriter(SegmentDownloader downloader, Settings settings)
{
    public static string? FindFfmpeg(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "ffmpeg.exe"))))
            if (File.Exists(path)) return path;
        return null;
    }
    public async Task<string> Write(Job job, string cache, Action<double,string> report, Action merging, CancellationToken ct)
    {
        var media = job.Media ?? throw new InvalidOperationException();
        string work = Path.Combine(cache, job.Id.ToString("N")); Directory.CreateDirectory(work);
        bool coverOnly=job.Format=="cover",audioOnly=job.Format is "m4a" or "mp3";
        MediaTrack? track=null;
        if(!coverOnly&&media.Kind!=MediaKind.Gallery&&(!audioOnly||media.Audio==null))
        {
            track=job.TrackId.Length>0?media.Videos.FirstOrDefault(v=>v.Id==job.TrackId):media.Videos.ElementAtOrDefault(Math.Max(0,job.TrackIndex));
            if(track==null)throw new GrabException("WG-1004","所选清晰度当前不可用，请登录后重新解析；不会自动降为其他画质");
        }
        string quality = coverOnly?"封面":audioOnly?"音频":media.Kind==MediaKind.Gallery?"图集":track!.Label;
        string name = Names.Render(settings.NameTemplate, media, quality);
        string result = Path.Combine(work, "result." + (media.Kind == MediaKind.Gallery ? "zip" : job.Format));
        string extension=media.Kind==MediaKind.Gallery?".zip":"."+job.Format;
        void Progress(DownloadProgress p) => report(p.Total > 0 ? p.Done * 90d / p.Total : 0, $"{p.Done / 1048576d:F1} MB / {(p.Total > 0 ? (p.Total / 1048576d).ToString("F1") : "?")} MB  ·  {p.BytesPerSecond / 1048576d:F1} MB/s");
        if(coverOnly)
        {
            if(string.IsNullOrWhiteSpace(media.CoverUrl))throw new GrabException("WG-1003","当前作品未提供封面图片");
            result=Path.Combine(work,"cover.bin");
            await downloader.Download(media.CoverUrl,media.Headers,Path.Combine(work,"cover"),result,1,Progress,ct);
            byte[] header=new byte[32];using(var image=File.OpenRead(result))image.Read(header);
            extension=header.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})?".png":header[0]==255&&header[1]==216?".jpg":
                System.Text.Encoding.ASCII.GetString(header,0,4)=="RIFF"&&System.Text.Encoding.ASCII.GetString(header,8,4)=="WEBP"?".webp":
                System.Text.Encoding.ASCII.GetString(header,0,3)=="GIF"?".gif":System.Text.Encoding.ASCII.GetString(header,8,4) is "avif" or "avis"?".avif":
                throw new GrabException("WG-1003","服务器未返回可识别的封面图片，请重新解析后重试");
        }
        else if (media.Kind == MediaKind.Gallery)
        {
            string images = Path.Combine(work,"images"); Directory.CreateDirectory(images);
            for (int i = 0; i < media.Images.Count; i++)
            {
                string ext = Path.GetExtension(new Uri(media.Images[i]).AbsolutePath).ToLowerInvariant(); if (ext is not (".jpg" or ".png" or ".webp" or ".jpeg" or ".avif")) ext = ".jpg";
                await downloader.Download(media.Images[i], media.Headers, Path.Combine(work,"img-"+i), Path.Combine(images,$"{i+1:D3}"+ext), 1, _ => { }, ct);
                report((i+1)*90d/media.Images.Count,$"图片 {i+1} / {media.Images.Count}");
            }
            ct.ThrowIfCancellationRequested(); if (File.Exists(result)) File.Delete(result); ZipFile.CreateFromDirectory(images,result);
        }
        else
        {
            string? ffmpeg = FindFfmpeg(settings.FfmpegPath);
            bool needsFfmpeg = audioOnly || !track!.HasAudio;
            if (needsFfmpeg && ffmpeg == null) throw new GrabException("WG-3002", "缺少 FFmpeg，请在设置中选择 ffmpeg.exe");
            string video = Path.Combine(work,"video.bin"), audio = Path.Combine(work,"audio.bin");
            if(!audioOnly||media.Audio==null)await downloader.Download(track!.Url,media.Headers,Path.Combine(work,"video"),video,settings.SegmentsPerTask,Progress,ct);
            if(media.Audio!=null&&(audioOnly||!track!.HasAudio))await downloader.Download(media.Audio.Url,media.Headers,Path.Combine(work,"audio"),audio,settings.SegmentsPerTask,Progress,ct);
            if (job.Format == "mp4" && track!.HasAudio) File.Copy(video,result,true);
            else
            {
                if (job.Format == "mp4" && !track!.HasAudio && media.Audio is null) throw new GrabException("WG-3001", "没有可用音轨，无法生成带声音的视频");
                merging(); report(95,"正在处理音视频，请稍候");
                string input = job.Format != "mp4" && media.Audio != null ? audio : video;
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", input };
                if (job.Format == "mp4" && media.Audio != null) args.AddRange(["-i",audio,"-map","0:v:0","-map","1:a:0","-c","copy","-movflags","+faststart"]);
                else args.AddRange(job.Format == "mp3" ? ["-vn","-c:a","libmp3lame","-q:a","2"] : ["-vn","-c:a","aac","-b:a","192k"]);
                args.Add(result); await Ffmpeg(ffmpeg!,args,ct);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (new FileInfo(result).Length == 0) throw new GrabException("WG-3001","输出文件为空");
        string output = Names.Reserve(settings.DownloadRoot,name,extension);
        try
        {
            await using (var source = File.OpenRead(result))
            await using (var destination = new FileStream(output,FileMode.Truncate,FileAccess.Write,FileShare.None,65536,true))
                await source.CopyToAsync(destination,ct);
            ct.ThrowIfCancellationRequested();
        }
        catch { File.Delete(output); throw; }
        return output;
    }
    static async Task Ffmpeg(string exe, List<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new GrabException("WG-3002","无法启动 FFmpeg");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token); var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stderr,stdout);
        if (process.ExitCode != 0) throw new GrabException("WG-3001","FFmpeg 处理失败，源文件保留在缓存目录，可重试或检查组件");
    }
}
