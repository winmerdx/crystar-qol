using System;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 经验倍率 ×N + 金钱倍率 ×N。
    ///
    /// 【v0.1.6 根因纠正 —— v0.1.5 的"参数名被抹"结论是错的】
    /// 实测对照：
    ///   - CharacterComponent.DamageHp（实例方法）按参数名注入 `ref int damage` 一直读到
    ///     真实值（日志 dmg 142 x2 -> 402）→ 按名注入对实例方法有效，interop 参数名没被抹。
    ///   - 掉落数值为 0 的，全是挂了**静态方法**的那批：BattleManager.GetExp/GetMoney/AddItem。
    ///     旧探针 `AddItem id=0 chara=262 count=5` 是铁证：只有第 1 参丢、其余全对 ——
    ///     IL2CPP 静态方法的隐藏首参 MethodInfo* 占 rcx（游戏调用点显式 xor ecx,ecx=0），
    ///     detour 包装器没跳过它 → __0/__args[0] 读到 rcx=0，**且原始方法被以 0 调用**
    ///     → 掉落金额/经验在源头就被写成 0（金钱/经验/道具 id 全灭）。
    ///   - 推论链验证：v0.1.3 expmoney 初始化失败（一个补丁都没挂上）→ 掉落全部正常；
    ///     v0.1.0/v0.1.1/v0.1.2 挂了静态 GetMoney → 从那时起货币就变 0。
    ///
    /// 【v0.1.6 对策】
    ///   - 绝不 patch / probe 任何**静态** IL2CPP 方法（除非确认参数无意义）。
    ///   - 只挂**实例方法**（rcx=this, edx=第1参，包装器编组正确——DamageHp 实证）：
    ///     经验 = PartyManager.GetExp(int exp, bool bossRush)（实例，__0=exp）
    ///     金钱 = UserParameter.IncreaseMoney(int)（实例，所有金钱入账唯一汇聚点）
    ///     飘字 = UIGameMainWindow.AddActionLogGetMoney(int)（实例，显示与入账一致翻倍）
    ///     上游静态转发壳（BattleManager.GetExp/GetMoney）不碰 → 原生值原样流进来。
    /// </summary>
    public sealed class FeatureExpMoney : IFeature
    {
        internal static FeatureExpMoney Inst;

        public string Id { get { return "expmoney"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        private MelonPreferences_Entry<float> _expMult;
        private MelonPreferences_Entry<float> _moneyMult;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            var c = ctx.Cfg;
            _expMult = c.Float("ExpMult", 1f, "EXP multiplier / 经验倍率");
            _moneyMult = c.Float("MoneyMult", 1f, "Money multiplier / 金钱倍率");

            // v0.1.4 纪律：每个补丁独立 try/catch，坏一个不拖垮其它 feature。
            Safe(ctx.Harmony, typeof(ExpPatch), "PartyManager.GetExp");
            Safe(ctx.Harmony, typeof(MoneySinkPatch), "UserParameter.IncreaseMoney");
            Safe(ctx.Harmony, typeof(MoneyLogPatch), "UIGameMainWindow.AddActionLogGetMoney");
            Safe(ctx.Harmony, typeof(ExpAddProbe), "CharacterParameter.AddExp（仅探针）");
            // v0.1.6：BattleManager.GetExp/GetMoney/AddItem（静态）探针已删除——
            // 静态 detour 会把原始方法的第一参数污染成 0，探针本身就是掉落归零的元凶。
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("expmoney: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }
        public void OnReset() { }   // 命中时判定，无每帧副作用

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Float(Loc.T("f.exp"), _expMult, 1f, 0.5f, 0.5f, 100f);
            yield return Item.Float(Loc.T("f.money"), _moneyMult, 1f, 0.5f, 0.5f, 100f);
        }

        private static int Scale(float mult, int value)
        {
            if (value <= 0 || mult <= 0f) return value;
            long v = (long)Math.Round(value * (double)mult);
            return v > int.MaxValue ? int.MaxValue : (int)v;
        }

        private static float ExpMult
        {
            get
            {
                var f = Inst;
                return f == null || f._expMult == null ? 1f : f._expMult.Value;
            }
        }

        private static float MoneyMult
        {
            get
            {
                var f = Inst;
                return f == null || f._moneyMult == null ? 1f : f._moneyMult.Value;
            }
        }

        // ---------- 实例方法入账点（v0.1.6 唯一允许的挂钩形态） ----------

        /// <summary>经验入账：PartyManager.GetExp(int exp, bool bossRush)，实例方法。
        /// 反汇编：BattleManager.GetExp(0x1B92E0) 尾跳 0x147F60 时 exp 走 edx；
        /// 实例 detour 编组 (rcx=this, edx=__0) 已被 DamageHp 长期实证可靠。</summary>
        [HarmonyPatch(typeof(PartyManager), "GetExp", new Type[] { typeof(int), typeof(bool) })]
        private sealed class ExpPatch
        {
            private static bool _live;
            private static void Prefix(ref int __0, bool __1)
            {
                // v0.1.7 铁律：倍率补丁必须过重入闸门（MelonLoader 0.5.7 的 IL2CPP detour
                // 一次调用会让前缀链执行两遍，第二遍读到的是第一遍的输出 → 倍率被平方）。
                // 见 ReentryGate 注释与 v0.1.6 实锤日志。
                if (!ReentryGate<ExpPatch>.Begin()) return;
                try
                {
                    if (!_live) { _live = true; QolLog.Info("exp: PartyManager.GetExp hook live"); }
                    float m = ExpMult;
                    int before = __0;
                    __0 = Scale(m, before);
                    QolLog.Trace("exp", "PartyManager.GetExp " + before + " x" + m.ToString("0.##") + " -> " + __0 + " bossRush=" + __1);
                }
                catch { }
            }

            private static void Postfix() { ReentryGate<ExpPatch>.End(); }
        }

        /// <summary>金钱最终入账汇聚点：UserParameter.IncreaseMoney(int value)，实例方法。
        /// 反汇编：BattleManager.GetMoney(0x1B94D0) 函数体里 call 0x1EC3A0 前
        /// mov edx, ebx（金额）→ 不挂静态壳后这里拿到的就是真实金额。</summary>
        [HarmonyPatch(typeof(UserParameter), "IncreaseMoney")]
        private sealed class MoneySinkPatch
        {
            private static bool _live;
            private static void Prefix(ref int __0)
            {
                if (!ReentryGate<MoneySinkPatch>.Begin()) return;
                try
                {
                    if (!_live) { _live = true; QolLog.Info("money: UserParameter.IncreaseMoney hook live"); }
                    float m = MoneyMult;
                    int before = __0;
                    __0 = Scale(m, before);
                    QolLog.Trace("money", "IncreaseMoney " + before + " x" + m.ToString("0.##") + " -> " + __0);
                }
                catch { }
            }

            private static void Postfix() { ReentryGate<MoneySinkPatch>.End(); }
        }

        /// <summary>金钱飘字：UIGameMainWindow.AddActionLogGetMoney(int value)，实例方法。
        /// GetMoney 函数体尾跳 0x2017B0（jmp 仍是进入函数入口，detour 能截到）。
        ///
        /// 【v0.1.8 补闸门】v0.1.7 唯独漏了这一个倍率补丁的门 —— 实锤日志：
        /// IncreaseMoney 24 x2 -&gt; 48（有门，一次）后紧跟 AddActionLogGetMoney 24 x2 -&gt; 48
        /// 再 48 x2 -&gt; 96（无门，平方）→ 屏幕上"获得 XX"总是实际入账的 2 倍。
        /// 现在飘字与入账都只乘一次。</summary>
        [HarmonyPatch(typeof(UIGameMainWindow), "AddActionLogGetMoney")]
        private sealed class MoneyLogPatch
        {
            private static bool _live;
            private static void Prefix(ref int __0)
            {
                if (!ReentryGate<MoneyLogPatch>.Begin()) return;
                try
                {
                    if (!_live) { _live = true; QolLog.Info("money.log: AddActionLogGetMoney hook live"); }
                    float m = MoneyMult;
                    int before = __0;
                    __0 = Scale(m, before);
                    QolLog.Trace("money.log", "AddActionLogGetMoney " + before + " x" + m.ToString("0.##") + " -> " + __0);
                }
                catch { }
            }

            private static void Postfix() { ReentryGate<MoneyLogPatch>.End(); }
        }

        /// <summary>纯探针（不改值）：CharacterParameter.AddExp(int value) 是经验真正落到角色身上的
        /// 最后一站（RVA 0x1CBDA0，实例方法）。只打日志，用来证明"倍率后的值确实入账了"，
        /// 不参与任何缩放 —— 探针类补丁严禁改参数。</summary>
        [HarmonyPatch(typeof(CharacterParameter), "AddExp", new Type[] { typeof(int) })]
        private sealed class ExpAddProbe
        {
            private static bool _live;
            private static void Prefix(int __0)
            {
                try
                {
                    if (!_live) { _live = true; QolLog.Info("exp.sink: CharacterParameter.AddExp hook live"); }
                    QolLog.Trace("exp.sink", "AddExp " + __0 + " (exp mult x" + ExpMult.ToString("0.##") + ")");
                }
                catch { }
            }
        }
    }
}
