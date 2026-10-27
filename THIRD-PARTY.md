# 来源与复用边界

本项目是独立桌面工具，未修改 GTIartist EXE 或其授权逻辑。JSON 入口兼容用户提供的可读 gtiartist-score-v1 数据。

## 源码参考

- HarmonicaPlayer，提交 2832001f35a27936eac5027ba3959c4891545e64：参考标准 SendInput、停止热键前置条件、失焦中断、释放失败反馈的设计。未复制其未标明通用许可证的程序代码或界面。
  https://github.com/lanselanlanxi-wq/HarmonicaPlayer
- HarmonicaScript，提交 90c65e3908ae890a988e57a12f011fc2d8f53a9c：参考转换核心/播放后端分层、绝对时间调度与 Windows INPUT 布局。Windows 结构声明与常量按 MIT 项目和 Microsoft API 定义适配，下附原项目 MIT 声明。
  https://github.com/LianZiZhou/HarmonicaScript
- delta-force-harmonica-tool，提交 2ceb0e680f745d866cc39edf93d92b71b1e84aea：参考将曲谱编译成可检查事件表、报告编排调整的思路；未复制其自定义许可的实现。
  https://github.com/2776525655/delta-force-harmonica-tool
- WhisperWind、ChickenD233/harmonica-auto-player、JikoSchnee/delta-harmonica-macro：用于比较权限、窗口监视、热键和宏导出边界；未引入其网络、钩子、更新器或界面代码。

程序不包含上述项目的预编译 EXE。曲谱转换、MIDI 解析、PCM 试听音源、界面和控制流程在本项目实现。试听使用数学合成的簧片近似音色，没有采样素材下载，音色不承诺与游戏一致。

## 曲库与运行时

个人交付目录中的 87 份曲库文件来自用户原有 songs 文件夹，原文件保留。它们不纳入本项目代码的 MIT 授权；源码自身提供的“小星星”样例为公共领域旋律的简单演示编排。

Windows 自包含发布包附带 Microsoft .NET 8 / Windows Desktop 运行时，其原始 LICENSE / ThirdPartyNotices 随发布保留。

## HarmonicaScript MIT 声明

MIT License

Copyright (c) 2026 HarmonicaScript contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
