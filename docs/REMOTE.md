# PerfTicker Linux 远程行情版

基于 QiuSui1145/PerfTicker v1.5.0 的本地改造，保留原作者和 GPL-3.0 许可。
上游基线：6ddf4d07dcb1f9a5a0061388e70feddfb64d370e。

Windows 客户端保留 K 线、自选行情、主力动向、逐笔事件、进程表和桌面小挂件。
数据通过 SSH 来自 Linux 算力机，不读取本机 Windows 的硬件数据，也不会在断线时回退为本机采集。

## 日常使用

双击 `dist\PerfTickerRemote.exe`。左上角显示远程主机名和行情连接状态。
选择 CPU、GPU、显存、温度等指标后，中央 K 线同步切换。
设置齿轮 → 远程算力机 → 连接，可调整主机地址、SSH 端口、账号和指纹。
采样周期可以在顶栏调整，连接断开后每 3 秒尝试重连。

- 关闭主窗口会缩到系统托盘；彻底停止监控请使用托盘菜单“退出”。
- “挂件”可打开桌面小窗口。
- “营收”仍是原项目的模拟玩法，不代表实际收益或模型吞吐量。
- 该版本仅监控，不提供远程进程结束功能。
- 每次启动重新积累 K 线；当前版本不提供持久化历史数据库。

## 数据说明

- CPU、逐核频率、内存、进程、线程、上下文切换：Linux `/proc` 和 `/sys`。
- CPU 进程占用统一按全机 0–100% 计，与原版进程行情保持一致。
- GPU 温度、功耗、频率、利用率、显存：`nvidia-smi`，默认 GPU 0。
- 进程表的“GPU显存”与 GPU 主力持仓使用 NVIDIA 计算进程显存；不把显存比例冒充 GPU 利用率。
- 不支持或没有权限读取的指标显示“—”，不生成零值 K 线。
- “文件描述符”对应 Linux 进程 fd，不是 Windows 句柄。
- 磁盘吞吐汇总普通物理块设备，排除 loop、ram、zram、dm、md，避免重复计算。
- 网络吞吐汇总具有物理设备的网卡，排除 Docker bridge/veth 的重复流量。
- 断线或超过 `max(5秒, 3×采样间隔)` 未收到样本时标记过期；图中保留最后收到的数据，不继续累积模拟营收。

## 连接和凭据

Windows 单文件程序自带 .NET 运行时和 SSH.NET，不依赖本机 Python 或 OpenSSH。
远端需要 Python 3、NVIDIA 驱动及 `nvidia-smi`；Python 采集器仅使用标准库。
采集脚本内嵌于 exe，通过 SSH 在内存中执行，不写入远端文件，不安装服务，不改驱动或模型设置。

配置位于 `%LOCALAPPDATA%\PerfTickerRemote\`：

- `remote.json`：主机、端口、用户名、固定 SSH 指纹，以及 Windows DPAPI 加密的密码。
- `settings.json`：界面偏好，与原版 PerfTicker 分开保存。
- `logs\`：本地诊断日志。

密码只能由当前 Windows 用户解密，配置不可直接复制给另一位用户使用。
已配置的 IPv6 地址带网卡索引，例如 `%9`。如果更换网卡或有线连接后无法连接，应重新确认网卡索引。
主机指纹不同会拒绝连接。只有核实算力机主机密钥后才能更新指纹。

## 构建与验证

需要 .NET 10 SDK：

```powershell
dotnet publish PerfTicker/PerfTicker.csproj -c Release -o dist
```

开发检查（Python 单元测试不需要第三方依赖）：

```powershell
py -m unittest discover -s tests -v
```

本机真实连接集成检查使用已加密的连接配置及 Paramiko：

```powershell
py tools/verify_remote.py
dotnet run --project tests/PerfTicker.Tests/PerfTicker.Tests.csproj -c Release -- artifacts/linux-samples.json --live
```

集成检查通过本机临时 TCP 转发模拟卡顿、断线和恢复，不中断模型服务或调整算力机网络。
`PerfTickerRemote.exe --probe <输出JSON路径>` 可在无界面模式下获取 3 次真实采样用于诊断。

本次验证：5 项 Python 单元测试、13 项 .NET/SSH 集成检查通过；Windows 实机界面检查通过。

## 来源

- 原项目：https://github.com/QiuSui1145/PerfTicker
- 原作者：QiuSui / QiuSui_Studio
- 协议：仓库根目录 LICENSE（GPL-3.0）
- SSH 客户端：SSH.NET 2026.0.0（MIT）
