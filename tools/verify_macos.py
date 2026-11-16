"""Run on macOS: validate the archived app, native GUI, and loopback SSH transport.

SSH uses synthetic Linux/four-GPU metrics over a real encrypted connection. It
does not claim access to physical NVIDIA hardware or a remote Linux machine.
"""
import argparse
import base64
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import plistlib
import socket
import subprocess
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[1]


def check(value, message):
    if not value:
        raise AssertionError(message)
    print("PASS " + message, flush=True)


def run(command, **kwargs):
    return subprocess.run(command, check=True, text=True, timeout=180, **kwargs)


def ssh_checks(test_dll, env):
    import paramiko

    host_key = paramiko.RSAKey.generate(2048)
    client_key = paramiko.RSAKey.generate(2048)
    stop = threading.Event()
    commands = []

    class Server(paramiko.ServerInterface):
        def get_allowed_auths(self, username):
            return "password,publickey"

        def check_auth_password(self, username, password):
            return paramiko.AUTH_SUCCESSFUL if username == "fixture" and password == "integration-only" else paramiko.AUTH_FAILED

        def check_auth_publickey(self, username, key):
            return paramiko.AUTH_SUCCESSFUL if username == "fixture" and key == client_key else paramiko.AUTH_FAILED

        def check_channel_request(self, kind, chanid):
            return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

        def check_channel_exec_request(self, channel, command):
            # Check that the application transmits its actual embedded collector.
            command = command.decode()
            if not command.startswith('python3 -u -c "import base64;exec(base64.b64decode(\''):
                return False
            encoded = command.split("'", 2)[1]
            if base64.b64decode(encoded) != (ROOT / "remote/collector.py").read_bytes():
                return False
            commands.append(command.rsplit(" ", 2)[-2:])
            threading.Thread(target=stream, args=(channel,), daemon=True).start()
            return True

    def stream(channel):
        try:
            for i in range(6):
                snapshot = {"Protocol": 2, "IsRemote": True, "HostName": "ssh-test-fixture",
                            "OsName": "Linux (synthetic fixture)", "CoreMHz": [2400, 2400],
                            "CpuPct": 20 + i, "MemTotalGB": 8, "MemUsedGB": 2,
                            "Gpus": [{"Index": n, "Id": f"GPU-fixture-{n}", "Available": True,
                                      "Name": f"Fixture GPU {n}", "UtilPct": 10 + n,
                                      "MemUsedMB": 512, "MemTotalMB": 4096} for n in range(4)]}
                channel.sendall((json.dumps(snapshot) + "\n").encode())
                time.sleep(0.5)
            channel.send_exit_status(0)
        except (OSError, EOFError):
            pass
        finally:
            channel.close()

    def connection(sock):
        transport = paramiko.Transport(sock)
        try:
            transport.add_server_key(host_key)
            transport.start_server(server=Server())
            while transport.is_active() and not stop.wait(0.1):
                pass
        except (paramiko.SSHException, EOFError, OSError):
            pass  # Fingerprint inspection and rejection intentionally abort.
        finally:
            transport.close()

    with socket.socket() as listener, tempfile.TemporaryDirectory() as directory:
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        listener.settimeout(0.2)

        def accept():
            while not stop.is_set():
                try:
                    sock, _ = listener.accept()
                    threading.Thread(target=connection, args=(sock,), daemon=True).start()
                except socket.timeout:
                    pass
                except OSError:
                    break

        thread = threading.Thread(target=accept, daemon=True)
        thread.start()
        key_path = Path(directory) / "identity"
        client_key.write_private_key_file(str(key_path))
        key_path.chmod(0o600)
        config = Path(directory) / "fixture.json"
        config.write_text(json.dumps({"Host": "127.0.0.1", "Port": listener.getsockname()[1],
                                     "User": "fixture", "PrivateKeyPath": str(key_path),
                                     "Fingerprint": "SHA256:" + base64.b64encode(hashlib.sha256(host_key.asbytes()).digest()).decode().rstrip("=")}), encoding="utf-8")
        try:
            run(["dotnet", str(test_dll), "--ssh"], env={**env, "PERFTICKER_SSH_FIXTURE": str(config)})
            check(len(commands) >= 6, "embedded collector transmitted across SSH reconnects")
        finally:
            stop.set()
            thread.join(timeout=2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=["osx-x64", "osx-arm64"])
    args = parser.parse_args()
    check(platform.system() == "Darwin", "running on real macOS")
    check(platform.machine() == {"osx-x64": "x86_64", "osx-arm64": "arm64"}[args.rid], "runner matches package architecture")
    output = ROOT / "artifacts/runtime"
    output.mkdir(parents=True, exist_ok=True)
    archive = ROOT / f"dist/packages/macOS/PerfTicker-1.7.0-{args.rid}.zip"
    expected = archive.with_suffix(".zip.sha256").read_text().split()[0]
    check(hashlib.sha256(archive.read_bytes()).hexdigest() == expected, "download archive checksum")
    extracted = output / "extracted with spaces"
    run(["ditto", "-x", "-k", str(archive), str(extracted)])
    app = next(extracted.glob("*/PerfTicker.app"))
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    executable = app / "Contents/MacOS" / info["CFBundleExecutable"]
    check(os.access(executable, os.X_OK), "archive preserves executable permission")
    run(["codesign", "--verify", "--deep", "--strict", str(app)])
    check(True, "extracted app signature verifies")
    probe = run([str(executable), "--probe-local"], capture_output=True, cwd="/tmp")
    samples = [json.loads(line) for line in probe.stdout.splitlines()]
    check(len(samples) == 3 and samples[0]["Tick"] < samples[1]["Tick"] < samples[2]["Tick"], "packaged app produces three advancing snapshots")
    for s in samples:
        check(math.isfinite(s["CpuPct"]) and 0 <= s["CpuPct"] <= 100, "real CPU within range")
        check(0 < s["MemTotalGB"] and 0 <= s["MemUsedGB"] <= s["MemTotalGB"], "real memory within range")
        check(s["ProcCount"] > 0 and len(s["Procs"]) > 0, "real processes enumerated")
        check(all(s[n] is not None and s[n] >= 0 for n in ["NetRxMBps", "NetTxMBps"]), "network counters available")
        check(s["Gpu"]["UtilPct"] is None, "unsupported Mac GPU utilization stays unavailable")
    # Launch the actual extracted bundle through Launch Services, as Finder does.
    with (output / "launch-stdout.log").open("w") as stdout, (output / "launch-stderr.log").open("w") as stderr:
        launch = subprocess.Popen(["open", "-W", "-n", str(app)], stdout=stdout, stderr=stderr)
        try:
            time.sleep(12)
            check(launch.poll() is None, "packaged app stays open through Launch Services")
            run(["pgrep", "-f", str(executable)], capture_output=True)
            check(True, "packaged native process is running")
        finally:
            subprocess.run(["pkill", "-f", str(executable)], check=False)
            launch.wait(timeout=20)
    test_dll = ROOT / "tests/PerfTicker.Desktop.Tests/bin/Release/net10.0/PerfTicker.Desktop.Tests.dll"
    env = {**os.environ, "PERFTICKER_TEST_OUTPUT": str(output)}
    run(["dotnet", str(test_dll), "--gui"], env=env)
    ssh_checks(test_dll, env)
    (output / "result.json").write_text(json.dumps({"result": "passed", "rid": args.rid,
        "macOS": platform.mac_ver()[0], "architecture": platform.machine(),
        "checks": ["archive", "signature", "live sampling", "Launch Services", "native GUI", "loopback SSH"],
        "ssh_metrics": "synthetic Linux / four GPU fixture; real SSH transport"}, indent=2), encoding="utf-8")
    print("All macOS runtime checks passed.", flush=True)


if __name__ == "__main__":
    main()
