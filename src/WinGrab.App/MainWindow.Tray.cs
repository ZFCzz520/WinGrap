using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.IO;

namespace WinGrab.App;
public partial class MainWindow
{
    TrayIcon? tray;
    readonly ContextMenu trayMenu=new();
    bool exitRequested,closeReady,resourcesClosed;
    Task? loading;
    void InitializeTray(HwndSource hwndSource)
    {
        void Add(string title,Action action)
        {var item=new MenuItem{Header=title};item.Click+=(_,_)=>{if(!exitRequested)action();};trayMenu.Items.Add(item);}
        Add("打开主界面",RestoreFromTray);
        Add("添加链接",()=>{RestoreFromTray();Workbench_Click(this,new RoutedEventArgs());Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(()=>{if(currentPage!=MainPage.Workbench||shuttingDown)return;LinkInput.Focus();Keyboard.Focus(LinkInput);}));});
        Add("查看下载历史",()=>{RestoreFromTray();Navigate(MainPage.History);});
        Add("设置",()=>{RestoreFromTray();Settings_Click(this,new RoutedEventArgs());});
        trayMenu.Items.Add(new Separator());Add("退出",RequestExit);
        trayMenu.Closed+=(_,_)=>tray?.Focus();
        tray=new TrayIcon(hwndSource,RestoreFromTray,ShowTrayMenu,RestoreFromTray);
        CloseButton.ToolTip=tray.Available?"关闭窗口，继续在托盘运行；退出请右键托盘图标":"退出 WinGrab";
    }
    void ShowTrayMenu()
    {
        if(exitRequested)return;
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        trayMenu.Placement=PlacementMode.MousePoint;trayMenu.IsOpen=true;
    }
    void RestoreFromTray()
    {
        if(exitRequested)return;
        Show();if(WindowState==WindowState.Minimized)SystemCommands.RestoreWindow(this);
        Activate();SetForegroundWindow(new WindowInteropHelper(this).Handle);
        if(loginOpen)foreach(Window owned in OwnedWindows)owned.Activate();
    }
    internal void RequestExit()
    {
        if(exitRequested||!ConfirmLeaveSettings())return;
        exitRequested=true;shuttingDown=true;timer.Stop();parsing?.Cancel();
        trayMenu.IsOpen=false;trayMenu.IsEnabled=false;IsEnabled=false;
        PauseForExit();
        foreach(Window owned in OwnedWindows.Cast<Window>().ToArray())owned.Close();
        _=CloseWhenReady();
    }
    internal void PrepareSessionEnd()
    {
        exitRequested=true;shuttingDown=true;timer.Stop();parsing?.Cancel();
        trayMenu.IsOpen=false;tray?.Dispose();
        PauseForExit();
        try{store.SaveSettings();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){exitSaveFailed=true;}
    }
    bool exitSaveFailed;
    void PauseForExit()
    {
        foreach(var job in jobs)
            try{Pause(job);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){exitSaveFailed=true;}
        foreach(var cts in running.Values)cts.Cancel();
    }
    internal void RemoveTrayIcon(){trayMenu.IsOpen=false;tray?.Dispose();}
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr hwnd);
}
