using System;
using System.Collections.Generic;

namespace CrystarQoL
{
    /// <summary>Safe = 随便开；Risky = 可能卡流程；Irreversible = 写 Steam / 改存档，不可逆。</summary>
    public enum RiskTier
    {
        Safe = 0,
        Risky = 1,
        Irreversible = 2
    }

    /// <summary>配置改动什么时候生效。UI 上直接标角标——"改了没反应"是最常被当 bug 报的问题。</summary>
    public enum Effect
    {
        Immediate = 0,
        ReopenWindow = 1,
        RestartGame = 2
    }

    public sealed class MenuItem
    {
        public string Label;
        public string Hint;
        public Func<string> ValueText;

        /// <summary>左右调整，delta 为 +1 / -1。</summary>
        public Action<int> Adjust;

        public Action Confirm;
        public Action Reset;

        public Effect Effect = Effect.Immediate;
        public RiskTier Risk = RiskTier.Safe;

        /// <summary>确认时需要二次确认（长按 / 再按一次）。不可逆项必须开。</summary>
        public bool NeedConfirm;
    }

    /// <summary>功能上下文：只暴露 Harmony 实例与配置登记器，功能之间互不可见。</summary>
    public sealed class FeatureContext
    {
        public HarmonyLib.Harmony Harmony;
        public ConfigRegistrar Cfg;

        /// <summary>功能需要"按键学习"时用它（比如绑定手柄组合）。</summary>
        public Menu Menu;
    }

    public interface IFeature
    {
        string Id { get; }
        RiskTier Risk { get; }

        /// <summary>注册配置 + 挂补丁。</summary>
        void Init(FeatureContext ctx);

        void OnUpdate();
        void OnSceneLoaded(int buildIndex, string sceneName);

        /// <summary>
        /// 真正撤销运行时副作用——不只是把配置值改回去。
        /// 一个"每帧强制写 GameSpeed"的功能，光把配置改回 1 没用，只要 Update 还在跑下一帧又写回去。
        /// </summary>
        void OnReset();

        IEnumerable<MenuItem> BuildMenu();
    }
}
