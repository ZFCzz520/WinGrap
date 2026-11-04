#include "engine.hpp"
#include <windowsx.h>
#include <shellapi.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include <thread>
#include <vector>
#include <stdexcept>

// Variant: yellow / neutral paper / standard cut / sparse hatch / calm motion.
// Product visual: approved WG icon, actual installation path and actual progress.
// Reading layout with a dark three-step navigation rail.
namespace {
constexpr COLORREF Paper=RGB(244,244,242), Ink=RGB(22,22,22), Muted=RGB(92,92,92),
    Rule=RGB(217,217,214), Accent=RGB(255,250,0), White=RGB(255,255,255);
constexpr UINT ProgressMessage=WM_APP+1, FinishedMessage=WM_APP+2;
constexpr int Primary=101, Secondary=102, Close=103, PathEdit=104, Desktop=105;
#ifdef WINGRAB_UNINSTALLER
constexpr bool Removing=true;
#else
constexpr bool Removing=false;
#endif
struct Update { int percent; std::wstring status; };
std::wstring ErrorText(const std::exception& error) {
    try {return setup::Wide(error.what());} catch(...) {return L"操作未完成，请检查安装路径、磁盘空间和文件占用后重试。";}
}
struct App {
    HINSTANCE instance{}; HWND window{}, primary{}, secondary{}, close{}, path{}, desktop{}, progressLabel{};
    HFONT body{}, small{}, title{}, latin{}, number{}, brand{};
    HANDLE fontResource{}; HBRUSH paperBrush=CreateSolidBrush(Paper), whiteBrush=CreateSolidBrush(White);
    int dpi=96, percent=0; bool busy=false, done=false, fatal=false;
    bool desktopChecked=true; setup::fs::path root; setup::Environment environment;
    std::wstring error, status; std::thread worker;
    ~App() { if(worker.joinable()) worker.join(); clearFonts(); if(fontResource) RemoveFontMemResourceEx(fontResource); DeleteObject(paperBrush); DeleteObject(whiteBrush); }
    int px(int value) const { return MulDiv(value,dpi,96); }
    RECT rect(int x,int y,int w,int h) const { return {px(x),px(y),px(x+w),px(y+h)}; }
    void clearFonts() { for(auto f:{body,small,title,latin,number,brand}) if(f) DeleteObject(f); body=small=title=latin=number=brand=nullptr; }
    HFONT font(int height,int weight,const wchar_t* family) { return CreateFontW(-px(height),0,0,0,weight,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,family); }
    void fonts() { clearFonts(); body=font(14,400,L"Microsoft YaHei UI"); small=font(12,400,L"Microsoft YaHei UI"); title=font(32,700,L"Microsoft YaHei UI"); latin=font(13,600,L"WG Interface"); number=font(52,700,L"WG Interface"); brand=font(27,700,L"WG Interface"); }
    void fill(HDC dc,RECT r,COLORREF color) { HBRUSH b=CreateSolidBrush(color); FillRect(dc,&r,b); DeleteObject(b); }
    void line(HDC dc,int x,int y,int w,COLORREF color) { fill(dc,rect(x,y,w,1),color); }
    void text(HDC dc,const std::wstring& s,RECT r,HFONT f,COLORREF color=Ink,UINT flags=DT_LEFT|DT_TOP|DT_NOPREFIX) {
        auto old=SelectObject(dc,f); SetBkMode(dc,TRANSPARENT); SetTextColor(dc,color); DrawTextW(dc,s.c_str(),-1,&r,flags); SelectObject(dc,old);
    }
    void text(HDC dc,const std::wstring& s,int x,int y,int w,int h,HFONT f,COLORREF color=Ink,UINT flags=DT_LEFT|DT_TOP|DT_NOPREFIX) { text(dc,s,rect(x,y,w,h),f,color,flags); }
    void layout() {
        auto place=[&](HWND h,int x,int y,int w,int height){MoveWindow(h,px(x),px(y),px(w),px(height),TRUE);};
        place(close,828,12,38,34); place(primary,626,480,210,46); place(secondary,486,480,126,46);
        place(path,296,276,526,24); place(desktop,284,321,552,30);
        place(progressLabel,284,363,552,56);
        SendMessageW(path,WM_SETFONT,reinterpret_cast<WPARAM>(body),TRUE);
        SendMessageW(desktop,WM_SETFONT,reinterpret_cast<WPARAM>(body),TRUE);
        SendMessageW(progressLabel,WM_SETFONT,reinterpret_cast<WPARAM>(body),TRUE);
        HRGN region=CreateRoundRectRgn(0,0,px(880)+1,px(560)+1,px(12),px(12));
        if(!SetWindowRgn(window,region,TRUE)) DeleteObject(region);
    }
    void sync() {
        bool ready=!busy&&!done;
        ShowWindow(path,ready&&!Removing?SW_SHOW:SW_HIDE); ShowWindow(desktop,ready&&!Removing?SW_SHOW:SW_HIDE);
        ShowWindow(progressLabel,busy?SW_SHOW:SW_HIDE); SetWindowTextW(progressLabel,status.c_str());
        EnableWindow(primary,!busy&&!fatal); EnableWindow(secondary,!busy); EnableWindow(close,!busy);
        SetWindowTextW(primary,busy?(Removing?L"正在卸载…":L"正在安装…"):done?(Removing?L"完成":L"启动 WinGrab"):Removing?L"确认卸载":L"开始安装");
        SetWindowTextW(secondary,done?L"关闭":L"取消"); ShowWindow(secondary,done&&Removing?SW_HIDE:SW_SHOW);
        InvalidateRect(window,nullptr,TRUE);
    }
    void paint(HDC dc) {
        fill(dc,rect(0,0,880,560),Paper); fill(dc,rect(0,0,240,560),Ink);
        HICON icon=static_cast<HICON>(LoadImageW(instance,MAKEINTRESOURCEW(1),IMAGE_ICON,px(48),px(48),LR_DEFAULTCOLOR));
        if(icon) {DrawIconEx(dc,px(30),px(36),icon,px(48),px(48),0,nullptr,DI_NORMAL); DestroyIcon(icon);}
        text(dc,L"WinGrab",30,96,190,46,brand,White);
        text(dc,Removing?L"// UNINSTALL":L"// INSTALL",30,146,190,24,latin,RGB(181,181,175));
        text(dc,std::wstring(L"VERSION  ")+setup::Version,30,176,190,22,latin,RGB(181,181,175));
        line(dc,30,226,180,RGB(65,65,61));
        int current=done?2:busy?1:0;
        const wchar_t* cn[3]={Removing?L"确认卸载":L"安装设置",Removing?L"移除文件":L"安装文件",L"完成"};
        const wchar_t* en[3]={Removing?L"CONFIRM":L"CONFIGURE",Removing?L"REMOVE":L"INSTALL",L"COMPLETE"};
        for(int i=0;i<3;++i) {
            int y=257+i*72;
            if(i==current) fill(dc,rect(0,y,4,48),Accent);
            fill(dc,rect(30,y+3,29,25),i==current?Accent:RGB(51,51,48));
            text(dc,L"0"+std::to_wstring(i+1),30,y+4,29,23,latin,i==current?Ink:White,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
            text(dc,cn[i],72,y,144,26,body,i==current?White:RGB(160,160,153));
            text(dc,en[i],72,y+28,150,20,latin,RGB(160,160,153));
        }
        for(int i=0;i<9;++i) { HPEN p=CreatePen(PS_SOLID,1,RGB(59,59,54)); auto old=SelectObject(dc,p); MoveToEx(dc,px(30+i*20),px(510),nullptr); LineTo(dc,px(42+i*20),px(494)); SelectObject(dc,old); DeleteObject(p); }
        text(dc,L"WINGRAB / WINDOWS",30,528,190,19,latin,RGB(160,160,153));
        text(dc,Removing?L"WinGrab 卸载程序":L"WinGrab 安装程序",284,24,500,24,small,Muted);
        const wchar_t* eyebrow=done?L"// COMPLETE":busy?(Removing?L"// REMOVING FILES":L"// INSTALLING FILES"):(Removing?L"// CONFIRM UNINSTALL":L"// INSTALLATION SETTINGS");
        text(dc,eyebrow,284,86,552,24,latin,Muted);
        text(dc,done?(Removing?L"卸载完成":L"安装完成"):busy?(Removing?L"正在卸载 WinGrab":L"正在安装 WinGrab"):(Removing?L"卸载 WinGrab":L"安装 WinGrab"),284,118,552,48,title);
        fill(dc,rect(284,181,94,3),RGB(217,45,140)); fill(dc,rect(378,181,93,3),Accent); fill(dc,rect(471,181,93,3),RGB(54,167,136));
        if(!busy&&!done) {
            text(dc,Removing?L"将移除此用户安装的 WinGrab 和相关快捷方式。":L"安装到当前 Windows 账户，无需管理员权限。",284,204,552,24,body,Muted);
            text(dc,Removing?L"安装位置 / INSTALL LOCATION":L"安装位置 / INSTALL LOCATION",284,241,552,24,small,Muted);
            if(Removing) text(dc,root.wstring(),284,271,552,46,body,Ink,DT_LEFT|DT_TOP|DT_NOPREFIX|DT_WORDBREAK|DT_PATH_ELLIPSIS);
            else {fill(dc,rect(284,268,552,40),Rule); fill(dc,rect(285,269,550,38),White);}
            if(Removing) {
                text(dc,L"下载的文件、账号和设置将保留。",284,331,552,27,body);
                text(dc,L"请先退出 WinGrab，再点击“确认卸载”。",284,362,552,24,small,Muted);
            } else text(dc,L"始终创建开始菜单快捷方式。",284,359,552,24,small,Muted);
        } else if(busy) {
            text(dc,Removing?L"请保持此窗口打开，正在移除安装文件。":L"请保持此窗口打开，正在校验并写入安装文件。",284,205,552,26,body,Muted);
            text(dc,std::to_wstring(percent)+L"%",280,253,552,70,number);
            fill(dc,rect(284,336,552,10),Rule); fill(dc,rect(284,336,552*percent/100,10),Ink);
        } else {
            text(dc,Removing?L"WinGrab 已从此账户移除。":L"WinGrab 已准备就绪。",284,210,552,29,body);
            text(dc,Removing?L"下载的文件、账号和设置已保留。":L"从桌面、开始菜单或下方按钮启动应用。",284,249,552,43,body,Muted,DT_LEFT|DT_TOP|DT_WORDBREAK|DT_NOPREFIX);
            text(dc,Removing?L"REMOVED":L"READY",280,308,552,72,number);
        }
        if(!busy&&error.empty()) {
            text(dc,done?L"100% / 已完成":L"0% / 等待确认",284,401,552,20,small,Muted);
            fill(dc,rect(284,430,552,6),Rule);
            if(done)fill(dc,rect(284,430,552,6),Ink);
        }
        if(!error.empty()) {
            fill(dc,rect(284,393,552,66),White); fill(dc,rect(284,393,5,66),Accent);
            text(dc,error,299,402,521,50,small,Ink,DT_LEFT|DT_TOP|DT_WORDBREAK|DT_NOPREFIX|DT_END_ELLIPSIS);
        }
        line(dc,284,466,552,Rule);
        text(dc,Removing?L"保留个人文件与设置":L"离线安装 / 当前用户",284,535,552,17,small,Muted);
    }
    void drawButton(const DRAWITEMSTRUCT& dis) {
        HDC dc=dis.hDC; RECT r=dis.rcItem; int id=dis.CtlID; bool disabled=(dis.itemState&ODS_DISABLED)!=0;
        fill(dc,r,Paper);
        if(id==Desktop) {
            RECT box={px(1),px(5),px(20),px(24)}; fill(dc,box,desktopChecked?Accent:White);
            FrameRect(dc,&box,static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
            if(desktopChecked) text(dc,L"✓",box,body,Ink,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
            RECT label=r; label.left=px(31); text(dc,L"创建桌面快捷方式",label,body,Ink,DT_VCENTER|DT_SINGLELINE|DT_NOPREFIX);
        } else if(id==Close) text(dc,L"×",r,body,disabled?Rule:Ink,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
        else {
            bool primary=id==Primary; COLORREF bg=disabled?Rule:primary?Accent:RGB(228,228,224);
            if(dis.itemState&ODS_SELECTED) bg=primary?RGB(226,222,0):RGB(210,210,205);
            POINT points[]={{0,0},{r.right,0},{r.right,r.bottom-px(10)},{r.right-px(10),r.bottom},{0,r.bottom}};
            auto brush=CreateSolidBrush(bg); auto oldBrush=SelectObject(dc,brush); auto oldPen=SelectObject(dc,GetStockObject(NULL_PEN));
            Polygon(dc,points,5); SelectObject(dc,oldBrush); SelectObject(dc,oldPen); DeleteObject(brush);
            wchar_t label[128]{}; GetWindowTextW(dis.hwndItem,label,128);
            text(dc,label,r,body,disabled?Muted:Ink,DT_CENTER|DT_VCENTER|DT_SINGLELINE|DT_NOPREFIX);
        }
        if(dis.itemState&ODS_FOCUS) { RECT focus=r; InflateRect(&focus,-px(4),-px(4)); DrawFocusRect(dc,&focus); }
    }
    void start() {
        if(busy||fatal) return;
        if(done) {
            if(Removing) {DestroyWindow(window); return;}
            auto result=reinterpret_cast<INT_PTR>(ShellExecuteW(window,L"open",(root/L"WinGrab.exe").c_str(),nullptr,root.c_str(),SW_SHOWNORMAL));
            if(result<=32) {error=L"无法启动 WinGrab，请从安装目录或开始菜单打开。"; sync();} else DestroyWindow(window);
            return;
        }
        try {
            if(Removing) setup::VerifyInstallation(root);
            else {int length=GetWindowTextLengthW(path); std::vector<wchar_t> value(length+1); GetWindowTextW(path,value.data(),length+1); root=setup::fs::path(value.data()); setup::ValidateDestination(root);}
            setup::CheckNotRunning(root);
            if(worker.joinable()) worker.join();
            error.clear(); status=L"准备文件…"; busy=true; percent=0; sync();
            bool desktopOption=desktopChecked;
            worker=std::thread([this,desktopOption] {
                auto started=GetTickCount64();
                std::wstring failure;
                try {
                    auto progress=[this](int p,const std::wstring& s) {auto value=std::make_unique<Update>(Update{std::clamp(p,0,100),s}); if(PostMessageW(window,ProgressMessage,0,reinterpret_cast<LPARAM>(value.get()))) value.release();};
                    if(Removing) {setup::VerifyInstallation(root); setup::Uninstall(root,environment,progress);}
                    else setup::Install(setup::SelfPath(),root,desktopOption,environment,progress);
                } catch(const std::exception& e) { failure=ErrorText(e); } catch(...) {failure=L"操作未完成，请重试。";}
                auto elapsed=GetTickCount64()-started;if(elapsed<450)Sleep(static_cast<DWORD>(450-elapsed));
                auto result=std::make_unique<std::wstring>(std::move(failure)); if(PostMessageW(window,FinishedMessage,0,reinterpret_cast<LPARAM>(result.get()))) result.release();
            });
        } catch(const std::exception& e) {busy=false; error=ErrorText(e); sync();}
    }
};
LRESULT CALLBACK WindowProc(HWND window,UINT message,WPARAM wParam,LPARAM lParam) {
    auto app=reinterpret_cast<App*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if(message==WM_NCCREATE) {app=static_cast<App*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams); app->window=window; SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(app));}
    if(!app) return DefWindowProcW(window,message,wParam,lParam);
    switch(message) {
    case WM_CREATE: {
        auto control=[&](const wchar_t* type,const wchar_t* caption,DWORD style,int id) {return CreateWindowExW(0,type,caption,WS_CHILD|WS_VISIBLE|WS_TABSTOP|style,0,0,0,0,window,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),app->instance,nullptr);};
        app->primary=control(L"BUTTON",Removing?L"确认卸载":L"开始安装",BS_OWNERDRAW,Primary);
        app->secondary=control(L"BUTTON",L"取消",BS_OWNERDRAW,Secondary); app->close=control(L"BUTTON",L"关闭",BS_OWNERDRAW,Close);
        app->path=control(L"EDIT",app->root.c_str(),ES_AUTOHSCROLL,PathEdit); SendMessageW(app->path,EM_SETLIMITTEXT,32700,0);
        app->desktop=control(L"BUTTON",L"创建桌面快捷方式",BS_OWNERDRAW,Desktop);
        app->progressLabel=CreateWindowExW(0,L"STATIC",L"",WS_CHILD|SS_LEFT|SS_NOPREFIX,0,0,0,0,window,reinterpret_cast<HMENU>(106),app->instance,nullptr);
        SendMessageW(app->desktop,BM_SETCHECK,BST_CHECKED,0);
        app->fonts(); app->layout(); app->sync(); return 0;
    }
    case WM_ERASEBKGND:return 1;
    case WM_PAINT: {PAINTSTRUCT ps; HDC dc=BeginPaint(window,&ps); RECT area; GetClientRect(window,&area); HDC back=CreateCompatibleDC(dc); HBITMAP bitmap=CreateCompatibleBitmap(dc,area.right,area.bottom); auto old=SelectObject(back,bitmap); app->paint(back); BitBlt(dc,0,0,area.right,area.bottom,back,0,0,SRCCOPY); SelectObject(back,old); DeleteObject(bitmap); DeleteDC(back); EndPaint(window,&ps); return 0;}
    case WM_DRAWITEM:app->drawButton(*reinterpret_cast<DRAWITEMSTRUCT*>(lParam));return TRUE;
    case WM_CTLCOLOREDIT:SetTextColor(reinterpret_cast<HDC>(wParam),Ink); SetBkColor(reinterpret_cast<HDC>(wParam),White);return reinterpret_cast<LRESULT>(app->whiteBrush);
    case WM_CTLCOLORSTATIC:SetTextColor(reinterpret_cast<HDC>(wParam),Muted); SetBkColor(reinterpret_cast<HDC>(wParam),Paper);return reinterpret_cast<LRESULT>(app->paperBrush);
    case WM_COMMAND:
        if(HIWORD(wParam)==BN_CLICKED) switch(LOWORD(wParam)) {
            case Primary:app->start();break;
            case Secondary:case Close:if(!app->busy) DestroyWindow(window);break;
            case Desktop:app->desktopChecked=!app->desktopChecked; SendMessageW(app->desktop,BM_SETCHECK,app->desktopChecked?BST_CHECKED:BST_UNCHECKED,0); InvalidateRect(app->desktop,nullptr,TRUE);break;
        } return 0;
    case WM_NCHITTEST: {LRESULT result=DefWindowProcW(window,message,wParam,lParam); if(result==HTCLIENT) {POINT p={GET_X_LPARAM(lParam),GET_Y_LPARAM(lParam)};ScreenToClient(window,&p);if(p.y<app->px(64)&&p.x<app->px(820)) return HTCAPTION;} return result;}
    case WM_DPICHANGED: {app->dpi=HIWORD(wParam); auto r=reinterpret_cast<RECT*>(lParam);SetWindowPos(window,nullptr,r->left,r->top,app->px(880),app->px(560),SWP_NOZORDER|SWP_NOACTIVATE);app->fonts();app->layout();return 0;}
    case ProgressMessage: {std::unique_ptr<Update> update(reinterpret_cast<Update*>(lParam));app->percent=update->percent;app->status=std::move(update->status);SetWindowTextW(app->progressLabel,(std::to_wstring(app->percent)+L"%  /  "+app->status).c_str());InvalidateRect(window,nullptr,FALSE);return 0;}
    case FinishedMessage: {std::unique_ptr<std::wstring> error(reinterpret_cast<std::wstring*>(lParam));if(app->worker.joinable())app->worker.join();app->busy=false;app->done=error->empty();app->error=std::move(*error);app->percent=app->done?100:app->percent;app->sync();SetFocus(app->primary);return 0;}
    case WM_CLOSE:if(!app->busy)DestroyWindow(window);return 0;
    case WM_DESTROY:PostQuitMessage(0);return 0;
    }
    return DefWindowProcW(window,message,wParam,lParam);
}

std::wstring Quote(const std::wstring& value) {
    // Windows command-line quoting, including literal backslashes before quotes.
    std::wstring result=L"\""; size_t slashes=0;
    for(auto c:value) {if(c==L'\\'){++slashes;continue;} if(c==L'\"')result.append(slashes*2+1,L'\\');else result.append(slashes,L'\\'); slashes=0;result+=c;}
    result.append(slashes*2,L'\\');result+=L'\"';return result;
}
bool BootstrapUninstaller(const setup::fs::path& root) {
    setup::VerifyInstallation(root);
    wchar_t temp[MAX_PATH+1]{}; if(!GetTempPathW(MAX_PATH,temp))throw std::runtime_error("Cannot locate temporary directory.");
    GUID guid{}; if(FAILED(CoCreateGuid(&guid)))throw std::runtime_error("Cannot create temporary identifier.");
    wchar_t id[40]{}; StringFromGUID2(guid,id,40);
    auto directory=setup::fs::path(temp)/(std::wstring(L"WinGrab-Uninstall-")+id);
    if(!CreateDirectoryW(directory.c_str(),nullptr))throw std::runtime_error("Cannot create temporary uninstaller directory.");
    auto helper=directory/L"WinGrab.Uninstall.exe";
    if(!CopyFileW(setup::SelfPath().c_str(),helper.c_str(),TRUE)) {RemoveDirectoryW(directory.c_str());throw std::runtime_error("Cannot copy uninstaller to temporary directory.");}
    std::wstring command=Quote(helper.wstring())+L" --uninstall-root "+Quote(root.wstring())+L" --parent-pid "+std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup{}; startup.cb=sizeof(startup); PROCESS_INFORMATION process{};
    if(!CreateProcessW(helper.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,directory.c_str(),&startup,&process)) {DeleteFileW(helper.c_str());RemoveDirectoryW(directory.c_str());throw std::runtime_error("Cannot start temporary uninstaller.");}
    CloseHandle(process.hThread);CloseHandle(process.hProcess);return true;
}
}

int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR,int show) {
    auto user32=GetModuleHandleW(L"user32.dll");
    using DpiAwareness=BOOL(WINAPI*)(HANDLE); DpiAwareness aware{};auto address=GetProcAddress(user32,"SetProcessDpiAwarenessContext");static_assert(sizeof(address)==sizeof(aware));std::memcpy(&aware,&address,sizeof(aware));
    if(aware) aware(reinterpret_cast<HANDLE>(-4));else SetProcessDPIAware();
    App app;app.instance=instance;bool tempHelper=false;
    int argc=0; wchar_t** argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    try {
        app.environment=setup::CurrentUserEnvironment();
        app.root=Removing?setup::SelfPath().parent_path().parent_path():setup::InstalledRoot(app.environment);
        if(app.root.empty())app.root=setup::DefaultInstallRoot();
        DWORD parentPid=0;
        for(int i=1;i<argc;++i) {
            std::wstring option=argv[i];
            if((!Removing&&option==L"--target")||(Removing&&option==L"--uninstall-root")) {
                if(++i>=argc)throw std::runtime_error("Missing installation directory.");
                app.root=argv[i];if(Removing)tempHelper=true;
            } else if(Removing&&option==L"--parent-pid") {
                if(++i>=argc)throw std::runtime_error("Missing parent process identifier.");
                size_t end=0;auto value=std::stoul(argv[i],&end);if(end!=wcslen(argv[i])||!value||value>MAXDWORD)throw std::runtime_error("Invalid parent process identifier.");parentPid=static_cast<DWORD>(value);
            } else throw std::runtime_error("Unknown command-line option. No automatic action is supported.");
        }
        if(!app.root.is_absolute())throw std::runtime_error("Installation directory must be an absolute path.");
        if(Removing) {
            // Arguments cannot bypass the temp-copy step while executing inside the installation.
            auto self=setup::SelfPath().lexically_normal().wstring();auto prefix=app.root.lexically_normal().wstring();
            if(!prefix.empty()&&prefix.back()!=L'\\')prefix+=L'\\';
            bool insideRoot=self.size()>prefix.size()&&_wcsnicmp(self.c_str(),prefix.c_str(),prefix.size())==0;
            if(!tempHelper||insideRoot) {BootstrapUninstaller(app.root);LocalFree(argv);return 0;}
            if(parentPid==GetCurrentProcessId())throw std::runtime_error("Invalid parent process identifier.");
            if(parentPid) {HANDLE parent=OpenProcess(SYNCHRONIZE,FALSE,parentPid);if(parent){DWORD wait=WaitForSingleObject(parent,10000);CloseHandle(parent);if(wait!=WAIT_OBJECT_0)throw std::runtime_error("Original uninstaller has not exited. Close it and retry.");}}
            // Always validate the destination before displaying a usable confirmation.
            setup::VerifyInstallation(app.root);
        }
    } catch(const std::exception& e) {app.error=ErrorText(e);app.fatal=true;}
    if(argv)LocalFree(argv);
    if(auto resource=FindResourceW(instance,MAKEINTRESOURCEW(2),RT_RCDATA)) {auto data=LoadResource(instance,resource);DWORD count=0;if(data)app.fontResource=AddFontMemResourceEx(LockResource(data),SizeofResource(instance,resource),nullptr,&count);}
    HDC screen=GetDC(nullptr);app.dpi=GetDeviceCaps(screen,LOGPIXELSX);ReleaseDC(nullptr,screen);
    WNDCLASSEXW wc{};wc.cbSize=sizeof(wc);wc.lpfnWndProc=WindowProc;wc.hInstance=instance;wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);wc.hIcon=LoadIconW(instance,MAKEINTRESOURCEW(1));wc.hIconSm=wc.hIcon;wc.lpszClassName=L"WinGrab.Setup.Window";
    if(!RegisterClassExW(&wc))return 2;
    POINT cursor{};GetCursorPos(&cursor);MONITORINFO monitor{};monitor.cbSize=sizeof(monitor);GetMonitorInfoW(MonitorFromPoint(cursor,MONITOR_DEFAULTTOPRIMARY),&monitor);
    int width=app.px(880),height=app.px(560);int x=monitor.rcWork.left+(monitor.rcWork.right-monitor.rcWork.left-width)/2,y=monitor.rcWork.top+(monitor.rcWork.bottom-monitor.rcWork.top-height)/2;
    HWND window=CreateWindowExW(WS_EX_APPWINDOW,wc.lpszClassName,Removing?L"WinGrab 卸载程序":L"WinGrab 安装程序",WS_POPUP|WS_MINIMIZEBOX|WS_SYSMENU,x,y,width,height,nullptr,nullptr,instance,&app);
    if(!window)return 3;
    ShowWindow(window,show);UpdateWindow(window);SetFocus(app.primary);
    MSG message{};while(GetMessageW(&message,nullptr,0,0)>0){if(!IsDialogMessageW(window,&message)){TranslateMessage(&message);DispatchMessageW(&message);}}
    if(tempHelper) {auto self=setup::SelfPath();if(DeleteFileW(self.c_str()))RemoveDirectoryW(self.parent_path().c_str());}
    return 0;
}
