# 三角洲口琴自动演奏脚本

基于 **C# / .NET 8 / WPF** 的 Windows x64 桌面工具。读取本地 JSON / MIDI 曲谱，选择音轨、调整速度和移调，生成单旋律演奏时间线；通过标准用户态 `SendInput` 输出口琴音键，支持本机合成试听、热键停止和目标窗口失焦取消。

## 使用风险：不保证免封

**本项目不保证不会封号，也不承诺不会被检测、限制使用或受到其他处罚。** 标准 Windows API、开源许可、离线测试通过以及 `SendInput` 返回成功，都不代表游戏官方允许使用此工具，更不代表游戏或反作弊系统认可其行为。

- 本项目不提供反检测、ACE 绕过、驱动注入或免封能力；不会修改游戏、ACE 或其他软件的授权逻辑。
- 使用前请自行阅读并遵守游戏的用户协议、第三方软件政策和适用规则；不能接受相关风险时，请勿在游戏中使用。
- MIT 许可证适用于项目代码，不授予违反游戏规则的许可。软件按 MIT 许可证以“现状”提供，具体免责范围以 [LICENSE](LICENSE) 为准。

**兼容性状态：离线构建与测试可验证代码行为；游戏实际接收输入和长期稳定性仍需独立验证。** 既往用户反馈普通窗口可收到输入而游戏无响应，不能将本项目宣传为“已验证游戏可用”。

## 功能

- JSON `gtiartist-score-v1` 和标准 MIDI Format 0/1（PPQ、速度变化、running status）导入。
- 音轨选择、速度、移调、八度折回与多声部单旋律简化；界面显示调整和舍弃数量。
- 本机 PCM 合成试听；试听不向目标窗口输入。
- F6–F11 可选开始/停止热键，默认 F8；3 秒倒计时，停止后从曲首重新开始。
- 前台窗口事件通知和轮询共同检查焦点；失焦后停止，切回不会自动继续。
- 记录自身按键状态，停止、异常和关闭时尝试释放；释放失败时保留重试入口。

## 从源码运行

要求：Windows 10/11 x64、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 PowerShell 7。源码构建需要 **SDK**；仅安装运行时不能编译。构建脚本不自动下载或安装 SDK。

```powershell
git clone https://github.com/one-more-loser/delta-force-harmonica-script.git
cd delta-force-harmonica-script
pwsh ./Build.ps1 -Mode Build
```

构建结果在 `.build/Harmonica.Desktop/bin/Release/net8.0-windows/`。该构建依赖本机 **.NET 8 Desktop Runtime x64**。

也可打开 `HarmonicaStudio.sln`，选择 `Harmonica.Desktop` 作为启动项目。默认普通权限运行；目标窗口权限更高时，Windows 输入权限隔离可能阻止送达。不要把游戏无响应直接归因于输入法或 ACE。

## 打包独立运行版本

```powershell
pwsh ./Build.ps1 -Mode Publish
# 自定义输出目录必须尚不存在，避免混用旧包
pwsh ./Build.ps1 -Mode Publish -OutputDirectory ./artifacts/my-release
```

发布使用 `win-x64 --self-contained true`，完整目录包含 .NET / Windows Desktop 运行时及其许可证，默认输出到 `artifacts/publish/win-x64/`。运行时不再要求安装系统 .NET。**分发或移动整个目录，不要只复制 EXE，也不要把普通 Build 的几个文件覆盖进去。**

## 使用流程

1. 选择或导入自己有权使用的曲谱，选旋律音轨并先试听。
2. 确认开始/停止热键已注册。默认 F8 被占用时，改用另一个可用键。
3. 在目标游戏中打开口琴演奏界面，再保持游戏前台，按设置的热键。
4. 等待 3 秒，松开音符键、鼠标键和 Ctrl / Alt / Shift；再次按同一热键停止。

也可点击“准备演奏（3秒）”后在倒计时结束前返回目标窗口。切回工具、截图窗口或其他程序可能触发失焦停止。游戏自带的“R 自动演奏”与本项目热键是不同功能。

默认音键为 `Z X C V B N M ,`；左/中/右鼠标键分别为降八度、升半音、升八度。程序不能确认游戏内部是否已经拿出口琴；源曲谱中无法表达的音符会按编排规则调整或舍弃。

## 项目结构

```text
src/
  Harmonica.Core/             解析、编排、调度、合成音频与数据模型
  Harmonica.Desktop/          WPF、存储、热键、窗口监听和 Windows 输入
tests/
  Harmonica.Tests/            核心行为、输入数据及解析边界测试
  Harmonica.Desktop.Tests/    隔离 WPF、文件锁、清理和关闭测试
scripts/                     隔离桌面回归运行器
examples/                    自有“小星星”演示曲谱
docs/                        架构、验证范围、故障排查和贡献说明
.github/workflows/           Windows 构建与测试
Build.ps1                    统一开发入口
HANDOFF.MD                   项目交接、已知问题和操作边界
```

## 测试

```powershell
pwsh ./Build.ps1 -Mode Test
pwsh ./Build.ps1 -Mode DesktopTest
# 已有本地依赖缓存时禁用 NuGet 网络源
pwsh ./Build.ps1 -Mode Test -Offline
```

桌面回归运行器先复制源码到系统临时目录，再隔离数据路径并禁用默认真实输入；不会把测试输入发往游戏或用户窗口。它需要可用的 Windows 桌面环境。本机 SDK 路径可通过 `-DotnetPath` 指定；个人曲库可通过 `-LibraryPath` 指定，不会上传到仓库。

测试结果、资源测量和未验证范围见 [验证说明](docs/verification.md)。`SendInput` 被 Windows 接受不等于游戏消费输入；不要据此宣称游戏兼容或免封。

## 数据、许可与文档

曲库默认位于程序旁的 `曲库/`；不可写时回退到 `%LOCALAPPDATA%/HarmonicaStudio/曲库`。设置和日志也位于该用户数据目录。试听临时文件使用专用 `previews-v1` 目录。应用本身离线运行，不含网络曲库、自动更新或开机启动。

本仓库只带自有“小星星”示例，**不分发个人交付目录中来自外部的 87 份曲谱**。请自行确认导入曲谱的使用和分发权。

- [架构与调用链](docs/architecture.md)
- [故障排查](docs/troubleshooting.md)
- [贡献指南](docs/contributing.md)
- [第三方来源与许可证](THIRD-PARTY.md)
- [MIT 许可证](LICENSE)
