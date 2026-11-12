# MelonLoader `Bootstrap.dll` console "zero-flicker" patch — reverse-engineering write-up

> Target: prevent the MelonLoader console window from ever being created, without
> asking the player to add any Steam launch option.
> Object: `MelonLoader\Dependencies\Bootstrap.dll` — **a framework file that this
> package itself ships, not a game file**.
> Result: **6 bytes of machine code** changed, equivalent to permanently enabling the
> official `--melonloader.hideconsole` flag. `--melonloader.debug` still produces a
> console, so the debugging path is preserved.

Implementation: [`scripts/patch_bootstrap_console.py`](../scripts/patch_bootstrap_console.py).
Application point: [`scripts/package.py`](../scripts/package.py) (applied to the staged
copy at packaging time, after upstream hash validation).

---

## 1. Why the framework binary has to be touched at all

| Layer | When it runs | Can it prevent `AllocConsole()`? |
|---|---|---|
| `version.dll` (Doorstop proxy) | earliest `DllMain` | ❌ it does not create a console; it only locates and `LoadLibraryW`s `Bootstrap.dll` |
| `Bootstrap.dll` | `DllMain` → `Core::Initialize` | ✅ this is where the console is created |
| MelonLoader managed layer / plugins / mods | after IL2CPP init | ❌ the window already exists; it can only be hidden afterwards |
| `Crystar.QoL.dll` / `Core/ConsoleHider.cs` | `OnInitializeMelon` + `OnUpdate` | ❌ same; kept only as a fallback sweep |

MelonLoader 0.5.7's `CommandLine::Read()` reads `GetCommandLineA()` only — there is no
environment-variable branch and it does **not** read `UserData/Loader.cfg` (that was
introduced in 0.6). So without a launch argument, the only place to intervene is the
branch logic inside `Bootstrap.dll`.

### Upstream source evidence (MelonLoader v0.5.7 tag)

| File | Key code |
|---|---|
| `Bootstrap/Base/DllMain.cpp` | `DLL_PROCESS_ATTACH` → `Core::Initialize(hinstDLL)` |
| `Bootstrap/Core.cpp` | `CommandLine::Read();` → `if (!Console::Initialize() \|\| !Logger::Initialize() \|\| …) return;` (command line is read **before** the console is created) |
| `Bootstrap/Utils/CommandLine.cpp` | `else if (strstr(command, "--melonloader.hideconsole") != NULL) Console::ShouldHide = true;` |
| `Bootstrap/Utils/Console.cpp` | `bool Console::Initialize() { if (!Debug::Enabled && ShouldHide) return true; if (!AllocConsole()) { Assertion::ThrowInternalFailure("Failed to Allocate Console!"); return false; } … }` |
| `MelonLoader/Documentation/README.md` | `--melonloader.hideconsole = Hides the Console` |

⇒ The official `hideconsole` route is "`Console::Initialize()` returns `true`
immediately", an upstream-maintained path. The patch only turns "take this path when
the flag is present" into "take this path by default".

## 2. File and PE layout (MelonLoader 0.5.7, x64)

| Item | Value |
|---|---|
| Source used for packaging | MelonLoader 0.5.7 official release, `MelonLoader/Dependencies/Bootstrap.dll` |
| Size / MD5 (original) | `181248 B` / `1CB05F16B8602BA424D396102D7EC158` |
| Size / MD5 (patched) | `181248 B` / `C18A85AB199FC0F6B933A0719024AB15` |
| PE version field | `0.6.0.0` ⚠ inconsistent with the framework version 0.5.7 — so version strings cannot be trusted, the file must be identified by bytes |
| Architecture / ImageBase | x64 (`0x8664`) / `0x180000000` |
| `.text` | RVA `0x1000`, RawOff `0x400`, RawSize `0x1E800` |
| `.rdata` | RVA `0x20000`, RawOff `0x1EC00` |
| `.data` | RVA `0x29000`, VSize `0x1A38`, **RawSize only `0x800`** ⇒ RVAs ≥ `0x29800` belong to `.bss` |
| `.pdata` | RVA `0x2B000`, RawOff `0x28000` (12 bytes per `RUNTIME_FUNCTION`) |

### How the function and the globals were located (reproducible)

1. Parse the `.pdata` `RUNTIME_FUNCTION` entries (`BeginAddress` / `EndAddress` /
   `UnwindData`) and disassemble from each function entry with capstone — linear sweep
   misaligns after jump tables.
2. Locate functions through string cross-references:
   - `"--melonloader.hideconsole"` @ RVA `0x22B98` → `CommandLine::Read` = **RVA `0x10117..0x10456`**
   - `"Failed to Allocate Console!"` @ RVA `0x22C28` plus the `AllocConsole` IAT slot `0x200B8` → `Console::Initialize` = **RVA `0x10470..0x107B6`**
3. Identify the global booleans by "who writes 1":

| RVA | Belongs to | Evidence |
|---|---|---|
| `0x29F4F` | `Debug::Enabled` | `--melonloader.debug` branch does `mov byte ptr [0x29F4F], 1`; first `cmp` in `Console::Initialize` reads it |
| `0x29F4D` | **`Console::ShouldHide`** | `--melonloader.hideconsole` branch does `mov byte ptr [0x29F4D], 1` (`0x180010286`); second `cmp` reads it |
| `0x29049` | `Console::ShouldSetTitle` | `--melonloader.consoledst` branch writes 0 (initial value 1 in `.data`) |
| `0x29CC3` | `Console::AlwaysOnTop` | `--melonloader.consoleontop` branch writes 1 |

⛔ **Important:** `ShouldHide` lives at RVA `0x29F4D`, i.e. inside `.bss` (past
`0x29800`), so the PE file contains **no initial-value byte** for it. Flipping a
"global default" is physically impossible on this binary; the code has to change.

## 3. `Console::Initialize` machine code (original)

```
0x180010494  cmp   byte ptr [rip+0x19AB4], 0   [0x29F4F]  ; Debug::Enabled
0x18001049B  jne   0x1800104AA                            ; debug → allocate console as usual
0x18001049D  cmp   byte ptr [rip+0x19AA9], 0   [0x29F4D]  ; ShouldHide
0x1800104A4  jne   0x18001078F                            ; hideconsole → mov al,1 / ret
0x1800104AA  call  qword ptr [rip+0xFC08]      [0x200B8]  ; AllocConsole()
0x1800104B0  test  eax, eax
…
0x18001078F  mov   al, 1                                  ; return true
0x180010791  … stack-cookie check + epilogue …
0x1800107B5  ret
```

Bytes at `0x1800104A4`: `0F 85 E5 02 00 00` (`jne rel32`, rel32 = `0x2E5`, target `0x18001078F`).

### The patch (6 bytes, `jne` → `jmp` + NOP)

```
original: 0F 85 E5 02 00 00    jne  0x18001078F   (len 6, rel32 = 0x2E5)
patched : E9 E6 02 00 00 90    jmp  0x18001078F   (len 5, rel32 = 0x2E6) + NOP
                               target = 0x104A9 + 0x2E6 = 0x1078F ✔
```

Disassembly after patching (re-verified with capstone):

```
0x180010494  cmp   byte ptr [rip+0x19AB4], 0
0x18001049B  jne   0x1800104AA              ; --melonloader.debug still reaches AllocConsole
0x18001049D  cmp   byte ptr [rip+0x19AA9], 0
0x1800104A4  jmp   0x18001078F              ; non-debug always jumps to "return true"
0x1800104A9  nop
0x1800104AA  call  qword ptr [rip+0xFC08]   ; AllocConsole
```

| File offset | RVA | Note |
|---|---|---|
| `0xF8A4` | `0x104A4` | patch site; `0xF8A4 = 0x104A4 − 0x1000 + 0x400` |

**Semantics:** `if (!Debug::Enabled && ShouldHide) return true;` →
`if (!Debug::Enabled) return true;`. In a non-debug run no console window is ever
created (真· zero flicker, not "hidden quickly"); with `--melonloader.debug`,
`Debug::Enabled = 1`, the first `jne` still jumps to `AllocConsole`, so the debugging
entry point remains.

## 4. Reproducible tooling and validation

| File | Role |
|---|---|
| `scripts/patch_bootstrap_console.py` | the only patch implementation. Constants: original/patched MD5, file offset, original/new bytes. `--check <dll>` classifies a DLL as original / patched / unknown; `--output` produces the patched file. Standard library only — no pefile or capstone needed to apply it |
| `scripts/package.py` | at packaging time calls `patch_bytes()` + `verify_patched()` on the staged `payload\MelonLoader\Dependencies\Bootstrap.dll` and prints the patched MD5. The framework source copy stays pristine |
| `payload\NOTICE.txt` | generated by `package.py`; states the 6-byte local patch explicitly |

Validation chain: source file size + MD5 + bytes at the patch offset → only the 6 bytes
inside the window are written → every byte outside the window is verified identical →
patched MD5 is checked. **Any mismatch aborts packaging**, so a MelonLoader upgrade can
never be patched with a stale offset.

## 5. Boundaries and constraints

1. ⛔ What is modified is the framework file **shipped by this package**, not a game
   file. `version.dll`, `UnityPlayer.dll`, `GameAssembly.dll` and everything under
   `CRYSTAR_Data\` remain untouched; the mod only ever *adds* files.
2. ⛔ Reinstalling or upgrading MelonLoader overwrites the patch: the patched
   `Bootstrap.dll` must be regenerated by `package.py` on every build. If the upstream
   MD5 changes, the script refuses to package and the offsets must be re-established
   by repeating section 2.
3. The `console.show` marker file is meaningless with the patched `Bootstrap.dll`
   (there is no window to show), but it is kept because it becomes relevant again if
   the original `Bootstrap.dll` is restored. `ConsoleHider.cs` stays as a fallback
   sweep; with no console present it returns 0 immediately and never calls
   `FreeConsole()`.
4. To watch the live console: add `--melonloader.debug` to the Steam launch options
   (the debug branch is intact), or temporarily restore the original
   `Bootstrap.dll`. On-disk logs (`MelonLoader\Latest.log`,
   `UserData\Crystar.QoL\debug.log`) are unaffected either way.
