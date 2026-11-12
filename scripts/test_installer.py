#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
install.cmd / uninstall.cmd 沙盒实测（**不碰真游戏目录**）。

为什么要沙盒：真游戏目录跑一次真卸载是不可逆的（会删 mod 数据目录、搬走 settings.cfg），
而且 2026-09-17 正是靠这个沙盒才发现两处硬 bug ——
  ① `[D] Mods` 走 `rd /s /q` 会把第三方 mod 一起删掉；
  ② 本机 Windows 11（26100）的 `rmdir` **不带 /s 也会静默递归删掉非空目录**
     ⇒ 裸 `rd` 不能当"空目录判定"（现由 uninstall.cmd 的 `:countentries` 显式计数）。

布局（自动创建，全部在 dist/sandbox 下）：
    dist/sandbox/FakeGame/CRYSTAR.exe        (空文件，脚本只判存在)
    dist/sandbox/FakeGame/CRYSTAR_Data/      (目录)
    dist/sandbox/FakeGame/GameAssembly.dll   (空文件)
    dist/sandbox/FakeGame/Crystar.QoL/       (包：install.cmd / uninstall.cmd / 使用说明.txt / payload)

用法：
    python scripts/test_installer.py      # 退出码 0 = 全部通过
报告：dist/sandbox/test_report.txt
"""
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]          # crystar-mod
SRC = ROOT / "scripts" / "install"
SANDBOX = ROOT / "dist" / "sandbox"
FAKE = SANDBOX / "FakeGame"
PKG = FAKE / "Crystar.QoL"
LOG = SANDBOX / "test_report.txt"

lines = []


def say(s=""):
    lines.append(str(s))
    print(s)


def crlf_copy(src: Path, dst: Path):
    """与 package.py 的 copy_text 同规则：.cmd 一律 CRLF 落盘。"""
    dst.parent.mkdir(parents=True, exist_ok=True)
    data = src.read_bytes().decode("utf-8")
    data = data.replace("\r\n", "\n").replace("\n", "\r\n")
    dst.write_bytes(data.encode("utf-8"))


def touch(p: Path, text=""):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


def run(cmd: str, cwd: Path, stdin: str = ""):
    p = subprocess.run(["cmd.exe", "/c", cmd], cwd=str(cwd),
                       input=stdin.encode("gbk", "replace"),
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return p.stdout.decode("gbk", "replace")


def setup_package():
    if PKG.exists():
        shutil.rmtree(PKG)
    crlf_copy(SRC / "install.cmd", PKG / "install.cmd")
    crlf_copy(SRC / "uninstall.cmd", PKG / "uninstall.cmd")
    crlf_copy(SRC / "使用说明.txt", PKG / "使用说明.txt")
    pl = PKG / "payload"
    touch(pl / "version.dll", "dummy-injector")
    touch(pl / "NOTICE.txt", "dummy notice")
    touch(pl / "MelonLoader" / "MelonLoader.dll", "dummy framework")
    touch(pl / "Mods" / "Crystar.QoL.dll", "dummy mod")
    if not (FAKE / "CRYSTAR.exe").exists():
        touch(FAKE / "CRYSTAR.exe")
    (FAKE / "CRYSTAR_Data").mkdir(exist_ok=True)
    touch(FAKE / "GameAssembly.dll")


def seed_state():
    """模拟：装过 mod、玩过一阵、Mods 里还有别人的 mod。"""
    ud = FAKE / "UserData"
    touch(ud / "Crystar.QoL" / "settings.cfg", "f.exp.scale=3\nf.money.full=false\n")
    touch(ud / "Crystar.QoL" / "debug.log", "x" * 4096)
    touch(ud / "Crystar.QoL" / "gate.pause")
    touch(ud / "Crystar.QoL" / "console.show")
    touch(ud / "Crystar.QoL" / "backup" / ".first-run-done", "2026-09-08 01:41:39")
    touch(ud / "Crystar.QoL" / "backup" / "20260910-020028_camp" / "local.sav", "SAVEDATA")
    touch(ud / "MelonPreferences.cfg", "[MelonLoader]\n")
    touch(ud / "MelonStartScreen" / "Themes" / "Default" / "Config.cfg", "theme")
    touch(FAKE / "Mods" / "OtherMod.dll", "third party")
    crlf_copy(SRC / "uninstall.cmd", FAKE / "CrystarQoL_uninstall.cmd")


def tree(root: Path):
    out = []
    for p in sorted(root.rglob("*")):
        rel = p.relative_to(root).as_posix()
        if rel.startswith("Crystar.QoL/"):
            continue
        out.append(rel + ("/" if p.is_dir() else ""))
    return out


def show_tree(title):
    say()
    say("-" * 72)
    say(title)
    say("-" * 72)
    for t in tree(FAKE):
        say("    " + t)


CHECKS = []


def check(name, ok, extra=""):
    CHECKS.append((name, ok))
    say(("[PASS] " if ok else "[FAIL] ") + name + ("   " + extra if extra else ""))


def main():
    say("=" * 72)
    say("sandbox test - install.cmd / uninstall.cmd")
    say("=" * 72)
    setup_package()
    show_tree("0. package placed inside the fake game folder")

    # ---------------- round 1: install ----------------
    out = run("install.cmd /y", PKG)
    say()
    say("=" * 72)
    say("1. install.cmd /y  ->")
    say("=" * 72)
    say(out)
    check("install: version.dll copied", (FAKE / "version.dll").exists())
    check("install: MelonLoader copied", (FAKE / "MelonLoader" / "MelonLoader.dll").exists())
    check("install: mod dll copied", (FAKE / "Mods" / "Crystar.QoL.dll").exists())
    check("install: uninstaller dropped in game root", (FAKE / "CrystarQoL_uninstall.cmd").exists())
    mf = (FAKE / "CrystarQoL_MANIFEST.txt")
    check("install: manifest written", mf.exists())
    if mf.exists():
        say("    manifest = " + repr(mf.read_text(encoding="utf-8")))
        check("install: manifest has no UserData entry",
              "UserData" not in mf.read_text(encoding="utf-8"))
    show_tree("2. after install")

    # ---------------- round 2: play a bit ----------------
    seed_state()
    show_tree("3. seeded: settings.cfg / debug.log / markers / save snapshot / "
              "MelonLoader files / another mod")

    # ---------------- round 3: dry run ----------------
    out = run("uninstall.cmd /dry", PKG)
    say()
    say("=" * 72)
    say("4. uninstall.cmd /dry  ->")
    say("=" * 72)
    say(out)
    check("dry: prints DRY RUN", "DRY RUN" in out)
    check("dry: lists UserData\\Crystar.QoL", "UserData\\Crystar.QoL" in out)
    check("dry: lists UserData\\MelonPreferences.cfg", "UserData\\MelonPreferences.cfg" in out)
    check("dry: no bare [D] UserData line", "[D]  UserData\n" not in out)
    check("dry: nothing was deleted", (FAKE / "UserData" / "Crystar.QoL" / "debug.log").exists()
          and (FAKE / "version.dll").exists())

    # ---------------- round 4: real uninstall (root copy = what a player runs) --------------
    ans = "\r\n".join(["YES"] + [""] * 6) + "\r\n"
    out = run("CrystarQoL_uninstall.cmd", FAKE, stdin=ans)
    say()
    say("=" * 72)
    say("5. CrystarQoL_uninstall.cmd (YES)  ->")
    say("=" * 72)
    say(out)
    ud = FAKE / "UserData"
    check("uninstall: settings.cfg rescued into backup\\",
          (ud / "Crystar.QoL" / "backup" / "settings.cfg").exists())
    check("uninstall: live settings.cfg removed", not (ud / "Crystar.QoL" / "settings.cfg").exists())
    check("uninstall: debug.log removed", not (ud / "Crystar.QoL" / "debug.log").exists())
    check("uninstall: marker gate.pause rescued + removed",
          (ud / "Crystar.QoL" / "backup" / "gate.pause").exists()
          and not (ud / "Crystar.QoL" / "gate.pause").exists())
    check("uninstall: save snapshot kept",
          (ud / "Crystar.QoL" / "backup" / "20260910-020028_camp" / "local.sav").exists())
    check("uninstall: MelonPreferences.cfg removed", not (ud / "MelonPreferences.cfg").exists())
    check("uninstall: MelonStartScreen removed", not (ud / "MelonStartScreen").exists())
    check("uninstall: other mod survives", (FAKE / "Mods" / "OtherMod.dll").exists())
    check("uninstall: Mods\\ folder survives", (FAKE / "Mods").is_dir())
    check("uninstall: our dll gone", not (FAKE / "Mods" / "Crystar.QoL.dll").exists())
    check("uninstall: MelonLoader removed", not (FAKE / "MelonLoader").exists())
    check("uninstall: version.dll removed", not (FAKE / "version.dll").exists())
    check("uninstall: uninstaller self-deleted", not (FAKE / "CrystarQoL_uninstall.cmd").exists())
    check("uninstall: manifest self-deleted", not (FAKE / "CrystarQoL_MANIFEST.txt").exists())
    check("uninstall: reports Verified", "Verified: no mod files remain." in out)
    check("uninstall: reports kept backup", "Kept on purpose" in out)
    check("uninstall: no FAIL lines", "[FAIL]" not in out)
    check("uninstall: self-delete leaves no error noise", "找不到批处理文件" not in out)
    check("uninstall: kept-message for other mods", "Mods\\ kept" in out or "Mods\\ was empty" in out)
    show_tree("6. after uninstall")

    # ---------------- round 5: reinstall -> restore ----------------
    setup_package()
    out = run("install.cmd /y", PKG)
    say()
    say("=" * 72)
    say("7. install.cmd /y again (no live settings.cfg, backup present)  ->")
    say("=" * 72)
    say(out)
    check("reinstall: settings.cfg restored", (ud / "Crystar.QoL" / "settings.cfg").exists())
    if (ud / "Crystar.QoL" / "settings.cfg").exists():
        body = (ud / "Crystar.QoL" / "settings.cfg").read_text(encoding="utf-8")
        check("reinstall: restored content matches", "f.exp.scale=3" in body, repr(body))
    check("reinstall: marker gate.pause restored", (ud / "Crystar.QoL" / "gate.pause").exists())
    check("reinstall: backed up copies still kept",
          (ud / "Crystar.QoL" / "backup" / "settings.cfg").exists())

    # ---------------- round 6: manifest deleted -> fallback must not touch UserData --------
    (FAKE / "CrystarQoL_MANIFEST.txt").unlink(missing_ok=True)
    out = run("uninstall.cmd /dry", PKG)
    say()
    say("=" * 72)
    say("8. uninstall.cmd /dry with the manifest deleted (fallback whitelist)  ->")
    say("=" * 72)
    say(out)
    check("fallback dry: still lists UserData\\Crystar.QoL", "UserData\\Crystar.QoL" in out)
    check("fallback dry: no bare UserData target", "[D]  UserData\n" not in out)
    check("fallback dry: nothing deleted", (FAKE / "version.dll").exists())

    say()
    say("=" * 72)
    bad = [n for n, ok in CHECKS if not ok]
    say("RESULT: %d checks, %d failed" % (len(CHECKS), len(bad)))
    for n in bad:
        say("   FAILED -> " + n)
    say("=" * 72)
    LOG.parent.mkdir(parents=True, exist_ok=True)
    LOG.write_text("\n".join(lines), encoding="utf-8")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
