namespace Harmonica.Core;
public static class ScoreCompiler
{
    static readonly int[] Degrees = [0, 2, 4, 5, 7, 9, 11, 12];
    public static Compilation Compile(Score score, CompileOptions options)
    {
        var o = options;
        if (!double.IsFinite(o.Speed) || o.Speed is < 0.25 or > 2 || !double.IsFinite(o.Gate) || o.Gate is < 0.1 or > 1
            || o.Transpose is < -48 or > 48 || o.LeadMs is < 0 or > 100 || o.MinHoldMs is < 1 or > 100 || o.GapMs is < 0 or > 100)
            throw new FormatException("速度、移调或时序设置超出范围。");
        var selected = score.Notes.Where(n => o.Track == null || n.Track == o.Track).OrderBy(n => n.StartMs).ThenByDescending(n => o.Highest ? n.Pitch : -n.Pitch).ToList();
        int total = selected.Count, adjusted = 0, dropped = 0;
        var single = new List<Note>(); double groupStart = -100; double origin = o.TrimStart && selected.Count > 0 ? selected[0].StartMs : 0;
        foreach (var source in selected)
        {
            ScoreLoader.ValidateNote(source);
            if (Math.Abs(source.StartMs - groupStart) < 0.5) { dropped++; continue; }
            groupStart = source.StartMs; int pitch = source.Pitch + o.Transpose;
            if (pitch is < 48 or > 85)
            {
                if (!o.Fold) { dropped++; continue; }
                while (pitch < 48) pitch += 12; while (pitch > 85) pitch -= 12; adjusted++;
            }
            single.Add(source with { Pitch = pitch, StartMs = (source.StartMs - origin) / o.Speed, DurationMs = source.DurationMs / o.Speed });
        }
        var events = new List<InputEvent>(); var audible = new List<AudibleNote>();
        for (int i = 0; i < single.Count; i++)
        {
            var n = single[i]; double start = n.StartMs, down = start + o.LeadMs;
            double end = start + Math.Min(5000, n.DurationMs * o.Gate);
            if (end < start + n.DurationMs * o.Gate - 0.01) adjusted++;
            if (i + 1 < single.Count && end > single[i + 1].StartMs - o.GapMs) { end = single[i + 1].StartMs - o.GapMs; adjusted++; }
            if (end - down < o.MinHoldMs) { dropped++; continue; }
            var (key, mods) = Fingering(n.Pitch);
            foreach (var m in mods) events.Add(new(start, m, true));
            events.Add(new(down, key, true)); events.Add(new(end, key, false));
            foreach (var m in mods.Reverse()) events.Add(new(end, m, false));
            audible.Add(new(n.Pitch, down, end - down));
        }
        if (audible.Count == 0) throw new FormatException("没有可演奏的音符。请检查音轨、音域或降低速度；过短音符无法可靠按下。");
        return new(events.OrderBy(e => e.TimeMs).ThenBy(e => e.Down ? 1 : 0).ToArray(), audible,
            events.Max(e => e.TimeMs) + 100, total, adjusted, dropped,
            $"原始 {total} 音 → 演奏 {audible.Count} 音 · 调整 {adjusted} 处 · 舍弃 {dropped} 音");
    }
    public static (Control Key, Control[] Modifiers) Fingering(int pitch)
    {
        (Control, Control[])? best = null; int cost = int.MaxValue;
        for (int octave = -1; octave <= 1; octave++) for (int sharp = 0; sharp <= 1; sharp++) for (int key = 0; key < 8; key++)
        {
            if (60 + octave * 12 + Degrees[key] + sharp != pitch) continue;
            int c = Math.Abs(octave) * 10 + sharp * 10 + key;
            if (c >= cost) continue; cost = c;
            var mods = new List<Control>(); if (octave < 0) mods.Add(Control.MouseLeft); if (octave > 0) mods.Add(Control.MouseRight); if (sharp != 0) mods.Add(Control.MouseMiddle);
            best = ((Control)key, mods.ToArray());
        }
        return best ?? throw new FormatException($"音高 {pitch} 超出口琴音域。");
    }
}
