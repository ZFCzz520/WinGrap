using System.Runtime.InteropServices;

namespace WinGrab.App;
static class SingleInstanceActivation
{
    const string Property="WinGrab.MainWindow.v1";
    public static readonly uint Message=RegisterWindowMessage("WinGrab.RestoreMainWindow.v1");
    public static void Mark(IntPtr hwnd)=>SetProp(hwnd,Property,new IntPtr(1));
    public static void Unmark(IntPtr hwnd)=>RemoveProp(hwnd,Property);
    public static bool TryRestore(int processId)
    {
        IntPtr target=IntPtr.Zero;
        EnumWindows((hwnd,_)=>{GetWindowThreadProcessId(hwnd,out uint owner);if(owner==processId&&GetProp(hwnd,Property)!=IntPtr.Zero){target=hwnd;return false;}return true;},IntPtr.Zero);
        if(target==IntPtr.Zero||Message==0)return false;
        AllowSetForegroundWindow((uint)processId);
        return PostMessage(target,Message,IntPtr.Zero,IntPtr.Zero);
    }
    delegate bool EnumWindowCallback(IntPtr hwnd,IntPtr param);
    [DllImport("user32.dll")]static extern bool EnumWindows(EnumWindowCallback callback,IntPtr param);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")]static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool SetProp(IntPtr hwnd,string name,IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetProp(IntPtr hwnd,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr RemoveProp(IntPtr hwnd,string name);
}
