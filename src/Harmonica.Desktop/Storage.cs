using System.IO;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Harmonica.Core;
namespace Harmonica.Desktop;

public sealed class StudioSettings
{
    public int Hotkey { get; set; } = 8;
    public string? LastSong { get; set; }
    public bool Compact { get; set; }
    public double Volume { get; set; } = 0.6;
}
public static class Storage
{
    public static string DataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarmonicaStudio");
    public static string LibraryRoot { get; private set; } = Path.Combine(AppContext.BaseDirectory, "曲库");
    public static string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public static string PreviewRoot => Path.Combine(DataRoot, "previews-v1");
    const int MaxPendingCleanup = 64, MaxBatchScan = 128, MaxBatchAttempts = 128;
    static readonly string previewSession = Guid.NewGuid().ToString("N");
    static readonly HashSet<string> pendingCleanup = new(StringComparer.OrdinalIgnoreCase);
    static readonly object cleanupLock = new();
    static readonly SemaphoreSlim previewCleanupGate = new(1, 1);
    static int deferredCleanupCalls;
    public static void Initialize()
    {
        Directory.CreateDirectory(DataRoot);
        try { Directory.CreateDirectory(LibraryRoot); string probe = Path.Combine(LibraryRoot, ".write-" + Guid.NewGuid()); File.WriteAllText(probe, ""); File.Delete(probe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LibraryRoot = Path.Combine(DataRoot, "曲库"); Directory.CreateDirectory(LibraryRoot); }
    }
    public static string CreatePreviewPath()
    {
        Directory.CreateDirectory(PreviewRoot);
        if (!PreviewDirectoryIsSafe()) throw new IOException("试听目录不能是重解析目录。");
        return Path.Combine(PreviewRoot, $"preview-{previewSession}-{Guid.NewGuid():N}.wav");
    }
    public static StudioSettings LoadSettings()
    {
        try { var s = JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(SettingsPath)); if (s != null && s.Hotkey is >= 6 and <= 11 && double.IsFinite(s.Volume)) { s.Volume = Math.Clamp(s.Volume, 0, 1); return s; } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }
    public static void SaveSettings(StudioSettings settings)
    {
        Directory.CreateDirectory(DataRoot); AtomicWrite(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static string Import(Score score)
    {
        string title = string.Concat(score.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c));
        title = title.Trim().TrimEnd('.'); if (title.Length == 0) title = "曲谱"; if (title.Length > 90) title = title[..90];
        string text = ScoreWriter.Serialize(score);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > ScoreLoader.MaxBytes)
            throw new FormatException($"导出 JSON 超过 {ScoreLoader.MaxBytes / 1024 / 1024} MB 限制；请减少音符数量或音轨数量。");
        try { var _ = ScoreLoader.FromJson(text, "验证"); }
        catch (Exception e) { throw new FormatException("导出 JSON 无法被本程序重新读取：" + e.Message, e); }
        for (int i = 0; i < 10000; i++)
        {
            string path = Path.Combine(LibraryRoot, title + (i == 0 ? "" : $"_{i + 1}") + ".json");
            if (File.Exists(path)) { if (File.ReadAllText(path) == text) return path; continue; }
            AtomicWrite(path, text); return path;
        }
        throw new IOException("同名文件过多，请修改曲名。");
    }
    static void AtomicWrite(string path, string text)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
    static bool PreviewDirectoryIsSafe() => Directory.Exists(PreviewRoot)
        && (File.GetAttributes(DataRoot) & FileAttributes.ReparsePoint) == 0
        && (File.GetAttributes(PreviewRoot) & FileAttributes.ReparsePoint) == 0;

    static bool TryPreviewName(string path, bool currentSessionOnly, out string fullPath)
    {
        fullPath = "";
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")) return false;
            string full = Path.GetFullPath(path), name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(PreviewRoot), StringComparison.OrdinalIgnoreCase)
                || name.Length != 77 || !name.StartsWith("preview-", StringComparison.Ordinal) || name[40] != '-'
                || !name.EndsWith(".wav", StringComparison.Ordinal)
                || !Guid.TryParseExact(name.AsSpan(8, 32), "N", out var session)
                || !Guid.TryParseExact(name.AsSpan(41, 32), "N", out var file)
                || name.Substring(8, 32) != session.ToString("N") || name.Substring(41, 32) != file.ToString("N")
                || (currentSessionOnly && name.Substring(8, 32) != previewSession)) return false;
            fullPath = full; return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    static bool TryPreviewPath(string path, bool currentSessionOnly, out string fullPath)
    {
        if (!TryPreviewName(path, currentSessionOnly, out fullPath)) return false;
        try
        {
            if (!PreviewDirectoryIsSafe()) return false;
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    static bool TryDeletePreview(string path, out string? error)
    {
        error = null;
        try
        {
            // 独占句柄和删除合并，不能删除仍由其它实例/媒体组件持有的文件。
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; return false; }
    }

    static bool RememberPreview(string path)
    {
        lock (cleanupLock) return pendingCleanup.Contains(path) || (pendingCleanup.Count < MaxPendingCleanup && pendingCleanup.Add(path));
    }

    static void DeferPreview(string path, string reason)
    {
        bool tracked = RememberPreview(path);
        Log($"试听文件清理延期（{(tracked ? "待办" : "队列已满，保留目录恢复")}）：{Path.GetFileName(path)}；{reason}");
    }

    static void LogDeferredCalls()
    {
        int count = Interlocked.Exchange(ref deferredCleanupCalls, 0);
        if (count > 0) Log($"{count} 次清理请求因已有任务而延期，文件保留供目录恢复。");
    }

    public static async Task CleanupPreviewFileAsync(string path)
    {
        // UI 上只做字符串检查，目录/属性检查也放入唯一后台清理任务。
        if (!TryPreviewName(path, true, out string full)) return;
        // 不积压等待者。忙时文件仍可由关闭时的本会话目录扫描找回。
        if (!await previewCleanupGate.WaitAsync(0).ConfigureAwait(false)) { RememberPreview(full); Interlocked.Increment(ref deferredCleanupCalls); return; }
        try
        {
            await Task.Run(async () => {
              try {
                string? error = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (TryPreviewPath(full, true, out _) && TryDeletePreview(full, out error))
                    { lock (cleanupLock) pendingCleanup.Remove(full); return; }
                    if (attempt < 2) await Task.Delay(100).ConfigureAwait(false);
                }
                DeferPreview(full, error ?? "路径已不可清理");
              } finally { LogDeferredCalls(); }
            }).ConfigureAwait(false);
        }
        finally { previewCleanupGate.Release(); }
    }

    public static Task CleanupAllPreviewFilesAsync() => CleanupPreviewBatchAsync(false);
    public static Task CleanupOldPreviewFilesAsync() => CleanupPreviewBatchAsync(true);

    static async Task CleanupPreviewBatchAsync(bool oldOnly)
    {
        if (!await previewCleanupGate.WaitAsync(0).ConfigureAwait(false)) { Interlocked.Increment(ref deferredCleanupCalls); return; }
        try { await Task.Run(() => { try { CleanupPreviewBatch(oldOnly); } finally { LogDeferredCalls(); } }).ConfigureAwait(false); }
        finally { previewCleanupGate.Release(); }
    }

    static void CleanupPreviewBatch(bool oldOnly)
    {
        var budget = Stopwatch.StartNew();
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool withinBudget() => attempted.Count < MaxBatchAttempts && budget.ElapsedMilliseconds < 500;
        void Attempt(string path)
        {
          try {
            if (!withinBudget() || !TryPreviewPath(path, !oldOnly, out string full) || attempted.Contains(full)) return;
            if (oldOnly && DateTime.UtcNow - File.GetLastWriteTimeUtc(full) <= TimeSpan.FromHours(24)) return;
            attempted.Add(full);
            if (TryDeletePreview(full, out string? error)) { lock (cleanupLock) pendingCleanup.Remove(full); }
            else if (!oldOnly) DeferPreview(full, error ?? "无法独占打开文件");
            else Log("旧试听文件保留：" + Path.GetFileName(full) + "；" + error);
          }
          catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("单个试听文件清理失败：" + Path.GetFileName(path) + "；" + e.Message); }
        }
        try
        {
            if (!PreviewDirectoryIsSafe()) return;
            if (!oldOnly)
            {
                string[] pending;
                lock (cleanupLock) pending = pendingCleanup.ToArray();
                foreach (string path in pending) { if (!withinBudget()) break; Attempt(path); }
            }
            // 扫描上限包含陌生文件，不能靠一目录无效名字制造无限扫描。
            int scanned = 0;
            foreach (string path in Directory.EnumerateFiles(PreviewRoot))
            {
                if (++scanned > MaxBatchScan || !withinBudget()) { Log("试听清理达到批次预算，剩余文件保留供后续恢复。"); break; }
                Attempt(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("批量试听清理失败：" + e.Message); }
    }
    public static void Log(string message)
    {
        try { Directory.CreateDirectory(DataRoot); var p = Path.Combine(DataRoot, "运行日志.txt"); if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) File.Move(p, p + ".old", true); File.AppendAllText(p, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); } catch (Exception) { }
    }
}
