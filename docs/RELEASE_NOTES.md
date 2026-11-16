Mac 与 Windows 的界面精修和 Mac 下载兼容修复。

- Mac：八项指数、K 线/均线/十字线、完整进程搜索与排序、置顶、主题、快捷键、显卡总览、托盘、挂件、登录启动、设置保存和共用营收模拟。
- 两端：22 像素高的紧凑 K 线周期选项；统一间距、字体层级和配色。Windows 修复高 DPI 进程列重叠、浅色主题涨跌色及底部黑边。
- Mac ZIP 改用原生归档保存签名扩展属性，包含应用图标、首次打开说明、源码和许可证。

选择下载：Apple M 系列使用 **osx-arm64**，Intel Mac 使用 **osx-x64**，Windows 使用 **win-x64**。包内自带 .NET 运行时，无需安装开发 SDK 或 Python。GitHub 自动生成的 Source code 是源码，不是安装包。

Mac 请在 Mac 上双击 ZIP 解压，把 `PerfTicker.app` 拖到“应用程序”。此版本没有 Apple Developer ID 公证；首次按“系统设置 → 隐私与安全性 → 仍要打开”允许这个应用。无需关闭系统整体安全保护。

这三个 ZIP 原样来自发布标签同一提交的成功主分支验证任务，实际任务和源码链接附在发布说明末尾；发布步骤重新核对 SHA256，没有重新打包。Intel/Apple Silicon 在 macOS 15 上完成原生运行验收，同一包在另外四台 macOS 15/26 runner 上重新下载并独立启动；Windows 通过构建、26 项回归与 8 项原生界面检查。

Mac 本机 GPU 利用率/显存/温度等、CPU 实时主频、磁盘吞吐仍有平台限制，显示 `—`；支持这些读数的 SSH 服务器不受本机限制。Mac 提供置顶挂件，Windows 桌面嵌入壁纸没有同等 Mac 实现。首次系统设置放行仍需用户操作，自动化没有代替点击该按钮。完整结果见 [测试报告](https://github.com/zangly55/PerfTicker-Remote/blob/main/docs/MACOS_VALIDATION.md) 和 [逐项对齐表](https://github.com/zangly55/PerfTicker-Remote/blob/main/docs/DESKTOP_PARITY.md)。

GPL-3.0；原作者 QiuSui / QiuSui_Studio。营收仅为模拟玩法。
