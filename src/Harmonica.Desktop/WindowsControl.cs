using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
namespace Harmonica.Desktop;

public sealed record TargetWindow(nint Handle, string Title)
{
    public override string ToString() => Title;
}
public static class WindowsControl
{
    public static string Title(nint h) { var text = new StringBuilder(512); GetWindowText(h, text, text.Capacity); return text.ToString(); }
    public static IReadOnlyList<TargetWindow> ListWindows()
    {
        var result = new List<TargetWindow>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint pid); string title = Title(h); if (IsWindowVisible(h) && pid != Environment.ProcessId && title.Length > 0) result.Add(new(h, title)); return true; }, 0);
        return result.OrderBy(w => w.Title, StringComparer.CurrentCulture).ToArray();
    }
    public static nint ResolveTarget(nint requested)
    {
        nint current = GetForegroundWindow(); if (current == 0 || !IsWindow(current)) return 0;
        GetWindowThreadProcessId(current, out uint pid); if (pid == Environment.ProcessId) return 0;
        if (requested != 0) return current == requested ? current : 0;
        string title = Title(current); var cls = new StringBuilder(256); GetClassName(current, cls, cls.Capacity);
        bool gameTitle = title.Contains("三角洲", StringComparison.OrdinalIgnoreCase) || title.Contains("Delta", StringComparison.OrdinalIgnoreCase);
        return gameTitle && cls.ToString().Contains("Unreal", StringComparison.OrdinalIgnoreCase) ? current : 0;
    }
    public static bool Focused(nint target) => target != 0 && GetForegroundWindow() == target && IsWindow(target);
    public static bool InstrumentControlsHeld()
    {
        int[] keys = [0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, 0xBC, 1, 2, 4, 0x10, 0x11, 0x12];
        return keys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
    }
    public delegate bool EnumWindowProc(nint h, nint p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc cb, nint p);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
public sealed class HotkeyController : IDisposable
{
    readonly nint hwnd; readonly HwndSource source; readonly int id = 0x4A71;
    bool registered; public string? Error { get; private set; }
    public event Action? Pressed;
    public bool Ready => registered;
    public HotkeyController(nint hwnd) { this.hwnd = hwnd; source = HwndSource.FromHwnd(hwnd); source.AddHook(Message); }
    public bool Register(int functionKey)
    {
        if (registered) UnregisterHotKey(hwnd, id); registered = false;
        if (functionKey is < 1 or > 24 || functionKey == 12) { Error = "该功能键不可用。"; return false; }
        registered = RegisterHotKey(hwnd, id, 0x4000, (uint)(0x6F + functionKey));
        Error = registered ? null : $"F{functionKey} 注册失败（错误码 {Marshal.GetLastWin32Error()}），可能被其他程序占用。请换一个热键。";
        return registered;
    }
    nint Message(nint h, int msg, nint w, nint l, ref bool handled)
    { if (msg == 0x0312 && w == id) { handled = true; Pressed?.Invoke(); } return 0; }
    public void Dispose() { if (registered) UnregisterHotKey(hwnd, id); registered = false; source.RemoveHook(Message); }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint h, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint h, int id);
}
public sealed class ForegroundMonitor : IDisposable
{
    readonly WinEventProc callback; readonly nint hook;
    public bool Available => hook != 0;
    public ForegroundMonitor(Action<nint> changed)
    {
        callback = (_, _, h, _, _, _, _) => changed(h);
        // OUTOFCONTEXT 的回调留在本程序，不映射到游戏进程；同时保留播放线程的焦点检查。
        hook = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
    }
    public void Dispose() { if (hook != 0) UnhookWinEvent(hook); GC.KeepAlive(callback); }
    delegate void WinEventProc(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);
}
