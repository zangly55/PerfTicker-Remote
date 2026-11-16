"""Validate a downloaded package on a different Mac runner from its builder.

No SDK setup, build outputs, Python packages or Homebrew paths are used by the app.
Gatekeeper assessment is reported separately from runtime compatibility: these
community packages are not notarized and require the user's per-app approval.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import subprocess
import tempfile
import time


def checked(command, **kwargs):
    return subprocess.run(command, text=True, check=True, timeout=90, **kwargs)


def loaded_libraries(load_commands):
    """Read load commands, excluding a dylib's own LC_ID_DYLIB install name."""
    command = ""
    for line in load_commands.splitlines():
        fields = line.strip().split(maxsplit=1)
        if len(fields) != 2:
            continue
        if fields[0] == "cmd":
            command = fields[1]
        elif fields[0] == "name" and command in {"LC_LOAD_DYLIB", "LC_LOAD_WEAK_DYLIB", "LC_REEXPORT_DYLIB", "LC_LOAD_UPWARD_DYLIB"}:
            yield fields[1].split(" (offset", 1)[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    assert platform.system() == "Darwin"
    assert platform.machine() == {"osx-arm64": "arm64", "osx-x64": "x86_64"}[args.rid]
    archives = list(args.input.rglob(f"PerfTicker-*-{args.rid}.zip"))
    assert len(archives) == 1, "Expected one distributable ZIP"
    archive = archives[0].resolve()
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == archive.with_suffix(".zip.sha256").read_text().split()[0]
    args.output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="PerfTicker download ") as scratch:
        scratch = Path(scratch)
        checked(["/usr/bin/ditto", "-x", "-k", str(archive), str(scratch / "unpacked")])
        source_app = next((scratch / "unpacked").glob("*/PerfTicker.app"))
        # Copy only the .app, as a recipient does; no adjacent source/build files.
        app = scratch / "Applications" / "PerfTicker.app"
        app.parent.mkdir()
        checked(["/usr/bin/ditto", str(source_app), str(app)])
        checked(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(app)])
        info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
        executable = app / "Contents/MacOS" / info["CFBundleExecutable"]
        assert executable.is_file() and os.access(executable, os.X_OK)
        native = [executable, *app.rglob("*.dylib")]
        for library in native:
            commands = checked(["/usr/bin/otool", "-l", str(library)], capture_output=True).stdout
            for dependency in loaded_libraries(commands):
                assert not dependency.startswith("/") or dependency.startswith(("/usr/lib/", "/System/Library/")), f"Non-system dependency: {dependency}"
        environment = {k: v for k, v in os.environ.items() if not k.startswith(("DOTNET_", "DYLD_", "NUGET_"))}
        environment.update(PATH="/usr/bin:/bin:/usr/sbin:/sbin", DOTNET_ROOT=str(scratch / "no-dotnet"),
                           DOTNET_ROOT_ARM64=str(scratch / "no-dotnet"), DOTNET_ROOT_X64=str(scratch / "no-dotnet"),
                           DOTNET_MULTILEVEL_LOOKUP="0")
        probes = checked([str(executable), "--probe-local"], cwd="/tmp", env=environment, capture_output=True)
        samples = [json.loads(line) for line in probes.stdout.splitlines()]
        assert len(samples) == 3 and samples[-1]["Tick"] > samples[0]["Tick"]
        assert all(0 <= s["CpuPct"] <= 100 and s["MemTotalGB"] > 0 and s["ProcCount"] > 0 for s in samples)
        print("PASS downloaded app samples without external SDK or package-manager paths", flush=True)
        process = subprocess.Popen([str(executable)], cwd="/tmp", env=environment,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
        try:
            time.sleep(12)
            assert process.poll() is None, "Standalone GUI exited before startup acceptance"
            print("PASS downloaded standalone GUI remains running", flush=True)
        finally:
            if process.poll() is None:
                process.terminate()
            _, stderr = process.communicate(timeout=20)
            (args.output / "standalone-stderr.log").write_text(stderr, encoding="utf-8")
        # A separate copy has never been launched. Preserve quarantine; do not
        # remove it, disable Gatekeeper, or label this a notarization success.
        quarantined = scratch / "First download" / "PerfTicker.app"
        quarantined.parent.mkdir()
        checked(["/usr/bin/ditto", str(source_app), str(quarantined)])
        checked(["/usr/bin/xattr", "-w", "com.apple.quarantine", f"0083;{int(time.time()):x};Safari;", str(quarantined)])
        checked(["/usr/bin/xattr", "-p", "com.apple.quarantine", str(quarantined)], capture_output=True)
        policy = subprocess.run(["/usr/sbin/spctl", "--assess", "--type", "execute", "--verbose=4", str(quarantined)],
                                capture_output=True, text=True, timeout=90)
        policy_text = policy.stdout + policy.stderr
        (args.output / "gatekeeper.log").write_text(policy_text, encoding="utf-8")
        assert policy.returncode == 3 or (policy.returncode == 1 and "rejected" in policy_text.lower()) or (policy.returncode == 2 and "rejected" in policy_text.lower()), policy_text
        assert "rejected" in policy_text.lower(), "Unexpected Gatekeeper verdict; investigate before distribution"
        checked(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(quarantined)])
        result = {"runtime": "passed", "rid": args.rid, "macOS": platform.mac_ver()[0],
                  "separate_from_builder": True, "external_dependency_paths": False,
                  "gatekeeper": "requires-user-approval", "notarized": False,
                  "manual_settings_approval_automated": False,
                  "package_sha256": hashlib.sha256(archive.read_bytes()).hexdigest()}
        (args.output / "download-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print("PASS signature remains valid with download quarantine; Gatekeeper requires per-app approval", flush=True)


if __name__ == "__main__":
    main()
