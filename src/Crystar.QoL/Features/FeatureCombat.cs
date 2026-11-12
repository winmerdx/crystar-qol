using System;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 战斗四件套：秒杀 / 强杀受保护 / 伤害倍率 / 玩家无敌。
    /// 全部挂在 CharacterComponent.DamageHp 一个入口上。
    ///
    /// 纪律：秒杀必须走 DamageHp，禁止直接写 set_Hp——set_Hp 会绕过
    /// Invincible / DamageResistance / ReverberationHp / notDie 五层保护，
    /// 且跳过 IsDead→IsDeadComplete 两段式死亡，不掉落、不给经验、不计击杀。
    /// </summary>
    public sealed class FeatureCombat : IFeature
    {
        internal static FeatureCombat Inst;

        public string Id { get { return "combat"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        /// <summary>v0.2 B7：游戏自己的暴击伤害倍率（反汇编 CalcDamage 0x1BF6F5 的
        /// `mulss xmm0,[rip+0x9EFABF]` → 浮点常量 0x180BAF1BC = 1.6f）。</summary>
        internal const float CritMultiplier = 1.6f;

        private MelonPreferences_Entry<bool> _oneHit;
        private MelonPreferences_Entry<bool> _forceProtected;
        private MelonPreferences_Entry<float> _dmgMult;
        private MelonPreferences_Entry<bool> _god;
        private MelonPreferences_Entry<bool> _critAlways;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            var c = ctx.Cfg;
            // v0.1.3：默认关闭。原则——玩家没动过 mod 之前，一切行为必须与原生游戏一致
            _oneHit = c.Bool("OneHit", false, "One-hit kill enemies / 秒杀敌人");
            _forceProtected = c.Bool("ForceProtected", false, "Also kill protected enemies / 连受保护的敌人一起杀");
            _dmgMult = c.Float("DamageMult", 1f, "Damage multiplier / 伤害倍率");
            _god = c.Bool("GodMode", false, "Player takes no damage / 玩家无敌");
            _critAlways = c.Bool("AlwaysCrit", false, "Every hit crits / 必定暴击");
            // 【v0.1.4 纪律】每个 PatchAll 独立 try/catch：坏一个不拖垮其它。
            // v0.2 起 B7 是**独立补丁类**（同一方法上的第二个 patch）——万一
            // `ref bool critical` 绑定不上，死的只有暴击，秒杀/无敌/伤害倍率不受影响。
            Safe(ctx.Harmony, typeof(DamagePatch), "CharacterComponent.DamageHp（秒杀/倍率/无敌）");
            Safe(ctx.Harmony, typeof(AlwaysCritPatch), "CharacterComponent.DamageHp（必定暴击）");
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("combat: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }

        /// <summary>这些项都是"命中时判定"，没有每帧写入，配置复位即真正复位。</summary>
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Bool(Loc.T("f.onehit"), _oneHit, true, RiskTier.Safe, Effect.Immediate);
            yield return Item.Bool(Loc.T("f.onehit.prot"), _forceProtected, false, RiskTier.Risky, Effect.Immediate, true);
            yield return Item.Float(Loc.T("f.dmg"), _dmgMult, 1f, 0.1f, 0.1f, 100f);
            yield return Item.Bool(Loc.T("f.crit"), _critAlways, false, RiskTier.Safe, Effect.Immediate);
            yield return Item.Bool(Loc.T("f.god"), _god, false, RiskTier.Safe, Effect.Immediate);
        }

        // 显式列出参数类型锁定重载。只写方法名的话 Harmony 会匹配所有同名重载，
        // 而 CharacterComponent.DamageHp 内部还会再调一次 CharacterParameter.DamageHp，
        // 一旦两个都被命中，倍率就会被连乘两次（1.5 倍变 2.25 倍、2 倍变 4 倍）。
        [HarmonyPatch(typeof(CharacterComponent), "DamageHp",
            new Type[] { typeof(int), typeof(bool), typeof(bool) })]
        private static class DamagePatch
        {
            /// <summary>重入闸门：一次真实的伤害结算里只允许乘一次。</summary>
            [ThreadStatic] private static bool _busy;
            [ThreadStatic] private static int _stamp;

            private static void Prefix(CharacterComponent __instance, ref int damage, bool notDie)
            {
                // 上一次进入后超过 2 秒还挂着，说明原始方法抛异常没走到 Postfix，当作脏标记清掉
                if (_busy && Environment.TickCount - _stamp > 2000) _busy = false;
                if (_busy)
                {
                    QolLog.Trace("dmg.nested", "nested DamageHp detected - skip re-scaling (damage=" + damage + ")");
                    return;
                }
                _busy = true;
                _stamp = Environment.TickCount;

                try
                {
                    var self = Inst;
                    if (self == null || __instance == null) return;

                    bool isEnemy = false, isPlayer = false;
                    try { isEnemy = __instance.IsEnemyCharacter(); } catch { }
                    try { isPlayer = __instance.IsPlayerCharacter(); } catch { }

                    // 无敌：打在玩家身上的伤害归零
                    if (isPlayer && self._god != null && self._god.Value)
                    {
                        damage = 0;
                        return;
                    }

                    if (!isEnemy || isPlayer) return;

                    int maxHp = 0;
                    try
                    {
                        var p = __instance.CharacterParameter;
                        if (p != null) maxHp = p.MaxHp;
                    }
                    catch { }

                    // 秒杀
                    bool oneHit = self._oneHit != null && self._oneHit.Value;
                    bool force = self._forceProtected != null && self._forceProtected.Value;
                    if (oneHit)
                    {
                        if (notDie && !force) return;   // 剧情必活/演出无敌敌人，不杀
                        int want = maxHp <= 0 ? 999999 : maxHp * 1000;
                        if (damage < want) damage = want;
                        QolLog.DebugThrottled("onehit", "one-hit damage -> " + want + " (maxHp " + maxHp + ")");
                        return;
                    }

                    // 伤害倍率
                    float m = self._dmgMult != null ? self._dmgMult.Value : 1f;
                    if (m != 1f && damage > 0)
                    {
                        int before = damage;
                        long v = (long)Math.Round(damage * (double)m);
                        damage = v > int.MaxValue ? int.MaxValue : (int)v;
                        QolLog.Trace("dmg", "dmg " + before + " x" + m.ToString("0.##") + " -> " + damage);
                    }
                    else if (damage > 0)
                    {
                        QolLog.Trace("dmg", "dmg " + damage + " (mult off)");
                    }
                }
                catch (Exception e)
                {
                    QolLog.DebugThrottled("combat", "FeatureCombat: " + e.Message);
                }
            }

            private static void Postfix()
            {
                _busy = false;
            }
        }

        /// <summary>
        /// v0.2 B7：必定暴击。
        ///
        /// ───────────── 为什么不是"把 DamageHp 的 critical 改成 true 就完事" ─────────────
        /// 反汇编 `CharacterComponent.DamageHp(int damage, bool critical, bool notDie)` 0x1C4B80：
        ///   • `movzx r14d, r8b` 保存 critical，但**真正扣血**那一步是
        ///     `call 0x1CC520`(CharacterParameter.DamageHp(int,bool))，**只传 damage / notDie**；
        ///   • critical(r14b) 唯一下游去处是 `0x1C4C51 movzx r9d,r14b` → `call 0x46990`
        ///     = `UIScreenDamageComponent.CreateDamage(ref pos, damage, critical, recovery, player)`
        ///     —— 即**伤害数字飘字**。
        ///   ⇒ `critical` 是**已经算好的结果位（显示用）**，改它只改飘字样式、**不改伤害**
        ///     （B7 的前置猜测据此被证实）。
        ///
        /// 真正的暴击运算在 **静态** `CalcParameter.CalcDamage`(0x1BF010) 里（暴击判定 +
        /// 伤害倍率）。静态方法按 10.8 铁律**不能 patch**，且判定处是**内联直读字段**
        /// （`movd xmm1,[CharacterParameter+0x50]` = mLuck / 100.0f vs Random.Range(0,1)，
        /// 见 0x1BF44E~0x1BF469），也没有可 patch 的 getter。
        ///
        /// ───────────── 因此本实现 = 在"伤害落地"这一步复刻暴击的最终效果 ─────────────
        /// 进入 DamageHp 时 damage 已是 CalcDamage 的输出；暴击的相对效果就是 **×1.6**
        /// （CalcDamage 0x1BF6F5 的常量）。于是：
        ///     critical = true（飘字显示暴击） + damage ×= 1.6（伤害也按暴击算）
        /// 只对"打在敌人身上"的伤害生效（与秒杀/倍率同一判定口径）。
        ///
        /// 【重入】倍率型改写 → 必须带 ReentryGate（10.9 铁律，防一次调用被执行两遍而平方）。
        /// 【独立补丁类】刻意与 DamagePatch 分开：`ref bool critical` 万一绑定失败，
        /// 只损失暴击，不会连带弄死秒杀/无敌/伤害倍率。
        /// </summary>
        [HarmonyPatch(typeof(CharacterComponent), "DamageHp",
            new Type[] { typeof(int), typeof(bool), typeof(bool) })]
        private sealed class AlwaysCritPatch
        {
            private static bool _live;

            private static void Prefix(CharacterComponent __instance, ref int damage, ref bool critical)
            {
                if (!ReentryGate<AlwaysCritPatch>.Begin()) return;
                try
                {
                    var self = Inst;
                    if (self == null || __instance == null) return;
                    if (self._critAlways == null || !self._critAlways.Value) return;
                    if (damage <= 0) return;

                    bool isEnemy = false, isPlayer = false;
                    try { isEnemy = __instance.IsEnemyCharacter(); } catch { }
                    try { isPlayer = __instance.IsPlayerCharacter(); } catch { }
                    if (!isEnemy || isPlayer) return;   // 只给玩家打敌人的伤害加暴击

                    if (!_live) { _live = true; QolLog.Info("crit: always-crit hook live"); }
                    int before = damage;
                    critical = true;
                    long v = (long)Math.Round(before * (double)CritMultiplier);
                    damage = v > int.MaxValue ? int.MaxValue : (int)v;
                    QolLog.Trace("crit", "always-crit " + before + " x" + CritMultiplier + " -> " + damage);
                }
                catch { }
            }

            private static void Postfix()
            {
                ReentryGate<AlwaysCritPatch>.End();
            }
        }
    }
}
