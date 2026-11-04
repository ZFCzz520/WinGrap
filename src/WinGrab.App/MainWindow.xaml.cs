using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinGrab.Contracts;
using WinGrab.Core;
using WinGrab.Infrastructure;
using WinGrab.Parsers;

namespace WinGrab.App;
public partial class MainWindow : Window
{
    public event EventHandler? StartupReady;
    readonly Store store;
    readonly HttpClient http;
    readonly ParserService parser;
    readonly MediaWriter writer;
    readonly ObservableCollection<Job> jobs = [];
    readonly Dictionary<Guid, CancellationTokenSource> running = [];
    readonly DispatcherTimer timer = new() { Interval=TimeSpan.FromMilliseconds(500) };
    ParsedMedia? preview;
    readonly Queue<ParsedMedia> pending = new();
    CancellationTokenSource? parsing;
    bool history, shuttingDown,loginOpen;
    bool suppressQualityEvents,qualityLoginInProgress;
    string lastClipboard=""; DateTime lastClipboardTime;
    HwndSource? source;
    public MainWindow()
    {
        InitializeComponent();
        SelectPlatform(selectedPlatform);
        Width=Math.Min(Width,Math.Max(MinWidth,SystemParameters.WorkArea.Width));
        Height=Math.Min(Height,Math.Max(MinHeight,SystemParameters.WorkArea.Height));
        StateChanged+=(_,_)=>UpdateChrome();
        string root = Environment.GetEnvironmentVariable("WINGRAB_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WinGrab");
        store = new Store(root); http = ParserService.CreateClient(store.Settings);
        parser = new(http,store,[new BilibiliParser(),new DouyinParser()]);
        writer = new(new SegmentDownloader(http,new BandwidthLimiter(()=>store.Settings.SpeedLimitMB)),store.Settings);
        IsEnabled=false;Footer.Text="正在加载本地任务…";
        timer.Tick += (_,_)=>Pump(); Loaded+=OnLoaded; Closing+=OnClosing; SourceInitialized+=OnSource;
        Navigate(MainPage.Workbench);
    }
    void OnSource(object? sender,EventArgs e) { source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source.AddHook(ClipboardHook); AddClipboardFormatListener(source.Handle);SingleInstanceActivation.Mark(source.Handle);InitializeTray(source); }
    async void OnLoaded(object sender,RoutedEventArgs e)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var load=Task.Run(store.Load);loading=load;
        var restored=await load;loading=null;
        if(shuttingDown)return;
        foreach(var job in restored){job.Report(job.State==JobState.Completed?100:0,job.State==JobState.Paused?"上次下载已暂停，可继续恢复":job.Error.Length>0?job.Error:job.StateLabel);jobs.Add(job);}
        IsEnabled=true;timer.Start();Refresh();Footer.Text="●  就绪";
        if (!store.Settings.AcceptedTerms)
        {
            if (ThemedDialog.Confirm(this,"欢迎使用 WinGrab\n\n请仅下载您拥有使用权或获授权的内容，供个人学习与离线观看。\n\n本应用不支持付费、私密或 DRM 内容，不提供签名破解或去水印功能。抖音解析为实验性能力。\n\nCookie 仅在本机加密保存；下载直接连接来源平台。\n\n是否确认拥有内容使用权并同意以上须知？","使用须知","同意并继续") == false) { RequestExit(); return; }
            store.Settings.AcceptedTerms=true; store.SaveSettings();
        }
        StartupReady?.Invoke(this,EventArgs.Empty);
        var args=Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length>0) { LinkInput.Text=string.Join(Environment.NewLine,args); await ParseInput(); }
    }
    IntPtr ClipboardHook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if((uint)msg==SingleInstanceActivation.Message){handled=true;RestoreFromTray();return IntPtr.Zero;}
        if (msg==0x031D && !shuttingDown && IsEnabled && store.Settings.AcceptedTerms && store.Settings.AutoClipboard && parsing is null && !loginOpen&&currentPage!=MainPage.Settings)
        {
            try
            {
                if (Clipboard.ContainsText()) { string? text=Links.Extract(Clipboard.GetText()); if (text!=null && Uri.TryCreate(text,UriKind.Absolute,out var uri) && Links.Platform(uri)!=null && (text!=lastClipboard || DateTime.Now-lastClipboardTime>TimeSpan.FromSeconds(10))) { lastClipboard=text;lastClipboardTime=DateTime.Now;LinkInput.Text=text;Dispatcher.BeginInvoke(async ()=>await ParseInput()); } }
            }
            catch (ExternalException) { }
        }
        return IntPtr.Zero;
    }
    async Task ParseInput()
    {
        if (parsing!=null||shuttingDown) return;
        parsing=new();ParseButton.IsEnabled=BiliPlatformButton.IsEnabled=DouyinPlatformButton.IsEnabled=false;InputStatus.Visibility=Visibility.Visible;InputStatus.Text="正在解析，请稍候…";
        pending.Clear(); preview=null;PreviewPanel.Visibility=Visibility.Collapsed;PopulateCollection();
        var lines=LinkInput.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Distinct().Take(30).ToArray();
        var errors=new List<string>();
        try
        {
            foreach (var line in lines)
            {
                try { var media=await parser.Parse(line,parsing.Token); pending.Enqueue(media); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { errors.Add(Error(ex)); }
            }
            ShowNext();
            InputStatus.Text=errors.Count>0? string.Join("\n",errors) : preview!=null ? "解析完成，请确认清晰度后加入下载" : "请先输入链接";
        }
        finally { parsing.Dispose();parsing=null;ParseButton.IsEnabled=BiliPlatformButton.IsEnabled=DouyinPlatformButton.IsEnabled=true; }
    }
    void ShowNext()
    {
        preview=pending.Count>0?pending.Dequeue():null;
        PreviewPanel.Visibility=preview==null?Visibility.Collapsed:Visibility.Visible;
        if (preview==null){PopulateCollection();return;}
        SelectPlatform(preview.Platform);
        PreviewTitle.Text=preview.Title;
        PreviewMeta.Text=$"{preview.Author}  ·  {(preview.Platform=="douyin"?"抖音（实验性）":"哔哩哔哩")}  ·  {(preview.Kind==MediaKind.Gallery?preview.Images.Count+" 张图片 · ZIP":TimeSpan.FromSeconds(preview.Duration).ToString(@"hh\:mm\:ss"))}";
        PopulateQualities();PopulateCollection();
        Cover.Source=null;
        // Decode a small in-memory cover. No cookie is sent to cover/CDN hosts.
        if (Uri.TryCreate(preview.CoverUrl,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https") _=LoadCover(uri,preview);
    }
    async Task LoadCover(Uri uri,ParsedMedia owner)
    {
        try { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8)); using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token); if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength>10_000_000)return; await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token); using var data=new MemoryStream(); byte[] buffer=new byte[16384]; int n; while ((n=await stream.ReadAsync(buffer,timeout.Token))>0) { if(data.Length+n>10_000_000)return; data.Write(buffer,0,n); } data.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=240;bitmap.StreamSource=data;bitmap.EndInit();bitmap.Freeze();if(ReferenceEquals(preview,owner))Cover.Source=bitmap; } catch { }
    }
    static string Canonical(ParsedMedia media)
    {
        if(media.Platform=="douyin")return "https://www.douyin.com/"+(media.Kind==MediaKind.Gallery?"note/":"video/")+media.ContentId;
        if(media.ContentId.Contains("-p")) { var parts=media.ContentId.Split("-p");return "https://www.bilibili.com/video/"+parts[0]+"?p="+parts[1]; }
        return "https://t.bilibili.com/"+media.ContentId;
    }
    void Pump()
    {
        if(shuttingDown)return;
        while(running.Count<store.Settings.MaxConcurrentTasks)
        { var job=jobs.FirstOrDefault(j=>j.State==JobState.Queued && !running.ContainsKey(j.Id));if(job==null)break;var cts=new CancellationTokenSource();running[job.Id]=cts;_=Run(job,cts); }
        Footer.Text=$"●  {running.Count} 个任务运行中  ·  并发 {store.Settings.MaxConcurrentTasks}";
    }
    internal void HandleUnexpectedError()
    {
        timer.Stop();
        foreach(var cts in running.Values)cts.Cancel();
        // Remove potentially invalid templates from the layout tree before showing an inline notice.
        JobsList.Visibility=Visibility.Collapsed;
        InputStatus.Visibility=Visibility.Visible;
        InputStatus.Text="界面出现异常，下载已暂停。请通过托盘菜单退出后重新打开应用；详细原因已记录在数据目录的 startup.json。";
        Footer.Text="界面异常 · 下载已暂停";
    }
    async Task Run(Job job,CancellationTokenSource cts)
    {
        try
        {
            job.Transition(JobState.Downloading);job.Report(0,"正在获取最新下载地址…");store.Save(job);
            for(int attempt=0;attempt<2;attempt++)
            {
                try
                {
                    if(job.Media==null || job.Media.Videos.Count==0 && job.Media.Images.Count==0 || attempt>0) job.Media=(await parser.Parse(job.Url,cts.Token,true)) with{Collection=null};
                    var progress=new Progress<(double,string)>(p=>job.Report(p.Item1,p.Item2));
                    string output=await Task.Run(()=>writer.Write(job,store.Cache,(p,d)=>((IProgress<(double,string)>)progress).Report((p,d)),()=>Dispatcher.Invoke(()=>job.Transition(JobState.Merging)),cts.Token));
                    if(cts.IsCancellationRequested) { File.Delete(output);cts.Token.ThrowIfCancellationRequested(); }
                    job.OutputPath=output;job.Transition(JobState.Completed);job.Report(100,"已保存 · "+Path.GetFileName(output));job.Error="";Clean(job);break;
                }
                catch(GrabException ex) when(ex.Code=="WG-2003" && attempt==0) { job.Report(0,"下载地址已失效，正在重新解析一次…"); }
            }
        }
        catch(OperationCanceledException) { if(job.State is JobState.Downloading or JobState.Merging) { job.Transition(JobState.Paused);job.Report(job.Percent,"已暂停，继续时将校验并恢复分片"); } }
        catch(Exception ex) { job.Error=Error(ex); if(job.State is JobState.Downloading or JobState.Merging) job.Transition(JobState.Failed);job.Report(job.Percent,job.Error);store.Log(job.Id,ex is GrabException ge?ge.Code:"WG-2001","任务失败（诊断不记录链接或凭证）"); }
        finally
        {
            try { if(job.State==JobState.Cancelled)Clean(job);store.Save(job); }
            catch(IOException) { job.Report(job.Percent,"任务记录保存失败，请检查系统盘空间与权限"); }
            finally { running.Remove(job.Id);cts.Dispose();Refresh(); }
        }
    }
    static string Error(Exception ex)=>ex switch { GrabException ge=>ge.Code+" · "+ge.Message,OperationCanceledException=>"WG-1002 · 请求超时或操作已取消，请稍后重试",UnauthorizedAccessException=>"WG-2004 · 无法写入目录，请更换保存目录",IOException=>"WG-2004 · 文件写入失败，请检查空间、路径及权限",HttpRequestException=>"WG-2001 · 网络或平台请求失败，请检查网络和代理",_=>"WG-1003 · 内容解析或处理失败，请重试或检查版本" };
    void Pause(Job job) { if(job.State is JobState.Queued or JobState.Downloading or JobState.Merging) { job.Transition(JobState.Paused);job.Report(job.Percent,"已暂停，可继续恢复下载");if(running.TryGetValue(job.Id,out var cts))cts.Cancel();store.Save(job); } }
    void Clean(Job job) { string path=Path.GetFullPath(Path.Combine(store.Cache,job.Id.ToString("N")));if(path.StartsWith(Path.GetFullPath(store.Cache)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(path)) { try { Directory.Delete(path,true); } catch { } } }
    void Refresh() { ClearHistoryButton.Visibility=history?Visibility.Visible:Visibility.Collapsed;JobsList.ItemsSource=history?jobs.Where(j=>j.State is JobState.Completed or JobState.Failed or JobState.Cancelled).ToList():jobs;CountText.Text=$"  {JobsList.Items.Count} 个任务";EmptyPanel.Visibility=JobsList.Items.Count==0?Visibility.Visible:Visibility.Collapsed; }
    async void Parse_Click(object sender,RoutedEventArgs e)=>await ParseInput();
    void ClearInput_Click(object sender,RoutedEventArgs e){LinkInput.Clear();LinkInput.Focus();}
    void SaveSettings_Click(object sender,RoutedEventArgs e)=>settingsPage?.TrySave();
    void Paste_Click(object sender,RoutedEventArgs e) { try{if(Clipboard.ContainsText())LinkInput.Text=Clipboard.GetText();}catch(ExternalException){InputStatus.Visibility=Visibility.Visible;InputStatus.Text="剪贴板被占用，请稍后重试";} }
    async void Window_Drop(object sender,DragEventArgs e) { if(currentPage!=MainPage.Workbench)return;if(e.Data.GetDataPresent(DataFormats.UnicodeText)){LinkInput.Text=(string)e.Data.GetData(DataFormats.UnicodeText);await ParseInput();} }
    void Resume_Click(object sender,RoutedEventArgs e) { if((sender as Button)?.Tag is Job job && job.State is JobState.Paused or JobState.Failed && !running.ContainsKey(job.Id)) { job.Error="";job.Transition(JobState.Queued);job.Report(job.Percent,"已加入队列");store.Save(job);Pump();Refresh(); } }
    void Pause_Click(object sender,RoutedEventArgs e) { if((sender as Button)?.Tag is Job job)Pause(job); }
    void PauseAll_Click(object sender,RoutedEventArgs e) { foreach(var j in jobs)Pause(j); }
    void More_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button { Tag:Job job } button)return;
        var menu=new ContextMenu();
        void Add(string title,Action action) { var item=new MenuItem { Header=title };item.Click+=(_,_)=>action();menu.Items.Add(item); }
        if(job.State is not (JobState.Completed or JobState.Cancelled))Add("取消任务并清理临时文件",()=>{job.Transition(JobState.Cancelled);if(running.TryGetValue(job.Id,out var cts))cts.Cancel();else Clean(job);job.Report(0,"已取消");store.Save(job);Refresh();});
        if(job.State==JobState.Completed) { Add("打开所在文件夹",()=>OpenFolder(Path.GetDirectoryName(job.OutputPath)!));Add("重新下载",()=>{var copy=new Job{Url=job.Url,TrackIndex=job.TrackIndex,TrackId=job.TrackId,Format=job.Format};copy.Transition(JobState.Queued);jobs.Insert(0,copy);store.Save(copy);Refresh();Pump();}); }
        if(job.State is JobState.Completed or JobState.Failed or JobState.Cancelled)Add("删除此历史记录（保留文件）",()=>DeleteHistory([job]));
        Add("复制诊断信息",()=>Clipboard.SetText($"WinGrab 1.0.1\n任务：{job.Id}\n状态：{job.State}\n{job.Error}"));
        Add("打开日志目录",()=>OpenFolder(store.Logs));menu.PlacementTarget=button;menu.IsOpen=true;
    }
    async void Workbench_Click(object sender,RoutedEventArgs e){if(!Navigate(MainPage.Workbench))return;if(credentialsNeedRefresh)await RefreshCredentials();}
    void History_Click(object sender,RoutedEventArgs e)=>Navigate(MainPage.History);
    void Settings_Click(object sender,RoutedEventArgs e)
    {
        if(parsing!=null){InputStatus.Visibility=Visibility.Visible;InputStatus.Text="请等待当前解析结束后再修改设置或登录状态";return;}
        Navigate(MainPage.Settings);
    }
    async void BiliLogin_Click(object sender,RoutedEventArgs e)
    {
        if(parsing!=null){InputStatus.Visibility=Visibility.Visible;InputStatus.Text="请等待当前解析结束后再登录";return;}
        loginOpen=true;
        try{new BilibiliLoginWindow(store){Owner=this}.ShowDialog();}finally{loginOpen=false;}
        await RefreshCredentials();
    }
    async Task RefreshCredentials()
    {
        if(shuttingDown)return;
        parser.ClearCache();
        pending.Clear();preview=null;PreviewPanel.Visibility=Visibility.Collapsed;PopulateCollection();
        if(currentPage==MainPage.Settings){credentialsNeedRefresh=true;return;}
        credentialsNeedRefresh=false;
        if(!shuttingDown&&parsing==null&&Links.Extract(LinkInput.Text) is string raw&&Uri.TryCreate(raw,UriKind.Absolute,out var uri)&&Links.Platform(uri)=="bilibili")
            await ParseInput();
    }
    void OpenRoot_Click(object sender,RoutedEventArgs e)=>OpenFolder(store.Settings.DownloadRoot);
    void OpenFolder(string path) { try { Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={path},UseShellExecute=true}); }catch{ThemedDialog.Info(this,"无法打开目录，请检查路径和权限");} }
    void About_Click(object sender,RoutedEventArgs e)=>ThemedDialog.Info(this,"WinGrab 1.0.1 · 内部验收版\n\n.NET 8 / WPF（MIT）\nWindows SQLite（系统组件）\nFFmpeg：独立进程；许可证依所选构建而定（LGPL/GPL）\n源码与许可：https://ffmpeg.org/legal.html\n\n抖音仅解析公开页面 JSON，不生成风控签名。\n不支持番剧、付费或私密内容。\n当前包未代码签名，不包含自动更新。\n\n任务与 Cookie 保存于：\n"+store.Root,"关于 WinGrab");
    void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady)return;
        e.Cancel=true;
        if(!exitRequested&&!ConfirmLeaveSettings())return;
        if(!exitRequested&&tray?.Available==true){Hide();return;}
        RequestExit();
    }
    async Task CloseWhenReady()
    {
        // Defer Closing re-entry and keep cleanup on the owning UI dispatcher,
        // including callers without a SynchronizationContext (startup/test hosts).
        await Dispatcher.Yield(DispatcherPriority.Background);
        while(running.Count>0||parsing!=null||loading is {IsCompleted:false})await Task.Delay(50);
        int code=exitSaveFailed?1:0;try{FinishClose();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){code=1;}
        finally{closeReady=true;Close();if(Application.Current is App app)app.Shutdown(code);}
    }
    void FinishClose()
    {
        if(resourcesClosed)return;resourcesClosed=true;
        shuttingDown=true;timer.Stop();parsing?.Cancel();tray?.Dispose();
        if(source!=null){SingleInstanceActivation.Unmark(source.Handle);RemoveClipboardFormatListener(source.Handle);source.RemoveHook(ClipboardHook);}
        try{store.SaveSettings();}finally{store.Dispose();http.Dispose();}
    }
    [DllImport("user32.dll")]static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
