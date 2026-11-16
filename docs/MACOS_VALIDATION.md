# macOS 运行验证 · 2026-11-16

本轮功能验收源码提交为 `a54cc40d3a8736563a548ecbc80cfdadb9b68344`。[验证任务](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429) 的七个任务全部成功：Windows 界面/打包、两种 Mac 架构的原生运行，以及四组重新下载后的独立运行。后续报告与发布配置不改变应用、测试或打包代码。主分支合并后会再次验证；发布流程只允许使用与发布标签相同提交的成功主分支任务，最终包来源附在发布说明末尾。

| 环境 | 结果 | 任务 |
| --- | --- | --- |
| Intel x86_64，macOS 15.7.9 原生运行 | 通过 | [日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/job/133250405537) |
| Apple Silicon arm64，macOS 15.8.2 原生运行 | 通过 | [日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/job/133250405477) |
| Intel，macOS 15.7.9 重新下载独立运行 | 通过 | [日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/job/133252496716) |
| Apple Silicon，macOS 15.8.2 重新下载独立运行 | 通过 | [日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/job/133252496489) |
| Intel，macOS 26.6.1 重新下载独立运行 | 通过 | [日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/job/133252496547) |
| Apple Silicon，macOS 26.7.2 重新下载独立运行 | 通过 | [日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/job/133252496512) |
| Windows x64 原生界面和安装包 | 通过 | [日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/job/133250405115) |

## 已验证安装包

- [Intel Mac 测试附件](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/artifacts/14220320206)
- [Apple Silicon Mac 测试附件](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/artifacts/14219150190)
- [Windows 测试附件](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43853094429/artifacts/14217924060)
- [v1.7.0 发布页](https://github.com/zangly55/PerfTicker-Remote/releases/tag/v1.7.0)：发布工作流只上传最终主分支验证任务中已测试 ZIP 的原始字节与校验文件。

Actions 附件需要登录 GitHub，保留 30 天；发布页的安装包供其他用户直接下载。Mac 附件内 `dist/packages/macOS/` 是对应 ZIP 和 SHA256，`artifacts/runtime/` 是结果、截图和日志。仓库 Download ZIP 与发布页自动生成的 Source code 均是源码。

## 发现并修复的问题

原打包脚本在暂存目录中签名、验证成功，随后使用 Python `zipfile` 写 ZIP。实际下载场景的“压缩 → 解压 → 验签”失败：

```text
code object is not signed at all
In subcomponent: .../Contents/MacOS/System.Diagnostics.Contracts.dll
```

[首次失败记录](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43843776078/job/133218712160)。原因是这类非 Mach-O 文件的签名存于扩展属性，普通 ZIP 写入丢失了这些元数据；参见 [Apple Code Signing Tasks](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/Procedures/Procedures.html)。

Mac 原生打包现改用 `ditto -c -k --sequesterRsrc --keepParent` 保留元数据。两种架构的回归测试都针对**已压缩并重新解压的最终包**执行 `codesign --verify --deep --strict`，随后运行同一个包完成本机采样和 Launch Services 启动检查。Windows/Linux 打包路径保持原有行为。

## 通过的检查

每种 Mac 架构通过 36 项桌面样例检查和 11 项 Python 采集器/打包检查；Windows 通过 26 项回归与 8 项原生界面检查。

1. ZIP 校验和、解压后的执行权限、包含空格路径、递归严格验签。
2. 解压包连续输出三条递增快照；CPU、内存合法，进程列表非空，网络计数可用，未支持的 GPU 利用率保持不可用。
3. 通过 macOS Launch Services 打开 `.app`，12 秒后应用与启动等待进程仍存活。
4. 使用生产 `DesktopApp` / `MarketWindow` 和原生 Avalonia/macOS 后端，确认采样到达界面、进程搜索/清空、K 线、采样与周期约束、暂停/恢复、置顶、配色、营收面板、挂件、GPU 总览、SSH 弹窗取消、设置保存、窗口缩放和深/浅主题截图。
5. 真实回环 SSH 握手、指纹探测、错误指纹拒绝、密码/私钥认证、嵌入式采集脚本传输、四卡快照解析、采样间隔改变后的重连、服务器主动关闭通道后的重连，以及释放后离线。
6. 在另外四台 runner 下载相同 ZIP，仅复制 `.app`，移到包含空格的独立目录；不安装开发 SDK，将 DOTNET_ROOT 指向不存在的路径、从 PATH 移除 Homebrew/SDK 路径，确认采样与 GUI 启动。检查 Mach-O 的真正加载依赖，不把 universal 架构标题或库自身的 LC_ID_DYLIB 当成依赖。
7. 另取从未启动的应用副本，加上互联网下载隔离标记。严格验签仍成功，Gatekeeper 返回拒绝，符合无 Developer ID 公证构建需逐应用允许的预期。测试没有去除隔离标记、关闭 Gatekeeper 或自动点击系统“仍要打开”。
8. Windows 检查紧凑周期选项的实际命中区域、采样约束、暂停、缩小窗口、显示 DPI 的进程列坐标和浅色主题。Windows UI 使用合成数据，未更改用户设置、登录项或连接服务器。

两种 Mac 的实际深色、小窗口和浅色截图已人工查看；本机 Windows 250% DPI 渲染发现并修复了进程表列坐标未缩放的问题。完整功能逐项结果见 [对齐表](DESKTOP_PARITY.md)。

## 验证边界

- SSH 服务运行于隔离测试环境，四卡指标为合成 Linux 数据；未连接用户的生产服务器，也未验证物理 NVIDIA GPU 的传感器准确性。
- GUI 测试使用真实窗口和渲染后端，操作由控件事件驱动；未执行人工鼠标键盘全流程验收。
- 实际运行环境为上表列出的 GitHub 托管系统，不能外推为 macOS 14、每种实体 Mac、所有安全软件配置或所有未来系统均已验证。
- 包为 ad-hoc 签名，未进行 Apple Developer ID 公证。已经验证带隔离标记时签名完整且 Gatekeeper 需要逐应用允许；系统设置中的人工首次放行未被自动化执行。操作见 [Apple 官方说明](https://support.apple.com/zh-cn/102445) 和 [包内说明](MACOS_FIRST_OPEN.txt)。
- Mac 本机 GPU 负载/显存/温度等、CPU 频率和磁盘吞吐仍是既有功能限制，显示 `—`；本轮没有把缺失读数伪装成零。

复现命令与测试设计见 [MACOS_TESTING.md](MACOS_TESTING.md)。
