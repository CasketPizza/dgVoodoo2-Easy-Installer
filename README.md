# dgVoodoo2 Easy Installer

A focused Windows utility that installs the latest stable dgVoodoo2 wrappers beside a game's executable.

## What it does

- Prompts for a game's main `.exe` at startup.
- Detects 32-bit, 64-bit, or ARM64 executables and scans for DirectX 1-9, D3DRM, Glide, and OpenGL usage.
- Downloads the latest stable archive from the official dgVoodoo2 website.
- Downloads the latest Mesa3D MSVC release from GitHub and verifies its published SHA-256 hash.
- Installs Mesa3D's OpenGL-on-Direct3D 12 libraries for x86 and x64 OpenGL games.
- Accepts manually downloaded dgVoodoo2 ZIP, D3DRM ZIP, and Mesa3D `.7z`/`.zip` packages.
- Installs only the selected wrapper DLLs plus a local configuration and control panel.
- Backs up every replaced file and records an installation manifest.
- Detects managed and pre-existing dgVoodoo2 installations.
- Uninstalls managed files and restores the exact backups it made.
- Opens `dgVoodooCpl.exe` in the game directory after installation.

dgVoodoo2 does not wrap OpenGL, so OpenGL support uses the MSVC build from
[Mesa3D for Windows](https://github.com/pal1000/mesa-dist-win). The installer deploys `opengl32.dll`,
`libgallium_wgl.dll`, and `dxil.dll` locally for the selected game. Mesa3D ARM64 binaries are not available
from that distribution.

## Build

Requires the .NET 8 SDK or newer on Windows.

```powershell
dotnet build dgVoodoo2EasyInstaller.sln -c Release
dotnet run --project tests/DgVoodooEasyInstaller.Tests -c Release
dotnet publish src/DgVoodooEasyInstaller -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o publish/win-x64-single
```

The published application requests administrator access because many game directories are under `Program Files`.

## Safety

Backups and `manifest.json` are stored in `.dgvoodoo-easy-installer` inside the selected game directory. Installation is rolled back if copying any required file fails. Existing installations not created by this tool can be removed, but cannot be restored because no backups exist.

dgVoodoo2 is downloaded on demand and is not bundled with this project. It is created and maintained by Dege; see the [official website](https://dege.freeweb.hu/dgVoodoo2/).
