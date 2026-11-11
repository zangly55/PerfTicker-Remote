# PerfTicker 1.7 · Windows / Linux / macOS 三个版本

三个版本分别打包，保留深色行情、红涨绿跌、独立 GPU 品种、K 线、进程排行，以及本机 / SSH 服务器选择。
Windows 延续原有 WinForms 客户端；Linux 与 macOS 使用 Avalonia 桌面客户端。桌面挂件、壁纸等 Windows 专有功能未迁移到另外两个版本。

## 下载与选择

在 [构建记录](https://github.com/zangly55/PerfTicker-Remote/actions/workflows/desktop-packages.yml) 中打开最新成功任务，从 **Artifacts** 下载对应系统。下载 Actions 附件需要登录 GitHub；附件保留 30 天，过期后可重新构建。仓库的 Download ZIP 是源码。

| 版本 | 软件包 | 适用设备 |
| --- | --- | --- |
| Windows | `PerfTicker-1.7.0-win-x64.zip` | Windows x64 |
| Linux | `PerfTicker-1.7.0-linux-x64.tar.gz` | 常见 Intel / AMD Linux 桌面、虚拟机 |
| Linux | `PerfTicker-1.7.0-linux-arm64.tar.gz` | ARM64 Linux 桌面 |
| macOS | `PerfTicker-1.7.0-osx-arm64.zip` | Apple Silicon（M 系列）Mac |
| macOS | `PerfTicker-1.7.0-osx-x64.zip` | Intel Mac |

Linux 和 macOS 各自的两种架构属于同一平台版本。可用 `uname -m` 判断：`x86_64` 选 x64，`arm64` / `aarch64` 选 ARM64。
所有包自带 .NET 运行时，客户端无需安装 Python。软件包附带原始许可、第三方许可、对应源码和 SHA256 校验文件。

**当前验证范围：** 已完成跨平台编译、行情界面布局检查及 27 项纯样例测试，涵盖 1/4/8/32 卡、UUID 归属、Mac 内存页解析和密码不落盘。按用户要求暂停本机运行，macOS/Linux 的实际采集和启动验收留待虚拟机或相应设备就绪。构建成功不等于各系统实机验证完成。

## Windows 版

解压后运行 `PerfTickerRemote.exe`。通过“设置 → 监控目标”切换本机或 Linux 服务器。
首次默认监控本机；原有桌面挂件等 Windows 功能继续保留。密码由 Windows 当前用户加密保护，不能直接复制加密配置到另一台电脑。

## Linux 版

目标环境为具备 X11 或 XWayland 的常见 glibc Linux 桌面，例如 Ubuntu 22.04/24.04、Debian 12/13。无图形界面的服务器不必安装此客户端，在另一台电脑通过 SSH 监控即可。Alpine/musl 不在此预编译包的支持范围。

Ubuntu/Debian 若缺少图形依赖或中文字体：

```bash
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 fonts-noto-cjk
tar -xzf PerfTicker-1.7.0-linux-x64.tar.gz
cd PerfTicker-1.7.0-linux-x64
./PerfTickerDesktop
```

ARM64 替换为对应包名。保持整个解压目录完整，不能只复制可执行文件。
程序读取当前用户可见的 CPU、内存、物理磁盘、网络、进程；NVIDIA 指标通过系统驱动附带的 `nvidia-smi` 读取。

## macOS 版

目标为 macOS 14 及更新系统。按 Mac 芯片选择软件包，解压后将 `PerfTicker.app` 拖入“应用程序”，再打开。
该开源构建没有 Apple Developer ID 公证；GitHub 的 Mac 构建会做本地 ad-hoc 签名，Windows 交叉构建不会生成整个应用包的 Mac 签名。如果 macOS 拒绝打开，请先核对来源和 SHA256，再按系统“隐私与安全性”对这个应用单独处理，或在 Mac 从源码构建。无需关闭系统整体安全保护。

Mac 本机支持 CPU 总负载、内存估算、网络吞吐和进程排行，并识别 Apple / AMD GPU 型号。以下读数当前显示 `—`：GPU 负载、显存、频率、温度、功耗、风扇，以及 CPU 实时频率、磁盘吞吐。Apple 统一内存不会伪装成独立显存。内存估算将 free / inactive / speculative 页视作可回收，与活动监视器“内存使用”的定义可能不同。
Mac 通过 SSH 监控 NVIDIA Linux 服务器时，以上 GPU 限制不适用，显示的是服务器实际读数。

## 三个版本如何连接服务器

1. Windows 打开“设置 → 监控目标”；Linux/Mac 点击顶部“连接设置”或选择“SSH 服务器”。
2. 从此电脑的 `~/.ssh/config` 选择已有主机，或填写服务器 IP、端口、用户名及密码/私钥路径。
3. 点击“检测 SSH 指纹”，与服务器身份核对；确认指纹后，测试并连接。
4. 左侧选择指标查看 K 线，右侧显卡板块可滚动查看全部 GPU，点击某张显卡跳到对应负载。进程显存随所选 GPU 区分归属。
5. 顶部选择“本机行情”即可切回当前电脑。切换目标会清空上一目标的历史，断线时保留历史并显示过期状态，不会悄悄改为本机数据。

“自动发现”只读取此电脑常见的 SSH config 条目，不扫描局域网、不猜测账号密码；`Include` / `Match` 不展开，跳板机 / ProxyCommand 暂不支持。已在终端建立的 SSH 会话不会自动附加到程序。
远端须为 Linux，具备 Python 3；NVIDIA 数据需要 `nvidia-smi`。无需 sudo、无需部署常驻服务。无 NVIDIA 的服务器仍可监控 CPU、内存等，缺少的传感器显示 `—`。

Linux/Mac 仅保存地址、用户名、私钥路径和主机指纹，密码/私钥口令只保留在内存，本次进程退出即清除。重启后从本机模式开始，重新连接时输入密码；无口令私钥可直接复用。只支持当前电脑可读取的私钥文件，未接入 SSH agent / 系统钥匙串。
配置使用 .NET `LocalApplicationData` 下的 `PerfTickerDesktop/remote.json`（Linux 通常为 `~/.local/share/PerfTickerDesktop/remote.json`，macOS 通常为 `~/Library/Application Support/PerfTickerDesktop/remote.json`）。不要把个人连接配置上传仓库。

## 虚拟机验收

等虚拟机准备好再执行这些步骤，构建流程不会自动启动监控程序。

- Linux VM 需安装桌面环境，若仅 SSH 登录，改在宿主机使用客户端连接这台 VM。
- 虚拟机没有直通物理显卡时，本机模式只会看到虚拟机可见的设备；这不影响它通过 SSH 查看服务器的四张或更多 NVIDIA GPU。
- 建议验证：启动、本机 CPU/内存变化、SSH 指纹检测、连接/断线重连、四卡列表与每卡 K 线、本机/远程切换、中文字体和窗口缩放。
- 无界面采集诊断（用户明确开始验收后）：Linux 运行 `./PerfTickerDesktop --probe-local`；Mac 运行 `PerfTicker.app/Contents/MacOS/PerfTickerDesktop --probe-local`。输出三条真实快照后退出，包含主机和进程信息，请勿提交到公开仓库。

## 源码构建

需要 .NET 10 SDK。以下命令只编译，不启动客户端：

```bash
# Windows 版（在 Windows 构建）
dotnet publish PerfTicker/PerfTicker.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64

# Linux 版；ARM64 改用 linux-arm64
dotnet publish CrossPlatform/PerfTicker.Desktop.csproj -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64

# Mac 版；Intel 改用 osx-x64
dotnet publish CrossPlatform/PerfTicker.Desktop.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/osx-arm64

# 用 Python 3 打包（打包工具需要，客户端不需要）
python3 tools/package_desktop.py --rid linux-x64 --input artifacts/linux-x64
python3 tools/package_desktop.py --rid osx-arm64 --input artifacts/osx-arm64
```

打包前提交源码；工作区未提交时可明确使用 `--allow-dirty` 制作开发包。脚本拒绝覆盖已有暂存目录，可用 `--output` 指向新的输出目录。输出按 Windows / Linux / macOS 分目录。

纯样例测试，不启动界面、不采集本机、不连接 SSH：

```bash
dotnet run --project tests/PerfTicker.Desktop.Tests -c Release
python3 -m unittest discover -s tests -p 'test_collector.py'
```

平台要求参考：[Avalonia 平台支持](https://docs.avaloniaui.net/docs/supported-platforms)、[macOS 打包与签名](https://docs.avaloniaui.net/docs/deployment/macos)。
