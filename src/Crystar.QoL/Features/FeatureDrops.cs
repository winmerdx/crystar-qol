using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 道具掉落数量倍率 ×N + 掉率一键拉满。
    ///
    /// 【v0.1.9 挂载点选择：从"掉落"入手，不是从"入账"入手】
    /// 用户要求：倍率必须作用在**掉落生成**那一刻（默认掉 1 个就掉 N 个），
    /// 而不是等玩家捡起来再翻倍。用户上一轮的质疑成立——入账点 ItemManager.AddItem
    /// 有 9 个调用者（BuyItem / ItemCreation / Equip / OnConversation / CheckItem…），
    /// 挂上去会把商店和炼成一起放大。
    ///
    /// 反汇编出来的掉落生成链路（GameAssembly.dll）：
    ///   CharacterTask.OnDropItem                      实例 RVA 0x1CFAC0
    ///     ├─ ParameterManager.GetDropList(eCharaID)   实例 RVA 0x4DEC0（唯一调用者就是 OnDropItem）
    ///     │     判定式：mDropRate × 100 &gt; Random.Range(0, 10000)
    ///     │     （0x1CFDEF 内联直读 [rax+0x18] → patch get_DropRate() 无效，只能改数据）
    ///     └─ DropItemManager.CreateDropItem(ref pos, ref dir, ref DropInfo)  **静态** RVA 0xA7200
    ///           └─ 0xA7559 Reset() → 0xA7563~0xA7584 逐字段写入 mCharaID/mItemID/mDropType/mCount
    ///              → 0xA75B3 DropItem() → 0xA75CA DropItemManager.AddDropItem(component) 实例 RVA 0xA6FF0
    ///
    /// ⛔ CreateDropItem 是**静态方法且带参数** → 按项目铁律禁止 patch（隐藏首参 MethodInfo*
    ///    会污染成 0）。所以"在 DropInfo 构造那一刻改 count"做不到。
    /// ✅ 最近的合法点就是 DropItemManager.AddDropItem：
    ///      - 它的**唯一调用者就是 CreateDropItem**（callers.py 实锤，1 处）；
    ///      - 而 CreateDropItem 的调用者只有 CharacterTask.OnDropItem（3 处）与
    ///        破坏物 OnCollisionReceive（1 处）——**全都是"地图里掉出来的"**；
    ///      - 商店 / 炼成 / 装备 / 事件给物品根本不产生落地物，天然不在这一条路上。
    ///    进入 AddDropItem 时 mItemID / mDropType / mCount 已被 CreateDropItem 写完
    ///    （0xA7584 写 mCount，0xA75CA 才调 AddDropItem），改它即等于"掉落时就按倍率"。
    ///
    /// 【数量恒为 1 的实锤】OnDropItem 0x1CFCF1 `mov r12d, 1`，落地物分支与直接入包分支
    /// 的 count 都取自 r12d —— 掉率表 ConstDropParameter 根本没有 count 字段，
    /// 所以"数量倍率"只能由 mod 在生成后放大 mCount。
    ///
    /// 【mCount 的双重语义 —— v0.1.9 修订】DropItemComponent.GetItem(0xA66D0)：
    ///   `0xA66F8 mov edx,[rbx+0x138]`（mItemID）
    ///   `0xA6727 test edx,edx; 0xA6729 jne 0xA67C8`  → 非 0 走**道具**分支
    ///                                                （`0xA67C8 mov r9d,[rbx+0x140]` 当数量）
    ///   mItemID == 0 时：`0xA672F mov edx,[rbx+0x140]; 0xA6738 call 0x1B94D0`
    ///                                                → 走**金钱**分支，mCount 当**金额**
    /// 两者 mCount 含义不同，所以：
    ///   * 道具 → clamp 到 99（Global.MAX_ITEM_COUNT，超了整笔丢弃）；
    ///   * 金钱 → **不能**套 99，否则一次掉 800 的精神碎片会被砍成 99（反而变少）。
    ///
    /// 【两道倍率正交 —— 用户 2026-09-09 明确要求】
    /// 金钱**同时**受两道倍率：本文件的"掉落倍率"（掉多少）与 FeatureExpMoney 的
    /// "金钱获取倍率"（入账时再乘一次），二者**独立相乘**，是刻意设计：
    ///   实际到手 = 基础金额 × 掉落倍率 × 金钱获取倍率
    /// （挂载点不同：本处在 DropItemManager.AddDropItem，那处在 UserParameter.IncreaseMoney。）
    ///
    /// 【残留分支：直接入包】OnDropItem 0x1CFE71 判 byte[rbp+0x60]（来自
    /// 0x1CFBBE `movzx edi, byte[rdx+0xEE]`），为真时走 0x1CFF6F BattleManager.AddItem
    /// 直接进包，不产生落地物 → AddDropItem 覆盖不到。兜底：OnDropItem 前后置作用域标志，
    /// ItemManager.AddItem 仅在作用域内放大（作用域带 1 秒时效，防异常残留污染商店）。
    ///
    /// 【harden】ItemManager.eError 有 ERROR_OVER_ITEM_COUNT(4)，Global.MAX_ITEM_COUNT = 99
    /// → 乘完必须 clamp 到 99，否则不是截断而是**整笔丢弃**。
    /// </summary>
    public sealed class FeatureDrops : IFeature
    {
        internal static FeatureDrops Inst;

        public string Id { get { return "drops"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        /// <summary>单格道具上限（Global.MAX_ITEM_COUNT）。超过会导致 AddItem 直接报错丢弃。</summary>
        private const int MaxCount = 99;

        /// <summary>金额上限：只做整数溢出防御，金钱不受 99 的单格限制。</summary>
        private const int MaxMoney = 1000000000;

        private MelonPreferences_Entry<float> _countMult;
        private MelonPreferences_Entry<bool> _rateMax;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            var c = ctx.Cfg;
            _countMult = c.Float("DropCountMult", 1f, "Item drop count multiplier / 掉落数量倍率");
            _rateMax = c.Bool("DropRateMax", false, "Force every drop rate to 100% / 掉率一键拉满");

            // 每个补丁独立 try/catch：坏一个不拖垮其它（v0.1.4 纪律）
            Safe(ctx.Harmony, typeof(DropSpawnPatch), "DropItemManager.AddDropItem");
            Safe(ctx.Harmony, typeof(OnDropItemScope), "CharacterTask.OnDropItem（作用域标志）");
            PatchAddItem(ctx.Harmony);
            Safe(ctx.Harmony, typeof(DropRatePatch), "ParameterManager.GetDropList");
        }

        /// <summary>
        /// ItemManager.AddItem 的第二个参数是 ref eError，`typeof(T).MakeByRefType()`
        /// 不是编译期常量、不能写进 HarmonyPatch 特性 → 手工 Patch。
        /// </summary>
        private static void PatchAddItem(HarmonyLib.Harmony h)
        {
            try
            {
                var mi = AccessTools.Method(typeof(ItemManager), "AddItem",
                    new Type[] { typeof(eItemID), typeof(ItemManager.eError).MakeByRefType(), typeof(int) });
                if (mi == null)
                {
                    QolLog.Warn("drops: 找不到 ItemManager.AddItem(eItemID, ref eError, int)");
                    return;
                }
                h.Patch(mi,
                    new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(DirectBagPatch), "Prefix")),
                    new HarmonyLib.HarmonyMethod(AccessTools.Method(typeof(DirectBagPatch), "Postfix")));
            }
            catch (Exception e)
            {
                QolLog.Warn("drops: 挂 ItemManager.AddItem 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("drops: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        public void OnUpdate() { }

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // 换场景后 ParameterManager 可能重建，掉率表里被我们改过的对象引用就失效了
            // —— 清掉记录，下次 GetDropList 会重新按当前开关记录原值并应用。
            DropRates.Forget();
        }

        public void OnReset()
        {
            DropRates.Restore();
        }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Float(Loc.T("f.dropcount"), _countMult, 1f, 1f, 1f, MaxCount);
            yield return Item.Bool(Loc.T("f.droprate"), _rateMax, false);
        }

        // ---------------- 数值助手 ----------------

        private static float CountMult
        {
            get
            {
                var f = Inst;
                return f == null || f._countMult == null ? 1f : f._countMult.Value;
            }
        }

        private static bool RateMax
        {
            get
            {
                var f = Inst;
                return f != null && f._rateMax != null && f._rateMax.Value;
            }
        }

        /// <summary>按倍率放大数量，clamp 到 [1, 99]（99 是单格道具上限，超了会被整笔丢弃）。</summary>
        private static int ScaleCount(float mult, int value)
        {
            if (value <= 0 || mult <= 0f) return value;
            long v = (long)Math.Round(value * (double)mult);
            if (v > MaxCount) v = MaxCount;
            if (v < 1) v = 1;
            return (int)v;
        }

        /// <summary>
        /// 按倍率放大**金额**。金钱没有 99 的单格上限（入账是 UserParameter 的余额累加），
        /// 只做整数溢出防御；下限保 1。
        /// </summary>
        private static int ScaleMoney(float mult, int value)
        {
            if (value <= 0 || mult <= 0f) return value;
            double v = Math.Round(value * (double)mult);
            if (v > MaxMoney) v = MaxMoney;
            if (v < 1) v = 1;
            return (int)v;
        }

        /// <summary>
        /// 掉落结算作用域：OnDropItem 执行期间为 true。
        /// 用来让 ItemManager.AddItem 只在"这是击杀掉落直接入包"时才放大，
        /// 商店/炼成/装备不在 OnDropItem 里，自然被排除。
        /// 带 1 秒时效：万一某个分支抛异常导致 Postfix 没跑到，也不会永久污染。
        /// </summary>
        private static int _scopeDepth;
        private static int _scopeStamp = -99999;

        internal static void EnterDropScope()
        {
            int t = Environment.TickCount;
            if (t - _scopeStamp > 1000) _scopeDepth = 0;   // 脏标记自愈
            _scopeDepth++;
            _scopeStamp = t;
        }

        internal static void ExitDropScope()
        {
            if (_scopeDepth > 0) _scopeDepth--;
        }

        private static bool InDropScope
        {
            get { return _scopeDepth > 0 && Environment.TickCount - _scopeStamp < 1000; }
        }

        // ---------------- 补丁 1：落地物生成时改 mCount ----------------

        [HarmonyPatch(typeof(DropItemManager), "AddDropItem", new Type[] { typeof(DropItemComponent) })]
        private sealed class DropSpawnPatch
        {
            private static bool _live;

            private static void Prefix(DropItemComponent __0)
            {
                if (!ReentryGate<DropSpawnPatch>.Begin()) return;
                try
                {
                    var c = __0;
                    if (c == null) return;

                    int itemId;
                    try { itemId = (int)c.ItemID; }
                    catch { return; }

                    int type;
                    try { type = (int)c.DropType; }
                    catch { type = -1; }

                    // 判定严格对齐 GetItem 的分支条件（0xA6727 test mItemID）：
                    // mItemID == 0 → mCount 当**金额**，走 BattleManager.GetMoney。
                    // 不用 DropType==MONEY 作条件，否则万一出现"DropType 是 MONEY 但
                    // mItemID 非 0"的落地物，会被当金额无限放大、却在 GetItem 里
                    // 走道具分支塞进 AddItem → 超过 99 直接整笔丢弃。
                    // 金钱照样吃掉落倍率（与"金钱获取倍率"是两道独立的乘数），
                    // 但绝不能套用道具的 99 上限。
                    bool isMoney = itemId == 0;

                    float m = CountMult;
                    int before = c.Count;
                    if (before <= 0) return;
                    int after = isMoney ? ScaleMoney(m, before) : ScaleCount(m, before);
                    if (after == before) return;
                    c.Count = after;

                    if (!_live) { _live = true; QolLog.Info("drops: DropItemManager.AddDropItem hook live"); }
                    QolLog.Trace("drops", (isMoney ? "spawn money" : "spawn item=" + itemId)
                        + " (type=" + type + ") count " + before
                        + " x" + m.ToString("0.##") + " -> " + after);
                }
                catch { }
            }

            private static void Postfix() { ReentryGate<DropSpawnPatch>.End(); }
        }

        // ---------------- 补丁 2/3：直接入包分支兜底 ----------------

        [HarmonyPatch(typeof(CharacterTask), "OnDropItem")]
        private sealed class OnDropItemScope
        {
            private static bool _live;
            private static void Prefix()
            {
                try
                {
                    if (!_live) { _live = true; QolLog.Info("drops: CharacterTask.OnDropItem hook live"); }
                    EnterDropScope();
                }
                catch { }
            }

            private static void Postfix()
            {
                try { ExitDropScope(); } catch { }
            }
        }

        /// <summary>
        /// ItemManager.AddItem(eItemID, ref eError, int count) —— 9 个调用者，
        /// 所以**只在掉落结算作用域内**才放大 count。
        /// 落地物被捡起时走的是这个 AddItem，但那时早已出了 OnDropItem 作用域
        /// （作用域只在击杀结算那一次调用链内有效），所以不会和补丁 1 叠乘。
        ///
        /// 注：第二个参数是 ref，无法写成特性实参（MakeByRefType() 不是常量），
        /// 因此改在 Init 里手工 Patch（见 PatchAddItem）。
        /// </summary>
        private sealed class DirectBagPatch
        {
            private static bool _live;

            private static void Prefix(eItemID __0, ref ItemManager.eError __1, ref int __2)
            {
                if (!ReentryGate<DirectBagPatch>.Begin()) return;
                try
                {
                    if (!InDropScope) return;
                    if ((int)__0 == 0) return;   // 金钱不走道具入账
                    float m = CountMult;
                    int before = __2;
                    if (before <= 0) return;
                    int after = ScaleCount(m, before);
                    if (after == before) return;
                    __2 = after;

                    if (!_live) { _live = true; QolLog.Info("drops: ItemManager.AddItem (in-drop scope) hook live"); }
                    QolLog.Trace("drops", "direct-bag item=" + (int)__0 + " count " + before
                        + " x" + m.ToString("0.##") + " -> " + after);
                }
                catch { }
            }

            private static void Postfix() { ReentryGate<DirectBagPatch>.End(); }
        }

        // ---------------- 补丁 4：掉率一键拉满 ----------------

        [HarmonyPatch(typeof(ParameterManager), "GetDropList", new Type[] { typeof(eCharaID) })]
        private sealed class DropRatePatch
        {
            private static bool _live;

            /// <summary>
            /// ⚠️ `__result` 只能声明成 `object`。
            /// 本工程编译期引用的是 Il2CppDumper 的 **DummyDll**（那里 List 是
            /// `System.Collections.Generic.List&lt;T&gt;`），而运行时游戏里加载的是 unhollower 的
            /// `Il2CppSystem.Collections.Generic.List&lt;T&gt;` —— 两者不是同一个类型。
            /// 按 DummyDll 的精确类型声明会在运行时 castclass 失败；声明成 `object`
            /// 则任何返回类型都能安全赋值。列表内容改用反射访问（见 DropRates）。
            /// </summary>
            private static void Postfix(object __result)
            {
                try
                {
                    if (!_live) { _live = true; QolLog.Info("drops: ParameterManager.GetDropList hook live"); }
                    DropRates.Apply(__result, RateMax);
                }
                catch { }
            }
        }

        /// <summary>
        /// 掉率改写 + 原值记录/还原。
        ///
        /// ⚠️ 幂等是硬要求：GetDropList 内部用 FindAll 返回的是**主表同一批对象引用**，
        /// 同一对象会被反复返回。若"每次乘"会指数爆炸；改成"记录原值 + 每次都从原值算"，
        /// 无论被调用多少次结果都一致。一键拉满设成常量 100 更是天然幂等。
        ///
        /// 判定式是 mDropRate × 100 &gt; Random.Range(0, 10000)：100 → 10000 &gt; [0,9999] 恒真。
        /// </summary>
        private static class DropRates
        {
            private sealed class Rec
            {
                public ConstDropParameter Item;   // 主表里的对象引用，按引用比较
                public float Original;
            }

            private static readonly List<Rec> _recs = new List<Rec>();
            private static MemberInfo _member;
            private static bool _resolved;
            private static bool _failed;

            /// <summary>换场景后旧引用失效，清掉记录（不还原——对象可能已经没了）。</summary>
            public static void Forget()
            {
                _recs.Clear();
            }

            public static void Restore()
            {
                if (_recs.Count == 0) return;
                int n = 0;
                for (int i = 0; i < _recs.Count; i++)
                {
                    try { Write(_recs[i].Item, _recs[i].Original); n++; }
                    catch { }
                }
                _recs.Clear();
                QolLog.Info("drops: 掉率已还原 " + n + " 条");
            }

            public static void Apply(object list, bool wantMax)
            {
                if (!wantMax) { Restore(); return; }
                if (list == null) return;
                if (!Resolve(list)) return;

                int count = ListCount(list);
                if (count <= 0) return;

                for (int i = 0; i < count; i++)
                {
                    ConstDropParameter p;
                    try { p = ListAt(list, i) as ConstDropParameter; }
                    catch { break; }
                    if (p == null) continue;

                    try
                    {
                        float cur = Read(p);
                        if (!TryFind(p, out Rec rec))
                        {
                            rec = new Rec { Item = p, Original = cur };
                            _recs.Add(rec);
                        }
                        if (cur != 100f) Write(p, 100f);
                    }
                    catch { }
                }
            }

            private static bool TryFind(ConstDropParameter p, out Rec rec)
            {
                for (int i = 0; i < _recs.Count; i++)
                {
                    if (ReferenceEquals(_recs[i].Item, p)) { rec = _recs[i]; return true; }
                }
                rec = null;
                return false;
            }

            // ---- 成员解析：unhollower 可能把字段生成属性，也可能保留字段，两边都试 ----

            private static bool Resolve(object list)
            {
                if (_resolved) return true;
                if (_failed) return false;
                try
                {
                    // 拿一个真实实例来判定运行时类型（unhollower 代理类型与 DummyDll 不同）
                    object sample = null;
                    int n = ListCount(list);
                    for (int i = 0; i < n && sample == null; i++) sample = ListAt(list, i);
                    if (sample == null) return false;

                    _member = FindRate(sample.GetType());
                    if (_member == null)
                    {
                        _failed = true;
                        QolLog.Warn("drops: ConstDropParameter 找不到 mDropRate，成员 -> " + Dump(sample.GetType()));
                        return false;
                    }
                    _resolved = true;
                    QolLog.Info("drops: mDropRate 解析成功 -> " + _member.Name
                        + " (" + (_member is PropertyInfo ? "prop" : "field") + ")");
                    return true;
                }
                catch (Exception e)
                {
                    _failed = true;
                    QolLog.Warn("drops: mDropRate 解析异常 " + e.Message);
                    return false;
                }
            }

            private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            private static MemberInfo FindRate(Type t)
            {
                var p = t.GetProperty("mDropRate", BF);
                if (p != null && p.CanRead) return p;
                var f = t.GetField("mDropRate", BF);
                if (f != null) return f;
                foreach (var pp in t.GetProperties(BF))
                    if (pp.PropertyType == typeof(float) && pp.CanRead) return pp;
                foreach (var ff in t.GetFields(BF))
                    if (ff.FieldType == typeof(float)) return ff;
                return null;
            }

            private static string Dump(Type t)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var x in t.GetProperties(BF)) sb.Append("P:" + x.Name + "(" + x.PropertyType.Name + ") ");
                foreach (var x in t.GetFields(BF)) sb.Append("F:" + x.Name + "(" + x.FieldType.Name + ") ");
                return sb.ToString();
            }

            private static float Read(object obj)
            {
                var p = _member as PropertyInfo;
                if (p != null) return Convert.ToSingle(p.GetValue(obj, null));
                return Convert.ToSingle(((FieldInfo)_member).GetValue(obj));
            }

            private static void Write(object obj, float v)
            {
                var p = _member as PropertyInfo;
                if (p != null) { p.SetValue(obj, v, null); return; }
                ((FieldInfo)_member).SetValue(obj, v);
            }

            // ---- Il2Cpp 列表通用访问（Count + get_Item），不假设具体类型 ----

            private static int ListCount(object list)
            {
                Type t = list.GetType();
                var p = t.GetProperty("Count", BF);
                if (p != null && p.CanRead) return Convert.ToInt32(p.GetValue(list, null));
                var m = t.GetMethod("get_Count", BF, null, Type.EmptyTypes, null);
                if (m != null) return Convert.ToInt32(m.Invoke(list, null));
                return -1;
            }

            private static object ListAt(object list, int i)
            {
                Type t = list.GetType();
                var m = t.GetMethod("get_Item", BF, null, new Type[] { typeof(int) }, null);
                if (m != null) return m.Invoke(list, new object[] { i });
                var p = t.GetProperty("Item", typeof(int));
                if (p != null && p.CanRead) return p.GetValue(list, new object[] { i });
                return null;
            }
        }
    }
}
