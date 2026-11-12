using System;
using MelonLoader;

namespace CrystarQoL
{
    /// <summary>
    /// 紧急总关：一键停用全部功能。
    ///
    /// 为什么必须独立于菜单：菜单本身可能因为某个功能把游戏搞到打不开，
    /// 这时"菜单里的总开关"就失效了。所以热键直接绑死，不依赖菜单状态。
    /// </summary>
    public static class KillSwitch
    {
        public static bool Tripped { get; private set; }

        /// <summary>
        /// 由 <c>ModMain</c> 在 Init 时注入：逐个 feature 调 <c>IFeature.OnReset()</c> 撤销运行时副作用。
        ///
        /// ⛔ 修正十六（r17）补的洞：<c>QolConfig.ResetAll()</c> 只是把**偏好值**恢复默认 + 落盘，
        /// **不碰任何 feature 的运行时状态**。而 <c>Master=false</c> 之后 ModMain 连
        /// <c>OnUpdate</c> 都不再调 ⇒ "改过数据表"的 feature（FeatureGrowth 的强化/思装变容/学习表）
        /// 永远等不到那一轮"开关跳变 → 还原"⇒ 表会一直停在 0（跟玩家报的「关掉还是 0 消耗」同源）。
        /// 挂上这个回调后，"紧急关闭"才真正等于"撤销 mod 做过的一切"。
        /// ⚠ 语义：**只管关（Trip）**，回来（Restore）时不调 —— 恢复后下一轮 OnUpdate 会重新套用。
        /// </summary>
        public static Action RevokeAll;

        public static void Toggle()
        {
            if (Tripped) Restore(); else Trip();
        }

        public static void Trip()
        {
            Tripped = true;
            try { QolConfig.Master.Value = false; } catch { }
            QolConfig.ResetAll();           // ① 把偏好值恢复默认并落盘（顺带让各 feature 回到"原版"语义）
            // ② 再撤销各 feature 的运行时副作用（改过的表要写回原值）。
            //    ⚠ 顺序不能反：撤销要按"原版"语义走，必须读**已重置**的偏好
            //    （例：FeatureGrowth 的幽怨槽池要按 ExpandOn()==false ⇒ 上限 10 去收缩）。
            if (RevokeAll != null)
            {
                try { RevokeAll(); }
                catch (Exception e) { QolLog.Warn("kill: 撤销运行时副作用失败 -> " + e.GetType().Name + ": " + e.Message); }
            }
            GameInputGate.Suppress = false; // 输入必须放回去，否则玩家会以为游戏卡死
            QolLog.Warn(Loc.T("kill.tripped"));
            MelonLogger.Warning("[QoL] " + Loc.T("kill.tripped"));
        }

        public static void Restore()
        {
            Tripped = false;
            try { QolConfig.Master.Value = true; } catch { }
            QolLog.Info(Loc.T("kill.restored"));
        }
    }
}
