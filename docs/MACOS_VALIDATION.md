# macOS 运行验证 · 2026-11-16

修复后，Intel 和 Apple Silicon 两种 Mac 包均在原生架构的 GitHub 托管 macOS runner 上跑通。本次测试对象为提交 `7c85703d84160c9c74e61a34b4c5b3c200e5066f`，最终应用代码与打包脚本以该提交为准；后续报告提交只修改文档。

| 环境 | 结果 | 任务 |
| --- | --- | --- |
| Intel x86_64，macOS 15.7.9 | 通过 | [Intel 日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43844035960/job/133219586213) |
| Apple Silicon arm64，macOS 15.8.2 | 通过 | [Apple Silicon 日志](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43844035960/job/133219585747) |

## 已验证安装包

- [Intel Mac 测试附件](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43844035960/artifacts/14214887229)
- [Apple Silicon Mac 测试附件](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43844035960/artifacts/14214053288)

登录 GitHub 后下载。附件内 `dist/packages/macOS/` 是对应 ZIP 安装包及 SHA256，`artifacts/runtime/` 是结果 JSON、界面截图和启动日志。附件按 30 天保留；过期后可重新运行 `macOS runtime validation` 工作流。主分支旧构建附件不包含本次修复，请使用上面的验证附件或合并修复后重新构建。

## 发现并修复的问题

原打包脚本在暂存目录中签名、验证成功，随后使用 Python `zipfile` 写 ZIP。实际下载场景的“压缩 → 解压 → 验签”失败：

```text
code object is not signed at all
In subcomponent: .../Contents/MacOS/System.Diagnostics.Contracts.dll
```

[首次失败记录](https://github.com/zangly55/PerfTicker-Remote/actions/runs/43843776078/job/133218712160)。原因是这类非 Mach-O 文件的签名存于扩展属性，普通 ZIP 写入丢失了这些元数据；参见 [Apple Code Signing Tasks](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/Procedures/Procedures.html)。

Mac 原生打包现改用 `ditto -c -k --sequesterRsrc --keepParent` 保留元数据。两种架构的回归测试都针对**已压缩并重新解压的最终包**执行 `codesign --verify --deep --strict`，随后运行同一个包完成本机采样和 Launch Services 启动检查。Windows/Linux 打包路径保持原有行为。

## 通过的检查

每种架构均通过原有 27 项桌面样例检查和 9 项 Python 采集器测试，以及以下运行检查：

1. ZIP 校验和、解压后的执行权限、包含空格路径、递归严格验签。
2. 解压包连续输出三条递增快照；CPU、内存合法，进程列表非空，网络计数可用，未支持的 GPU 利用率保持不可用。
3. 通过 macOS Launch Services 打开 `.app`，12 秒后应用与启动等待进程仍存活。
4. 使用生产 `DesktopApp` / `MarketWindow` 和原生 Avalonia/macOS 后端，确认三条采样到达界面、CPU/内存显示、进程行、K 线渲染、暂停/恢复、SSH 设置弹窗/取消及缩放后的截图输出。
5. 真实回环 SSH 握手、指纹探测、错误指纹拒绝、密码/私钥认证、嵌入式采集脚本传输、四卡快照解析、采样间隔改变后的重连、服务器主动关闭通道后的重连，以及释放后离线。

## 验证边界

- SSH 服务运行于隔离测试环境，四卡指标为合成 Linux 数据；未连接用户的生产服务器，也未验证物理 NVIDIA GPU 的传感器准确性。
- GUI 测试使用真实窗口和渲染后端，操作由控件事件驱动；未执行人工鼠标键盘全流程验收。
- 本轮实际运行系统是上述 macOS 15 两个版本，不能外推为 macOS 14 或所有未来系统均已验证。
- 包为 ad-hoc 签名，未进行 Apple Developer ID 公证，也未测试带互联网下载隔离标记的 Gatekeeper 首次放行。系统可能仍要求在“隐私与安全性”中允许该应用。
- Mac 本机 GPU 负载/显存/温度等、CPU 频率和磁盘吞吐仍是既有功能限制，显示 `—`；本轮没有把缺失读数伪装成零。

复现命令与测试设计见 [MACOS_TESTING.md](MACOS_TESTING.md)。
