using System;
using MelonLoader;

namespace CrystarQoL
{
    /// <summary>菜单项工厂：把配置条目包装成菜单项，统一风险等级与生效时机。</summary>
    public static class Item
    {
        public static MenuItem Bool(string label, MelonPreferences_Entry<bool> e, bool def,
            RiskTier risk = RiskTier.Safe, Effect effect = Effect.Immediate, bool needConfirm = false)
        {
            return new MenuItem
            {
                Label = label,
                Risk = risk,
                Effect = effect,
                NeedConfirm = needConfirm,
                ValueText = () => Loc.OnOff(e.Value),
                Adjust = d => { e.Value = !e.Value; },
                Confirm = () => e.Value = !e.Value,
                Reset = () => e.Value = def
            };
        }

        public static MenuItem Float(string label, MelonPreferences_Entry<float> e, float def,
            float step, float min, float max, Effect effect = Effect.Immediate,
            RiskTier risk = RiskTier.Safe, bool needConfirm = false)
        {
            return new MenuItem
            {
                Label = label,
                Risk = risk,
                Effect = effect,
                NeedConfirm = needConfirm,
                ValueText = () => "x" + e.Value.ToString("0.##"),
                Adjust = d =>
                {
                    float v = e.Value + (d > 0 ? step : -step);
                    if (v < min) v = min;
                    if (v > max) v = max;
                    e.Value = (float)Math.Round(v, 2);
                },
                Reset = () => e.Value = def
            };
        }

        public static MenuItem CycleInt(string label, MelonPreferences_Entry<int> e, int def,
            int min, int max, Func<string> text, Effect effect = Effect.Immediate)
        {
            return new MenuItem
            {
                Label = label,
                Effect = effect,
                ValueText = text,
                Adjust = d =>
                {
                    int v = e.Value + (d > 0 ? 1 : -1);
                    if (v > max) v = min;
                    if (v < min) v = max;
                    e.Value = v;
                },
                Reset = () => e.Value = def
            };
        }
    }
}
