using System.Text;
using System.IO;
using Harmonica.Core;
using Harmonica.Tests;

var tests = new List<(string, Action)>();
void Test(string name, Action test) => tests.Add((name, test));
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}, actual {actual}"); }
void True(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (FormatException) { return; } throw new Exception("invalid input was accepted"); }
Score Song(params Note[] notes) => new("测试曲", notes, new Dictionary<int, string> { [1] = "主旋律" }, new());

const string json = """{"format":"gtiartist-score-v1","title":"测试","tracks":{"1":"Flute"},"notes":[{"pitch":60,"start":1.5,"duration":0.5,"track":1}],"playback_settings":{"track":1,"transpose":2,"speed":1.25}}""";
Test("JSON seconds become milliseconds; source settings remain separate", () => {
    var s = ScoreLoader.FromJson(json); Equal(1500d, s.Notes[0].StartMs); Equal(500d, s.Notes[0].DurationMs);
    Equal(60, s.Notes[0].Pitch); Equal(2, s.Settings.Transpose); Equal(1.25, s.Settings.Speed);
});
Test("Malformed/unknown JSON, negative times, empty notes and invalid pitch are rejected", () => {
    Reject(() => ScoreLoader.FromJson("{")); Reject(() => ScoreLoader.FromJson(json.Replace("gtiartist-score-v1", "unknown")));
    Reject(() => ScoreLoader.FromJson(json.Replace("1.5", "-1.5")));
    Reject(() => ScoreLoader.FromJson(json.Replace("\"pitch\":60", "\"pitch\":128")));
    Reject(() => ScoreLoader.FromJson("""{"format":"gtiartist-score-v1","notes":[],"title":"x"}"""));
});
Test("All 38 chromatic instrument pitches map to valid controls", () => {
    foreach (int pitch in Enumerable.Range(48, 38)) {
        var p = ScoreCompiler.Compile(Song(new Note(pitch, 0, 300)), new()); Equal(1, p.Notes.Count); Equal(pitch, p.Notes[0].Pitch);
        True(p.Events.Count(e => e.Down && e.Control < Control.MouseLeft) == 1);
        var down = p.Events.Where(e => e.Down).Select(e => e.Control).ToArray();
        True(!(down.Contains(Control.MouseLeft) && down.Contains(Control.MouseRight)));
        foreach (var c in down) True(p.Events.Any(e => !e.Down && e.Control == c), "missing release");
    }
});
Test("Transpose and speed apply once without changing source", () => {
    var s = Song(new Note(60, 1000, 500)); var p = ScoreCompiler.Compile(s, new(Transpose: 2, Speed: 2));
    Equal(62, p.Notes[0].Pitch); Equal(512d, p.Notes[0].StartMs); Equal(60, s.Notes[0].Pitch); Equal(1000d, s.Notes[0].StartMs);
});
Test("Polyphonic and overlapping notes become one voice with explicit adjustment report", () => {
    var p = ScoreCompiler.Compile(Song(new Note(60, 0, 800), new(64, 0, 800), new(67, 400, 400)), new());
    Equal(2, p.Notes.Count); Equal(64, p.Notes[0].Pitch); True(p.Dropped >= 1);
    True(p.Notes[0].StartMs + p.Notes[0].DurationMs <= p.Notes[1].StartMs);
});
Test("Fold keeps notes playable; disabling fold reports dropped notes", () => {
    var s = Song(new Note(96, 0, 500), new(60, 1000, 500));
    var folded = ScoreCompiler.Compile(s, new()); True(folded.Notes.All(n => n.Pitch is >= 48 and <= 85)); True(folded.Adjusted > 0);
    var raw = ScoreCompiler.Compile(s, new(Fold: false)); Equal(1, raw.Notes.Count); Equal(1, raw.Dropped);
});
Test("Track selection excludes other tracks", () => {
    var p = ScoreCompiler.Compile(Song(new Note(60, 0, 300, 1), new(80, 0, 300, 2)), new(Track: 1)); Equal(60, p.Notes.Single().Pitch);
});
Test("Invalid speed and impossible short score fail before input", () => {
    Reject(() => ScoreCompiler.Compile(Song(new Note(60, 0, 100)), new(Speed: 0)));
    Reject(() => ScoreCompiler.Compile(Song(new Note(60, 0, 5)), new()));
});
Test("MIDI tempo changes preserve absolute note time", () => {
    var s = ScoreLoader.FromMidi(Fixtures.Midi()); Equal(2, s.Notes.Count);
    Equal(500d, s.Notes[0].DurationMs); Equal(500d, s.Notes[1].StartMs); Equal(1000d, s.Notes[1].DurationMs);
});
Test("MIDI running status and channel identity are supported", () => {
    var s = ScoreLoader.FromMidi(Fixtures.RunningMidi()); Equal(2, s.Notes.Count); True(s.Tracks.Count == 1);
});
Test("Truncated MIDI and SMPTE timing are rejected", () => {
    Reject(() => ScoreLoader.FromMidi([1, 2, 3])); var b = Fixtures.Midi(); b[12] = 0xE7; Reject(() => ScoreLoader.FromMidi(b));
});
Compilation Plan() => ScoreCompiler.Compile(Song(new Note(60, 0, 500)), new());
Test("Normal playback releases owned controls", () => {
    var sink = new FakeSink(); var r = new Playback().Run(Plan(), sink, new FakeClock(), () => true, default);
    Equal(EndReason.Finished, r.Reason); True(sink.Held.Count == 0); Equal(1, sink.ReleaseCalls);
});
Test("Cancellation during a held note releases it and sends no later presses", () => {
    using var cts = new CancellationTokenSource(); var sink = new FakeSink();
    var clock = new FakeClock { BeforeWait = deadline => { if (deadline > 100) cts.Cancel(); } };
    var r = new Playback().Run(Plan(), sink, clock, () => true, cts.Token);
    Equal(EndReason.Stopped, r.Reason); True(sink.Held.Count == 0); Equal(1, sink.Events.Count(e => e.Down));
});
Test("Focus loss during a long wait aborts and releases", () => {
    bool focus = true; var sink = new FakeSink(); var clock = new FakeClock { BeforeWait = d => { if (d > 100) focus = false; } };
    var r = new Playback().Run(Plan(), sink, clock, () => focus, default);
    Equal(EndReason.FocusLost, r.Reason); True(sink.Held.Count == 0);
});
Test("Input failure and release failure are visible failures", () => {
    var sink = new FakeSink { FailSend = true }; var r = new Playback().Run(Plan(), sink, new FakeClock(), () => true, default);
    Equal(EndReason.Failed, r.Reason); Equal(1, sink.ReleaseCalls);
    sink = new FakeSink { FailRelease = true }; r = new Playback().Run(Plan(), sink, new FakeClock(), () => true, default);
    Equal(EndReason.Failed, r.Reason); True(r.Error?.Contains("release") == true);
});
Test("No initial focus means no key press", () => {
    var sink = new FakeSink(); var r = new Playback().Run(Plan(), sink, new FakeClock(), () => false, default);
    Equal(EndReason.FocusLost, r.Reason); Equal(0, sink.Events.Count);
});
if (args.Length > 0 && Directory.Exists(args[0])) Test("User library: every existing JSON loads and produces a playable plan", () => {
    var files = Directory.GetFiles(args[0], "*.json"); True(files.Length > 0);
    foreach (var f in files) { var s = ScoreLoader.Load(f); var p = ScoreCompiler.Compile(s, new(Track: s.Settings.Track)); True(p.Notes.Count > 0, Path.GetFileName(f)); }
    Console.WriteLine($"  library: {files.Length} scores checked");
});
int failed = 0;
Test("Cancellation during modifier lead releases mouse buttons before any note key", () => {
    using var cts = new CancellationTokenSource(); var sink = new FakeSink();
    var p = ScoreCompiler.Compile(Song(new Note(49, 0, 500)), new());
    var clock = new FakeClock { BeforeWait = d => { if (d > 0) cts.Cancel(); } };
    var r = new Playback().Run(p, sink, clock, () => true, cts.Token);
    Equal(EndReason.Stopped, r.Reason); Equal(0, sink.Held.Count);
    Equal(0, sink.Events.Count(e => e.Down && e.Control < Control.MouseLeft)); True(sink.Events.Count > 0);
});
Test("Real clock cancels a long rest without waiting for the deadline", () => {
    using var cts = new CancellationTokenSource(); var sink = new FakeSink();
    var clock = new SystemPlaybackClock(); var p = ScoreCompiler.Compile(Song(new Note(60, 10000, 300)), new());
    cts.CancelAfter(30);
    var r = new Playback().Run(p, sink, clock, () => true, cts.Token);
    Equal(EndReason.Stopped, r.Reason); Equal(0, sink.Events.Count); True(clock.ElapsedMs < 1000);
});
Test("Real clock detects focus loss during an owned held note", () => {
    var clock = new SystemPlaybackClock(); var sink = new FakeSink();
    var r = new Playback().Run(Plan(), sink, clock, () => clock.ElapsedMs < 30, default);
    Equal(EndReason.FocusLost, r.Reason); True(sink.Events.Any(e => e.Down)); Equal(0, sink.Held.Count); True(clock.ElapsedMs < 500);
});
Test("Native keyboard events use scan codes and a matching release flag", () => {
    var down = Harmonica.Desktop.NativeInputSink.Build(Control.Z, true);
    var up = Harmonica.Desktop.NativeInputSink.Build(Control.Z, false);
    Equal((ushort)0x2C, down.Data.Keyboard.Scan); Equal((ushort)0, down.Data.Keyboard.Vk);
    Equal(0x8u, down.Data.Keyboard.Flags); Equal(0xAu, up.Data.Keyboard.Flags);
});
Test("Leading silence can be trimmed without changing intervals", () => {
    var p = ScoreCompiler.Compile(Song(new Note(60, 2000, 300), new(62, 3000, 300)), new(TrimStart: true));
    Equal(12d, p.Notes[0].StartMs); Equal(1012d, p.Notes[1].StartMs);
});
Test("Normalized JSON round-trips note times and track settings", () => {
    var s = ScoreLoader.FromJson(json); var restored = ScoreLoader.FromJson(ScoreWriter.Serialize(s));
    Equal(s.Notes[0], restored.Notes[0]); Equal(s.Settings, restored.Settings);
});
Test("Offline WAV rendering creates bounded PCM with the compiled duration", () => {
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
    try { var p = Plan(); AudioRenderer.Render(p, path); var b = File.ReadAllBytes(path);
        Equal("RIFF", Encoding.ASCII.GetString(b,0,4)); Equal("WAVE", Encoding.ASCII.GetString(b,8,4));
        Equal((short)1, BitConverter.ToInt16(b,22)); Equal(22050, BitConverter.ToInt32(b,24));
        True(b.Length > 44); Equal(b.Length - 44, BitConverter.ToInt32(b,40));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { AudioRenderer.Render(p, path + ".cancel", cts.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
    } finally { File.Delete(path); File.Delete(path + ".cancel"); }
});
Test("Native INPUT has correct x64 size and starts with no held controls", () => {
    Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<Harmonica.Desktop.WinInput>());
    Equal(0, new Harmonica.Desktop.NativeInputSink(_ => 1).HeldCount);
});
Test("Native send failure retains attempted control until successful release", () => {
    bool fail = true; var native = new Harmonica.Desktop.NativeInputSink(_ => fail ? 0u : 1u);
    try { native.Emit(new(0, Control.Z, true)); throw new Exception("failure ignored"); } catch (IOException) { }
    Equal(1, native.HeldCount);
    try { native.ReleaseAll(); throw new Exception("release failure ignored"); } catch (IOException) { }
    Equal(1, native.HeldCount); fail = false; native.ReleaseAll(); Equal(0, native.HeldCount);
});
Test("Native rejects controls outside the instrument whitelist before calling Windows", () => {
    int calls = 0; var native = new Harmonica.Desktop.NativeInputSink(_ => { calls++; return 1; });
    Reject(() => native.Emit(new(0, (Control)100, true))); Equal(0, calls);
});
foreach (var (name, run) in tests) { try { run(); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
Console.WriteLine($"RESULT {tests.Count - failed}/{tests.Count} passed");

Console.WriteLine("\n=== 回归测试：修复验证 ===");
try { RegressionTests.RunAll(); }
catch (Exception e) { Console.WriteLine($"回归测试失败: {e.Message}"); return 1; }

return failed == 0 ? 0 : 1;

sealed class FakeSink : IInputSink
{
    public List<InputEvent> Events = []; public HashSet<Control> Held = []; public int ReleaseCalls;
    public bool FailSend, FailRelease;
    public void Emit(InputEvent e) { Events.Add(e); if (e.Down) Held.Add(e.Control); else Held.Remove(e.Control); if (FailSend) throw new IOException("send failed"); }
    public void ReleaseAll() { ReleaseCalls++; if (FailRelease) throw new IOException("release failed"); Held.Clear(); }
}
sealed class FakeClock : IPlaybackClock
{
    public Action<double>? BeforeWait; public double ElapsedMs { get; private set; }
    public void Start() => ElapsedMs = 0;
    public void WaitUntil(double deadlineMs, CancellationToken token, Func<bool> focused) { BeforeWait?.Invoke(deadlineMs); token.ThrowIfCancellationRequested(); if (!focused()) throw new FocusLostException(); ElapsedMs = deadlineMs; }
}
static class Fixtures
{
    public static byte[] Midi() => Wrap([0,0xFF,0x51,3,7,0xA1,0x20, 0,0x90,60,100, 0x83,0x60,0x80,60,0,
        0,0xFF,0x51,3,15,0x42,0x40, 0,0x90,62,100, 0x83,0x60,0x80,62,0, 0,0xFF,0x2F,0]);
    public static byte[] RunningMidi() => Wrap([0,0x90,60,100, 0x81,0x70,62,100, 0x81,0x70,60,0, 0,62,0, 0,0xFF,0x2F,0]);
    static byte[] Wrap(byte[] track) {
        using var m = new MemoryStream(); using var w = new BinaryWriter(m); w.Write(Encoding.ASCII.GetBytes("MThd"));
        w.Write(new byte[]{0,0,0,6,0,0,0,1,1,0xE0}); w.Write(Encoding.ASCII.GetBytes("MTrk"));
        w.Write(new[]{(byte)(track.Length>>24),(byte)(track.Length>>16),(byte)(track.Length>>8),(byte)track.Length}); w.Write(track); return m.ToArray();
    }
}
