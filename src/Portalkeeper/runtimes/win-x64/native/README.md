# StormLib Windows x64 runtime

StormLib 9.40, unmodified x64/StormLib.dll from the official release:
https://github.com/ladislav-zezula/StormLib/releases/tag/v9.40
https://github.com/ladislav-zezula/StormLib/releases/download/v9.40/stormlib_dll.zip

Archive SHA-256: b2c9635e7b63edee1bd7c82e7dc180d739f3accb2b8994804c7774e464ce89ae
DLL SHA-256: 93321f6f030be5d7d79eb4cbf433d6ef7e83ce20d47d8963717f207d54afbe16

The DLL is PE AMD64 (0x8664), with Unicode archive filenames. Its imports are
KERNEL32.dll, USER32.dll and WININET.dll; no separate Visual C++ runtime
installation is needed. The MIT license is included in StormLib.LICENSE.txt.

Portalkeeper.csproj copies the DLL and license into build and publish outputs.
Builds without a runtime identifier keep runtimes/win-x64/native; win-x64
outputs place both files beside Portalkeeper.dll. Other explicit runtime
identifiers do not deploy this Windows dependency. RID-less outputs remain
portable, and the Windows resolver loads only on an x64 Windows process.

ClientAssets.cs resolves the bundled DLL relative to AppContext.BaseDirectory,
so loading does not depend on the working directory or PATH. Windows archive
paths use UTF-16; Linux/macOS archive paths remain UTF-8. Linux continues to
resolve libstorm.so and libstorm.so.9 using the existing system-library search.

No native installation or manual DLL copying is required after applying these
source files. Keep the DLL and license in source control.
