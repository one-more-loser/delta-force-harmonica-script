using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Harmonica.Core;
namespace Harmonica.Desktop;

public sealed record LibraryItem(string Path, Score Score)
{
    public string Title => Score.Title;
    public string Detail => $"{Score.Notes.Count} 音 · {Score.Tracks.Count} 条音轨";
}
public sealed record TrackChoice(int? Id, string Label) { public override string ToString() => Label; }
public sealed record SpeedChoice(double Value) { public override string ToString() => $"{Value * 100:0}%"; }
public sealed record PitchChoice(int Value) { public override string ToString() => Value == 0 ? "原调" : $"{Value:+0;-0} 半音"; }

public partial class MainWindow : Window
{
    readonly StudioSettings settings;
    readonly bool testMode;
    readonly NativeInputSink sink = new();
    MediaPlayer media = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    List<LibraryItem> library = [];
    Score? score;
    Compilation? plan;
    HotkeyController? hotkeys;
    ForegroundMonitor? focusMonitor;
    CancellationTokenSource? operation;
    Task operationTask = Task.CompletedTask;
    SystemPlaybackClock? clock;
    bool binding, busy, gameMode, closeRequested, allowClose, compact;
    nint boundTarget;
    string? forcedStop;
    double gate = 0.92;

    public MainWindow(bool testMode = false)
    {
        this.testMode = testMode;
        if (!testMode) Storage.Initialize();
        settings = testMode ? new() : Storage.LoadSettings();
        InitializeComponent();
        binding = true;
        Speed.ItemsSource = new[] { 0.5, 0.75, 1, 1.25, 1.5, 2 }.Select(x => new SpeedChoice(x)).ToArray(); Speed.SelectedIndex = 2;
        Transpose.ItemsSource = Enumerable.Range(-48, 97).Select(x => new PitchChoice(x)).ToArray(); Transpose.SelectedIndex = 48;
        Timing.ItemsSource = new[] { "稳健时序（较宽间隔）", "标准时序" }; Timing.SelectedIndex = 0;
        Strategy.ItemsSource = new[] { "重叠时保留高音", "重叠时保留低音" }; Strategy.SelectedIndex = 0;
        HotkeyBox.ItemsSource = Enumerable.Range(6, 6).Select(i => $"F{i}").ToArray(); HotkeyBox.SelectedIndex = settings.Hotkey - 6;
        Targets.ItemsSource = new[] { new TargetWindow(0, "自动识别三角洲游戏窗口") }; Targets.SelectedIndex = 0;
        binding = false;
        media.Volume = settings.Volume;
        timer.Tick += (_, _) => UpdateProgress(); timer.Start();
        SourceInitialized += (_, _) => InitializeNative();
        Loaded += async (_, _) => { if (!testMode) { operationTask = InitializeLibraryAsync(); await operationTask; } };
        Closing += OnClosing;
        DragOver += (_, e) => { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) { operationTask = ImportAsync(paths); await operationTask; } };
        if (testMode) SetTestScore();
        SetCompact(settings.Compact || SystemParameters.WorkArea.Height < 790 || SystemParameters.WorkArea.Width < 880);
        UpdateControls();
    }
    void InitializeNative()
    {
        if (testMode) return;
        var handle = new WindowInteropHelper(this).Handle;
        hotkeys = new(handle); hotkeys.Pressed += ToggleFromHotkey; RegisterHotkey();
        focusMonitor = new(h => {
            if (busy && gameMode && boundTarget != 0 && h != boundTarget) { forcedStop = "目标窗口失去焦点，已停止；切回后请重新按热键。"; StopOperation(); }
        });
    }
    void RegisterHotkey()
    {
        if (hotkeys == null) return;
        if (!hotkeys.Register(settings.Hotkey)) SetMessage(hotkeys.Error ?? "热键不可用。", true);
        else SetMessage($"热键 F{settings.Hotkey} 已就绪。请先试听确认旋律。", false);
        UpdateControls();
    }
    async Task InitializeLibraryAsync()
    {
        if (busy || closeRequested) return;
        // 初始化清理也属于当前任务，避免 await 期间被试听、拖放或热键覆盖。
        busy = true; UpdateControls();
        try { await Storage.CleanupOldPreviewFilesAsync(); }
        catch (Exception e) { Storage.Log("启动试听清理失败：" + e); }
        finally { busy = false; UpdateControls(); }
        if (closeRequested) return;
        await RefreshLibraryAsync();
        if (!closeRequested) RefreshWindows();
    }
    async Task RefreshLibraryAsync(string? preferred = null)
    {
        if (busy) return;
        busy = true; UpdateControls();
        var failures = new List<string>();
        List<LibraryItem> loaded;
        try { loaded = await Task.Run(() => {
            var result = new List<LibraryItem>();
            foreach (var file in Directory.EnumerateFiles(Storage.LibraryRoot).Where(IsScoreFile))
                try { result.Add(new(file, ScoreLoader.Load(file))); } catch (Exception e) { failures.Add(Path.GetFileName(file) + "：" + e.Message); }
            return result.OrderBy(x => x.Title, StringComparer.CurrentCulture).ToList();
        }); }
        catch (Exception e) { SetMessage("读取曲库失败：" + e.Message, true); Storage.Log(e.ToString()); return; }
        finally { busy = false; UpdateControls(); }
        if (closeRequested) return;
        library = loaded;
        if (library.Count == 0)
        {
            var sample = DemoScore(); string path = Storage.Import(sample); library.Add(new(path, sample));
        }
        FilterLibrary();
        string? selected = preferred ?? settings.LastSong;
        Songs.SelectedItem = library.FirstOrDefault(x => string.Equals(Path.GetFileName(x.Path), selected, StringComparison.OrdinalIgnoreCase)) ?? library[0];
        if (failures.Count > 0) { Storage.Log(string.Join("\n", failures)); SetMessage($"已载入 {library.Count} 首，另有 {failures.Count} 个文件无效。首个错误：{failures[0]}", true); }
    }
    static bool IsScoreFile(string p) => Path.GetExtension(p).ToLowerInvariant() is ".json" or ".mid" or ".midi";
    void FilterLibrary()
    {
        if (Songs == null) return;
        string text = SearchBox?.Text.Trim() ?? "";
        var items = library.Where(x => x.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var previous = Songs.SelectedItem; binding = true; Songs.ItemsSource = items; Songs.SelectedItem = items.Contains(previous) ? previous : null; binding = false;
        LibraryCount.Text = $"{items.Length} 首";
    }
    void SearchChanged(object sender, TextChangedEventArgs e) { if (!binding && !busy) FilterLibrary(); }
    void SongChanged(object sender, SelectionChangedEventArgs e)
    {
        if (binding || busy || Songs.SelectedItem is not LibraryItem item) return;
        SetScore(item.Score); settings.LastSong = Path.GetFileName(item.Path); SaveSettings();
    }
    void SetScore(Score value)
    {
        score = value; binding = true;
        SongTitle.Text = value.Title; SongMeta.Text = $"{value.Notes.Count} 个音符 · {value.Tracks.Count} 条音轨 · 本地曲谱";
        var trackCounts = value.Notes.GroupBy(n => n.Track).ToDictionary(g => g.Key, g => g.Count());
        var choices = value.Tracks.OrderBy(t => t.Key).Select(t => new TrackChoice(t.Key, $"{t.Value} · {trackCounts.GetValueOrDefault(t.Key, 0)} 音")).ToList();
        choices.Add(new(null, "全部音轨（简化成单旋律）")); Tracks.ItemsSource = choices;
        int? suggested = value.Settings.Track;
        if (suggested == null || !trackCounts.ContainsKey(suggested.Value))
            suggested = trackCounts.OrderByDescending(kv => kv.Value).First().Key;
        Tracks.SelectedItem = choices.FirstOrDefault(x => x.Id == suggested) ?? choices[0];
        Speed.SelectedItem = Speed.Items.Cast<SpeedChoice>().First(x => x.Value == 1);
        Transpose.SelectedItem = Transpose.Items.Cast<PitchChoice>().First(x => x.Value == 0); gate = 0.92;
        binding = false; CompileCurrent();
        if (value.Warnings?.Count > 0) SetMessage(string.Join(" ", value.Warnings), false);
        else SetMessage("已选曲。先本机试听，确认旋律音轨后再回游戏按热键。", false);
    }
    CompileOptions Options()
    {
        bool robust = Timing.SelectedIndex == 0;
        return new((Tracks.SelectedItem as TrackChoice)?.Id, (Transpose.SelectedItem as PitchChoice)?.Value ?? 0,
            (Speed.SelectedItem as SpeedChoice)?.Value ?? 1, gate, Fold.IsChecked == true, Strategy.SelectedIndex != 1,
            robust ? 20 : 12, robust ? 45 : 35, robust ? 12 : 8, Trim.IsChecked == true);
    }
    bool CompileCurrent()
    {
        if (score == null) return false;
        try
        {
            plan = ScoreCompiler.Compile(score, Options()); CompileSummary.Text = plan.Summary;
            Overview.Plan = plan; Overview.Position = 0; Overview.InvalidateVisual(); Progress.Value = 0; UpdateProgress(); UpdateControls(); return true;
        }
        catch (Exception e) { plan = null; CompileSummary.Text = "当前设置无法生成演奏时间线。"; SetMessage(e.Message, true); UpdateControls(); return false; }
    }
    void OptionChanged(object sender, SelectionChangedEventArgs e) { if (!binding && !busy) CompileCurrent(); }
    void OptionClick(object sender, RoutedEventArgs e) { if (!binding && !busy) CompileCurrent(); }
    void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (score == null || busy) return;
        binding = true;
        var s = score.Settings;
        if (s.Track != null) Tracks.SelectedItem = Tracks.Items.Cast<TrackChoice>().FirstOrDefault(x => x.Id == s.Track) ?? Tracks.SelectedItem;
        Transpose.SelectedItem = Transpose.Items.Cast<PitchChoice>().First(x => x.Value == s.Transpose);
        if (!Speed.Items.Cast<SpeedChoice>().Any(x => x.Value == s.Speed))
            Speed.ItemsSource = Speed.Items.Cast<SpeedChoice>().Append(new(s.Speed)).OrderBy(x => x.Value).ToArray();
        Speed.SelectedItem = Speed.Items.Cast<SpeedChoice>().First(x => x.Value == s.Speed); gate = s.Gate; Fold.IsChecked = s.Fold;
        binding = false; CompileCurrent(); SetMessage("已应用文件保存的设置一次；原始曲谱未改写。", false);
    }
    void RefreshWindows()
    {
        if (busy) return;
        nint old = (Targets.SelectedItem as TargetWindow)?.Handle ?? 0;
        var list = new List<TargetWindow> { new(0, "自动识别三角洲游戏窗口") }; list.AddRange(WindowsControl.ListWindows());
        Targets.ItemsSource = list; Targets.SelectedItem = list.FirstOrDefault(x => x.Handle == old) ?? list[0];
    }
    void RefreshClick(object sender, RoutedEventArgs e) => RefreshWindows();
    void HotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (binding || busy || HotkeyBox.SelectedIndex < 0) return;
        settings.Hotkey = HotkeyBox.SelectedIndex + 6; RegisterHotkey(); SaveSettings();
    }
    void ToggleFromHotkey()
    {
        if (closeRequested) return;
        if (busy) { StopOperation(); return; }
        operationTask = RunGameAsync(false);
    }
    void PlayClick(object sender, RoutedEventArgs e) { if (!busy && !closeRequested) operationTask = RunGameAsync(true); }
    async Task RunGameAsync(bool prepare)
    {
        if (busy || !CompileCurrent() || plan == null) return;
        if (hotkeys?.Ready != true) { SetMessage("开始 / 停止热键不可用，请先更换热键。", true); return; }
        if (sink.HeldCount > 0) { SetMessage("仍有按键未能释放，请先重试释放；必要时手动按下并松开相关按键。", true); return; }
        nint requested = (Targets.SelectedItem as TargetWindow)?.Handle ?? 0;
        nint target = WindowsControl.ResolveTarget(requested);
        if (!prepare && target == 0) { SetMessage("当前前台不是目标游戏窗口。自动识别失败时，在“目标窗口”中手动选择游戏窗口。", true); return; }
        var compiled = plan; BeginOperation(true, target); var token = operation!.Token;
        try
        {
            for (int seconds = 3; seconds > 0; seconds--)
            {
                StateText.Text = $"{seconds} 秒后开始 · 再按 F{settings.Hotkey} 可取消";
                for (int i = 0; i < 50; i++) { await Task.Delay(20, token); if (boundTarget != 0 && !WindowsControl.Focused(boundTarget)) throw new FocusLostException(); }
            }
            target = WindowsControl.ResolveTarget(requested); if (target == 0) throw new IOException("倒计时结束时游戏窗口不在前台。请回到游戏后按热键，或先手动选择目标窗口。");
            boundTarget = target;
            if (WindowsControl.InstrumentControlsHeld()) throw new IOException("请先松开音符键、鼠标键和 Ctrl / Alt / Shift，再开始演奏。");
            StateText.Text = $"正在演奏 · F{settings.Hotkey} 停止";
            clock = new();
            var result = await Task.Run(() => new Playback().Run(compiled, sink, clock, () => WindowsControl.Focused(target), token));
            if (forcedStop != null) SetMessage(forcedStop, false);
            else SetMessage(result.Reason switch {
                EndReason.Finished => "本曲演奏结束。再次按热键会从曲首开始。",
                EndReason.Stopped => "演奏已停止。再次按热键会从曲首开始。",
                _ => result.Error ?? "演奏已停止。"
            }, result.Reason == EndReason.Failed);
            Storage.Log($"演奏结束 {result.Reason}，发送 {result.Sent} 个事件。" + (result.Error ?? ""));
        }
        catch (OperationCanceledException) { SetMessage(forcedStop ?? "已取消演奏。", false); }
        catch (FocusLostException e) { SetMessage(e.Message, false); }
        catch (Exception e) { SetMessage(e.Message, true); Storage.Log(e.ToString()); }
        finally { EndOperation(); }
    }
    async void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (busy || closeRequested) { if (busy) StopOperation(); return; }
        if (!CompileCurrent() || plan == null) return;
        operationTask = PreviewAsync(plan); await operationTask;
    }
    async Task PreviewAsync(Compilation compiled)
    {
        BeginOperation(false, 0); var token = operation!.Token;
        string? path = null; MediaPlayer? player = null;
        try
        {
            // 路径/媒体创建失败也必须进入 finally，不能留下 busy 或 CTS。
            path = await Task.Run(Storage.CreatePreviewPath);
            player = new MediaPlayer { Volume = settings.Volume }; media = player;
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (_, _) => opened.TrySetResult();
            player.MediaEnded += (_, _) => ended.TrySetResult();
            player.MediaFailed += (_, e) => { var error = new IOException("本机试听失败：" + e.ErrorException.Message); if (!opened.Task.IsCompleted) opened.TrySetException(error); else ended.TrySetException(error); };
            StateText.Text = "正在准备本机试听…";
            await Task.Run(() => AudioRenderer.Render(compiled, path, token)); token.ThrowIfCancellationRequested();
            player.Open(new Uri(path)); await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            player.Play(); StateText.Text = "本机试听 · 不操作游戏";
            await ended.Task.WaitAsync(TimeSpan.FromMilliseconds(compiled.DurationMs + 8000), token); SetMessage("本机试听结束，可以回到游戏按热键演奏。", false);
        }
        catch (OperationCanceledException) { SetMessage("本机试听已停止。", false); }
        catch (Exception e) { SetMessage(e.Message, true); Storage.Log(e.ToString()); }
        finally
        {
            try { player?.Stop(); } catch (Exception e) { Storage.Log("停止试听媒体失败：" + e); }
            try { player?.Close(); } catch (Exception e) { Storage.Log("关闭试听媒体失败：" + e); }
            try { if (path != null) await Storage.CleanupPreviewFileAsync(path); }
            catch (Exception e) { Storage.Log("试听文件清理失败：" + e); }
            finally { EndOperation(); }
        }
    }
    void BeginOperation(bool game, nint target)
    {
        if (closeRequested) throw new InvalidOperationException("关闭期间不能启动新操作。");
        busy = true; gameMode = game; boundTarget = target; forcedStop = null; operation = new(); clock = null; Progress.Value = 0; UpdateControls();
    }
    void EndOperation()
    {
        busy = false; gameMode = false; boundTarget = 0; clock = null;
        if (operation != null) { try { operation.Dispose(); } catch (Exception) { } operation = null; }
        StateText.Text = sink.HeldCount == 0 ? "待命" : "有按键未释放"; UpdateControls(); UpdateProgress();
    }
    void StopOperation() { if (operation != null && !operation.IsCancellationRequested) { operation.Cancel(); StateText.Text = "正在停止并释放按键…"; } }
    void StopClick(object sender, RoutedEventArgs e) => StopOperation();
    void RetryReleaseClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try { sink.ReleaseAll(); SetMessage("已完成按键释放。", false); } catch (Exception ex) { SetMessage(ex.Message, true); }
        UpdateControls();
    }
    void UpdateProgress()
    {
        if (ProgressText == null) return;
        double position = busy ? gameMode ? clock?.ElapsedMs ?? 0 : media.Position.TotalMilliseconds : 0;
        double total = plan?.DurationMs ?? 0; Progress.Value = total > 0 ? Math.Clamp(position / total, 0, 1) : 0;
        ProgressText.Text = Time(position) + " / " + Time(total); Overview.Position = position; Overview.InvalidateVisual();
    }
    static string Time(double ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"; }
    void UpdateControls()
    {
        if (PlayButton == null) return;
        bool allowNew = !busy && !closeRequested;
        foreach (var c in new UIElement[] { Songs, SearchBox, ImportButton, FolderButton, Tracks, Speed, Transpose, Fold, Trim, Timing, Strategy, RestoreButton, Targets, RefreshButton, HotkeyBox }) c.IsEnabled = allowNew;
        PreviewButton.IsEnabled = allowNew && plan != null;
        PlayButton.IsEnabled = allowNew && plan != null && (testMode || hotkeys?.Ready == true) && sink.HeldCount == 0;
        StopButton.IsEnabled = busy && operation != null;
        RetryReleaseButton.Visibility = sink.HeldCount > 0 && !busy ? Visibility.Visible : Visibility.Collapsed;
    }
    void SetMessage(string text, bool error)
    { Detail.Text = text; Detail.ToolTip = text; Detail.Foreground = new SolidColorBrush(error ? Color.FromRgb(156, 58, 43) : Color.FromRgb(73, 90, 79)); }
    async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (closeRequested) return;
        var dialog = new OpenFileDialog { Title = "导入曲谱", Multiselect = true, Filter = "口琴曲谱|*.json;*.mid;*.midi|JSON 曲谱|*.json|MIDI|*.mid;*.midi" };
        if (dialog.ShowDialog(this) == true) { operationTask = ImportAsync(dialog.FileNames); await operationTask; }
    }
    async void FolderClick(object sender, RoutedEventArgs e)
    {
        if (closeRequested) return;
        var dialog = new OpenFolderDialog { Title = "选择曲谱文件夹（读取顶层 JSON 和 MIDI）" };
        if (dialog.ShowDialog(this) == true) { operationTask = ImportAsync([dialog.FolderName]); await operationTask; }
    }
    async Task ImportAsync(IEnumerable<string> paths)
    {
        if (busy || closeRequested) return; busy = true; StateText.Text = "正在导入曲谱…"; UpdateControls();
        string? last = null; int ok = 0; var errors = new List<string>();
        try
        {
            await Task.Run(() => {
                foreach (var path in paths)
                {
                    IEnumerable<string> files = Directory.Exists(path) ? Directory.EnumerateFiles(path).Where(IsScoreFile) : [path];
                    foreach (var file in files.Take(1000)) try { var loaded = ScoreLoader.Load(file); last = Storage.Import(loaded); ok++; } catch (Exception e) { errors.Add(Path.GetFileName(file) + "：" + e.Message); }
                }
            });
        }
        catch (Exception e) { errors.Add(e.Message); }
        finally { busy = false; StateText.Text = "待命"; UpdateControls(); }
        if (closeRequested) return;
        await RefreshLibraryAsync(last == null ? null : Path.GetFileName(last));
        SetMessage($"已导入 {ok} 个文件。" + (errors.Count > 0 ? $" {errors.Count} 个失败：{errors[0]}" : "原始文件保留，曲库副本已保存。"), errors.Count > 0);
        if (errors.Count > 0) Storage.Log(string.Join("\n", errors));
    }
    void SaveSettings() { if (testMode) return; try { Storage.SaveSettings(settings); } catch (Exception e) { SetMessage("设置保存失败：" + e.Message, true); } }
    void CompactClick(object sender, RoutedEventArgs e) { SetCompact(!compact); settings.Compact = compact; SaveSettings(); }
    void SetCompact(bool value)
    {
        compact = value; LibraryPanel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        LibraryColumn.Width = new GridLength(value ? 0 : 226); GapColumn.Width = new GridLength(value ? 0 : 18);
        MinHeight = value ? 600 : 740; Height = value ? 620 : 780; Width = value ? 530 : 850;
        OverviewPanel.Visibility = SongMeta.Visibility = BrandCaption.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        Advanced.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        CompactButton.Content = value ? "展开曲库" : "紧凑";
    }
    void HelpClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "1. 导入 JSON / MIDI，选中曲目和旋律音轨。\n2. 本机试听确认，速度和移调可调整。\n3. 游戏中拿出口琴，回到游戏后按热键开始，再按同一键停止。\n4. 切换窗口会停止；切回后不会自动继续。\n5. 自动识别失败时，刷新并手动选择游戏窗口。\n\n普通权限运行；输入被系统或游戏拒收时会报错停止。本工具不提供 ACE 免封保证。\n\n曲库：" + Storage.LibraryRoot + "\n设置与日志：" + Storage.DataRoot,
            "使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closeRequested) return;
        closeRequested = true; UpdateControls(); StopOperation();
        await Dispatcher.Yield(DispatcherPriority.Background);
        try { await operationTask; }
        catch (Exception ex) { Storage.Log("operationTask 异常：" + ex.ToString()); }
        try { sink.ReleaseAll(); }
        catch (Exception ex) { Storage.Log("ReleaseAll 失败：" + ex.ToString()); StateText.Text = "退出时部分按键未能释放：" + ex.Message; }
        try { timer.Stop(); } catch (Exception) { }
        try { media.Close(); } catch (Exception) { }
        try { focusMonitor?.Dispose(); } catch (Exception) { }
        try { hotkeys?.Dispose(); } catch (Exception) { }
        try { SaveSettings(); } catch (Exception) { }
        try { await Storage.CleanupAllPreviewFilesAsync(); }
        catch (Exception ex) { Storage.Log("退出试听清理失败：" + ex); }
        allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    public void SetTestScore()
    {
        var demo = DemoScore(); library = [new("demo.json", demo)]; FilterLibrary(); Songs.SelectedIndex = 0;
        SetMessage("先试听确认旋律，再返回游戏按 F8 开始。", false);
    }
    internal void SetVerificationScore(Score value) { if (!testMode) throw new InvalidOperationException(); SetScore(value); }
    internal void SetVerificationCompact(bool value) { if (!testMode) throw new InvalidOperationException(); SetCompact(value); }
    internal bool VerificationPlayable => testMode && plan != null;
    internal Task BeginTestPlayback(IInputSink testSink)
    {
        if (!testMode) throw new InvalidOperationException("仅供离线自检。");
        BeginOperation(true, 0);
        return operationTask = RunTestAsync();
        async Task RunTestAsync()
        {
            try { await Task.Run(() => new Playback().Run(plan!, testSink, new SystemPlaybackClock(), () => true, operation!.Token)); }
            finally { EndOperation(); }
        }
    }
    public static Score DemoScore()
    {
        int[] pitches = [60, 60, 67, 67, 69, 69, 67, 65, 65, 64, 64, 62, 62, 60];
        return new("小星星 · 示例", pitches.Select((p, i) => new Note(p, i * 500, i is 6 or 13 ? 800 : 450)).ToArray(),
            new Dictionary<int, string> { [1] = "主旋律" }, new(Track: 1));
    }
}
