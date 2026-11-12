# Third-party notices

The release archive of this mod (`Crystar.QoL-v0.3.zip`) bundles the MelonLoader
framework so that players need no separate install step. Those files are the
official upstream release, unmodified — with the single documented exception of
`MelonLoader\Dependencies\Bootstrap.dll`, which receives a 6-byte console patch
(see [`docs/bootstrap-console-patch.md`](docs/bootstrap-console-patch.md)).

## MelonLoader 0.5.7

```
MelonLoader
Copyright 2020 - 2022 Lava Gang

This product contains software (https://github.com/LavaGang/MelonLoader) developed
by Lava Gang, licensed under the Apache-2.0 license.
```

- Project: https://github.com/LavaGang/MelonLoader · documentation: https://melonwiki.xyz
- License: Apache License 2.0 (`MelonLoader/Documentation/LICENSE.md` in the archive)
- Version bundled: 0.5.7, x64, as published upstream
- Redistributed files: `version.dll` (Doorstop proxy DLL), `MelonLoader\` (framework
  assemblies, dependencies, documentation) and the Mono runtime files it ships with
- Modification: `Bootstrap.dll` only, 6 bytes, conditional jump → unconditional jump
  in `Console::Initialize()`; no network code, no new imports, no behavioural change
  other than "never allocate the console window unless `--melonloader.debug`"

## 0Harmony

Bundled inside the MelonLoader release (`MelonLoader\0Harmony.dll`), used as the
patching library. Distributed as part of the official MelonLoader release; see the
harmony project at https://github.com/pardeike/Harmony for its license terms.

## Mono runtime

`MelonLoader\` ships a Mono/IL2CPP support runtime as part of the official
MelonLoader 0.5.7 release. Its files are redistributed verbatim from that release.

## Not redistributed — and why

The mod is compiled against, but does **not** redistribute or bundle:

- **Il2CppDumper** (https://github.com/Perfare/Il2CppDumper) — used offline to produce
  reference assemblies (`DummyDll\`) for compiling against the game's IL2CPP types.
  Only generated metadata stubs are used, at compile time only; neither the tool nor
  its output is shipped.
- **Game files and game-derived binaries** — `GameAssembly.dll`, `CRYSTAR_Data\`,
  `UnityPlayer.dll` and any dump output. Nothing from the game is bundled, patched or
  redistributed. The mod only reads game data at runtime through the game's own APIs.
  DummyDll files are intentionally absent from this repository: they are derived from
  the game and must be generated locally by whoever owns a copy of the game.

## Game and trademarks

CRYSTAR and all related assets are property of Spike Chunsoft. This is an unofficial,
non-commercial fan-made mod, not affiliated with or endorsed by Spike Chunsoft.
