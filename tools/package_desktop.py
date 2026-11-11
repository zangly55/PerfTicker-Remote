"""Package a published build without running it; usable on Windows, Linux or macOS."""
import argparse
import hashlib
import os
from pathlib import Path
import plistlib
import shutil
import stat
import subprocess
import sys
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "1.7.0"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=["win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"])
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", default=ROOT / "dist" / "packages", type=Path)
    parser.add_argument("--allow-dirty", action="store_true", help="Include current tracked/new source for a local development package")
    args = parser.parse_args()
    source = args.input.resolve()
    if not source.is_dir():
        raise SystemExit("Missing publish directory")
    family = "Windows" if args.rid.startswith("win") else "macOS" if args.rid.startswith("osx") else "Linux"
    output = args.output.resolve() / family
    output.mkdir(parents=True, exist_ok=True)
    name = f"PerfTicker-{VERSION}-{args.rid}"
    staging = output / name
    # Never erase an existing staging tree, especially a user-selected directory.
    staging.mkdir(exist_ok=False)
    if family == "macOS":
        app = staging / "PerfTicker.app"
        target = app / "Contents" / "MacOS"
        target.mkdir(parents=True)
        info = {"CFBundleName": "PerfTicker", "CFBundleDisplayName": "PerfTicker 性能行情终端",
                "CFBundleIdentifier": "io.github.zangly55.perfticker", "CFBundleExecutable": "PerfTickerDesktop",
                "CFBundlePackageType": "APPL", "CFBundleVersion": VERSION,
                "CFBundleShortVersionString": VERSION, "LSMinimumSystemVersion": "14.0",
                "NSHighResolutionCapable": True}
        (app / "Contents" / "Info.plist").write_bytes(plistlib.dumps(info))
    else:
        target = staging
    for path in source.iterdir():
        if path.is_dir():
            shutil.copytree(path, target / path.name)
        else:
            shutil.copy2(path, target / path.name)
    executable = target / ("PerfTickerRemote.exe" if family == "Windows" else "PerfTickerDesktop")
    if not executable.is_file():
        raise SystemExit("Published executable is missing")
    executable.chmod(0o755)
    for file in target.rglob("*"):
        if file.is_file() and file.suffix in (".so", ".dylib"):
            file.chmod(0o755)
    shutil.copy2(ROOT / "LICENSE", staging / "LICENSE")
    shutil.copy2(ROOT / "docs" / "PLATFORMS.md", staging / "README.md")
    shutil.copytree(ROOT / "packaging" / "licenses", staging / "licenses")
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=normal"], cwd=ROOT, text=True).strip()
    if dirty and not args.allow_dirty:
        raise SystemExit("Source is uncommitted; commit before packaging or use --allow-dirty")
    if args.allow_dirty:
        files = subprocess.check_output(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT).decode().split("\0")
        with zipfile.ZipFile(staging / "source.zip", "w", zipfile.ZIP_DEFLATED) as archive:
            for relative in sorted(set(files)):
                if relative and (ROOT / relative).is_file():
                    archive.write(ROOT / relative, "PerfTicker-Remote/" + relative)
    else:
        subprocess.run(["git", "archive", "--format=zip", "--prefix=PerfTicker-Remote/", "-o", str(staging / "source.zip"), "HEAD"], cwd=ROOT, check=True)
    (staging / "SOURCE.txt").write_text(f"GPL-3.0 · Original author QiuSui / QiuSui_Studio\nhttps://github.com/zangly55/PerfTicker-Remote\nCommit: {sha}\nRID: {args.rid}\nSource: source.zip" + (" (working tree)" if dirty else "") + "\n", encoding="utf-8")
    if family == "macOS" and sys.platform == "darwin":
        # Local ad-hoc signature permits a coherent bundle, not Developer ID notarization.
        subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(app)], check=True)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], check=True)
    if family == "Linux":
        bundle = output / (name + ".tar.gz")
        with tarfile.open(bundle, "w:gz") as archive:
            def mode(info):
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                info.mode = 0o755 if info.isdir() or info.name.endswith("/PerfTickerDesktop") or info.name.endswith((".so", ".dylib")) else 0o644
                return info
            archive.add(staging, arcname=name, filter=mode)
    else:
        bundle = output / (name + ".zip")
        with zipfile.ZipFile(bundle, "w", zipfile.ZIP_DEFLATED) as archive:
            for path in staging.rglob("*"):
                if not path.is_file():
                    continue
                relative = path.relative_to(output).as_posix()
                info = zipfile.ZipInfo.from_file(path, relative)
                info.create_system = 3
                executable_file = path == executable or path.suffix in (".so", ".dylib")
                info.external_attr = (stat.S_IFREG | (0o755 if executable_file else 0o644)) << 16
                archive.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
    with bundle.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    bundle.with_name(bundle.name + ".sha256").write_text(f"{digest}  {bundle.name}\n", encoding="ascii")
    print(bundle)


if __name__ == "__main__":
    main()
