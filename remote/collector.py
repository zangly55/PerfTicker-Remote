"""Read-only Linux telemetry. Stdlib only; JSON lines on stdout, no listening port.

Runs from memory over SSH. Arguments: interval_seconds legacy_gpu_index [sample_count].
All NVIDIA GPUs are collected in two batched nvidia-smi queries per sample.
Unavailable numeric sensors are JSON null. CPU process percentages are whole-host.
"""
import csv
import glob
import io
import json
import math
import os
import platform
import signal
import subprocess
import sys
import time


def read(path, default=""):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return default


def number(value):
    try:
        n = float(value)
        return n if math.isfinite(n) else None
    except (TypeError, ValueError):
        return None


def cpu_stats():
    cpus, ctxt = {}, 0
    for line in read('/proc/stat').splitlines():
        p = line.split()
        if p and p[0].startswith('cpu'):
            v = list(map(int, p[1:9]))  # exclude guest, already included in user/nice
            cpus[p[0]] = (sum(v), v[3] + v[4])
        elif p and p[0] == 'ctxt':
            ctxt = int(p[1])
    return cpus, ctxt


def cpu_percent(now, before):
    dt, di = now[0] - before[0], now[1] - before[1]
    return max(0.0, min(100.0, 100.0 * (dt - di) / dt)) if dt > 0 else 0.0


def memory():
    m = {}
    for line in read('/proc/meminfo').splitlines():
        key, val = line.split(':', 1)
        m[key] = int(val.strip().split()[0])
    total = m.get('MemTotal', 0)
    available = m.get('MemAvailable', m.get('MemFree', 0) + m.get('Buffers', 0) + m.get('Cached', 0))
    used = max(0, total - available)
    return dict(MemTotalGB=total / 1048576, MemUsedGB=used / 1048576,
                MemPct=100 * used / total if total else 0,
                PageUsedGB=m.get('Committed_AS', 0) / 1048576,
                PageTotalGB=m.get('CommitLimit', 0) / 1048576)


def disks():
    result = {}
    for path in glob.glob('/sys/block/*/stat'):
        name = path.split('/')[-2]
        if name.startswith(('loop', 'ram', 'zram', 'dm-', 'md')):
            continue
        p = read(path).split()
        if len(p) >= 11:
            result[name] = (int(p[2]) * 512, int(p[6]) * 512, int(p[9]))
    return result


def network():
    result = {}
    for line in read('/proc/net/dev').splitlines()[2:]:
        name, counters = line.split(':', 1)
        name = name.strip()
        # Host physical NICs only, avoiding bridge/veth/container double counting.
        if not os.path.exists('/sys/class/net/' + name + '/device'):
            continue
        p = counters.split()
        result[name] = (int(p[0]), int(p[8]))
    return result


def rates(current, previous, dt, width):
    totals = [0.0] * width
    for name, values in current.items():
        if name not in previous:
            continue
        for i in range(width):
            totals[i] += max(0, values[i] - previous[name][i]) / dt
    return totals


def processes(previous, dt, cores):
    current, rows = {}, []
    hz = os.sysconf('SC_CLK_TCK')
    page = os.sysconf('SC_PAGE_SIZE')
    for folder in glob.glob('/proc/[0-9]*'):
        try:
            raw = read(folder + '/stat')
            left, right = raw.index('('), raw.rindex(')')
            pid = int(raw[:left].strip())
            p = raw[right + 2:].split()
            ticks = int(p[11]) + int(p[12])
            start = int(p[19])
            current[pid] = (ticks, start)
            old = previous.get(pid)
            cpu = min(100.0, max(0, ticks - old[0]) / hz / dt / cores * 100) if old and old[1] == start else 0.0
            try:
                handles = len(os.listdir(folder + '/fd'))
            except OSError:
                handles = -1
            rows.append(dict(Pid=pid, Name=raw[left + 1:right], CpuPct=cpu, CpuNorm=cpu,
                             GpuPct=None, GpuMemMB=None, MemMB=max(0, int(p[21])) * page / 1048576,
                             Threads=int(p[17]), Handles=handles))
        except (OSError, ValueError, IndexError):
            continue  # Process exited during sampling.
    return current, rows


GPU_FIELDS = ['name', 'driver_version', 'utilization.gpu', 'utilization.memory',
              'clocks.sm', 'clocks.mem', 'clocks.max.sm', 'memory.used', 'memory.total',
              'temperature.gpu', 'power.draw', 'power.limit', 'fan.speed']
GPU_NUMBERS = ['UtilPct', 'MemUtilPct', 'ClockMHz', 'MemClockMHz', 'MaxClockMHz',
               'MemUsedMB', 'MemTotalMB', 'TempC', 'PowerW', 'PowerLimitW', 'FanPct']


def gpus():
    try:
        result = subprocess.run(['nvidia-smi',
                                 '--query-gpu=index,uuid,' + ','.join(GPU_FIELDS),
                                 '--format=csv,noheader,nounits'],
                                capture_output=True, text=True, timeout=3, check=True)
        cards, drivers, warnings = [], [], []
        for row in csv.reader(io.StringIO(result.stdout)):
            if not row:
                continue
            try:
                if len(row) != len(GPU_FIELDS) + 2 or int(row[0]) < 0 or not row[1].strip():
                    raise ValueError('unexpected nvidia-smi columns')
                g = dict(zip(GPU_NUMBERS, (number(x.strip()) for x in row[4:])))
                g.update(Index=int(row[0]), Id=row[1].strip(), Name=row[2].strip(), Available=True, Nvml=True)
                cards.append(g)
                drivers.append(row[3].strip())
            except (ValueError, IndexError):
                warnings.append('部分 GPU 数据无法解析')
        return sorted(cards, key=lambda g: g['Index']), ', '.join(dict.fromkeys(drivers)), '; '.join(dict.fromkeys(warnings))
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        return [], '', 'GPU 采集不可用 (' + type(exc).__name__ + ')'


def cpu_info():
    entries = []
    for block in read('/proc/cpuinfo').strip().split('\n\n'):
        info = dict(line.split(':', 1) for line in block.splitlines() if ':' in line)
        entries.append({k.strip(): v.strip() for k, v in info.items()})
    return entries


def gpu_process_memory():
    try:
        result = subprocess.run(['nvidia-smi',
                                 '--query-compute-apps=gpu_uuid,pid,used_gpu_memory', '--format=csv,noheader,nounits'],
                                capture_output=True, text=True, timeout=3, check=True)
        memory = {}
        for row in csv.reader(io.StringIO(result.stdout)):
            if len(row) != 3:
                continue
            try:
                pid, identity, value = int(row[1].strip()), row[0].strip(), number(row[2].strip())
            except ValueError:
                continue
            by_gpu = memory.setdefault(pid, {})
            old = by_gpu.get(identity, 0)
            by_gpu[identity] = old + value if old is not None and value is not None else None
        return memory
    except (OSError, ValueError, subprocess.SubprocessError):
        return {}


def attach_gpu_memory(rows, memory):
    for row in rows:
        by_gpu = memory.get(row['Pid'], {})
        row['GpuMemory'] = by_gpu
        row['GpuMemMB'] = sum(by_gpu.values()) if by_gpu and all(v is not None for v in by_gpu.values()) else None


def main():
    interval = max(0.5, min(10, float(sys.argv[1]) if len(sys.argv) > 1 else 1))
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    info = cpu_info()
    physical = len({(c['physical id'], c['core id']) for c in info if 'physical id' in c and 'core id' in c})
    osinfo = dict(line.split('=', 1) for line in read('/etc/os-release').splitlines() if '=' in line)
    metadata = dict(Protocol=2, IsRemote=True, HostName=platform.node(),
                    OsName=osinfo.get('PRETTY_NAME', 'Linux').strip('"'),
                    OsBuild=platform.release(), Arch=platform.machine(), PhysicalCores=physical,
                    CpuName=info[0].get('model name', '处理器') if info else '处理器')
    prev_cpu, prev_ctx = cpu_stats()
    cores = max(1, len(prev_cpu) - 1)
    previous_procs, _ = processes({}, 1, cores)
    prev_disks, prev_net = disks(), network()
    last = time.monotonic()
    count = 0
    if hasattr(signal, 'SIGPIPE'):
        signal.signal(signal.SIGPIPE, signal.SIG_DFL)
    while not limit or count < limit:
        time.sleep(max(0.01, interval - (time.monotonic() - last)))
        now = time.monotonic()
        dt = max(0.001, now - last)
        cpu, ctxt = cpu_stats()
        keys = sorted((k for k in cpu if k != 'cpu'), key=lambda k: int(k[3:]))
        cores = len(keys)
        procs, rows = processes(previous_procs, dt, max(1, cores))
        disk, net = disks(), network()
        dr = rates(disk, prev_disks, dt, 3)
        nr = rates(net, prev_net, dt, 2)
        mhz = {int(c.get('processor', '-1')): number(c.get('cpu MHz')) for c in cpu_info()}
        freqs = []
        for key in keys:
            value = number(read('/sys/devices/system/cpu/' + key + '/cpufreq/scaling_cur_freq').strip())
            freqs.append(value / 1000 if value is not None else mhz.get(int(key[3:])))
        good_freqs = [x for x in freqs if x is not None]
        peak = [number(read(p).strip()) for p in glob.glob('/sys/devices/system/cpu/cpu*/cpufreq/cpuinfo_max_freq')]
        speeds = [number(read('/sys/class/net/' + name + '/speed').strip()) for name in net]
        cards, driver, warning = gpus()
        attach_gpu_memory(rows, gpu_process_memory() if cards else {})
        snap = dict(metadata, Tick=count + 1, WallSeconds=now, CoreMHz=freqs,
                    CorePct=[cpu_percent(cpu[k], prev_cpu.get(k, cpu[k])) for k in keys],
                    CpuPct=cpu_percent(cpu['cpu'], prev_cpu.get('cpu', cpu['cpu'])),
                    CpuAvgMHz=sum(good_freqs) / len(good_freqs) if good_freqs else None,
                    CpuMaxMHz=max(good_freqs, default=None),
                    CpuPeakMHz=max((v / 1000 for v in peak if v is not None), default=max(good_freqs, default=0)),
                    Gpus=cards, GpuDriver=driver, SensorWarning=warning,
                    DiskReadMBps=dr[0] / 1048576, DiskWriteMBps=dr[1] / 1048576,
                    DiskBusyPct=min(100, dr[2] / 10 / max(1, len(disk))), FixedDisks=len(disk),
                    NetRxMBps=nr[0] / 1048576, NetTxMBps=nr[1] / 1048576,
                    NetMaxGbps=max((v / 1000 for v in speeds if v is not None and v > 0), default=0),
                    CtxSwitchPerSec=max(0, ctxt - prev_ctx) / dt, ProcCount=len(rows),
                    Threads=sum(p['Threads'] for p in rows), Handles=sum(max(0, p['Handles']) for p in rows),
                    UptimeSeconds=number(read('/proc/uptime').split()[0]), Procs=rows, **memory())
        print(json.dumps(snap, ensure_ascii=True, allow_nan=False, separators=(',', ':')), flush=True)
        prev_cpu, prev_ctx, previous_procs, prev_disks, prev_net, last = cpu, ctxt, procs, disk, net, now
        count += 1


if __name__ == '__main__':
    main()
