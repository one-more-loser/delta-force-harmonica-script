# 架构与调用链

项目由两个生产程序集组成，测试程序集不进入发布目录。目录整理保持生产 C#、XAML 和资源内容不变；命名空间与程序集名称保持兼容。

| 模块 | 职责 |
|---|---|
| Core/ScoreLoader、MidiReader | 限制输入大小，解析 JSON / MIDI，验证音符与最终轨道数 |
| Core/ScoreCompiler | 应用一次用户设置，处理音域、重叠与单旋律，生成绝对毫秒事件表 |
| Core/Playback、SystemPlaybackClock | 可取消调度，发送前检查焦点，严重迟到时停止，最终释放 |
| Core/AudioRenderer | 将编排结果写为有界 PCM WAV，支持取消 |
| Desktop/MainWindow | WPF 状态、初始化、导入、试听、演奏和关闭任务顺序 |
| Desktop/NativeInput | 构造 x64 INPUT、限制输出范围、跟踪 held、失败时保留释放状态 |
| Desktop/WindowsControl | 目标窗口识别、RegisterHotKey、前台事件监听 |
| Desktop/Storage | 设置、导入副本、日志、专用试听目录与有界后台清理 |

主链：文件 → ScoreLoader → Score → ScoreCompiler → Compilation → Playback → NativeInputSink → SendInput。

试听链：Compilation → 后台创建路径和渲染 WAV → UI 线程 MediaPlayer → Stop/Close → await 清理 → finally EndOperation。

关闭先阻止新操作、取消当前操作并等待 operationTask，再尝试最终释放、关闭媒体和监听、保存设置和有界清理。不得让清理异常跳过状态复位，不得让初始化 await 期间的新操作覆盖当前任务。

资源预算：JSON 最大32MiB，最多250,000音符、10,000个最终唯一轨道；曲谱最长30分钟。调度迟到超过150ms终止。清理 pending 最多64项，批次最多128个目录项/128个候选，500ms预算停止发起新操作；该预算不是单次文件I/O硬超时。

运行时没有新增常驻清理线程或定时器。目录调整只影响开发、构建和测试路径，不能提高或证明游戏兼容性。
