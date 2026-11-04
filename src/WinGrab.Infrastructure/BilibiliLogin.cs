using System.Net;
using System.Text.Json;
using WinGrab.Contracts;

namespace WinGrab.Infrastructure;
public enum QrLoginState { Waiting, AwaitingConfirmation, Expired, Success }
public sealed record LoginAccount(bool LoggedIn,string Name);
public sealed class QrLoginSession(string url,string key)
{
    public string Url {get;}=url;
    public string Key {get;}=key;
}
public sealed class QrLoginResult(QrLoginState state,string? cookie=null)
{
    public QrLoginState State {get;}=state;
    public string? Cookie {get;}=cookie;
}
public sealed class BilibiliLogin(HttpClient http,string root)
{
    const string Passport="https://passport.bilibili.com/x/passport-login/web/qrcode/";
    static HttpRequestMessage Request(string url,string? cookie=null)
    {
        var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.TryAddWithoutValidation("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer","https://www.bilibili.com/");
        if(!string.IsNullOrWhiteSpace(cookie))request.Headers.TryAddWithoutValidation("Cookie",cookie);
        return request;
    }
    static JsonElement Data(string json)
    {
        using var doc=JsonDocument.Parse(json);
        if(!doc.RootElement.TryGetProperty("code",out var code)||code.GetInt32()!=0||!doc.RootElement.TryGetProperty("data",out var data))
            throw new GrabException("WG-1004","B站未接受登录请求，请稍后重试");
        return data.Clone();
    }
    public async Task<QrLoginSession> GenerateAsync(CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request=Request(Passport+"generate");using var response=await http.SendAsync(request,timeout.Token);response.EnsureSuccessStatusCode();
        var data=Data(await response.Content.ReadAsStringAsync(timeout.Token));
        string? url=data.GetProperty("url").GetString(),key=data.GetProperty("qrcode_key").GetString();
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host is not ("passport.bilibili.com" or "account.bilibili.com")||string.IsNullOrWhiteSpace(key))
            throw new GrabException("WG-1004","登录二维码内容无效，请刷新重试");
        return new(url!,key);
    }
    public async Task<QrLoginResult> PollAsync(string key,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request=Request(Passport+"poll?qrcode_key="+Uri.EscapeDataString(key));
        using var response=await http.SendAsync(request,timeout.Token);response.EnsureSuccessStatusCode();
        var data=Data(await response.Content.ReadAsStringAsync(timeout.Token));
        int code=data.GetProperty("code").GetInt32();
        if(code!=0)return code switch {86101=>new(QrLoginState.Waiting),86090=>new(QrLoginState.AwaitingConfirmation),86038=>new(QrLoginState.Expired),_=>throw new GrabException("WG-1004","B站返回了未知登录状态，请刷新二维码")};
        var cookies=new CookieContainer();
        if(response.Headers.TryGetValues("Set-Cookie",out var values))
            foreach(var value in values) {try{cookies.SetCookies(new Uri(Passport),value);}catch(CookieException){}}
        var allowed=new HashSet<string>(StringComparer.Ordinal){"SESSDATA","bili_jct","DedeUserID","DedeUserID__ckMd5","buvid3","buvid4"};
        var accepted=cookies.GetCookies(new Uri("https://api.bilibili.com/")).Cast<Cookie>().Where(c=>allowed.Contains(c.Name)&&!c.Expired).ToList();
        if(!accepted.Any(c=>c.Name=="SESSDATA"&&!string.IsNullOrEmpty(c.Value)))throw new GrabException("WG-1004","扫码已确认，但未收到有效登录凭证，请刷新二维码重试");
        return new(QrLoginState.Success,string.Join("; ",accepted.Select(c=>c.Name+"="+c.Value)));
    }
    public async Task<LoginAccount> GetAccountAsync(string? cookie,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(cookie))return new(false,"");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request=Request("https://api.bilibili.com/x/web-interface/nav",cookie);
        using var response=await http.SendAsync(request,timeout.Token);response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if(!doc.RootElement.TryGetProperty("code",out var code)||code.GetInt32()!=0)return new(false,"");
        var data=doc.RootElement.GetProperty("data");
        bool logged=data.TryGetProperty("isLogin",out var value)&&value.ValueKind==JsonValueKind.True;
        return new(logged,logged&&data.TryGetProperty("uname",out var name)?name.GetString()??"B站用户":"");
    }
    public async Task<LoginAccount> SaveVerifiedAsync(string cookie,CancellationToken ct)
    {
        var account=await GetAccountAsync(cookie,ct);
        if(!account.LoggedIn)throw new GrabException("WG-1004","登录凭证无效或已过期，请重新扫码");
        ct.ThrowIfCancellationRequested();Credentials.Save(root,"bilibili",cookie);return account;
    }
}
