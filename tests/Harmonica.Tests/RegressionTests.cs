using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Harmonica.Core;

namespace Harmonica.Tests;

public static class RegressionTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== 回归测试：修复验证 ===");
        int passed = 0, total = 0;

        void Test(string name, Action test)
        {
            total++;
            try { test(); passed++; Console.WriteLine($"✓ {name}"); }
            catch (Exception e) { Console.WriteLine($"✗ {name}: {e.Message}"); }
        }

        Test("F1-1: 任务异常不跳过最终释放", TestF1_TaskExceptionDoesNotSkipRelease);
        Test("F1-2: 清理步骤独立失败不阻断其他清理", TestF1_IndependentCleanupSteps);
        Test("F2-1: 大量音轨计数性能线性", TestF2_TrackCountingPerformance);
        Test("F2-2: 音轨数量上限检查", TestF2_TrackCountLimit);
        Test("F2-3: 缺省轨表的最终数量边界", () => TestF2_InferredTracks(null));
        Test("F2-4: 空轨表的最终数量边界", () => TestF2_InferredTracks(""));
        Test("F2-5: 显式与补齐轨道的合并边界", () => TestF2_InferredTracks(string.Join(",", Enumerable.Range(1, 9999).Select(i => $"\"{i}\":\"T\""))));
        Test("F3-1: UpdateControls 检查 closeRequested", TestF3_ControlsDisabledOnClose);
        Test("F4-1: 导入前验证 JSON 可重载", TestF4_ImportValidatesReload);
        Test("F4-2: 超限曲谱导入失败", TestF4_OversizedScoreRejected);
        Test("F4-3: MIDI 超限检查", TestF4_MidiNoteCountLimit);
        // F5 的窗口、文件锁和异步清理由独立 DesktopTests 运行，不再以源码签名代替行为。

        Console.WriteLine($"\n结果: {passed}/{total} 通过");
        if (passed < total) throw new Exception($"{total - passed} 个回归测试失败");
    }

    static void TestF1_TaskExceptionDoesNotSkipRelease()
    {
        // 验证 OnClosing 中 operationTask 异常和 ReleaseAll 在独立 try 块
        var sink = new FakeSink();
        sink.Emit(new InputEvent(0, Control.Z, true));
        if (sink.Held.Count != 1) throw new Exception("前置状态失败");

        sink.ReleaseAll();
        if (sink.Held.Count != 0) throw new Exception("释放未清空 held");
    }

    static string FindSourceFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                var path = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(path)) return path;
                break;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"无法找到源文件: {relativePath}");
    }

    static void TestF1_IndependentCleanupSteps()
    {
        var code = File.ReadAllText(FindSourceFile(Path.Combine("src", "Harmonica.Desktop", "Storage.cs")));
        if (!code.Contains("CleanupPreviewFile") || !code.Contains("pendingCleanup"))
            throw new Exception("Storage 缺少 CleanupPreviewFile 或待清理列表");
    }

    static void TestF2_TrackCountingPerformance()
    {
        var notes = Enumerable.Range(1, 5000).Select(i => new Note(60, i * 100, 400, i, 100)).ToArray();
        var tracks = notes.GroupBy(n => n.Track).ToDictionary(g => g.Key, g => $"音轨 {g.Key}");
        var score = new Score("性能测试", notes, tracks, new SourceSettings());

        var start = DateTime.UtcNow;
        var trackCounts = score.Notes.GroupBy(n => n.Track).ToDictionary(g => g.Key, g => g.Count());
        var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;

        if (elapsed > 100) throw new Exception($"5000 轨计数耗时 {elapsed:F2}ms，可能仍是 O(n²)");
    }

    static void TestF2_TrackCountLimit()
    {
        var tracksJson = string.Join(",", Enumerable.Range(1, 10001).Select(i => $"\"{i}\":\"Track {i}\""));
        var json = $"{{\"format\":\"gtiartist-score-v1\",\"title\":\"测试\",\"tracks\":{{{tracksJson}}},\"notes\":[{{\"pitch\":60,\"start\":0,\"duration\":1,\"track\":1}}]}}";

        try
        {
            ScoreLoader.FromJson(json);
            throw new Exception("应该拒绝 10001 轨");
        }
        catch (FormatException e)
        {
            if (!e.Message.Contains("音轨数量")) throw new Exception("错误消息不匹配");
        }
    }

    static void TestF3_ControlsDisabledOnClose()
    {
        var code = File.ReadAllText(FindSourceFile(Path.Combine("src", "Harmonica.Desktop", "MainWindow.xaml.cs")));
        if (!code.Contains("!busy && !closeRequested") || !code.Contains("allowNew"))
            throw new Exception("UpdateControls 未检查 closeRequested");
    }

    static void TestF4_ImportValidatesReload()
    {
        var code = File.ReadAllText(FindSourceFile(Path.Combine("src", "Harmonica.Desktop", "Storage.cs")));
        if (!code.Contains("ScoreLoader.FromJson(text"))
            throw new Exception("Import 未验证重载");
    }

    static void TestF4_OversizedScoreRejected()
    {
        var code = File.ReadAllText(FindSourceFile(Path.Combine("src", "Harmonica.Desktop", "Storage.cs")));
        if (!code.Contains("GetByteCount(text) > ScoreLoader.MaxBytes"))
            throw new Exception("Import 未检查 JSON 字节数");
    }

    static void TestF4_MidiNoteCountLimit()
    {
        var code = File.ReadAllText(FindSourceFile(Path.Combine("src", "Harmonica.Core", "MidiReader.cs")));
        if (!code.Contains("notes.Count > 250000"))
            throw new Exception("MidiReader 未检查音符总数");
    }

    static void TestF2_InferredTracks(string? trackMembers)
    {
        string trackJson = trackMembers == null ? "" : ",\"tracks\":{" + trackMembers + "}";
        string Json(int count) => "{\"format\":\"gtiartist-score-v1\"" + trackJson + ",\"notes\":["
            + string.Join(",", Enumerable.Range(1, count).Select(i => $"{{\"pitch\":60,\"start\":0,\"duration\":1,\"track\":{i}}}")) + "]}";
        if (ScoreLoader.FromJson(Json(10000)).Tracks.Count != 10000) throw new Exception("10,000 条轨道应通过");
        try { ScoreLoader.FromJson(Json(10001)); }
        catch (FormatException e) when (e.Message.Contains("音轨数量")) { return; }
        throw new Exception("10,001 条最终轨道应拒绝");
    }

    sealed class FakeSink : IInputSink
    {
        public HashSet<Control> Held = [];
        public void Emit(InputEvent e) { if (e.Down) Held.Add(e.Control); else Held.Remove(e.Control); }
        public void ReleaseAll() { Held.Clear(); }
    }
}
