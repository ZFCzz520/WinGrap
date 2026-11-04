using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinGrab.App;

// Uses the existing WPF HWND and system Shell API, with no WinForms dependency.
sealed class TrayIcon:IDisposable
{
    const uint Callback=0x8000+85;
    readonly HwndSource source;
    readonly Action open,menu;
    readonly Action unavailable;
    readonly uint taskbarCreated=RegisterWindowMessage("TaskbarCreated");
    NotifyData data;
    IntPtr icon;
    bool disposed;
    public bool Available{get;private set;}
    public TrayIcon(HwndSource source,Action open,Action menu,Action unavailable)
    {
        this.source=source;this.open=open;this.menu=menu;this.unavailable=unavailable;
        using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/WinGrab;component/Assets/WinGrab.ico")).Stream;
        using var reader=new BinaryReader(stream);
        reader.ReadUInt16();reader.ReadUInt16();int count=reader.ReadUInt16();
        int chosenSize=0;uint length=0,offset=0;
        for(int i=0;i<count;i++)
        {
            int size=reader.ReadByte();if(size==0)size=256;
            reader.ReadBytes(7);uint bytes=reader.ReadUInt32(),start=reader.ReadUInt32();
            if(size==32||chosenSize==0){chosenSize=size;length=bytes;offset=start;}
        }
        stream.Position=offset;byte[] pixels=reader.ReadBytes(checked((int)length));
        icon=CreateIconFromResourceEx(pixels,(uint)pixels.Length,true,0x00030000,32,32,0);
        data=new NotifyData{Size=(uint)Marshal.SizeOf<NotifyData>(),Window=source.Handle,Id=1,Flags=1|2|4|0x80,Message=Callback,Icon=icon,Tip="WinGrab · 双击打开，右键查看更多",Info="",InfoTitle=""};
        source.AddHook(Hook);Register();
    }
    void Register()
    {
        if(disposed)return;
        Available=icon!=IntPtr.Zero&&(Shell_NotifyIcon(0,ref data)||Shell_NotifyIcon(1,ref data));
        if(Available){data.Version=4;Shell_NotifyIcon(4,ref data);}
    }
    IntPtr Hook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(disposed)return IntPtr.Zero;
        if(taskbarCreated!=0&&(uint)message==taskbarCreated)
        {
            Register();if(!Available)unavailable();
        }
        else if((uint)message==Callback)
        {
            handled=true;
            int action=(int)(lParam.ToInt64()&0xffff);
            if(action is 0x0203 or 0x0400 or 0x0401)open();
            else if(action is 0x007B or 0x0205)menu();
        }
        return IntPtr.Zero;
    }
    public void Focus(){if(!disposed&&Available)Shell_NotifyIcon(3,ref data);}
    public void Dispose()
    {
        if(disposed)return;disposed=true;Available=false;
        Shell_NotifyIcon(2,ref data);source.RemoveHook(Hook);
        if(icon!=IntPtr.Zero){DestroyIcon(icon);icon=IntPtr.Zero;}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct NotifyData
    {
        public uint Size;public IntPtr Window;public uint Id,Flags,Message;public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)]static extern bool Shell_NotifyIcon(uint command,ref NotifyData data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]static extern IntPtr CreateIconFromResourceEx(byte[] bits,uint length,bool icon,uint version,int width,int height,uint flags);
    [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr icon);
}
