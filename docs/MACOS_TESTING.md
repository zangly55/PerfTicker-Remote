# Desktop validation

The default checks use fixtures and do not start a GUI, sample hardware or open SSH connections:

```sh
dotnet run --project tests/PerfTicker.Desktop.Tests -c Release
python3 -m unittest discover -s tests -p 'test_collector.py'
```

## macOS runtime checks

`.github/workflows/macos-runtime.yml` runs separately on native Intel and Apple Silicon macOS 15 runners. It builds a self-contained app, signs and archives it, then tests the **extracted ZIP**, including a directory containing spaces:

- SHA256, executable permissions and strict recursive signature verification after extraction.
- Three advancing local snapshots with valid CPU, memory, network and process data.
- Launch Services startup (`open -W -n`), with the packaged process remaining alive.
- Actual Avalonia/macOS backend, live labels/process rows/candle rendering, pause/resume, opening/cancelling SSH settings, resize and rendered screenshot.
- Real loopback SSH handshake, host fingerprint discovery/rejection, password and private-key authentication, embedded collector transmission, four-GPU snapshot parsing, interval changes, EOF reconnection and disposal.

The SSH endpoint uses temporary generated keys and **synthetic Linux/four-GPU metrics**. This tests the Mac client's transport and parsing; it does not test a physical NVIDIA server. Native GUI checks use the production `DesktopApp` and `MarketWindow` with the normal platform backend, not Avalonia Headless. Their control events are programmatic, not mouse/keyboard automation.

To reproduce on a Mac with .NET 10 SDK and Python 3.12 (use `osx-x64` on Intel):

```sh
dotnet run --project tests/PerfTicker.Desktop.Tests -c Release
dotnet publish CrossPlatform/PerfTicker.Desktop.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/publish
python3 tools/package_desktop.py --rid osx-arm64 --input artifacts/publish
python3 -m pip install paramiko==4.0.0
python3 tools/verify_macos.py --rid osx-arm64
```

Use a clean checkout; packaging refuses to overwrite a previous staging directory. The runtime command opens and closes application windows and uses local hardware/loopback networking. Run it in a dedicated test account: GUI initialization reads that account's PerfTicker and SSH configuration. Screenshots can show host and process names; CI only uploads screenshots from disposable hosted runners. Raw local snapshots and temporary SSH credentials are not uploaded.

The workflow uploads the package, screenshot, launch logs and result metadata. Gatekeeper acceptance of a quarantined internet download, Developer ID notarization, macOS 14, and physical GPU accuracy require separate validation. An ad-hoc signature is not Developer ID notarization.
