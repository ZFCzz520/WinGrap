#pragma once
#ifndef UNICODE
#define UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <filesystem>
#include <functional>
#include <string>

namespace setup {
namespace fs=std::filesystem;
inline constexpr wchar_t Version[]=L"1.0.1";
inline constexpr char ProductId[]="WinGrab.7E864768-40DE-48DF-9019-672EB8F177B2.v1";
using Progress=std::function<void(int,const std::wstring&)>;
struct Environment {std::wstring registryKey;fs::path menuDirectory,desktopDirectory;};
Environment CurrentUserEnvironment();
fs::path DefaultInstallRoot();
fs::path InstalledRoot(const Environment& environment);
fs::path SelfPath();
std::wstring Wide(const std::string& value);
std::string Utf8(const std::wstring& value);
void ValidateDestination(const fs::path& root);
void CheckNotRunning(const fs::path& root);
void VerifyInstallation(const fs::path& root);
void Install(const fs::path& package,const fs::path& root,bool desktop,const Environment& environment,const Progress& progress);
void Uninstall(const fs::path& root,const Environment& environment,const Progress& progress);
}
