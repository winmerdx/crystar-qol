using System;

namespace CrystarQoL
{
    /// <summary>
    /// 倍率类补丁的通用重入闸门（泛型静态：每个 T 一份独立状态，互不干扰）。
    ///
    /// 【为什么必须有 —— v0.1.7 铁律】
    /// MelonLoader 0.5.7 + IL2CPP 的 Harmony 补丁，一次原生调用会让 Prefix/Postfix
    /// 链路实际执行两遍，第二遍读到的是第一遍改过的值。不设闸门的倍率会被平方：
    ///   1.5 倍实际 2.25 倍、2 倍实际 4 倍（v0.1.6 实锤日志：
    ///   IncreaseMoney 47 x2 -> 94 紧跟 94 x2 -> 188；rate 1 x2 -> 2 紧跟 2 x2 -> 4）。
    /// Combat 的 DamageHp 因为一开始就有 _busy 闸门一直正确，其余倍率全部中招。
    ///
    /// 【用法 A：Prefix 缩放参数型（exp / money）】
    ///   Prefix { if (!ReentryGate&lt;MyPatch&gt;.Begin()) return; ...缩放...; }
    ///   Postfix { ReentryGate&lt;MyPatch&gt;.End(); }   // 必须配套，否则后续调用被误判嵌套
    ///
    /// 【用法 B：Postfix 缩放返回值型（speed / killcount）】无法用 Begin/End
    ///   （没有前置 Prefix 标记链路起点），改用"输出回声去重"：第二遍 Postfix 的
    ///   输入正好是我们上一遍的输出 → 短时间窗口内跳过。见 FeatureMoveSpeed / FeatureKillCounter。
    /// </summary>
    internal static class ReentryGate<T>
    {
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static int _stamp;

        /// <summary>true = 本次链路首次进入（允许缩放）；false = 重复执行（必须跳过）。</summary>
        public static bool Begin()
        {
            // 上次进入后超过 2 秒还挂着，说明原始方法抛异常没走到 End —— 当脏标记清掉
            if (_busy && Environment.TickCount - _stamp > 2000) _busy = false;
            if (_busy) return false;
            _busy = true;
            _stamp = Environment.TickCount;
            return true;
        }

        /// <summary>在 Postfix 里调用，结束本次链路。</summary>
        public static void End()
        {
            _busy = false;
        }
    }
}
