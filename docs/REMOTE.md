# PerfTicker 本机 / 多显卡远程版

基于 [QiuSui1145/PerfTicker](https://github.com/QiuSui1145/PerfTicker) v1.5.0，保留原作者 QiuSui / QiuSui_Studio 及 GPL-3.0 许可。
当前分支版本：1.6.0-remote.2。上游基线：6ddf4d07dcb1f9a5a0061388e70feddfb64d370e。

保留股票行情、K 线、自选、主力动向、进程表、桌面挂件。支持 Windows 本机和 Linux SSH 服务器，详见 [快速上手](QUICKSTART.md)。

## 采集与多卡

| 对象 | 本机 Windows | 远程 Linux |
| --- | --- | --- |
| CPU、内存、进程 | Windows 原生接口与性能计数器 | `/proc`、`/sys` |
| NVIDIA 显卡 | NVML 枚举全部设备 | `nvidia-smi` 每轮批量查询全部设备 |
| 非 NVIDIA 显卡 | 可用时提供系统 GPU 引擎汇总，详细传感器可能缺失 | 暂不支持 AMD/Intel GPU 传感器，CPU/内存仍可用 |
| GPU 进程排行 | 全机 GPU 引擎利用率，明确标注“全机” | NVIDIA 计算进程显存，可按 GPU UUID 分卡统计 |
| 系统句柄 | Windows 句柄 | Linux 文件描述符 |

显卡以 UUID 绑定行情。四卡、八卡或更多设备自动出现在总览中，不限制为 GPU 0。选卡后分别显示频率、负载、显存、温度、功耗、风扇及已用显存。
多卡汇总为负载/频率平均、显存/功耗求和、温度/风扇最大；不完整读数保持缺失。设备替换不继承另一张卡的历史，新增设备不会清空其他卡与 CPU 的历史。

远程采集每轮只执行一次全卡指标查询和一次全卡进程显存查询，不按卡数增加 SSH 连接或启动独立采集器。
支持 NVIDIA 驱动枚举的物理 GPU；MIG 实例的独立行情尚未实现。容器或权限限制可能只暴露部分设备。
传感器不支持或权限不足显示“—”，不会生成假的零值 K 线。

磁盘吞吐汇总普通物理块设备，排除 loop、ram、zram、dm、md，避免重复计数。网络汇总物理网卡，排除 bridge/veth 的重复流量。
每次启动或切换目标重新积累行情，不提供持久化历史数据库。营收是模拟玩法，不代表真实收益或模型吞吐量。两个模式均不提供结束进程功能。

## 连接与配置

单文件 Windows x64 程序包含 .NET 运行时和 SSH.NET。客户端不需要 Python 或 OpenSSH；远端需要 Python 3、NVIDIA 驱动及 `nvidia-smi`。
Python 采集器仅使用标准库，通过 SSH 在内存运行，不在远端写文件或安装常驻服务。

支持密码与私钥文件（含口令）认证。检测按钮只读取主机指纹；正式认证必须匹配已核实的固定指纹。指纹不一致拒绝连接。
配置位于 `%LOCALAPPDATA%\PerfTickerRemote\`：

- `remote.json`：地址、端口、用户名、主机指纹、私钥路径及 DPAPI 加密凭据。解密后的属性明确排除在 JSON 序列化之外。
- `settings.json`：当前监控模式与界面偏好。
- `logs\`：诊断日志。

新电脑默认本机模式；旧版用户保留远程模式。切换时销毁旧采集器、清空旧主机的图表与进程状态。服务器断线时仅重连原目标，不回退到本机。
超过 `max(5 秒, 3 × 采样间隔)` 未收到数据时标记过期，保留最后数据并停止模拟营收累计。每 3 秒尝试重连。

## 构建与验证

```powershell
dotnet publish PerfTicker/PerfTicker.csproj -c Release -o dist
py -m unittest discover -s tests -v
dotnet run --project tests/PerfTicker.Tests -c Release
dotnet run --project tests/PerfTicker.Tests -c Release -- --local
```

默认 .NET 测试使用合成数据，不需要用户配置或采样文件。覆盖 4/8/32 卡、设备重排/替换、聚合缺失值、按卡进程显存、SSH config 解析及凭据序列化。
`--local` 额外验证真实 Windows 本机采集与停止状态。

真实 SSH 验证使用当前 Windows 用户已保存的配置。Python 工具另需安装 Paramiko：

```powershell
py tools/verify_remote.py
dotnet run --project tests/PerfTicker.Tests -c Release -- artifacts/linux-samples.json --live
```

通过临时 TCP 转发测试卡顿、断线、恢复、周期变更和错误主机指纹，不改服务器网络或模型服务。
`PerfTickerRemote.exe --probe <输出JSON路径>` 获取 3 次真实远程采样，诊断文件可能含主机与进程信息，不应提交仓库。

验证范围：多卡采用 4/8/32 卡模拟数据；实机验证为 Windows 本机和一台 Linux 单卡 NVIDIA 服务器，未声称完成真实四卡/八卡机器测试。
本版检查结果：9 项 Python 测试、38 项 .NET / 本机 / SSH 检查通过；主窗口、显卡总览、逐卡 K 线及连接表单已检查，并修正高 DPI 表单缩放。

## 来源

- 上游作者与许可证：仓库根目录 `LICENSE`（GPL-3.0）。
- SSH 客户端：[SSH.NET](https://github.com/sshnet/SSH.NET)（MIT）。
- NVIDIA 指标：[nvidia-smi 官方说明](https://docs.nvidia.com/deploy/nvidia-smi/)。
