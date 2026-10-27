namespace Harmonica.Core;

public sealed record Note(int Pitch, double StartMs, double DurationMs, int Track = 1, int Velocity = 100);
public sealed record SourceSettings(int? Track = null, int Transpose = 0, double Speed = 1, double Gate = 1, bool Fold = true);
public sealed record Score(string Title, IReadOnlyList<Note> Notes, IReadOnlyDictionary<int, string> Tracks,
    SourceSettings Settings, string Source = "", IReadOnlyList<string>? Warnings = null);
public enum Control { Z, X, C, V, B, N, M, Comma, MouseLeft, MouseMiddle, MouseRight }
public sealed record InputEvent(double TimeMs, Control Control, bool Down);
public sealed record AudibleNote(int Pitch, double StartMs, double DurationMs);
public sealed record CompileOptions(int? Track = null, int Transpose = 0, double Speed = 1,
    double Gate = 0.92, bool Fold = true, bool Highest = true, int LeadMs = 12, int MinHoldMs = 35, int GapMs = 8, bool TrimStart = false);
public sealed record Compilation(IReadOnlyList<InputEvent> Events, IReadOnlyList<AudibleNote> Notes,
    double DurationMs, int SourceCount, int Adjusted, int Dropped, string Summary);
public interface IInputSink { void Emit(InputEvent input); void ReleaseAll(); }
public interface IPlaybackClock
{
    double ElapsedMs { get; }
    void Start();
    void WaitUntil(double deadlineMs, CancellationToken token, Func<bool> focused);
}
public enum EndReason { Finished, Stopped, FocusLost, Failed }
public sealed record PlaybackResult(EndReason Reason, string? Error, int Sent);
public sealed class FocusLostException : Exception { public FocusLostException() : base("目标窗口已失去焦点，演奏已停止。") { } }
