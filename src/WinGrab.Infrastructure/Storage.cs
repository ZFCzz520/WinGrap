using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinGrab.Core;
namespace WinGrab.Infrastructure;

public sealed class Store : IDisposable
{
    public string Root { get; }
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public Settings Settings { get; }
    readonly object gate = new();
    IntPtr db;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Store(string root)
    {
        Root = root; Directory.CreateDirectory(root); Directory.CreateDirectory(Cache); Directory.CreateDirectory(Logs);
        try { Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json"))) ?? new(); } catch { Settings = new(); }
        Settings.MaxConcurrentTasks = Math.Clamp(Settings.MaxConcurrentTasks, 1, 4);
        if (Sqlite.sqlite3_open(Path.Combine(root, "wingrab.db"), out db) != 0) throw new IOException("无法打开任务数据库");
        Exec("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, payload TEXT NOT NULL); PRAGMA busy_timeout=5000;");
    }
    public void SaveSettings() => AtomicJson(Path.Combine(Root, "settings.json"), Settings);
    public static void AtomicJson<T>(string path, T value) { File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json)); File.Move(path + ".tmp", path, true); }
    void Exec(string sql) { if (Sqlite.sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out var err) != 0) { Sqlite.sqlite3_free(err); throw new IOException("任务数据库写入失败"); } }
    public void Save(Job job)
    {
        lock (gate)
        {
            if (Sqlite.sqlite3_prepare_v2(db, "INSERT OR REPLACE INTO jobs VALUES(?1,?2)", -1, out var stmt, IntPtr.Zero) != 0) throw new IOException("任务数据库准备失败");
            try
            {
                Bind(stmt, 1, job.Id.ToString());
                // Signed CDN URLs and cookies are never persisted in task history.
                var saved = new Job { Id = job.Id, Url = job.Url, TrackIndex = job.TrackIndex,TrackId=job.TrackId, Format = job.Format, OutputPath = job.OutputPath, State = job.State, Created = job.Created, Error = job.Error,
                    Media = job.Media is null ? null : job.Media with { Videos = [], Audio = null, Images = [], Headers = [], CoverUrl = null,Qualities=[],Collection=null } };
                Bind(stmt, 2, JsonSerializer.Serialize(saved));
                if (Sqlite.sqlite3_step(stmt) != 101) throw new IOException("任务记录保存失败");
            }
            finally { Sqlite.sqlite3_finalize(stmt); }
        }
    }
    static void Bind(IntPtr stmt, int index, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); Sqlite.sqlite3_bind_text(stmt, index, bytes, bytes.Length, new IntPtr(-1)); }
    public List<Job> Load()
    {
        lock (gate)
        {
            var list = new List<Job>();
            Sqlite.sqlite3_prepare_v2(db, "SELECT payload FROM jobs", -1, out var stmt, IntPtr.Zero);
            try { while (Sqlite.sqlite3_step(stmt) == 100) { try { var job = JsonSerializer.Deserialize<Job>(Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_text(stmt, 0))!); if (job != null && job.Created > DateTime.Now.AddDays(-90)) { if (job.State is JobState.Downloading or JobState.Queued or JobState.Merging) job.State = JobState.Paused; list.Add(job); } } catch (JsonException) { } } }
            finally { Sqlite.sqlite3_finalize(stmt); }
            return list.OrderByDescending(j => j.Created).ToList();
        }
    }
    public void Log(Guid? id, string code, string message)
    {
        lock (gate) File.AppendAllText(Path.Combine(Logs, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), JsonSerializer.Serialize(new { ts = DateTimeOffset.Now, taskId = id, code, msg = message }) + Environment.NewLine);
    }
    public bool DeleteHistory(Guid id)
    {
        lock(gate)
        {
            if(Sqlite.sqlite3_prepare_v2(db,"SELECT payload FROM jobs WHERE id=?1",-1,out var statement,IntPtr.Zero)!=0)throw new IOException("无法读取历史记录");
            try
            {
                Bind(statement,1,id.ToString());
                if(Sqlite.sqlite3_step(statement)!=100)return false;
                var job=JsonSerializer.Deserialize<Job>(Marshal.PtrToStringUTF8(Sqlite.sqlite3_column_text(statement,0))!);
                if(job?.State is not(JobState.Completed or JobState.Failed or JobState.Cancelled))return false;
            }
            finally{Sqlite.sqlite3_finalize(statement);}
            if(Sqlite.sqlite3_prepare_v2(db,"DELETE FROM jobs WHERE id=?1",-1,out statement,IntPtr.Zero)!=0)throw new IOException("无法删除历史记录");
            try{Bind(statement,1,id.ToString());if(Sqlite.sqlite3_step(statement)!=101)throw new IOException("历史记录删除失败");}
            finally{Sqlite.sqlite3_finalize(statement);}
            return true;
        }
    }
    public void Dispose() { lock (gate) { if (db != IntPtr.Zero) { Sqlite.sqlite3_close(db); db = IntPtr.Zero; } } }
    static class Sqlite
    {
        const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_close(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr cb, IntPtr arg, out IntPtr error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void sqlite3_free(IntPtr value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] value, int len, IntPtr free);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    }
}
public static class Credentials
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr ptr);
    static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length); Blob output = default;
        try
        {
            bool ok = protect ? CryptProtectData(ref input, "WinGrab", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new IOException("Cookie 无法解密，请重新导入");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    public static void Save(string root, string platform, string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Cookie 必须是一行请求头文本");
        string path = Path.Combine(root, platform + ".credentials.dat");
        if (string.IsNullOrWhiteSpace(value)) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllBytes(path, Transform(Encoding.UTF8.GetBytes(value), true));
    }
    public static string? Read(string root, string platform)
    { var path = Path.Combine(root, platform + ".credentials.dat"); return File.Exists(path) ? Encoding.UTF8.GetString(Transform(File.ReadAllBytes(path), false)) : null; }
}
