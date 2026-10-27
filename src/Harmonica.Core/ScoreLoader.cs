using System.Text.Json;
namespace Harmonica.Core;
public static class ScoreLoader
{
    public const int MaxBytes = 32 * 1024 * 1024;
    public const int MaxTracks = 10000;
    public static Score Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) throw new FormatException("曲谱文件不能超过 32MB。");
        using var buffer = new MemoryStream();
        var block = new byte[8192]; int read;
        while ((read = stream.Read(block)) > 0) { if (buffer.Length + read > MaxBytes) throw new FormatException("曲谱文件过大。"); buffer.Write(block, 0, read); }
        var bytes = buffer.ToArray();
        return Path.GetExtension(path).ToLowerInvariant() switch {
            ".json" => FromJson(new System.Text.UTF8Encoding(false, true).GetString(bytes), path),
            ".mid" or ".midi" => FromMidi(bytes, Path.GetFileNameWithoutExtension(path), path),
            _ => throw new FormatException("请选择 JSON 曲谱或 MIDI 文件（.mid / .midi）。")
        };
    }
    public static Score FromJson(string text, string source = "")
    {
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new FormatException("JSON 曲谱过大。");
            using var doc = JsonDocument.Parse(text.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.GetProperty("format").GetString() != "gtiartist-score-v1") throw new FormatException("不支持的 JSON 格式；当前兼容 gtiartist-score-v1。");
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(source) : title;
            if (string.IsNullOrWhiteSpace(title)) title = "未命名曲谱";
            title = title.Length > 200 ? title[..200] : title;
            var tracks = new Dictionary<int, string>();
            // 显式轨表和音符补出的轨道共用门槛；重复 ID 不增加轨道数。
            void AddTrack(int id, string name, bool replace)
            {
                if (tracks.ContainsKey(id)) { if (replace) tracks[id] = name; return; }
                if (tracks.Count >= MaxTracks) throw new FormatException("音轨数量超过 10,000，请简化曲谱。");
                tracks.Add(id, name);
            }
            if (root.TryGetProperty("tracks", out var ts) && ts.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in ts.EnumerateObject()) if (int.TryParse(item.Name, out int id)) AddTrack(id, item.Value.ToString(), true);
            }
            var notes = new List<Note>(); var sourceNotes = root.GetProperty("notes");
            if (sourceNotes.ValueKind != JsonValueKind.Array || sourceNotes.GetArrayLength() > 250000) throw new FormatException("音符列表无效或过大。");
            foreach (var n in sourceNotes.EnumerateArray())
            {
                int track = n.TryGetProperty("track", out var tr) ? tr.GetInt32() : 1;
                int velocity = n.TryGetProperty("velocity", out var v) ? v.GetInt32() : 100;
                var note = new Note(n.GetProperty("pitch").GetInt32(), n.GetProperty("start").GetDouble() * 1000,
                    n.GetProperty("duration").GetDouble() * 1000, track, Math.Clamp(velocity, 1, 127));
                ValidateNote(note); notes.Add(note);
                AddTrack(track, $"音轨 {track}", false);
            }
            if (notes.Count == 0) throw new FormatException("曲谱中没有音符。");
            var settings = new SourceSettings();
            if (root.TryGetProperty("playback_settings", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                int? track = p.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Number ? tr.GetInt32() : null;
                settings = new(track, ReadInt(p, "transpose", 0, -48, 48), ReadDouble(p, "speed", 1, 0.25, 2),
                    ReadDouble(p, "gate", 1, 0.1, 1), !p.TryGetProperty("fold_octaves", out var fold) || fold.ValueKind != JsonValueKind.False);
            }
            return new(title, notes.OrderBy(n => n.StartMs).ThenBy(n => n.Track).ToArray(), tracks, settings, source);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or ArgumentException)
        { throw new FormatException("JSON 曲谱结构无效：" + e.Message, e); }
    }
    static int ReadInt(JsonElement p, string key, int fallback, int min, int max) =>
        p.TryGetProperty(key, out var x) && x.TryGetInt32(out var v) ? Math.Clamp(v, min, max) : fallback;
    static double ReadDouble(JsonElement p, string key, double fallback, double min, double max) =>
        p.TryGetProperty(key, out var x) && x.TryGetDouble(out var v) && double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
    internal static void ValidateNote(Note n)
    {
        if (n.Pitch is < 0 or > 127 || !double.IsFinite(n.StartMs) || !double.IsFinite(n.DurationMs)
            || n.StartMs < 0 || n.DurationMs <= 0 || n.StartMs + n.DurationMs > 1_800_000)
            throw new FormatException("音符包含无效音高、时间或时长；支持最长 30 分钟的曲谱。");
    }
    public static Score FromMidi(byte[] bytes, string title = "MIDI 曲谱", string source = "") => MidiReader.Read(bytes, title, source);
}
