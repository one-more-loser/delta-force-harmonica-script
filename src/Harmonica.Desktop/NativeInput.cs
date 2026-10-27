using System.Runtime.InteropServices;
using System.IO;
using Harmonica.Core;
namespace Harmonica.Desktop;

[StructLayout(LayoutKind.Sequential)]
public struct WinInput { public uint Type; public InputUnion Data; }
[StructLayout(LayoutKind.Explicit)]
public struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
[StructLayout(LayoutKind.Sequential)]
public struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nuint ExtraInfo; }
[StructLayout(LayoutKind.Sequential)]
public struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public nuint ExtraInfo; }

public sealed class NativeInputSink : IInputSink
{
    readonly Func<WinInput[], uint> api;
    readonly HashSet<Control> held = [];
    readonly object sync = new();
    public NativeInputSink(Func<WinInput[], uint>? api = null) { this.api = api ?? (inputs => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WinInput>())); }
    public int HeldCount { get { lock (sync) return held.Count; } }
    public void Emit(InputEvent input)
    {
        lock (sync)
        {
            var value = Build(input.Control, input.Down);
            // 发送失败也保留可能按下的键，下一步释放会逐项尝试。
            if (input.Down) held.Add(input.Control);
            if (api([value]) != 1) throw new IOException($"Windows 未接受输入（错误码 {Marshal.GetLastWin32Error()}）。请核对窗口和权限；目标程序也可能限制模拟输入。");
            if (!input.Down) held.Remove(input.Control);
        }
    }
    public void ReleaseAll()
    {
        lock (sync)
        {
            var failed = new List<string>();
            foreach (var control in held.OrderBy(c => c >= Control.MouseLeft ? 1 : 0).ToArray())
                try { Emit(new(0, control, false)); } catch (Exception e) { failed.Add(control + ": " + e.Message); }
            if (failed.Count > 0) throw new IOException(string.Join("；", failed));
        }
    }
    public static WinInput Build(Control control, bool down)
    {
        int index = (int)control;
        if (index is < 0 or > 10) throw new FormatException("拒绝发送口琴范围外的按键。");
        if (index < 8)
        {
            ushort[] scans = [0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33];
            return new() { Type = 1, Data = new() { Keyboard = new() { Scan = scans[index], Flags = 0x0008u | (down ? 0u : 0x0002u) } } };
        }
        uint flag = control switch {
            Control.MouseLeft => down ? 0x0002u : 0x0004u,
            Control.MouseMiddle => down ? 0x0020u : 0x0040u,
            Control.MouseRight => down ? 0x0008u : 0x0010u,
            _ => throw new FormatException("无效鼠标键。")
        };
        return new() { Type = 0, Data = new() { Mouse = new() { Flags = flag } } };
    }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, WinInput[] inputs, int size);
}
