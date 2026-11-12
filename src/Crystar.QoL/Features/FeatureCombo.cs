using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// v0.2 批次 1：连击不中断（B4）。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 挂点取证（2026-09-10 反汇编，GameAssembly.dll）
    /// ─────────────────────────────────────────────────────────────────────
    /// `BattleManager`（Game.BattleManager : SingletonMonoBehaviour&lt;BattleManager&gt;）的连击状态是
    /// 两个**相邻字段**（dump.cs:157041-157042）：
    ///   • `private int   mHitComboCount; // 0xC0`
    ///   • `private float mHitComboTimer; // 0xC4`
    ///
    /// 三个相关方法（RVA 全部实测）：
    ///
    /// ① `AddHitComboCount(int count)` 0x1B68C0 —— **只加不清**：
    ///      `mov esi,[rbx+0xC0]` / `lea eax,[rsi+rdi]` / `cmp eax,0x270F` / `cmovg` → 上限 9999
    ///      `mov [rbx+0xC0],eax`；随后把另一对象的 0xBC 拷进 `[rbx+0xC4]`（= 重置倒计时）。
    ///
    /// ② `UpdateHitComboCount()` 0x1BC420 —— **超时清零（唯一活跃的清零路径）**：
    ///      `movss xmm6,[rbx+0xC4]` / `if (timer <= 0) return`
    ///      `xmm6 -= Global.DeltaTime` / `movss [rbx+0xC4],xmm6`
    ///      `if (0 < xmm6) return` ← 还没到点
    ///      否则 `mov qword [rbx+0xC0],0`（**一次清掉 count + timer**）
    ///      调用者仅 1 处：`BattleManager.Update()`（0x1BCB70 内 0x1BCB8F），每帧跑。
    ///
    /// ③ `ResetHitComboCount()` 0x1BAB90 —— 两条指令的裸清零：
    ///      `xor eax,eax` / `mov qword [rcx+0xC0],rax; ret`
    ///      **callers.py 全量扫描：0 处调用**（疑似调试遗留 / 仅被委托间接引用）。
    ///      另有 `BattleManager.ResetAll()` 0x1BAB00 也会 `mov qword [rbx+0xC0],0`
    ///      —— 那是**战斗整体复位**（清连击 + 清召唤币等），属正当复位点，**刻意不动**。
    ///
    /// 【本功能的取法】只掐"超时"这一条活跃清零路径：
    ///   Prefix 里把 `mHitComboTimer(0xC4)` 顶到一个大值，使 `timer -= dt` 永远到不了 0。
    ///   → 战斗内连击不会因为"停手一会儿"而断；
    ///   → 换战斗/复位时仍由 `ResetAll` 正常清零，不会把连击带到下一场。
    /// 常数覆盖天然幂等（同一值写两遍结果相同），不需要 ReentryGate。
    ///
    /// 【为什么不去 patch ResetHitComboCount】它是 0 调用者的死路径，挂上去只有两种结果：
    ///   要么什么都不发生（浪费），要么它其实被间接调用 —— 而那种场合多半是"战斗开始/结束"
    ///   的整体复位，掐掉反而让连击跨战斗残留。宁可不挂。
    ///
    /// 副作用撤销：本功能不做持久写入，停止"续命"后倒计时自然走完、自动清零 → OnReset 空实现。
    /// </summary>
    public sealed class FeatureCombo : IFeature
    {
        internal static FeatureCombo Inst;

        public string Id { get { return "combo"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        /// <summary>mHitComboTimer 的字段偏移。</summary>
        private const int ComboTimerOffset = 0xC4;
        /// <summary>把倒计时顶到的值（秒）。远大于单场战斗时长即可。</summary>
        private const float KeepSeconds = 9999f;

        private MelonPreferences_Entry<bool> _noBreak;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            // v0.1.3 原则：默认关，玩家不动 mod 就与原生一致
            _noBreak = ctx.Cfg.Bool("NoBreak", false, "Hit combo never times out / 连击不中断");
            Safe(ctx.Harmony, typeof(ComboTimeoutPatch), "BattleManager.UpdateHitComboCount");
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("combo: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Enabled
        {
            get { var f = Inst; return f != null && f._noBreak != null && f._noBreak.Value; }
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }

        /// <summary>无持久副作用：停止续命后倒计时自然到 0 并清零。</summary>
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Bool(Loc.T("f.combo"), _noBreak, false, RiskTier.Safe, Effect.Immediate);
        }

        /// <summary>BattleManager.UpdateHitComboCount() 0x1BC420（实例方法，唯一调用者 = Update）。</summary>
        [HarmonyPatch(typeof(BattleManager), "UpdateHitComboCount")]
        private static class ComboTimeoutPatch
        {
            private static bool _live;
            private static void Prefix(BattleManager __instance)
            {
                try
                {
                    if (!Enabled || __instance == null) return;
                    if (!_live) { _live = true; QolLog.Info("combo: UpdateHitComboCount hook live"); }
                    Marshal.WriteInt32(IntPtr.Add(NativePtr.Of(__instance), ComboTimerOffset),
                                       NativePtr.FloatBits(KeepSeconds));
                }
                catch { }
            }
        }
    }
}
