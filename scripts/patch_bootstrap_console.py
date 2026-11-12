#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MelonLoader 0.5.7 Bootstrap.dll —— 控制台「零闪」补丁（方案 A）。

目的：不填任何 Steam 启动参数，让进程从一开始就不创建 MelonLoader 控制台窗口。
做法：只改 6 字节机器码，把 Console::Initialize 里对 ShouldHide 的条件跳转改成无条件跳转。

== 取证（见 docs/MelonLoader-Bootstrap控制台取证.md）==
本机 Bootstrap.dll : 181248 B, md5 1CB05F16B8602BA424D396102D7EC158
PE                 : x64, ImageBase 0x180000000, .text RVA 0x1000 / RawOff 0x400
Console::Initialize: RVA 0x10470..0x107B6（pdata 函数边界 + "Failed to Allocate Console!" xref）
CommandLine::Read  : RVA 0x10117..0x10456（"--melonloader.hideconsole" xref）

    0x180010494  cmp  byte ptr [rip+0x19ab4], 0   [0x29f4f]  ; Debug::Enabled
    0x18001049b  jne  0x1800104aa                            ; debug → 照常建控制台
    0x18001049d  cmp  byte ptr [rip+0x19aa9], 0   [0x29f4d]  ; Console::ShouldHide
    0x1800104a4  jne  0x18001078f                            ; hideconsole → mov al,1; ret
    0x1800104aa  call qword ptr [rip+0xfc08]       [0x200b8] ; AllocConsole()

CommandLine::Read 的 --melonloader.hideconsole 分支：
    0x180010286  mov  byte ptr [rip+0x19cc0], 1   [0x29f4d]  ; ShouldHide = true

⇒ ShouldHide 在 .bss（RVA 0x29f4d），PE 文件里没有它的初值字节，改「全局初值」不可行。
⇒ 改代码：0x1800104a4 的 `jne rel32` → `jmp rel32` + NOP。
   非 debug 时恒跳到 0x18001078f (mov al,1 / epilogue / ret)，与官方 hideconsole 同一路径；
   --melonloader.debug 时第一条 jne 仍跳到 AllocConsole，排错入口保留。

原字节 : 0F 85 E5 02 00 00   (jne  0x18001078f, rel32=0x2E5, len=6)
新字节 : E9 E6 02 00 00 90   (jmp  0x18001078f, rel32=0x2E6, len=5 + NOP)

严格校验：源文件 md5 / 大小 / 偏移处字节；产物仅这 6 字节不同；产物 md5 与期望值一致。
只用标准库，打包机不需要 pefile/capstone（取证分析脚本另放 docs/）。
"""
import argparse
import hashlib
import sys
from pathlib import Path

ORIGINAL_MD5 = "1cb05f16b8602ba424d396102d7ec158"
PATCHED_MD5 = "c18a85ab199fc0f6b933a0719024ab15"  # 本机实测；空值表示只做字节级校验
FILE_SIZE = 181248
PATCH_RVA = 0x104A4
PATCH_FILE_OFFSET = 0xF8A4  # = PATCH_RVA - .text RVA(0x1000) + .text RawOff(0x400)
ORIG_BYTES = bytes.fromhex("0f85e5020000")
NEW_BYTES = bytes.fromhex("e9e602000090")


class PatchError(Exception):
    pass


def md5(data: bytes) -> str:
    return hashlib.md5(data).hexdigest()


def verify_original(data: bytes) -> None:
    if len(data) != FILE_SIZE:
        raise PatchError(f"源文件大小不符: {len(data)} != {FILE_SIZE}")
    got = md5(data)
    if got != ORIGINAL_MD5:
        raise PatchError(
            f"源文件 md5 不符: {got} != {ORIGINAL_MD5}。"
            "说明 MelonLoader 版本变了，禁止盲打补丁——请重新反汇编取证并更新本脚本常量。"
        )
    actual = data[PATCH_FILE_OFFSET:PATCH_FILE_OFFSET + len(ORIG_BYTES)]
    if actual != ORIG_BYTES:
        raise PatchError(
            f"文件偏移 0x{PATCH_FILE_OFFSET:X} 处字节不符: {actual.hex()} != {ORIG_BYTES.hex()}"
        )


def verify_patched(data: bytes) -> None:
    if len(data) != FILE_SIZE:
        raise PatchError(f"补丁版大小不符: {len(data)} != {FILE_SIZE}")
    actual = data[PATCH_FILE_OFFSET:PATCH_FILE_OFFSET + len(NEW_BYTES)]
    if actual != NEW_BYTES:
        raise PatchError(
            f"补丁版偏移 0x{PATCH_FILE_OFFSET:X} 处字节不符: {actual.hex()} != {NEW_BYTES.hex()}"
        )
    if PATCHED_MD5 and md5(data) != PATCHED_MD5:
        raise PatchError(f"补丁版 md5 不符: {md5(data)} != {PATCHED_MD5}")


def patch_bytes(data: bytes) -> bytes:
    """校验原版 → 只改 6 字节 → 校验产物 → 确认差异集合。"""
    verify_original(data)
    out = bytearray(data)
    out[PATCH_FILE_OFFSET:PATCH_FILE_OFFSET + len(NEW_BYTES)] = NEW_BYTES
    result = bytes(out)
    win_end = PATCH_FILE_OFFSET + len(NEW_BYTES)
    if (result[:PATCH_FILE_OFFSET] != data[:PATCH_FILE_OFFSET]
            or result[win_end:] != data[win_end:]):
        raise PatchError("补丁改动溢出 6 字节窗口")
    diffs = [i for i, (a, b) in enumerate(zip(data, result)) if a != b]
    allowed = set(range(PATCH_FILE_OFFSET, win_end))
    if not diffs or not set(diffs).issubset(allowed):
        raise PatchError(f"差异字节集合异常: {[hex(i) for i in diffs]}")
    verify_patched(result)
    return result


def describe(data: bytes) -> str:
    return (
        f"size={len(data)} md5={md5(data)} "
        f"patch@file:0x{PATCH_FILE_OFFSET:X}/rva:0x{PATCH_RVA:X} "
        f"{ORIG_BYTES.hex()} -> {NEW_BYTES.hex()}"
    )


def check(path: Path) -> int:
    try:
        data = path.read_bytes()
    except OSError as e:
        print(f"[ERROR] 读不到文件: {path} ({e})")
        return 2
    try:
        verify_patched(data)
        print(f"[PATCHED] {path}  {describe(data)}")
        return 0
    except PatchError:
        pass
    try:
        verify_original(data)
        print(f"[ORIGINAL] {path}  {describe(data)}")
        return 0
    except PatchError as e:
        print(f"[UNKNOWN] {path}  {e}")
        return 2


def main() -> int:
    ap = argparse.ArgumentParser(description="Bootstrap.dll 控制台零闪补丁（6 字节）")
    ap.add_argument("source", nargs="?", type=Path, help="原版 Bootstrap.dll")
    ap.add_argument("--output", "-o", type=Path, help="补丁版输出路径（不写则只校验源）")
    ap.add_argument("--check", type=Path, help="只检查某个 DLL 是原版/补丁版/未知")
    args = ap.parse_args()

    if args.check:
        return check(args.check)

    if not args.source:
        ap.error("需要 source（或用 --check）")
    data = args.source.read_bytes()
    patched = patch_bytes(data)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(patched)
        verify_patched(args.output.read_bytes())
        print(f"[OK] {args.source} -> {args.output}")
        print(f"     {describe(patched)}")
    else:
        verify_original(data)
        print(f"[OK] 源文件校验通过，补丁可应用: {describe(patched)}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except PatchError as e:
        print(f"[ERROR] {e}", file=sys.stderr)
        sys.exit(1)
