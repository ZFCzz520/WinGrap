using System.Text.Json;
namespace WinGrab.Parsers;
static class JsonTools
{
    public static JsonElement At(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string Str(this JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind == JsonValueKind.Number ? e.GetRawText() : "";
    public static long Num(this JsonElement e) => long.TryParse(e.Str(), out var n) ? n : 0;
    public static JsonElement[] Arr(this JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToArray() : [];
    public static string FirstUrl(this JsonElement e) => e.At("url_list").Arr().Select(v => v.Str()).FirstOrDefault(s => s.StartsWith("http")) ?? e.At("urlList").Arr().Select(v => v.Str()).FirstOrDefault() ?? "";
}
