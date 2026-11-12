using System;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>击杀计数倍率：把 UserParameter 的击杀计数一次补足 N 次。</summary>
    public sealed class FeatureKillCounter : IFeature
    {
        internal static FeatureKillCounter Inst;

        public string Id { get { return "killcounter"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        private MelonPreferences_Entry<int> _mult;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            _mult = ctx.Cfg.Int("Multiplier", 1, "Kill count multiplier / 击杀计数倍率");
            ctx.Harmony.PatchAll(typeof(KillPatch));
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return new MenuItem
            {
                Label = Loc.T("f.kill"),
                ValueText = () => "x" + _mult.Value,
                Adjust = d =>
                {
                    int v = _mult.Value + (d > 0 ? 1 : -1);
                    if (v < 1) v = 1;
                    if (v > 99) v = 99;
                    _mult.Value = v;
                },
                Reset = () => _mult.Value = 1
            };
        }

        [HarmonyPatch(typeof(UserParameter), "IncrementKillEnemy")]
        private static class KillPatch
        {
            private static int _lastWritten = -1;
            private static int _lastTick = -99999;

            private static void Postfix(UserParameter __instance)
            {
                try
                {
                    var f = Inst;
                    if (f == null || f._mult == null || __instance == null) return;
                    int m = f._mult.Value;
                    if (m <= 1) return;

                    // v0.1.7 输出回声去重：Postfix 链会执行两遍，第二遍看到的计数
                    // 已包含第一遍补足的次数（cur == _lastWritten）→ 跳过。
                    // 正常的下一次击杀计数只会更大（原始 +1），不会误判。
                    int now = Environment.TickCount;
                    int cur = __instance.KillEnemyCount;
                    if (cur == _lastWritten && now - _lastTick < 100) return;

                    __instance.KillEnemyCount = cur + (m - 1);
                    _lastWritten = __instance.KillEnemyCount;
                    _lastTick = now;
                    QolLog.DebugThrottled("kill", "kill count +" + (m - 1));
                }
                catch (Exception e)
                {
                    QolLog.DebugThrottled("kill", "FeatureKillCounter: " + e.Message);
                }
            }
        }
    }
}
