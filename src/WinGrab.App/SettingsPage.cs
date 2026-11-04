using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WinGrab.Infrastructure;
using WinGrab.Core;
namespace WinGrab.App;
public sealed class SettingsPage : UserControl
{
    public bool CredentialsChanged {get;private set;}
    public event EventHandler? CredentialsUpdated;
    public event EventHandler? UnsavedChangesChanged;
    FormValues? savedValues;
    bool updatingForm;
    public bool HasUnsavedChanges=>savedValues!=null&&(ReadValues()!=savedValues||cookie.Password.Length>0);
    readonly Store store;
    readonly StackPanel fields=new() { Margin=new Thickness(26) };
    readonly TextBox directory=new(),template=new(),ffmpeg=new(),proxy=new(),flags=new();
    readonly ComboBox concurrent=new() { ItemsSource=new[]{1,2,3,4} },speed=new() { ItemsSource=new[]{0,1,5,10} };
    readonly CheckBox clipboard=new() { Content="自动识别剪贴板中的平台链接",Margin=new Thickness(0,14,0,6) };
    readonly ComboBox platform=new() { ItemsSource=new[]{"bilibili","douyin"},SelectedIndex=0 };
    readonly PasswordBox cookie=new() { Padding=new Thickness(10),Margin=new Thickness(0,8,0,8) };
    readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.DimGray,Margin=new Thickness(0,14,0,10)};
    public SettingsPage(Store store)
    {
        this.store=store;
        Content=new Border{Background=System.Windows.Media.Brushes.White,Child=new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        directory.Text=store.Settings.DownloadRoot;Add("保存目录",directory);Browse(directory,true);
        template.Text=store.Settings.NameTemplate;Add("文件命名模板",template);Hint("可用变量：{title} {author} {date} {platform} {quality}");
        concurrent.SelectedItem=store.Settings.MaxConcurrentTasks;Add("同时下载任务数（1–4）",concurrent);
        speed.SelectedItem=store.Settings.SpeedLimitMB;Add("全局下载限速（MB/s，0 为不限速）",speed);
        ffmpeg.Text=store.Settings.FfmpegPath;Add("FFmpeg 程序路径（留空时自动查找）",ffmpeg);Browse(ffmpeg,false);
        Hint(MediaWriter.FindFfmpeg(store.Settings.FfmpegPath) is null?"尚未找到 FFmpeg；DASH 下载和音频导出需要此组件。":"已检测到 FFmpeg 组件");
        proxy.Text=store.Settings.Proxy;Add("HTTP 代理（保存后重启生效）",proxy);
        flags.Text=store.Settings.ParserFlagsUrl;Add("平台开关 HTTPS 配置地址（可选）",flags);Hint("留空不访问远程配置；配置仅控制平台启停，不下载执行代码。");
        clipboard.IsChecked=store.Settings.AutoClipboard;fields.Children.Add(clipboard);
        fields.Children.Add(new Separator{Margin=new Thickness(0,18,0,10)});
        var qrLogin=new Button{Content="扫码登录 / 管理 B站账号",HorizontalAlignment=HorizontalAlignment.Left,Style=(Style)FindResource("Primary")};
        qrLogin.Click+=(_,_)=>{var dialog=new BilibiliLoginWindow(store){Owner=Window.GetWindow(this)};dialog.ShowDialog();if(dialog.CredentialsChanged)NotifyCredentialsChanged();};fields.Children.Add(qrLogin);
        Add("导入自己的 Cookie（单行 Cookie 请求头）",platform);fields.Children.Add(cookie);Hint("仅按平台用于解析请求。使用 Windows DPAPI 加密，不上传到产品服务器。留空点击清除可删除已有凭证。");
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,16)};
        var import=new Button{Content="加密保存 Cookie"};import.Click+=(_,_)=>{try{Credentials.Save(store.Root,(string)platform.SelectedItem,cookie.Password);NotifyCredentialsChanged();cookie.Clear();status.Text="Cookie 已保存；返回工作台后将刷新清晰度。";}catch(Exception){status.Text="Cookie 保存失败，请检查输入及数据目录权限。";}};
        var clear=new Button{Content="清除此平台 Cookie"};clear.Click+=(_,_)=>{try{Credentials.Save(store.Root,(string)platform.SelectedItem,"");NotifyCredentialsChanged();cookie.Clear();status.Text="已清除此平台 Cookie";}catch(IOException){status.Text="清除失败，请检查数据目录权限";}};row.Children.Add(import);row.Children.Add(clear);fields.Children.Add(row);
        fields.Children.Add(status);
        savedValues=ReadValues();
        foreach(var field in new[]{directory,template,ffmpeg,proxy,flags})field.TextChanged+=(_,_)=>Changed();
        foreach(var selection in new[]{concurrent,speed})selection.SelectionChanged+=(_,_)=>Changed();
        clipboard.Checked+=(_,_)=>Changed();clipboard.Unchecked+=(_,_)=>Changed();cookie.PasswordChanged+=(_,_)=>Changed();
    }
    void NotifyCredentialsChanged(){CredentialsChanged=true;CredentialsUpdated?.Invoke(this,EventArgs.Empty);}
    void Add(string label,Control control) { fields.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,13,0,7),FontWeight=FontWeights.Medium});fields.Children.Add(control); }
    void Hint(string text)=>fields.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.DimGray,FontSize=11,Margin=new Thickness(0,6,0,0)});
    void Browse(TextBox field,bool folder)
    {
        var button=new Button{Content=folder?"选择目录…":"选择 ffmpeg.exe…",HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,6,0,0)};
        button.Click+=(_,_)=>{var dialog=new PathPickerWindow(field.Text,folder){Owner=Window.GetWindow(this)};if(dialog.ShowDialog()==true)field.Text=dialog.SelectedPath;};
        fields.Children.Add(button);
    }
    void Save(object sender,RoutedEventArgs e)=>TrySave();
    void Changed(){if(!updatingForm)UnsavedChangesChanged?.Invoke(this,EventArgs.Empty);}
    sealed record FormValues(string Directory,string Template,string Ffmpeg,string Proxy,string Flags,int Concurrent,int Speed,bool Clipboard);
    FormValues ReadValues()=>new(directory.Text,template.Text,ffmpeg.Text,proxy.Text,flags.Text,(int?)concurrent.SelectedItem??store.Settings.MaxConcurrentTasks,(int?)speed.SelectedItem??store.Settings.SpeedLimitMB,clipboard.IsChecked==true);
    void SetValues(FormValues value)
    {
        updatingForm=true;
        try{directory.Text=value.Directory;template.Text=value.Template;ffmpeg.Text=value.Ffmpeg;proxy.Text=value.Proxy;flags.Text=value.Flags;concurrent.SelectedItem=value.Concurrent;speed.SelectedItem=value.Speed;clipboard.IsChecked=value.Clipboard;cookie.Clear();}
        finally{updatingForm=false;}
    }
    public void DiscardChanges()
    {
        if(savedValues==null)return;SetValues(savedValues);status.Text="已放弃未保存的修改";Changed();
    }
    public bool TrySave()
    {
        try
        {
            string dir=Path.GetFullPath(directory.Text.Trim());
            if(string.IsNullOrWhiteSpace(template.Text)||Regex.Matches(template.Text,@"\{([^}]+)\}").Any(m=>m.Groups[1].Value is not ("title" or "author" or "date" or "platform" or "quality")))throw new ArgumentException("命名模板包含未知变量");
            if(proxy.Text.Trim().Length>0&&(!Uri.TryCreate(proxy.Text.Trim(),UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")))throw new ArgumentException("代理必须是 http(s) 地址");
            if(flags.Text.Trim().Length>0&&(!Uri.TryCreate(flags.Text.Trim(),UriKind.Absolute,out var flagUri)||flagUri.Scheme!="https"))throw new ArgumentException("平台开关必须使用 HTTPS 地址");
            if(ffmpeg.Text.Trim().Length>0&&!File.Exists(ffmpeg.Text.Trim()))throw new ArgumentException("FFmpeg 文件不存在");
            if(cookie.Password.Contains('\r')||cookie.Password.Contains('\n'))throw new ArgumentException("Cookie 必须是单行请求头");
            Directory.CreateDirectory(dir);string probe=Path.Combine(dir,".wingrab-write-"+Guid.NewGuid().ToString("N"));using(var f=File.Create(probe)){}File.Delete(probe);
            var candidate=new Settings{DownloadRoot=dir,NameTemplate=template.Text,FfmpegPath=ffmpeg.Text.Trim(),Proxy=proxy.Text.Trim(),ParserFlagsUrl=flags.Text.Trim(),MaxConcurrentTasks=(int)concurrent.SelectedItem,SpeedLimitMB=(int)speed.SelectedItem,AutoClipboard=clipboard.IsChecked==true,SegmentsPerTask=store.Settings.SegmentsPerTask,AcceptedTerms=store.Settings.AcceptedTerms};
            bool hasCookie=cookie.Password.Length>0;
            // Persist the candidate before changing the settings used by running jobs.
            Store.AtomicJson(Path.Combine(store.Root,"settings.json"),candidate);
            if(hasCookie)
            {
                try{Credentials.Save(store.Root,(string)platform.SelectedItem,cookie.Password);}
                catch{Store.AtomicJson(Path.Combine(store.Root,"settings.json"),store.Settings);throw;}
                NotifyCredentialsChanged();
            }
            store.Settings.DownloadRoot=candidate.DownloadRoot;store.Settings.NameTemplate=candidate.NameTemplate;store.Settings.FfmpegPath=candidate.FfmpegPath;store.Settings.Proxy=candidate.Proxy;store.Settings.ParserFlagsUrl=candidate.ParserFlagsUrl;store.Settings.MaxConcurrentTasks=candidate.MaxConcurrentTasks;store.Settings.SpeedLimitMB=candidate.SpeedLimitMB;store.Settings.AutoClipboard=candidate.AutoClipboard;
            SetValues(new(candidate.DownloadRoot,candidate.NameTemplate,candidate.FfmpegPath,candidate.Proxy,candidate.ParserFlagsUrl,candidate.MaxConcurrentTasks,candidate.SpeedLimitMB,candidate.AutoClipboard));
            savedValues=ReadValues();status.Text="设置已保存。代理更改需重启应用生效。";Changed();return true;
        }
        catch(Exception ex){status.Text="设置未保存："+ex.Message;status.BringIntoView();Changed();return false;}
    }
}
