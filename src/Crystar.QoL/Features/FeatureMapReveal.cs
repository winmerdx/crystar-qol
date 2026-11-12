using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Common;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 全图去迷雾（v4）。
    ///
    /// 前几版都卡在同一个地方：读/写 BitFlag 的**私有字段**时类型对不上。
    ///   日志实证：BitFlag 成员 -> P:mBitFlag(Il2CppStructArray`1) P:mFlagCount(UInt32)。
    ///   unhollower 把 IL2CPP 的 int[] 字段生成为 **Il2CppStructArray&lt;int&gt;** 属性，
    ///   不是托管 int[] —— 用 typeof(int[]) 匹配必然失败（v0.1.3/0.1.4 就这么失败的），
    ///   Reveal 从未真正执行 → 雾一直在。
    ///
    /// v4 纪律：
    ///   - 一个 BitFlag 方法都不调（GetData/SetBit/GetBit/Initialize 全可能被 IL2CPP 裁，
    ///     v0.1.2 的 MissingMethodException 就是这么来的，会把大地图绘制整个打断）。
    ///   - 拿到 mBitFlag 属性值后**不假设它的静态类型**：先试 int[]，不行就用反射
    ///     走 Length/Item 索引器（Il2CppStructArray&lt;int&gt; 同样暴露这两个成员），
    ///     原地逐元素改写 —— 数组是引用类型，改完即影响绘制，不依赖 setter 写回。
    ///
    /// 挂点（v0.1.2 起没变过，是正确的）：UserParameter.GetOpenMinimapFlag(eMapID)，
    /// 反汇编确认它是大地图 UIGameMainAllMapWindow.SetTexture 生成遮罩纹理的**唯一**
    /// BitFlag 来源。小地图是另一条预烘焙底图路径，不受本 feature 影响。
    ///
    /// bit 极性（1=已探索 还是 1=遮罩）从数据自推：原始置位率 &lt; 50% 认为 1=已探索
    /// → 填全 1；否则认为 1=遮罩 → 填全 0。猜错就在 UserData/Crystar.QoL/ 建
    /// `map.invert` 反转（每 3 秒最多探一次盘，不影响游戏内改文件生效）。
    ///
    /// 【v0.1.7 修复：极性自抵消】推断只认"自然状态"。一次开图 GetOpenMinimapFlag
    /// 会被连续调两次，第 2 次读到的是我们填过的 flag，重新推断会得到相反极性并把
    /// 上一次的填充原样撤销（实测 3ms 内 all-ones → all-zero）。现在极性带缓存，
    /// 见 _lastFill 注释。反汇编佐证：SetTexture(0xF10F0) 在 0xF1839 调
    /// GetOpenMinimapFlag 后把返回引用存进 mOpenFlag[0xF8]，0xF1AAB 的纹理生成循环
    /// 逐格调用 BitFlag.GetBit(0x3201F0) —— 数据链路本身是通的。
    /// </summary>
    public sealed class FeatureMapReveal : IFeature
    {
        internal static FeatureMapReveal Inst;

        public string Id { get { return "map"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        private MelonPreferences_Entry<bool> _enabled;

        // BitFlag 的数据成员。v0.1.5：不再按类型精确匹配，只按名字找
        // （mBitFlag -> 任意数组型；mFlagCount -> 整数）。
        private static MemberInfo _mData;
        private static MemberInfo _mCount;
        private static bool _fieldsResolved;
        private static bool _fieldsFailed;

        // 极性反转标记（每 3 秒最多探一次磁盘）
        private static bool _invert;
        private static int _invertStamp = -99999;

        // v0.1.7：极性缓存。一次开图 GetOpenMinimapFlag 会被连续调用两次（实测相隔 3ms），
        // 第 2 次读到的是我们第 1 次填过的 flag —— 若重新推断极性，全 1 会被判成
        // "1=遮罩"再填回全 0，把揭示效果原样撤销（v0.1.6 去雾无效的直接原因，
        // 日志实锤：#1 fill=all-ones 紧跟 #2 set@start=65536 fill=all-zero）。
        private static int _lastFill;
        private static bool _hasFill;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            _enabled = ctx.Cfg.Bool("RevealAll", false, "Reveal whole field map / 全图去迷雾");
            ctx.Harmony.PatchAll(typeof(FlagPatch));
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }
        public void OnReset() { }   // 命中时返回改写后的对象，从不写存档，无副作用需要撤销

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Bool(Loc.T("f.map"), _enabled, false,
                RiskTier.Safe, Effect.ReopenWindow, false);
        }

        private static bool Invert
        {
            get
            {
                try
                {
                    int t = Environment.TickCount;
                    if (t - _invertStamp > 3000)
                    {
                        _invertStamp = t;
                        _invert = File.Exists(Path.Combine(QolConfig.Dir, "map.invert"));
                    }
                }
                catch { }
                return _invert;
            }
        }

        private static object GetVal(MemberInfo m, object obj)
        {
            var p = m as PropertyInfo;
            if (p != null) return p.GetValue(obj, null);
            return ((FieldInfo)m).GetValue(obj);
        }

        private static bool IsArrayLike(Type t)
        {
            if (t == null) return false;
            if (t.IsArray) return true;
            string n = t.Name;
            return n.IndexOf("Array", StringComparison.Ordinal) >= 0
                || n.IndexOf("List", StringComparison.Ordinal) >= 0;
        }

        /// <summary>按名字找属性/字段（优先属性），不卡死类型 —— 运行时类型不可信。</summary>
        private static MemberInfo FindMember(Type t, string name, bool wantArrayish, bool wantInt)
        {
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var p in t.GetProperties(bf))
            {
                if (p.Name == name && p.CanRead)
                {
                    if (wantArrayish && IsArrayLike(p.PropertyType)) return p;
                    if (wantInt && (p.PropertyType == typeof(int) || p.PropertyType == typeof(uint))) return p;
                }
            }
            foreach (var f in t.GetFields(bf))
            {
                if (f.Name == name) return f;
            }
            // 兜底：名字对不上也按类型找（防 unhollower 改名）
            foreach (var p in t.GetProperties(bf))
            {
                if (!p.CanRead) continue;
                if (wantArrayish && IsArrayLike(p.PropertyType)) return p;
                if (wantInt && (p.PropertyType == typeof(int) || p.PropertyType == typeof(uint))) return p;
            }
            return null;
        }

        private static bool ResolveFields()
        {
            if (_fieldsResolved) return true;
            if (_fieldsFailed) return false;
            try
            {
                _mData = FindMember(typeof(BitFlag), "mBitFlag", true, false);
                _mCount = FindMember(typeof(BitFlag), "mFlagCount", false, true);

                if (_mData != null)
                {
                    _fieldsResolved = true;
                    QolLog.Info(string.Format("map: BitFlag 成员解析成功 data={0}({1}) count={2}",
                        _mData.Name, _mData is PropertyInfo ? "prop" : "field",
                        _mCount == null ? "null" : _mCount.Name));
                    return true;
                }

                _fieldsFailed = true;
                var sb = new System.Text.StringBuilder();
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var x in typeof(BitFlag).GetProperties(bf)) sb.Append("P:" + x.Name + "(" + x.PropertyType.Name + ") ");
                foreach (var x in typeof(BitFlag).GetFields(bf)) sb.Append("F:" + x.Name + "(" + x.FieldType.Name + ") ");
                QolLog.Warn("map: BitFlag 成员解析失败。实际成员 -> " + sb);
            }
            catch (Exception e)
            {
                _fieldsFailed = true;
                QolLog.Warn("map: 成员反射异常 " + e.Message);
            }
            return false;
        }

        private static int PopCount(uint v)
        {
            v = v - ((v >> 1) & 0x55555555u);
            v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
            v = (v + (v >> 4)) & 0x0F0F0F0Fu;
            return (int)((v * 0x01010101u) >> 24);
        }

        // ---- 对"任意类数组对象"（int[] / Il2CppStructArray<int>）的统一访问器 ----
        private static Func<object, long> _lengthOf;
        private static Func<object, int, object> _indexerGet;
        private static Func<object, int, object, bool> _indexerSet;
        private static bool _rwReady;
        private static bool _rwFailed;

        private static bool EnsureAccessors(object data)
        {
            if (_rwReady) return true;
            if (_rwFailed) return false;
            try
            {
                Type t = data.GetType();
                if (data is Array)
                {
                    _lengthOf = o => ((Array)o).LongLength;
                    _indexerGet = (o, i) => ((Array)o).GetValue(i);
                    _indexerSet = (o, i, v) => { ((Array)o).SetValue(v, i); return true; };
                }
                else
                {
                    var lenP = t.GetProperty("Length") ?? t.GetProperty("Count");
                    if (lenP == null) throw new MissingMemberException("no Length/Count on " + t.FullName);
                    _lengthOf = o => Convert.ToInt64(lenP.GetValue(o, null));

                    // Il2CppStructArray<T> 有 T this[int]，反射名是 "Item"
                    var idx = t.GetProperty("Item", typeof(int));
                    if (idx == null)
                    {
                        foreach (var p in t.GetProperties())
                        {
                            var prms = p.GetIndexParameters();
                            if (prms.Length == 1 && prms[0].ParameterType == typeof(int))
                            { idx = p; break; }
                        }
                    }
                    if (idx == null) throw new MissingMemberException("no int indexer on " + t.FullName);
                    MethodInfo g = idx.GetGetMethod();
                    MethodInfo s = idx.GetSetMethod();
                    if (g == null) throw new MissingMemberException("indexer not readable on " + t.FullName);
                    _indexerGet = (o, i) => g.Invoke(o, new object[] { i });
                    if (s != null) _indexerSet = (o, i, v) => { s.Invoke(o, new object[] { i, v }); return true; };
                }
                _rwReady = true;
                QolLog.Info("map: 数组访问器就绪 type=" + t.Name
                    + (data is Array ? " (managed array)" : " (reflected indexer)"));
            }
            catch (Exception e)
            {
                _rwFailed = true;
                QolLog.Warn("map: 数组访问器初始化失败 " + e.Message);
            }
            return _rwReady;
        }

        /// <summary>把整块 bit 全填成 1 或 0。绝不调用 BitFlag 的任何方法。</summary>
        internal static void Reveal(BitFlag flag)
        {
            if (flag == null) return;
            if (!ResolveFields()) return;

            object data = null;
            try { data = GetVal(_mData, flag); }
            catch (Exception e) { QolLog.Warn("map: 读 mBitFlag 失败 " + e.Message); _fieldsFailed = true; return; }

            if (data == null) return;
            if (!EnsureAccessors(data)) return;

            long totalBits;
            try { totalBits = _lengthOf(data); }
            catch (Exception e) { QolLog.Warn("map: 读 Length 失败 " + e.Message); return; }
            if (totalBits <= 0) return;
            totalBits *= 32L; // int 数组：每元素 32 bit

            // 上限：mFlagCount 若存在且更小，用它（避免把有效格子数之外的 bit 也置位）
            if (_mCount != null)
            {
                try
                {
                    long c = Convert.ToInt64(GetVal(_mCount, flag));
                    if (c > 0 && c <= totalBits) totalBits = c;
                }
                catch { }
            }

            // 原始置位统计 → 推极性（最多看前 4096 个 int，防异常输入）
            long set = 0;
            int nInt = (int)System.Math.Min((totalBits + 31L) / 32L, 4096L);
            for (int i = 0; i < nInt; i++)
            {
                object v;
                try { v = _indexerGet(data, i); }
                catch { break; }
                try { set += PopCount(unchecked((uint)Convert.ToInt64(v))); }
                catch { }
            }

            // 极性判定（v0.1.7 重写）：
            // 若当前置位分布正好是我们上一次的填充结果（全 1 或全 0），
            // 说明读到了自己改过的 flag —— 沿用上次极性，绝不重新推断。
            // 否则才从自然状态推断：原始置位率 < 50% 认为 1=已探索 → 填全 1；
            // 否则认为 1=遮罩 → 填全 0。猜错仍可用 map.invert 反转。
            long countedBits = (long)nInt * 32L;
            int fill;
            bool cachedPolarity;
            if (_hasFill && ((_lastFill == -1 && set >= countedBits) || (_lastFill == 0 && set == 0)))
            {
                fill = _lastFill;
                cachedPolarity = true;
            }
            else
            {
                bool onesMeanOpen = set * 2 < totalBits;
                if (Invert) onesMeanOpen = !onesMeanOpen;
                fill = onesMeanOpen ? -1 : 0; // 0xFFFFFFFF / 0x00000000
                _lastFill = fill;
                _hasFill = true;
                cachedPolarity = false;
            }

            int wrote = 0;
            for (int i = 0; i < nInt; i++)
            {
                try
                {
                    if (_indexerSet == null) break;
                    if (!_indexerSet(data, i, fill)) break;
                    wrote++;
                }
                catch { break; }
            }

            // 原地改数组已生效；再尝试把属性写回（unhollower 若返回副本则这步兜底，失败无害）
            try { SetVal(_mData, flag, data); }
            catch (Exception e) { QolLog.Trace("map", "setter 写回跳过: " + e.Message); }

            QolLog.Trace("map", string.Format(
                "reveal: ints={0} type={1} bits={2} set@start={3} -> 1={4}, wrote={5}, fill={6}{7}",
                nInt, data.GetType().Name, totalBits, set, fill == -1 ? "open" : "mask",
                wrote, fill == -1 ? "all-ones" : "all-zero",
                cachedPolarity ? " (polarity cached)" : " (polarity inferred)"));
        }

        private static void SetVal(MemberInfo m, object obj, object v)
        {
            var p = m as PropertyInfo;
            if (p != null) { p.SetValue(obj, v, null); return; }
            ((FieldInfo)m).SetValue(obj, v);
        }

        [HarmonyPatch(typeof(UserParameter), "GetOpenMinimapFlag")]
        private static class FlagPatch
        {
            private static bool _hitOnce;
            private static void Postfix(ref BitFlag __result)
            {
                // 这里一旦抛异常就会打断 UIGameMainAllMapWindow.SetTexture，
                // 地图整个画不出来 —— 所以最外层必须吞干净，且 catch 里不再调任何游戏 API。
                try
                {
                    var f = Inst;
                    if (f == null || f._enabled == null || !f._enabled.Value)
                    {
                        // 诊断：证明钩子本身活着、只是开关没开（否则日志里什么都没有，
                        // 无法区分"没打到钩子"和"忘了开开关"——v0.1.5 实测吃过这个亏）
                        if (!_hitOnce)
                        {
                            _hitOnce = true;
                            QolLog.Info("map: GetOpenMinimapFlag 命中，但 RevealAll=关闭（去迷雾未生效属正常）");
                        }
                        return;
                    }
                    Reveal(__result);
                }
                catch { }
            }
        }
    }
}
