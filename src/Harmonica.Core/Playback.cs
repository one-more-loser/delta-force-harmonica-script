using System.Diagnostics;
namespace Harmonica.Core;
public sealed class Playback
{
    public PlaybackResult Run(Compilation plan, IInputSink sink, IPlaybackClock clock,
        Func<bool> focused, CancellationToken token)
    {
        EndReason reason = EndReason.Finished; string? error = null; int sent = 0;
        try
        {
            token.ThrowIfCancellationRequested(); if (!focused()) throw new FocusLostException();
            clock.Start();
            foreach (var input in plan.Events)
            {
                clock.WaitUntil(input.TimeMs, token, focused);
                token.ThrowIfCancellationRequested(); if (!focused()) throw new FocusLostException();
                // 严重迟到时终止，避免调度恢复后瞬间倾倒大量过期按键。
                if (clock.ElapsedMs - input.TimeMs > 150) throw new IOException("系统调度延迟超过 150ms，演奏已停止。可关闭高负载任务后重试。");
                sink.Emit(input); sent++;
            }
        }
        catch (OperationCanceledException) { reason = EndReason.Stopped; }
        catch (FocusLostException e) { reason = EndReason.FocusLost; error = e.Message; }
        catch (Exception e) { reason = EndReason.Failed; error = e.Message; }
        finally
        {
            try { sink.ReleaseAll(); }
            catch (Exception e) { reason = EndReason.Failed; error = (error == null ? "" : error + "；") + "释放按键失败（release）：" + e.Message; }
        }
        return new(reason, error, sent);
    }
}
public sealed class SystemPlaybackClock : IPlaybackClock
{
    readonly Stopwatch clock = new();
    public double ElapsedMs => clock.Elapsed.TotalMilliseconds;
    public void Start() => clock.Restart();
    public void WaitUntil(double deadlineMs, CancellationToken token, Func<bool> focused)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); if (!focused()) throw new FocusLostException();
            double left = deadlineMs - ElapsedMs; if (left <= 0) return;
            if (left > 2) token.WaitHandle.WaitOne((int)Math.Clamp(left - 1, 1, 4)); else Thread.SpinWait(50);
        }
    }
}
