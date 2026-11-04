#include "engine.hpp"
#include <shlobj.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <sddl.h>
#include <zlib.h>
#include <algorithm>
#include <array>
#include <fstream>
#include <set>
#include <sstream>
#include <vector>
#include <stdexcept>

namespace setup {
namespace {
[[noreturn]]void fail(const std::wstring& message){throw std::runtime_error(Utf8(message));}
void require(bool value,const std::wstring& message){if(!value)fail(message);}
std::wstring lower(std::wstring s){std::transform(s.begin(),s.end(),s.begin(),::towlower);return s;}
fs::path full(const fs::path& path){return fs::absolute(path).lexically_normal();}
bool same(const fs::path& a,const fs::path& b){return lower(full(a).wstring())==lower(full(b).wstring());}
fs::path known(REFKNOWNFOLDERID id){PWSTR p=nullptr;require(SUCCEEDED(SHGetKnownFolderPath(id,0,nullptr,&p)),L"无法取得当前用户目录");fs::path value=p;CoTaskMemFree(p);return value;}
std::wstring unique(){GUID g;require(SUCCEEDED(CoCreateGuid(&g)),L"无法创建临时标识");wchar_t text[40];StringFromGUID2(g,text,40);return text;}
void noLinks(const fs::path& path){
    auto absolute=full(path);fs::path part;
    for(const auto& component:absolute){part/=component;auto attributes=GetFileAttributesW(part.c_str());
        if(attributes!=INVALID_FILE_ATTRIBUTES)require(!(attributes&FILE_ATTRIBUTE_REPARSE_POINT),L"安装路径不能包含符号链接或目录联接");}
}
fs::path relative(const std::string& name){
    auto wide=Wide(name);require(!wide.empty()&&wide.size()<220,L"安装清单路径无效");
    require(wide.find_first_of(L"\\:\r\n\t<>\"|?*")==std::wstring::npos,L"安装清单包含非法路径");
    fs::path result(wide);require(!result.is_absolute()&&!result.has_root_path(),L"安装清单包含绝对路径");
    for(const auto& component:result){auto s=component.wstring();require(!s.empty()&&s!=L"."&&s!=L".."&&s.back()!=L'.'&&s.back()!=L' ',L"安装清单路径超出范围");
        auto stem=lower(component.stem().wstring());require(stem!=L"con"&&stem!=L"prn"&&stem!=L"aux"&&stem!=L"nul"&&!(stem.size()==4&&(stem.starts_with(L"com")||stem.starts_with(L"lpt"))&&stem[3]>=L'0'&&stem[3]<=L'9'),L"安装清单包含设备路径");}
    require(result.lexically_normal()==result,L"安装清单路径未规范化");return result;
}
std::string bytes(const fs::path& file){std::ifstream in(file,std::ios::binary);require(bool(in),L"无法读取安装文件");return {std::istreambuf_iterator<char>(in),{}};}
struct Hash{
    BCRYPT_ALG_HANDLE alg=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
    Hash(){require(BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0,L"无法初始化文件校验");if(BCryptCreateHash(alg,&hash,nullptr,0,nullptr,0,0)<0){BCryptCloseAlgorithmProvider(alg,0);fail(L"无法初始化文件校验");}}
    ~Hash(){if(hash)BCryptDestroyHash(hash);if(alg)BCryptCloseAlgorithmProvider(alg,0);}
    void add(const void* p,size_t n){require(BCryptHashData(hash,(PUCHAR)p,(ULONG)n,0)>=0,L"文件校验失败");}
    std::array<unsigned char,32> finish(){std::array<unsigned char,32> result;require(BCryptFinishHash(hash,result.data(),32,0)>=0,L"文件校验失败");return result;}
};
template<class T>T read(std::istream& in){T value{};in.read((char*)&value,sizeof(value));require(bool(in),L"安装包不完整，请重新获取");return value;}
void readExact(std::istream& in,void* p,size_t n){in.read((char*)p,(std::streamsize)n);require(bool(in),L"安装包不完整，请重新获取");}
struct Entry{fs::path path;uint64_t raw,packed,offset;std::array<unsigned char,32> hash;};
struct Package{std::vector<Entry> entries;uint64_t total=0;};
Package inspect(const fs::path& file,const Progress& progress){
    std::ifstream in(file,std::ios::binary);require(bool(in),L"无法打开安装包");in.seekg(0,std::ios::end);auto length=(uint64_t)in.tellg();require(length>=68,L"安装包不完整");
    in.seekg(length-56);char magic[8];readExact(in,magic,8);require(std::string(magic,8)=="WGPKG001",L"安装包缺少有效载荷");
    auto offset=read<uint64_t>(in),size=read<uint64_t>(in);std::array<unsigned char,32> expected;readExact(in,expected.data(),32);
    require(offset<=length-56&&size==length-56-offset&&size>=12,L"安装包范围无效");
    in.seekg(offset);Hash hash;std::vector<char> buffer(1<<20);uint64_t done=0;
    while(done<size){auto n=(size_t)std::min<uint64_t>(buffer.size(),size-done);readExact(in,buffer.data(),n);hash.add(buffer.data(),n);done+=n;progress((int)(done*10/size),L"正在校验安装包…");}
    require(hash.finish()==expected,L"安装包校验失败，请重新获取安装程序");
    in.seekg(offset);readExact(in,magic,8);require(std::string(magic,8)=="WGPAY001",L"安装包格式无效");auto count=read<uint32_t>(in);require(count>0&&count<10000,L"安装清单数量无效");
    Package package;std::set<std::wstring> names;
    for(uint32_t i=0;i<count;i++){
        auto chars=read<uint32_t>(in);Entry e{};e.raw=read<uint64_t>(in);e.packed=read<uint64_t>(in);readExact(in,e.hash.data(),32);
        require(chars>0&&chars<880&&e.raw<4ull*1024*1024*1024&&e.packed<=size,L"安装文件大小或名称无效");
        std::string name(chars,'\0');readExact(in,name.data(),chars);e.path=relative(name);
        require(names.insert(lower(e.path.generic_wstring())).second,L"安装包包含重复文件");
        require(lower(e.path.generic_wstring())!=L".maintenance/installed-files.txt",L"安装包清单名称冲突");
        e.offset=(uint64_t)in.tellg();require(e.offset<=offset+size&&e.packed<=offset+size-e.offset,L"安装包文件范围无效");
        package.total+=e.raw;require(package.total<8ull*1024*1024*1024,L"安装包超出大小限制");package.entries.push_back(e);in.seekg(e.packed,std::ios::cur);
    }
    require((uint64_t)in.tellg()==offset+size&&names.contains(L"wingrab.exe")&&names.contains(L".maintenance/wingrab.uninstall.exe"),L"安装包缺少应用或卸载程序");return package;
}
fs::path manifestPath(const fs::path& root){return root/L".maintenance/installed-files.txt";}
std::vector<fs::path> inventory(const fs::path& root){
    noLinks(root);auto text=bytes(manifestPath(root));require(text.size()<2000000,L"安装清单过大");std::istringstream input(text);std::string line;
    std::getline(input,line);require(line==ProductId,L"此目录不是有效的 WinGrab 安装目录");std::getline(input,line);require(same(fs::path(Wide(line)),root),L"安装清单与目录不匹配");
    std::vector<fs::path> files;std::set<std::wstring> names;
    while(std::getline(input,line)){auto path=relative(line);require(names.insert(lower(path.generic_wstring())).second,L"安装清单重复");noLinks(root/path);files.push_back(path);}
    require(names.contains(L"wingrab.exe")&&names.contains(L".maintenance/wingrab.uninstall.exe"),L"安装清单缺少必要文件");return files;
}
void checkWritableFile(const fs::path& file){
    if(!fs::exists(file))return;require(fs::is_regular_file(file),L"安装文件位置被目录占用");
    HANDLE h=CreateFileW(file.c_str(),GENERIC_WRITE|DELETE,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    require(h!=INVALID_HANDLE_VALUE,L"文件被占用或没有写入权限，请从系统托盘退出 WinGrab 后重试");CloseHandle(h);
}
void cleanOwnedTemp(const fs::path& path,const fs::path& parent){
    try{auto absolute=full(path);require(same(absolute.parent_path(),parent)&&absolute.filename().wstring().starts_with(L".wingrab-stage-"),L"临时目录范围无效");noLinks(absolute);if(fs::exists(absolute))fs::remove_all(absolute);}catch(...){}
}
struct Stage{fs::path path,parent;bool keep=false;Stage(const fs::path& root){parent=full(root).parent_path();fs::create_directories(parent);noLinks(parent);path=parent/(L".wingrab-stage-"+unique());require(fs::create_directory(path),L"无法创建安装临时目录");}~Stage(){if(!keep)cleanOwnedTemp(path,parent);}};
struct OperationLock{
    HANDLE handle=nullptr;
    OperationLock(){
        HANDLE token=nullptr;require(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token),L"无法确认安装账户");DWORD size=0;GetTokenInformation(token,TokenUser,nullptr,0,&size);std::vector<BYTE> buffer(size);
        if(!GetTokenInformation(token,TokenUser,buffer.data(),size,&size)){CloseHandle(token);fail(L"无法确认安装账户");}CloseHandle(token);
        LPWSTR sid=nullptr;require(ConvertSidToStringSidW(((TOKEN_USER*)buffer.data())->User.Sid,&sid),L"无法确认安装账户");
        std::wstring name=L"Global\\WinGrab.Maintenance."+std::wstring(sid);LocalFree(sid);handle=CreateMutexW(nullptr,FALSE,name.c_str());require(handle!=nullptr,L"无法创建安装操作锁");
        DWORD wait=WaitForSingleObject(handle,0);if(wait!=WAIT_OBJECT_0&&wait!=WAIT_ABANDONED){CloseHandle(handle);handle=nullptr;fail(L"另一个 WinGrab 安装或卸载程序正在处理文件，请等待完成后重试。");}
    }
    ~OperationLock(){if(handle){ReleaseMutex(handle);CloseHandle(handle);}}
};
void extract(const fs::path& file,const Package& package,const fs::path& stage,const Progress& progress){
    std::ifstream in(file,std::ios::binary);std::vector<unsigned char> compressed(1<<18),expanded(1<<18);uint64_t all=0;
    for(const auto& entry:package.entries){
        auto destination=stage/L"files"/entry.path;fs::create_directories(destination.parent_path());std::ofstream out(destination,std::ios::binary);require(bool(out),L"无法写入临时安装文件，请检查磁盘空间");
        in.seekg(entry.offset);z_stream z{};require(inflateInit(&z)==Z_OK,L"无法初始化解压组件");
        struct ZEnd{z_stream* z;~ZEnd(){inflateEnd(z);}}end{&z};Hash hash;uint64_t remaining=entry.packed,written=0;int code=Z_OK;
        while(code!=Z_STREAM_END){
            if(z.avail_in==0&&remaining){auto n=(size_t)std::min<uint64_t>(compressed.size(),remaining);readExact(in,compressed.data(),n);remaining-=n;z.next_in=compressed.data();z.avail_in=(uInt)n;}
            z.next_out=expanded.data();z.avail_out=(uInt)expanded.size();code=inflate(&z,Z_NO_FLUSH);
            require(code==Z_OK||code==Z_STREAM_END,L"安装文件解压失败，安装包可能损坏");auto n=expanded.size()-z.avail_out;
            require(n<=entry.raw-written,L"安装文件大小与清单不符");out.write((char*)expanded.data(),n);require(bool(out),L"磁盘空间不足或文件写入失败");hash.add(expanded.data(),n);written+=n;all+=n;
            progress(10+(int)(all*70/std::max<uint64_t>(package.total,1)),L"正在释放文件："+entry.path.filename().wstring());
            require(code==Z_STREAM_END||n>0||z.avail_in>0||remaining>0,L"安装文件数据不完整");
        }
        out.close();require(bool(out)&&written==entry.raw&&remaining==0&&z.avail_in==0&&hash.finish()==entry.hash,L"安装文件校验失败，未替换原有程序");
    }
}
std::wstring regString(HKEY key,const wchar_t* name){DWORD type=0,size=0;if(RegQueryValueExW(key,name,nullptr,&type,nullptr,&size)!=ERROR_SUCCESS||type!=REG_SZ||size>65536)return L"";std::vector<wchar_t> value(size/2+1);if(RegQueryValueExW(key,name,nullptr,&type,(BYTE*)value.data(),&size)!=ERROR_SUCCESS)return L"";return value.data();}
struct RegValue{std::wstring name;DWORD type;std::vector<BYTE> data;};
struct RegistrySnapshot{bool existed=false;std::vector<RegValue> values;};
RegistrySnapshot snapshot(const Environment& env){
    RegistrySnapshot result;HKEY key=nullptr;if(RegOpenKeyExW(HKEY_CURRENT_USER,env.registryKey.c_str(),0,KEY_READ,&key)!=ERROR_SUCCESS)return result;result.existed=true;
    DWORD subkeys=0;RegQueryInfoKeyW(key,nullptr,nullptr,nullptr,&subkeys,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr);if(subkeys){RegCloseKey(key);fail(L"卸载注册信息包含未知子项，无法覆盖");}
    for(DWORD i=0;;i++){wchar_t name[32768];DWORD chars=32768,size=0,type=0;auto code=RegEnumValueW(key,i,name,&chars,nullptr,&type,nullptr,&size);if(code==ERROR_NO_MORE_ITEMS)break;
        if(code!=ERROR_SUCCESS){RegCloseKey(key);fail(L"无法备份卸载注册信息");}std::vector<BYTE> data(size);chars=32768;
        if(RegEnumValueW(key,i,name,&chars,nullptr,&type,data.data(),&size)!=ERROR_SUCCESS){RegCloseKey(key);fail(L"无法备份卸载注册信息");}result.values.push_back({name,type,data});}
    RegCloseKey(key);return result;
}
void restoreRegistry(const Environment& env,const RegistrySnapshot& previous){
    RegDeleteTreeW(HKEY_CURRENT_USER,env.registryKey.c_str());if(!previous.existed)return;HKEY key=nullptr;
    if(RegCreateKeyExW(HKEY_CURRENT_USER,env.registryKey.c_str(),0,nullptr,0,KEY_WRITE,nullptr,&key,nullptr)==ERROR_SUCCESS){for(const auto& v:previous.values)RegSetValueExW(key,v.name.c_str(),0,v.type,v.data.data(),(DWORD)v.data.size());RegCloseKey(key);}
}
void registerApp(const fs::path& root,uint64_t size,const Environment& env){
    HKEY key=nullptr;require(RegCreateKeyExW(HKEY_CURRENT_USER,env.registryKey.c_str(),0,nullptr,0,KEY_WRITE,nullptr,&key,nullptr)==ERROR_SUCCESS,L"无法创建当前用户卸载入口");
    struct KeyClose{HKEY k;~KeyClose(){RegCloseKey(k);}}close{key};
    auto str=[&](const wchar_t* name,const std::wstring& value){require(RegSetValueExW(key,name,0,REG_SZ,(const BYTE*)value.c_str(),(DWORD)((value.size()+1)*2))==ERROR_SUCCESS,L"无法写入卸载信息");};
    auto num=[&](const wchar_t* name,DWORD value){require(RegSetValueExW(key,name,0,REG_DWORD,(const BYTE*)&value,sizeof(value))==ERROR_SUCCESS,L"无法写入卸载信息");};
    str(L"DisplayName",L"WinGrab 视频下载器");str(L"DisplayVersion",Version);str(L"Publisher",L"superman");str(L"InstallLocation",root.wstring());
    str(L"UninstallString",L"\""+(root/L".maintenance/WinGrab.Uninstall.exe").wstring()+L"\"");str(L"DisplayIcon",(root/L"WinGrab.exe").wstring()+L",0");
    SYSTEMTIME now;GetLocalTime(&now);wchar_t date[16];swprintf(date,16,L"%04u%02u%02u",now.wYear,now.wMonth,now.wDay);str(L"InstallDate",date);num(L"EstimatedSize",(DWORD)((size+1023)/1024));num(L"NoModify",1);num(L"NoRepair",1);
}
struct Com{HRESULT result;Com():result(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED)){require(SUCCEEDED(result)||result==RPC_E_CHANGED_MODE,L"无法初始化快捷方式组件");}~Com(){if(SUCCEEDED(result))CoUninitialize();}};
void makeShortcut(const fs::path& output,const fs::path& target){
    Com com;IShellLinkW* link=nullptr;require(SUCCEEDED(CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&link))),L"无法创建快捷方式");
    auto code=link->SetPath(target.c_str());if(SUCCEEDED(code))code=link->SetWorkingDirectory(target.parent_path().c_str());if(SUCCEEDED(code))code=link->SetIconLocation(target.c_str(),0);
    IPersistFile* persist=nullptr;if(SUCCEEDED(code))code=link->QueryInterface(IID_PPV_ARGS(&persist));if(SUCCEEDED(code))code=persist->Save(output.c_str(),TRUE);
    if(persist)persist->Release();link->Release();require(SUCCEEDED(code),L"无法写入快捷方式");
}
bool shortcutOwned(const fs::path& shortcut,const fs::path& root){
    if(!fs::exists(shortcut))return false;noLinks(shortcut);Com com;IShellLinkW* link=nullptr;if(FAILED(CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&link))))return false;
    IPersistFile* persist=nullptr;bool owned=false;if(SUCCEEDED(link->QueryInterface(IID_PPV_ARGS(&persist)))){
        if(SUCCEEDED(persist->Load(shortcut.c_str(),STGM_READ))){wchar_t path[32768];if(SUCCEEDED(link->GetPath(path,32768,nullptr,SLGP_RAWPATH))&&*path)owned=same(fs::path(path),root/L"WinGrab.exe")||same(fs::path(path),root/L".maintenance/WinGrab.Uninstall.exe");}persist->Release();}link->Release();return owned;
}
struct Change{fs::path target,backup;bool existed;};
void applyFile(const fs::path& source,const fs::path& target,const fs::path& backups,std::vector<Change>& changes){
    noLinks(target);fs::create_directories(target.parent_path());checkWritableFile(target);bool existed=fs::exists(target);fs::path saved=backups/std::to_wstring(changes.size());
    if(existed)require(CopyFileW(target.c_str(),saved.c_str(),FALSE),L"无法备份原安装文件");changes.push_back({target,saved,existed});
    auto temporary=fs::path(target.wstring()+L".wingrab-new-"+unique());
    try{require(CopyFileW(source.c_str(),temporary.c_str(),TRUE),L"无法复制安装文件，请检查磁盘空间和权限");require(MoveFileExW(temporary.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH),L"无法替换文件，请从托盘退出 WinGrab 后重试");}
    catch(...){DeleteFileW(temporary.c_str());throw;}
}
void rollback(const std::vector<Change>& changes){
    bool failed=false;for(auto i=changes.rbegin();i!=changes.rend();++i)try{if(i->existed)require(CopyFileW(i->backup.c_str(),i->target.c_str(),FALSE),L"恢复原文件失败");else fs::remove(i->target);}catch(...){failed=true;}
    if(failed)fail(L"更新失败，部分原文件未能恢复。请保留安装目录并重新运行安装程序。");
}
void removeEmpty(const fs::path& root){
    if(!fs::exists(root))return;std::vector<fs::path> dirs;
    for(fs::recursive_directory_iterator i(root,fs::directory_options::skip_permission_denied),end;i!=end;++i){auto attrs=GetFileAttributesW(i->path().c_str());if(attrs!=INVALID_FILE_ATTRIBUTES&&(attrs&FILE_ATTRIBUTE_REPARSE_POINT)){i.disable_recursion_pending();continue;}if(i->is_directory())dirs.push_back(i->path());}
    std::sort(dirs.begin(),dirs.end(),[](const auto& a,const auto& b){return a.native().size()>b.native().size();});std::error_code error;for(const auto& dir:dirs)fs::remove(dir,error);fs::remove(root,error);
}
}
std::wstring Wide(const std::string& value){if(value.empty())return {};int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),(int)value.size(),nullptr,0);require(n>0,L"文件名编码无效");std::wstring result(n,0);MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),(int)value.size(),result.data(),n);return result;}
std::string Utf8(const std::wstring& value){if(value.empty())return {};int n=WideCharToMultiByte(CP_UTF8,0,value.data(),(int)value.size(),nullptr,0,nullptr,nullptr);std::string result(n,0);WideCharToMultiByte(CP_UTF8,0,value.data(),(int)value.size(),result.data(),n,nullptr,nullptr);return result;}
fs::path SelfPath(){std::vector<wchar_t> buffer(32768);auto n=GetModuleFileNameW(nullptr,buffer.data(),(DWORD)buffer.size());require(n>0&&n<buffer.size(),L"无法取得程序路径");return fs::path(std::wstring(buffer.data(),n));}
Environment CurrentUserEnvironment(){return {L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\WinGrab",known(FOLDERID_Programs)/L"WinGrab",known(FOLDERID_Desktop)};}
fs::path DefaultInstallRoot(){return known(FOLDERID_LocalAppData)/L"Programs/WinGrab";}
fs::path InstalledRoot(const Environment& env){HKEY key=nullptr;if(RegOpenKeyExW(HKEY_CURRENT_USER,env.registryKey.c_str(),0,KEY_READ,&key)!=ERROR_SUCCESS)return {};auto path=regString(key,L"InstallLocation");RegCloseKey(key);return path.empty()?fs::path{}:full(fs::path(path));}
void ValidateDestination(const fs::path& root){
    require(root.is_absolute(),L"请输入绝对安装路径，例如 D:\\Apps\\WinGrab");auto path=full(root);require(path!=path.root_path()&&path.wstring().size()<190,L"安装路径过短或过长，请选择专用子目录");
    auto drive=path.root_name().wstring();require(drive.size()==2&&iswalpha(drive[0])&&drive[1]==L':',L"请选择本机磁盘上的安装目录");
    for(const auto& part:path.relative_path()){auto text=part.wstring();require(!text.empty()&&text.back()!=L'.'&&text.back()!=L' '&&text.find_first_of(L":\r\n\t\"<>|?*")==std::wstring::npos,L"安装目录路径无效");}
    auto name=path.filename().wstring();require(!name.empty()&&name.back()!=L'.'&&name.back()!=L' '&&name.find_first_of(L"\r\n\t\"<>|?*")==std::wstring::npos,L"安装目录名称无效");
    wchar_t windows[32768];GetWindowsDirectoryW(windows,32768);auto w=lower(full(windows).wstring());auto value=lower(path.wstring());require(value!=w&&!value.starts_with(w+L"\\"),L"不能安装到 Windows 系统目录");
    for(auto id:{&FOLDERID_Profile,&FOLDERID_LocalAppData,&FOLDERID_RoamingAppData,&FOLDERID_Desktop,&FOLDERID_Downloads})require(!same(path,known(*id)),L"请选择专用于 WinGrab 的子目录");
    noLinks(path);if(fs::exists(path)){require(fs::is_directory(path),L"安装位置已被文件占用");if(!fs::is_empty(path))inventory(path);}
}
void CheckNotRunning(const fs::path& root){
    HANDLE processes=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);require(processes!=INVALID_HANDLE_VALUE,L"无法检查正在运行的程序，请稍后重试");PROCESSENTRY32W entry{sizeof(entry)};
    if(Process32FirstW(processes,&entry))do{if(_wcsicmp(entry.szExeFile,L"WinGrab.exe")!=0)continue;HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,entry.th32ProcessID);if(!p)continue;
        wchar_t path[32768];DWORD n=32768;bool running=QueryFullProcessImageNameW(p,0,path,&n)&&same(fs::path(path),full(root)/L"WinGrab.exe");CloseHandle(p);if(running){CloseHandle(processes);fail(L"WinGrab 仍在后台运行，请在系统托盘右键选择“退出”后重试。");}
    }while(Process32NextW(processes,&entry));CloseHandle(processes);
}
void VerifyInstallation(const fs::path& root){ValidateDestination(root);inventory(full(root));}
void Install(const fs::path& packagePath,const fs::path& destination,bool desktop,const Environment& env,const Progress& progress){
    OperationLock operation;
    require(!env.registryKey.empty(),L"卸载注册路径无效");progress(0,L"正在检查安装位置…");ValidateDestination(destination);auto root=full(destination);CheckNotRunning(root);
    auto registered=InstalledRoot(env);require(registered.empty()||same(registered,root),L"已经在其他目录安装 WinGrab，请先卸载旧版本后再更换路径。");
    std::set<std::wstring> managed;
    if(fs::exists(manifestPath(root)))for(const auto& path:inventory(root))managed.insert(lower(path.generic_wstring()));
    auto package=inspect(packagePath,progress);
    for(const auto& entry:package.entries)require(!fs::exists(root/entry.path)||managed.contains(lower(entry.path.generic_wstring())),L"安装包与此目录中的个人文件重名，已停止更新并保留文件："+entry.path.wstring());
    Stage stage(root);extract(packagePath,package,stage.path,progress);auto backup=stage.path/L"backups";fs::create_directory(backup);
    auto stagedManifest=stage.path/L"manifest";{std::ofstream file(stagedManifest,std::ios::binary);file<<ProductId<<'\n'<<Utf8(root.wstring())<<'\n';for(const auto& e:package.entries)file<<Utf8(e.path.generic_wstring())<<'\n';require(bool(file),L"无法写入安装清单");}
    std::vector<std::pair<fs::path,fs::path>> shortcuts;
    auto prepare=[&](const fs::path& output,const fs::path& target){noLinks(output);require(!fs::exists(output)||shortcutOwned(output,root),L"已有同名快捷方式属于其他程序，请更名后重试");auto file=stage.path/(L"shortcut-"+std::to_wstring(shortcuts.size())+L".lnk");makeShortcut(file,target);shortcuts.push_back({file,output});};
    prepare(env.menuDirectory/L"WinGrab.lnk",root/L"WinGrab.exe");prepare(env.menuDirectory/L"卸载 WinGrab.lnk",root/L".maintenance/WinGrab.Uninstall.exe");if(desktop)prepare(env.desktopDirectory/L"WinGrab.lnk",root/L"WinGrab.exe");
    for(const auto& e:package.entries){noLinks(root/e.path);checkWritableFile(root/e.path);}checkWritableFile(manifestPath(root));for(const auto& s:shortcuts)checkWritableFile(s.second);
    auto previous=snapshot(env);std::vector<Change> changes;bool registryTouched=false;
    try{
        size_t index=0;for(const auto& e:package.entries){applyFile(stage.path/L"files"/e.path,root/e.path,backup,changes);progress(80+(int)(++index*14/package.entries.size()),L"正在安装："+e.path.filename().wstring());}
        applyFile(stagedManifest,manifestPath(root),backup,changes);for(const auto& s:shortcuts)applyFile(s.first,s.second,backup,changes);
        progress(97,L"正在创建卸载入口…");registryTouched=true;registerApp(root,package.total,env);progress(100,L"安装完成，可以开始使用 WinGrab。");
    }catch(...){auto error=std::current_exception();if(registryTouched)restoreRegistry(env,previous);try{rollback(changes);}catch(...){stage.keep=true;fail(L"更新失败，原文件备份已保留于："+stage.path.wstring()+L"。请保留备份并重新运行安装程序。");}removeEmpty(root);std::rethrow_exception(error);}
}
void Uninstall(const fs::path& destination,const Environment& env,const Progress& progress){
    OperationLock operation;
    progress(0,L"正在检查安装文件…");auto root=full(destination);VerifyInstallation(root);CheckNotRunning(root);auto files=inventory(root);
    for(const auto& path:files)checkWritableFile(root/path);checkWritableFile(manifestPath(root));
    std::vector<fs::path> shortcuts;for(const auto& s:{env.menuDirectory/L"WinGrab.lnk",env.menuDirectory/L"卸载 WinGrab.lnk",env.desktopDirectory/L"WinGrab.lnk"})if(shortcutOwned(s,root)){checkWritableFile(s);shortcuts.push_back(s);}
    auto registered=InstalledRoot(env);require(registered.empty()||same(registered,root),L"卸载入口已属于另一处安装，已停止操作。");
    std::stable_sort(files.begin(),files.end(),[](const auto& a,const auto& b){return lower(a.generic_wstring())!=L".maintenance/wingrab.uninstall.exe"&&lower(b.generic_wstring())==L".maintenance/wingrab.uninstall.exe";});
    size_t done=0;for(const auto& path:files){noLinks(root/path);if(fs::exists(root/path))require(DeleteFileW((root/path).c_str()),L"部分文件无法删除，请退出占用程序后重试卸载。");progress(5+(int)(++done*85/files.size()),L"正在移除："+path.filename().wstring());}
    for(const auto& path:shortcuts)require(DeleteFileW(path.c_str()),L"快捷方式无法删除，请重试卸载");
    if(!registered.empty())require(RegDeleteTreeW(HKEY_CURRENT_USER,env.registryKey.c_str())==ERROR_SUCCESS,L"无法移除卸载入口，请重试卸载");
    require(DeleteFileW(manifestPath(root).c_str()),L"无法移除安装清单，请重试卸载");removeEmpty(root);std::error_code error;fs::remove(env.menuDirectory,error);progress(100,L"卸载完成。已下载内容和个人设置已保留。");
}
}
