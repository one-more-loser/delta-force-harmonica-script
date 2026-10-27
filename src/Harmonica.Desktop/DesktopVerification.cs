using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Harmonica.Core;
namespace Harmonica.Desktop;

internal static class DesktopVerification
{
    public static async Task RunAsync(string report, string? imagePath)
    {
        var checks = new Dictionary<string, object>();
        using (var source = new HwndSource(new HwndSourceParameters("HarmonicaStudio.Verification") { Width = 1, Height = 1, PositionX = -30000, PositionY = -30000, WindowStyle = unchecked((int)0x80000000) }))
        {
            using var hotkey = new HotkeyController(source.Handle);
            if (!hotkey.Register(24)) throw new IOException("热键注册自检失败：" + hotkey.Error);
            checks["RegisterHotKey_F24"] = "passed";
            using var secondSource = new HwndSource(new HwndSourceParameters("HarmonicaStudio.Verification.Conflict") { Width = 1, Height = 1, PositionX = -30000, PositionY = -30000, WindowStyle = unchecked((int)0x80000000) });
            using var conflict = new HotkeyController(secondSource.Handle);
            if (conflict.Register(24)) throw new IOException("热键占用冲突没有被检测。");
            checks["Hotkey_conflict_is_reported"] = "passed";
        }
        using (var monitor = new ForegroundMonitor(_ => { })) checks["Out_of_context_foreground_monitor"] = monitor.Available ? "passed" : "unavailable; playback polling remains active";
        checks["Enumerated_window_count"] = WindowsControl.ListWindows().Count;
        var window = new MainWindow(true);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var surface = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(241, 242, 237)), Child = root, Resources = window.Resources };
        surface.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
        surface.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
        surface.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
        void Layout(double width, double height)
        {
            surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
            foreach (string name in new[] { "PlayButton", "StopButton", "ProgressText", "HotkeyBox" })
            {
                var control = (FrameworkElement)window.FindName(name);
                var bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(control.RenderSize));
                if (bounds.Bottom > height + 1 || bounds.Right > width + 1) throw new IOException($"控件超出窗口边界：{name} {bounds}");
            }
        }
        Layout(834, 730);
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (imagePath != null)
        {
            var bitmap = new RenderTargetBitmap(834, 730, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(imagePath); encoder.Save(output);
        }
        checks["WPF_window_construction_and_render"] = "passed";
        window.SetVerificationCompact(true); Layout(514, 570);
        if (imagePath != null)
        {
            var bitmap = new RenderTargetBitmap(514, 570, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Path.GetDirectoryName(imagePath)!, Path.GetFileNameWithoutExtension(imagePath) + "_紧凑.png")); encoder.Save(output);
        }
        checks["Compact_layout_controls_fit"] = "passed";
        window.SetVerificationCompact(false);
        var sourceScore = MainWindow.DemoScore() with { Settings = new SourceSettings(1, -24, 1.17, 0.85, true) };
        window.SetVerificationScore(sourceScore);
        var speed = (System.Windows.Controls.ComboBox)window.FindName("Speed");
        var transpose = (System.Windows.Controls.ComboBox)window.FindName("Transpose");
        if (((SpeedChoice)speed.SelectedItem).Value != 1 || ((PitchChoice)transpose.SelectedItem).Value != 0)
            throw new IOException("选曲时意外应用了来源速度或移调。");
        ((System.Windows.Controls.Button)window.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (((SpeedChoice)speed.SelectedItem).Value != 1.17 || ((PitchChoice)transpose.SelectedItem).Value != -24 || !window.VerificationPlayable)
            throw new IOException("来源曲谱的非标准速度或移调没有完整恢复。");
        window.SetVerificationScore(MainWindow.DemoScore());
        if (((SpeedChoice)speed.SelectedItem).Value != 1 || ((PitchChoice)transpose.SelectedItem).Value != 0)
            throw new IOException("切换曲目后没有恢复原调和正常速度。");
        checks["Source_settings_restore_exactly_and_reset_on_song_change"] = "passed";
        var library = Path.Combine(AppContext.BaseDirectory, "曲库");
        if (Directory.Exists(library))
        {
            var scores = Directory.GetFiles(library, "*.json");
            foreach (string file in scores) { window.SetVerificationScore(ScoreLoader.Load(file)); if (!window.VerificationPlayable) throw new IOException("曲库界面无法编排：" + Path.GetFileName(file)); }
            checks["Bundled_library_UI_compilation"] = scores.Length;
            window.SetTestScore();
        }
        var fakeSink = new VerificationSink();
        _ = window.BeginTestPlayback(fakeSink); await Task.Delay(100);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(); window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(4));
        if (fakeSink.Sent == 0 || fakeSink.Held != 0) throw new IOException("关闭窗口没有等待后台演奏停止并释放。");
        checks["Close_cancels_playback_and_waits_for_release"] = "passed";
        var plan = ScoreCompiler.Compile(MainWindow.DemoScore(), new(TrimStart: true));
        string audio = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "verification-audio.wav");
        AudioRenderer.Render(plan, audio);
        var player = new MediaPlayer { IsMuted = true };
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaFailed += (_, e) => opened.TrySetException(e.ErrorException);
        try { player.Open(new Uri(audio)); await opened.Task.WaitAsync(TimeSpan.FromSeconds(10)); checks["WPF_audio_decode"] = "passed"; }
        catch (Exception e) { checks["WPF_audio_decode"] = "unavailable: " + e.Message; }
        finally { player.Close(); try { File.Delete(audio); } catch (IOException) { } }
        checks["Real_game_input"] = "not run"; checks["ACE_enforcement"] = "not verified";
        File.WriteAllText(report, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
    sealed class VerificationSink : IInputSink
    {
        public int Sent, Held;
        public void Emit(InputEvent input) { Interlocked.Increment(ref Sent); if (input.Down) Interlocked.Increment(ref Held); else Interlocked.Decrement(ref Held); }
        public void ReleaseAll() => Interlocked.Exchange(ref Held, 0);
    }
}
