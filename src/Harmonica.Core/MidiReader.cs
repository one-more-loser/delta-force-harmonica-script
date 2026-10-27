using System.Text;
namespace Harmonica.Core;

// 标准 SMF 解析：轨道/通道分离，跨轨速度表，NoteOn/NoteOff 配对。无设备或网络依赖。
internal static class MidiReader
{
    sealed record TickNote(int Pitch, long Start, long End, int Track, int Velocity);
    public static Score Read(byte[] bytes, string title, string source)
    {
        try
        {
            if (bytes.Length > ScoreLoader.MaxBytes) throw new FormatException("MIDI 文件过大。");
            using var r = new BinaryReader(new MemoryStream(bytes));
            int B() { int b = r.BaseStream.ReadByte(); if (b < 0) throw new EndOfStreamException(); return b; }
            int U16() => (B() << 8) | B();
            uint U32() => ((uint)B() << 24) | ((uint)B() << 16) | ((uint)B() << 8) | (uint)B();
            string Chunk() => Encoding.ASCII.GetString(r.ReadBytes(4));
            if (Chunk() != "MThd") throw new FormatException("不是标准 MIDI 文件：缺少 MThd。");
            uint length = U32(); if (length < 6 || length > bytes.Length - r.BaseStream.Position) throw new FormatException("MIDI 文件头长度错误。");
            int format = U16(), count = U16(), division = U16();
            if (format is not (0 or 1) || count is < 1 or > 256 || (format == 0 && count != 1)) throw new FormatException("支持标准 MIDI Format 0/1，最多 256 条轨道。");
            if (division == 0 || (division & 0x8000) != 0) throw new FormatException("暂不支持 SMPTE 时间码 MIDI，请导出为 PPQ MIDI。");
            r.BaseStream.Position += length - 6;
            var tempos = new List<(long Tick, int Tempo, int Order)> { (0, 500000, -1) };
            var notes = new List<TickNote>(); var names = new Dictionary<int, string>(); var warnings = new HashSet<string>();
            int events = 0;
            for (int ti = 0; ti < count; ti++)
            {
                if (Chunk() != "MTrk") throw new FormatException($"第 {ti + 1} 轨缺少 MTrk。");
                long size = U32(), end = checked(r.BaseStream.Position + size);
                if (end > bytes.Length) throw new FormatException("MIDI 轨道被截断。");
                int TB() { if (r.BaseStream.Position >= end) throw new FormatException("MIDI 事件被截断。"); return B(); }
                int VLQ() { int value = 0; for (int i = 0; i < 4; i++) { int b = TB(); value = (value << 7) | (b & 127); if (b < 128) return value; } throw new FormatException("MIDI 可变长度数值错误。"); }
                byte[] Data(int n) { if (n < 0 || n > end - r.BaseStream.Position) throw new FormatException("MIDI 事件长度越界。"); return r.ReadBytes(n); }
                long tick = 0; int running = 0; string trackName = $"音轨 {ti + 1}"; bool ended = false;
                var active = new Dictionary<(int Channel, int Pitch), Queue<(long Tick, int Velocity)>>();
                while (r.BaseStream.Position < end)
                {
                    if (++events > 500000) throw new FormatException("MIDI 事件超过 50 万，文件过于复杂。");
                    tick = checked(tick + VLQ()); int status = TB(), first = -1;
                    if (status < 128) { if (running == 0) throw new FormatException("MIDI 缺少 running status。"); first = status; status = running; }
                    if (status == 0xFF)
                    {
                        int kind = TB(); byte[] data = Data(VLQ());
                        if (kind == 0x2F) { if (data.Length != 0) throw new FormatException("MIDI 结束事件错误。"); ended = true; r.BaseStream.Position = end; break; }
                        if (kind == 0x51) { if (data.Length != 3) throw new FormatException("MIDI 速度事件错误。"); int tempo = (data[0] << 16) | (data[1] << 8) | data[2]; if (tempo == 0) throw new FormatException("MIDI 速度不能为零。"); tempos.Add((tick, tempo, events)); }
                        if (kind == 3) { trackName = Encoding.UTF8.GetString(data); if (trackName.Length > 100) trackName = trackName[..100]; }
                        continue;
                    }
                    if (status is 0xF0 or 0xF7) { Data(VLQ()); running = 0; continue; }
                    if (status is < 0x80 or >= 0xF0) throw new FormatException("MIDI 事件状态不受支持。");
                    running = status; int type = status & 0xF0, ch = status & 15;
                    int a = first >= 0 ? first : TB(), b = type is 0xC0 or 0xD0 ? 0 : TB();
                    if (a > 127 || b > 127) throw new FormatException("MIDI 音符数据越界。");
                    int id = ti * 16 + ch + 1;
                    if (type == 0x90 && b != 0)
                    {
                        if (!active.TryGetValue((ch, a), out var q)) active[(ch, a)] = q = new();
                        q.Enqueue((tick, b)); names[id] = $"{trackName} · 通道 {ch + 1}" + (ch == 9 ? "（打击乐）" : "");
                    }
                    else if (type == 0x80 || (type == 0x90 && b == 0))
                    {
                        if (active.TryGetValue((ch, a), out var q) && q.Count > 0) { var n = q.Dequeue(); if (tick > n.Tick) notes.Add(new(a, n.Tick, tick, id, n.Velocity)); }
                    }
                    else if (type == 0xE0 || (type == 0xB0 && a == 64)) warnings.Add("弯音和延音踏板未还原，按音符起止时间演奏。");
                }
                if (!ended) throw new FormatException($"第 {ti + 1} 轨缺少结束事件，文件可能不完整。");
                foreach (var (key, q) in active) while (q.Count > 0) { var n = q.Dequeue(); if (tick > n.Tick) notes.Add(new(key.Pitch, n.Tick, tick, ti * 16 + key.Channel + 1, n.Velocity)); warnings.Add("部分音符缺少结束消息，已截到音轨末尾。"); }
            }
            if (notes.Count > 250000) throw new FormatException($"MIDI 解析出 {notes.Count} 个音符，超过 250,000 上限；请简化 MIDI 或减少音轨。");
            var table = tempos.OrderBy(x => x.Tick).ThenBy(x => x.Order).GroupBy(x => x.Tick).Select(g => g.Last()).ToArray();
            var times = new double[table.Length];
            for (int i = 1; i < table.Length; i++) times[i] = times[i - 1] + (table[i].Tick - table[i - 1].Tick) * (double)table[i - 1].Tempo / division / 1000;
            double Ms(long tick) { int lo = 0, hi = table.Length - 1; while (lo < hi) { int mid = (lo + hi + 1) / 2; if (table[mid].Tick <= tick) lo = mid; else hi = mid - 1; } return times[lo] + (tick - table[lo].Tick) * (double)table[lo].Tempo / division / 1000; }
            var result = notes.Select(n => new Note(n.Pitch, Ms(n.Start), Ms(n.End) - Ms(n.Start), n.Track, n.Velocity)).OrderBy(n => n.StartMs).ToArray();
            if (result.Length == 0) throw new FormatException("MIDI 中没有可用音符。");
            foreach (var n in result) ScoreLoader.ValidateNote(n);
            var candidates = result.Where(n => (n.Track - 1) % 16 != 9).GroupBy(n => n.Track).OrderByDescending(g => g.Count()).ToArray();
            int selected = candidates.Length > 0 ? candidates[0].Key : result[0].Track;
            return new(title, result, names, new(Track: selected), source, warnings.ToArray());
        }
        catch (Exception e) when (e is EndOfStreamException or OverflowException or IndexOutOfRangeException or ArgumentException)
        { throw new FormatException("MIDI 文件损坏或数据被截断。", e); }
    }
}
