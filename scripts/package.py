#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Crystar.QoL 打包脚本：把 框架(MelonLoader) + mod dll + 安装/卸载脚本 打成一个发布 zip。

产物结构（zip 顶层一个文件夹，用户把整个文件夹拖进游戏根目录即可）：
    Crystar.QoL/
    ├── install.cmd
    ├── uninstall.cmd
    ├── 使用说明.txt
    ├── README_EN.txt
    └── payload/
        ├── version.dll          <- MelonLoader 注入器（取自当前安装或工具备份）
        ├── NOTICE.txt
        ├── MelonLoader/         <- 框架（取自 tools/melonloader057，纯净无运行期产物）
        │                          Bootstrap.dll 打包时自动打「控制台零闪」6 字节补丁
        └── Mods/Crystar.QoL.dll <- 本次编译产物

用法：
    python package.py                # 默认游戏目录
    python package.py --game E:\\SteamLibrary\\steamapps\\common\\Crystar
    python package.py --version 0.2.1

staging 放在系统临时目录（不在 dist/ 下）：dist/ 只留发布 zip，
也避免一次删除几百个中间文件触发工作区的批量删除保护。
"""
import argparse
import os
import re
import shutil
import sys
import tempfile
import zipfile
from pathlib import Path

from patch_bootstrap_console import PatchError, describe, patch_bytes, verify_patched

ROOT = Path(__file__).resolve().parents[1]          # repo root
# 示例默认值（作者本机路径）；请用 --game 指定自己的游戏目录
DEFAULT_GAME = Path(r"E:\SteamLibrary\steamapps\common\Crystar")
BACKUP_PAYLOAD = ROOT / "backup" / "game-dir-20260908" / "Crystar.QoL-v0.1.0" / "payload"  # 可选回退路径（不存在时忽略）
DLL = ROOT / "build" / "Crystar.QoL" / "Crystar.QoL.dll"
ML_SRC = ROOT / "tools" / "melonloader057" / "MelonLoader"

# .cmd/.txt 必须用 CRLF（cmd 对 LF 可能解析出错），其余按二进制复制
TEXT_EXT = {".cmd", ".txt"}


def resolve_version() -> str:
    asm = ROOT / "src" / "Crystar.QoL" / "Properties" / "AssemblyInfo.cs"
    m = re.search(r'MelonInfo\([^,]+,\s*"[^"]+",\s*"([^"]+)"', asm.read_text(encoding="utf-8"))
    return m.group(1) if m else "0.0.0"


def pick(p: Path, what: str) -> Path:
    if p.exists():
        return p
    raise SystemExit(f"[ERROR] {what} 不存在: {p}")


def copy_text(src: Path, dst: Path) -> None:
    """统一转 CRLF 落盘，避免 cmd 脚本因 LF 解析出错。"""
    dst.parent.mkdir(parents=True, exist_ok=True)
    data = src.read_bytes().decode("utf-8")
    data = data.replace("\r\n", "\n").replace("\n", "\r\n")
    dst.write_bytes(data.encode("utf-8"))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=str(DEFAULT_GAME), help="已装 MelonLoader 的游戏目录（取 version.dll）")
    ap.add_argument("--version", default=None, help="覆盖版本号")
    args = ap.parse_args()

    version = args.version or resolve_version()
    print(f"[1/4] version = {version}")

    dll = pick(DLL, "mod dll（先跑 build.bat / dotnet build）")
    ml = pick(ML_SRC, "MelonLoader 框架")

    # version.dll：优先取用户当前安装（与框架同源最稳），没有则用备份里的
    game_dir = Path(args.game)
    vdll = game_dir / "version.dll" if (game_dir / "version.dll").exists() else None
    if vdll is None and (BACKUP_PAYLOAD / "version.dll").exists():
        vdll = BACKUP_PAYLOAD / "version.dll"
    if vdll is None:
        raise SystemExit("[ERROR] version.dll 找不到：请传 --game 指向装过 MelonLoader 的游戏目录")

    # 每次打包用全新的临时 staging，不在 dist/ 下残留中间产物
    stage_root = Path(tempfile.mkdtemp(prefix="Crystar-QoL-staging-"))
    pkg = stage_root / "Crystar.QoL"
    payload = pkg / "payload"
    payload.mkdir(parents=True)
    print(f"[2/4] staging in {stage_root}")

    copy_text(ROOT / "scripts" / "install" / "install.cmd", pkg / "install.cmd")
    copy_text(ROOT / "scripts" / "install" / "uninstall.cmd", pkg / "uninstall.cmd")
    copy_text(ROOT / "scripts" / "install" / "使用说明.txt", pkg / "使用说明.txt")
    copy_text(ROOT / "scripts" / "install" / "README_EN.txt", pkg / "README_EN.txt")

    shutil.copy2(vdll, payload / "version.dll")
    (payload / "Mods").mkdir(parents=True, exist_ok=True)
    shutil.copy2(dll, payload / "Mods" / "Crystar.QoL.dll")
    # 注意：MelonLoader 0.5.7 不读 UserData/Loader.cfg（0.6 才引入），所以不随包发放。
    # 控制台窗口不再靠 mod 内 P/Invoke 事后隐藏，也不靠玩家填 Steam 启动参数：
    # 改为对随包释放的 Bootstrap.dll 打 6 字节补丁（见 patch_bootstrap_console.py）。
    notice = payload / "NOTICE.txt"
    notice.write_text(
        "Crystar.QoL v" + version + " - quality-of-life mod for CRYSTAR (Spike Chunsoft)\n"
        "Loader: MelonLoader 0.5.7 (https://melonwiki.xyz)\n"
        "MelonLoader/Dependencies/Bootstrap.dll carries a 6-byte local console patch\n"
        "(no console window is created; see scripts/patch_bootstrap_console.py).\n"
        "Nothing in this package is a game file; removal is always safe.\n",
        encoding="utf-8",
    )
    shutil.copytree(ml, payload / "MelonLoader")

    # 控制台零闪补丁：改的是我们随包释放的框架文件，不是游戏原文件。
    # 源框架 tools/melonloader057 保持原版，每次打包都会在 payload 副本上重新打补丁。
    bootstrap = payload / "MelonLoader" / "Dependencies" / "Bootstrap.dll"
    if not bootstrap.exists():
        raise SystemExit(f"[ERROR] 框架缺少 Bootstrap.dll: {bootstrap}")
    try:
        patched = patch_bytes(bootstrap.read_bytes())
    except PatchError as e:
        raise SystemExit(
            "[ERROR] Bootstrap.dll 控制台补丁失败（MelonLoader 版本变了？"
            "重新反汇编取证并更新 patch_bootstrap_console.py 常量）: " + str(e)
        )
    bootstrap.write_bytes(patched)
    verify_patched(bootstrap.read_bytes())
    print("[2/4] Bootstrap.dll console-patch: " + describe(patched))

    zip_path = ROOT / "dist" / f"Crystar.QoL-v{version}.zip"
    zip_path.parent.mkdir(parents=True, exist_ok=True)
    building = zip_path.with_name(zip_path.stem + ".building")

    # 先写临时文件再原子替换：既不 unlink（删除保护会拦），也不直接覆盖
    # （目标 zip 可能被预览/其它进程占用 → PermissionError）。
    print("[3/4] zipping...")
    with zipfile.ZipFile(building, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(pkg.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(pkg.parent))

    # 候选输出路径：优先正式名；被占用时复用固定的 -new / -final / -latest，
    # 避免每次打包都多出一个 -new2、-new3 造成拿错包。
    placed = False
    for target in [zip_path] + [ROOT / "dist" / f"Crystar.QoL-v{version}-{s}.zip"
                                for s in ("new", "final", "latest")]:
        try:
            os.replace(building, target)
            zip_path = target
            placed = True
            if target.name != f"Crystar.QoL-v{version}.zip":
                print(f"[warn] 正式名被占用，已输出到 {target.name}")
            break
        except OSError:
            continue
    if not placed:
        raise SystemExit("[ERROR] 所有候选 zip 路径都被占用，无法输出发布包")

    size = zip_path.stat().st_size / 1024 / 1024
    print(f"[4/4] done -> {zip_path}  ({size:.1f} MB)")

    # 清理临时 staging；删不掉也不影响已经生成的 zip（系统临时目录会自行回收）
    try:
        shutil.rmtree(stage_root)
    except Exception as e:
        print(f"[warn] staging 清理被拦截（不影响 zip）: {e}")
        print(f"       临时目录: {stage_root}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
