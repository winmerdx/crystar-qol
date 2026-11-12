using System;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 移速倍率：只改 AIUtility.GetPlayerMoveSpeedRate 的返回值，只影响玩家，动画联动。
    ///
    /// 【v0.1.7】重入去重改为无条件生效（不再依赖 movespeed.once 标记）：
    /// MelonLoader 0.5.7 的 IL2CPP detour 一次调用会让 Postfix 链执行两遍，
    /// 第二遍的 __result 正是第一遍的输出 —— 不去重倍率就平方（2 倍速实际 4 倍速，
    /// v0.1.6 日志实锤：rate 1 x2 -> 2 紧跟 rate 2 x2 -> 4）。
    /// 去重原理见 Postfix 内注释；属于 ReentryGate 注释里的"用法 B"。
    /// </summary>
    public sealed class FeatureMoveSpeed : IFeature
    {
        internal static FeatureMoveSpeed Inst;

        public string Id { get { return "movespeed"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        private MelonPreferences_Entry<float> _mult;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            _mult = ctx.Cfg.Float("Multiplier", 1f, "Player move speed multiplier / 移速倍率");
            ctx.Harmony.PatchAll(typeof(SpeedPatch));
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Float(Loc.T("f.move"), _mult, 1f, 0.1f, 0.1f, 10f);
        }

        [HarmonyPatch(typeof(AIUtility), "GetPlayerMoveSpeedRate")]
        private static class SpeedPatch
        {
            private static float _lastOut = -999f;
            private static int _lastTick = -99999;

            private static void Postfix(ref float __result)
            {
                try
                {
                    var f = Inst;
                    if (f == null || f._mult == null) return;
                    float m = f._mult.Value;
                    if (m <= 0f || Math.Abs(m - 1f) < 0.001f) return;

                    // 输出回声去重：同一链路里第二遍 Postfix 收到的 __result 与我们
                    // 上一遍的输出完全一致（误差 <1e-4）且在极短时间内 → 跳过。
                    // 用系统时间而非游戏时间：菜单打开时 GameSpeed 被冻结，游戏时间不走。
                    // 已知极小误伤：24ms 内速率恰好跳变到与上次输出相同值（如 1.0→2.0）
                    // 会被漏乘一次，下一帧自行恢复 —— 对速度 getter 可接受。
                    int now = Environment.TickCount;
                    if (Math.Abs(__result - _lastOut) < 0.0001f && now - _lastTick < 24) return;

                    float before = __result;
                    __result = before * m;
                    _lastOut = __result;
                    _lastTick = now;
                    QolLog.Trace("speed", "rate " + before.ToString("0.###") + " x" + m.ToString("0.##")
                                          + " -> " + __result.ToString("0.###"));
                }
                catch (Exception e)
                {
                    QolLog.DebugThrottled("movespeed", "FeatureMoveSpeed: " + e.Message);
                }
            }
        }
    }
}
