using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using WinGrab.Infrastructure;
using WinGrab.Contracts;

namespace WinGrab.App;
public sealed class BilibiliLoginWindow:DialogWindow
{
    readonly Store store;
    readonly HttpClient http;
    readonly BilibiliLogin login;
    readonly Image qr=new(){Name="LoginQrImage",Width=248,Height=248,Stretch=Stretch.Uniform};
    readonly TextBlock heading=new(){Name="LoginHeading",Text="扫码登录 B站",FontSize=23,FontWeight=FontWeights.SemiBold,TextAlignment=TextAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Height=30};
    readonly TextBlock instruction=new(){Name="LoginInstruction",Text="请使用手机 B站 App 扫码，并在手机上确认登录",TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Height=18,Margin=new(0,10,0,18),Foreground=Brushes.DimGray};
    readonly System.Windows.Shapes.Path successIcon=new(){Name="LoginSuccessIcon",Width=168,Height=132,Stretch=Stretch.Uniform,
        Data=Geometry.Parse("M 0,62 L 24,38 L 61,75 L 139,0 L 164,25 L 61,126 Z"),
        Fill=new SolidColorBrush(Color.FromRgb(42,129,87)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
    readonly Border qrFrame;
    readonly TextBlock status=new(){Name="LoginStatus",Text="正在连接 B站…",TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,Height=34,Margin=new(0,14,0,16)};
    readonly Button refresh=new(){Name="RefreshLoginButton",Content="刷新二维码"},logout=new(){Name="LogoutButton",Content="退出本机登录"};
    CancellationTokenSource? operation;
    bool closed;
    public bool CredentialsChanged {get;private set;}
    public BilibiliLoginWindow(Store store,HttpClient? client=null)
    {
        this.store=store;http=client??ParserService.CreateClient(store.Settings);login=new(http,store.Root);
        refresh.IsEnabled=false;logout.IsEnabled=false;
        Title="B站扫码登录 · WinGrab";Eyebrow="// ACCOUNT";Width=460;Height=720;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        FontFamily=new FontFamily("Microsoft YaHei UI");Background=(Brush)FindResource("Paper");Foreground=(Brush)FindResource("Ink");
        var panel=new StackPanel{Margin=new(26)};Body=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(heading);panel.Children.Add(instruction);
        var visual=new Grid{Width=264,Height=264,HorizontalAlignment=HorizontalAlignment.Center};
        qrFrame=new Border{Background=Brushes.White,Padding=new(8),Child=qr};
        visual.Children.Add(qrFrame);visual.Children.Add(successIcon);panel.Children.Add(visual);
        panel.Children.Add(status);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};buttons.Children.Add(refresh);buttons.Children.Add(logout);panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock{Text="凭证仅在本机加密保存。不读取浏览器账号或密码。\n普通 1080P 以视频及账号实际权限为准。",TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray,FontSize=11,Margin=new(0,20,0,0)});
        refresh.Click+=async(_,_)=>await BeginLogin();
        logout.Click+=(_,_)=>Logout();
        Loaded+=async(_,_)=>await Initialize();
        Closed+=(_,_)=>{closed=true;operation?.Cancel();http.Dispose();};
    }
    async Task Initialize()
    {
        var cts=new CancellationTokenSource();operation=cts;
        try
        {
            string? cookie=Credentials.Read(store.Root,"bilibili");
            if(cookie!=null)
            {
                var account=await login.GetAccountAsync(cookie,cts.Token);
                if(closed||!ReferenceEquals(operation,cts))return;
                if(account.LoggedIn){ShowSuccess(account.Name);return;}
            }
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){if(closed||!ReferenceEquals(operation,cts))return;status.Text="已有登录状态无法验证，请重新扫码。";}
        finally {if(ReferenceEquals(operation,cts))operation=null;cts.Dispose();if(!closed){refresh.IsEnabled=true;logout.IsEnabled=true;}}
        if(!closed)await BeginLogin();
    }
    async Task BeginLogin()
    {
        operation?.Cancel();var cts=new CancellationTokenSource(TimeSpan.FromSeconds(180));operation=cts;
        ShowScanning();refresh.IsEnabled=false;status.Text="正在获取登录二维码…";
        try
        {
            var session=await login.GenerateAsync(cts.Token);
            if(closed||!ReferenceEquals(operation,cts))return;
            qr.Source=RenderQr(session.Url);refresh.IsEnabled=true;
            status.Text="请用手机 B站 App 扫码（3 分钟内有效）";
            while(true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3),cts.Token);
                var result=await login.PollAsync(session.Key,cts.Token);
                if(closed||!ReferenceEquals(operation,cts))return;
                if(result.State==QrLoginState.Expired){qr.Source=null;status.Text="二维码已过期，请点击刷新二维码";return;}
                if(result.State==QrLoginState.Waiting){status.Text="等待扫码 · 请使用 B站 App 的扫一扫";continue;}
                if(result.State==QrLoginState.AwaitingConfirmation){status.Text="已扫码，请在手机上确认登录";continue;}
                var account=await login.SaveVerifiedAsync(result.Cookie!,cts.Token);
                if(closed||!ReferenceEquals(operation,cts))return;
                CredentialsChanged=true;ShowSuccess(account.Name);
                return;
            }
        }
        catch(OperationCanceledException){if(!closed&&ReferenceEquals(operation,cts)){qr.Source=null;status.Text="二维码已过期或操作已取消，请刷新重试";}}
        catch(Exception ex)when(ex is not OutOfMemoryException)
        {if(!closed&&ReferenceEquals(operation,cts)){qr.Source=null;status.Text=ex is GrabException ge?ge.Message:"无法连接 B站登录服务，请检查网络或代理后刷新重试";}}
        finally{if(ReferenceEquals(operation,cts)){operation=null;if(!closed)refresh.IsEnabled=true;}cts.Dispose();}
    }
    void Logout()
    {
        operation?.Cancel();operation=null;refresh.IsEnabled=true;
        ShowScanning();
        try{Credentials.Save(store.Root,"bilibili","");CredentialsChanged=true;status.Text="已退出本机登录并删除本机凭证";}
        catch(IOException){status.Text="无法清除凭证，请检查数据目录权限";}
    }
    void ShowSuccess(string name)
    {
        heading.Text="登录成功："+name;heading.ToolTip=heading.Text;
        instruction.Text="关闭窗口后将自动刷新可用清晰度";
        qr.Source=null;qr.Visibility=Visibility.Collapsed;qrFrame.Visibility=Visibility.Collapsed;successIcon.Visibility=Visibility.Visible;
        status.Text="";status.Visibility=Visibility.Hidden;refresh.Content="更换账号 / 重新扫码";
    }
    void ShowScanning()
    {
        heading.Text="扫码登录 B站";heading.ToolTip=null;
        instruction.Text="请使用手机 B站 App 扫码，并在手机上确认登录";
        successIcon.Visibility=Visibility.Collapsed;qr.Visibility=Visibility.Visible;qrFrame.Visibility=Visibility.Visible;qr.Source=null;
        status.Visibility=Visibility.Visible;refresh.Content="刷新二维码";
    }
    public static BitmapSource RenderQr(string text)
    {
        using var generator=new QRCodeGenerator();using var data=generator.CreateQrCode(text,QRCodeGenerator.ECCLevel.M);
        using var renderer=new PngByteQRCode(data);using var stream=new MemoryStream(renderer.GetGraphic(6));
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
    }
}

