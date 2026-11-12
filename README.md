# Crystar.QoL

A quality-of-life mod for **CRYSTAR** (Spike Chunsoft, Unity IL2CPP), built on **MelonLoader 0.5.7** for .NET Framework 4.7.2.

**NexusMods page:** https://www.nexusmods.com/crystar/mods/4
**Current version:** 0.3
**Author:** winmerdx
**Download the released archive:** https://github.com/winmerdx/crystar-qol/releases/download/v0.3/Crystar.QoL-v0.3.zip

This repository is the public source-code home of the mod. It exists so that the source, the build steps and the packaging pipeline of the released file are fully auditable — including the one place where a third-party binary is modified (see [The 6-byte console patch](#the-6-byte-console-patch)).

---

## What is in the released archive

The Nexus file `Crystar.QoL-v0.3.zip` (SHA-256 `7ebf615c01e5784537c145aeaaf4b2df635438e2d3fbf3b986fbed594bee60bb`) is a single standard DEFLATE ZIP with one top-level folder:

```
Crystar.QoL\
├── install.cmd            installer   (copy files + write an install manifest)
├── uninstall.cmd          uninstaller (removes only what the manifest lists)
├── 使用说明.txt            player guide (Chinese)
├── README_EN.txt          player guide (English)
└── payload\
    ├── version.dll        MelonLoader 0.5.7 Doorstop proxy DLL (unmodified upstream)
    ├── NOTICE.txt         third-party notice
    ├── MelonLoader\       MelonLoader 0.5.7 framework + bundled Mono runtime (unmodified upstream,
    │                      except Dependencies\Bootstrap.dll — see below)
    └── Mods\
        └── Crystar.QoL.dll   the ONLY binary built by this project
```

Of the 136 DLLs in the archive, **exactly one is produced by this project** (`Mods/Crystar.QoL.dll`). The other 135 are the unmodified official MelonLoader 0.5.7 release and its bundled Mono runtime, redistributed so that players need no separate install step. `version.dll` is MelonLoader's Doorstop proxy DLL — the standard loader mechanism for IL2CPP Unity games; it loads the framework from next to `CRYSTAR.exe`. **No game file is ever modified, replaced or patched.**

The archive contains **no .exe, no self-extracting archive, no nested archive and no password protection**.

## What this repository contains

| Path | Content |
|---|---|
| `src/Crystar.QoL/` | Full C# source of the mod (`Crystar.QoL.csproj` + `Core/`, `Infra/`, `UI/`, `Features/`) |
| `scripts/package.py` | Packaging pipeline: framework + mod DLL + install scripts → release zip |
| `scripts/patch_bootstrap_console.py` | The 6-byte `Bootstrap.dll` console patch, with strict hash validation |
| `scripts/install/` | Source of `install.cmd`, `uninstall.cmd` and both player guides |
| `scripts/test_installer.py` | Sandboxed test harness for the install/uninstall scripts |
| `docs/bootstrap-console-patch.md` | Reverse-engineering write-up behind the 6-byte patch |

Deliberately **not** in this repository: game files, game-derived assemblies, the MelonLoader binaries, Il2CppDumper output, build output and release archives. None of them are needed to audit the code, and redistributing game-derived binaries would not be lawful.

## Build

Requirements:

- .NET SDK (any recent version; `dotnet build` is used)
- .NET Framework 4.7.2 **reference assemblies** — the NuGet package `Microsoft.NETFramework.ReferenceAssemblies.net472` (version 1.0.3). The project file is a non-SDK project on purpose: it points at reference assemblies directly, so the build is fully offline and needs no NuGet restore.
- **MelonLoader 0.5.7** release (https://melonwiki.xyz) — the build references `MelonLoader.dll` and `0Harmony.dll`
- **Il2CppDumper** output (`DummyDll\`) generated from *your own copy* of the game — the build compiles against the game's IL2CPP proxy assemblies (`Assembly-CSharp.dll`, `UnityEngine.*.dll`, `Il2CppDummyDll.dll`)

The project file expects these three locations by default:

```
<repo>/.nuget/microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2/
<repo>/tools/melonloader057/MelonLoader/
<repo>/dump/DummyDll/
```

Any of them can be overridden on the command line, so no edit to the project file is needed:

```bash
dotnet build src/Crystar.QoL/Crystar.QoL.csproj -c Release --nologo \
  -p:RefAsmDir="/path/to/.NETFramework/v4.7.2" \
  -p:MelonLoaderDir="/path/to/MelonLoader" \
  -p:DummyDllDir="/path/to/DummyDll"
```

Output: `build/Crystar.QoL/Crystar.QoL.dll` (net472, zero NuGet package references, `AnyCPU`).

The build is deterministic with respect to the inputs: the same sources, the same MelonLoader 0.5.7 DLLs and the same DummyDll set produce the same IL. `scripts/package.py` reads the mod version from `src/Crystar.QoL/Properties/AssemblyInfo.cs`, so the version string has a single source of truth.

## Package

```bash
# 1. put the MelonLoader 0.5.7 release under tools/melonloader057/MelonLoader/
# 2. build the mod DLL (see above)
# 3. package
python scripts/package.py --game "/path/to/your/CRYSTAR/install"
```

`package.py` copies the framework, the mod DLL and the install scripts into a temporary staging folder, applies the `Bootstrap.dll` patch to the staged copy (verifying the upstream hash first), writes `payload/NOTICE.txt`, and emits `dist/Crystar.QoL-v<version>.zip`. The upstream framework copy is never modified in place.

## The 6-byte console patch

MelonLoader 0.5.7 allocates a console window very early in process start-up. A managed mod cannot prevent that — it can only hide the window afterwards, which still flickers for a second or two. This mod therefore applies a documented **6-byte** patch to the `Bootstrap.dll` that ships inside the package:

```
original : 0F 85 E5 02 00 00   jne  0x18001078F   ; Console::ShouldHide → skip AllocConsole
patched  : E9 E6 02 00 00 90   jmp  0x18001078F   ; always take the official hideconsole path
```

Semantics: `if (!Debug::Enabled && ShouldHide) return true;` becomes `if (!Debug::Enabled) return true;`. With `--melonloader.debug` the first branch still reaches `AllocConsole()`, so the debugging path is preserved.

Important properties of this patch:

- It modifies **only the framework file that this package itself ships**. `version.dll`, `UnityPlayer.dll`, `GameAssembly.dll` and every game file are untouched.
- It changes **nothing but those 6 bytes**. `scripts/patch_bootstrap_console.py` validates the upstream file size, MD5 and the original bytes at the patch offset, then verifies that the patched output differs only inside that 6-byte window, and finally checks the resulting MD5. Any mismatch aborts packaging — a MelonLoader version bump can never be silently patched with a stale offset.
- It **adds no network access, no downloads and no code execution**. The patch is a conditional-jump rewrite to a code path MelonLoader already contains and already uses for `--melonloader.hideconsole`.
- Upstream `NOTICE.txt` in the package states the patch explicitly.

Full reverse-engineering write-up, including how the function and the two globals were located: [`docs/bootstrap-console-patch.md`](docs/bootstrap-console-patch.md).

## The install and uninstall scripts

Both scripts are plain local Windows batch files; their source is in `scripts/install/`.

- `install.cmd` — verifies the package layout, locates the game folder by searching upward for `CRYSTAR.exe` + `CRYSTAR_Data` + `GameAssembly.dll`, refuses to run while the game is running (`tasklist`), copies `version.dll` / `MelonLoader\` / `Mods\` into the game folder, and records every added file in `CrystarQoL_MANIFEST.txt`.
- `uninstall.cmd` — removes **only** the files listed in that manifest (element level: a shared `Mods\` folder is only removed when it ends up empty), plus a fixed set of MelonLoader-created names, after the same "game not running" check and an explicit typed confirmation.

Neither script contains any network access: no `curl`, no `http`, no `powershell`, no registry writes — they only copy and delete files inside the game folder. `scripts/test_installer.py` exercises both scripts against a synthetic fake game folder (install → uninstall → reinstall) with assertions, so the destructive paths can be tested without touching a real install.

## Third-party components

See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). Short version: the framework is the official **MelonLoader 0.5.7** release (Apache-2.0 plus its additional terms, see https://melonwiki.xyz), redistributed unmodified except for the documented 6-byte console patch described above; `0Harmony` and the bundled Mono runtime are part of that same official release. No game assets, no game code and no game binaries are redistributed.

## License

The mod's own source code is released under the **MIT License** — see [`LICENSE`](LICENSE). Third-party components keep their own licenses.

## 中文摘要

本仓库是 CRYSTAR 生活便利 mod（NexusMods: `crystar/mods/4`）的公开源码仓库，用于满足 NexusMods 对「含编译代码的 mod 须提供源码与构建步骤」的要求。

- 发布包中 **只有一个 DLL 是本项目产物**（`Mods/Crystar.QoL.dll`），其余 135 个 DLL 是官方 MelonLoader 0.5.7 框架与其内置 Mono 运行时，未作修改。
- 唯一被修改的第三方文件是随包释放的 `MelonLoader\Dependencies\Bootstrap.dll`，改动为 **6 字节**：把创建控制台窗口的条件跳转改为无条件跳转，等价于永久开启官方的 `--melonloader.hideconsole`。补丁实现见 `scripts/patch_bootstrap_console.py`，取证过程见 `docs/bootstrap-console-patch.md`。
- `install.cmd` / `uninstall.cmd` 只做本地文件复制与按清单删除，**不联网、不写注册表、不改任何游戏原文件**。
- 仓库不含游戏文件、游戏衍生程序集、MelonLoader 二进制、构建产物与发布包（构建方式见上文 *Build* 一节）。
