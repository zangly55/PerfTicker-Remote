import importlib.util
from pathlib import Path
import struct
import unittest

ROOT = Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


class MacPackageChecks(unittest.TestCase):
    def test_universal_headers_and_own_install_name_are_not_dependencies(self):
        text = """/tmp/Application With Spaces.app/libAvalonia.Native.OSX.dylib (architecture arm64):
Load command 3
          cmd LC_ID_DYLIB
         name /usr/local/lib/libAvalonia.Native.OSX.dylib (offset 24)
Load command 4
          cmd LC_LOAD_DYLIB
         name /System/Library/Frameworks/AppKit.framework/Versions/C/AppKit (offset 24)
Load command 5
          cmd LC_LOAD_WEAK_DYLIB
         name @rpath/Example With Spaces.dylib (offset 24)
Load command 6
          cmd LC_LOAD_DYLIB
         name /usr/local/lib/actual-external-dependency.dylib (offset 24)
"""
        self.assertEqual(list(module("verify_macos_download").loaded_libraries(text)), [
            "/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit",
            "@rpath/Example With Spaces.dylib", "/usr/local/lib/actual-external-dependency.dylib"])

    def test_native_icon_reuses_existing_png(self):
        icon = module("package_desktop").mac_icon((ROOT / "PerfTicker/app.ico").read_bytes())
        self.assertEqual(icon[:4], b"icns")
        self.assertEqual(struct.unpack_from(">I", icon, 4)[0], len(icon))
        self.assertEqual(icon[8:12], b"ic08")
        self.assertEqual(icon[16:24], b"\x89PNG\r\n\x1a\n")


if __name__ == "__main__":
    unittest.main()
