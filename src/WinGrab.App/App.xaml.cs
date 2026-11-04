using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
namespace WinGrab.App;
public partial class App : System.Windows.Application
{
    Mutex? mutex;
    int unhandledErrorReported;
    protected override void OnStartup(StartupEventArgs e)
    {
        var startup=Stopwatch.StartNew();
        mutex=new Mutex(true,"Local\\WinGrab.Desktop.v1",out bool first);
        if(!first)
        {
            foreach(var process in Process.GetProcessesByName("WinGrab"))
            using(process)
            {
                if(process.Id==Environment.ProcessId)continue;
                if(SingleInstanceActivation.TryRestore(process.Id)){Shutdown();return;}
            }
            ThemedDialog.Info(null,"WinGrab 正在启动，请稍候。","WinGrab");Shutdown();return;
        }
        DispatcherUnhandledException+=(_,args)=>
        {
            // Mark handled before doing anything that might pump dispatcher messages.
            // Never open a modal dialog from this handler: layout failures can re-enter it.
            args.Handled=true;
            if(Interlocked.Exchange(ref unhandledErrorReported,1)!=0)return;
            RecordError(args.Exception);
            try { if(MainWindow is MainWindow main)main.HandleUnexpectedError(); } catch { }
        };
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        try
        {
            var window=new MainWindow();MainWindow=window;
            long renderedMs=-1;
            window.ContentRendered+=(_,_)=>{renderedMs=startup.ElapsedMilliseconds;WriteStartupRecord(new{version="1.0.1",renderedMs,time=DateTimeOffset.Now});};
            window.StartupReady+=async(_,_)=>
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                WriteStartupRecord(new{version="1.0.1",renderedMs,readyMs=startup.ElapsedMilliseconds,time=DateTimeOffset.Now});
                if(Environment.GetEnvironmentVariable("WINGRAB_STARTUP_PROBE")=="1")
                {
                    _=BilibiliLoginWindow.RenderQr("https://passport.bilibili.com/offline-package-probe");
                    window.RequestExit();
                }
            };
            window.Show();
        }
        catch(Exception ex){ReportStartupError(ex);Shutdown(1);}
    }
    static string DataRoot=>Environment.GetEnvironmentVariable("WINGRAB_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WinGrab");
    static void WriteStartupRecord(object record)
    {try{Directory.CreateDirectory(DataRoot);File.WriteAllText(Path.Combine(DataRoot,"startup.json"),JsonSerializer.Serialize(record));}catch{}}
    static void ReportStartupError(Exception ex)
    {
        RecordError(ex);
        ThemedDialog.Info(null,"WinGrab 启动或操作失败。请确认已完整解压应用目录，并检查数据目录的空间与权限。\n\n错误类型："+ex.GetType().Name+"\n启动记录："+Path.Combine(DataRoot,"startup.json"),"WinGrab");
    }
    static void RecordError(Exception ex)=>WriteStartupRecord(new{version="1.0.1",error=ex.GetType().Name,innerError=ex.GetBaseException().GetType().Name,stack=ex.GetBaseException().StackTrace,time=DateTimeOffset.Now});
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {try{if(MainWindow is MainWindow main)main.PrepareSessionEnd();}finally{base.OnSessionEnding(e);}}
    protected override void OnExit(ExitEventArgs e){if(MainWindow is MainWindow main)main.RemoveTrayIcon();mutex?.Dispose();base.OnExit(e);}
}
