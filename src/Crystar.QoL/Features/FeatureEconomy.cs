using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Game;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// v0.2 批次 2：经济类三件套 —— C2 商店全解锁 / C6 金钱直写 / C7 任意物品生成。
    ///
    /// 三者都不需要 Harmony 补丁：全部走"取单例 → 调游戏自己的实例方法 / 直写表字段"，
    /// 因此对 10.8 铁律（静态方法禁 patch）天然免疫。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// C2 商店全解锁 —— ⛔ v0.2.3 的"改表"方向是错的（v0.2.4 修正）
    /// ─────────────────────────────────────────────────────────────────────
    /// v0.2.3 以为 `ShopManager.GetShopLineupList()` 0x166620（static，禁 patch）→ 谓词
    /// `<GetShopLineupList>m__0` 0x166730 → `GameManager.CheckScenarioProgress` 是商店的过滤链，
    /// 于是去改 `ConstShopParameter` 的进度门槛字段。实机结果：**完全无效**。原因：
    ///   • `GetShopLineupList()` 的 `callers.py` 扫描结果是 **0 个调用者**（死代码）；
    ///   • 真过滤在 `UIShopItemListWindow.&lt;InitializeUI&gt;c__AnonStorey0.&lt;&gt;m__0`(0x16E870)，
    ///     而且它用的判定是 `0x2D3D0`（`start&lt;=0` 不设下限 / `end&lt;=0` 直接放行 /
    ///     否则 `start &lt;= progress &lt; end`）—— 改表在语义上本该有效；
    ///   • 但商品列表 `mShopParamList`(0x138) 只在 `InitializeUI` 构建一次并被缓存，
    ///     之后每次开窗（`OpenWindow` 0x18D130）**不重建列表** → 改表看不见。
    ///
    /// ✅ v0.2.4：`UIShopItemListWindow.OpenWindow` Postfix 里把 `mShopParamList` 换成全量
    ///   再 `UpdateContentDataList()` 重建。
    ///
    /// ⛔⛔ v0.2.5/v0.2.6 仍然**完全无效** —— v0.2.7 找到真根因：**DummyDll 的 List ≠ 运行期的 List**。
    ///   v0.2.5 把 `GetShopList()` 换成字段直读（对，因为该方法运行期会抛 MissingMethodException），
    ///   但仍写 `raw as List&lt;ConstShopParameter&gt;`：编译期 `System.Collections.Generic.List&lt;T&gt;`，
    ///   运行期 `Il2CppSystem.Collections.Generic.List&lt;T&gt;` ⇒ `as` 恒 null。
    ///   实机日志（2026-09-12 14:13:19）：
    ///     `shop.paramtype: ScriptableShopParameter.param 类型不符：
    ///      Il2CppSystem.Collections.Generic.List`1[[Game.ConstShopParameter,...]]（反射 Count=27）`
    ///   —— 表拿得到（27 条），却被类型转换判死；`ForceFullList` 还想把托管 List 写进
    ///   游戏对象字段，同样对不上。⇒ **C2 从 v0.2.3 到 v0.2.6 一次都没真正跑到过改表那一步。**
    ///
    /// ✅ v0.2.7：整条链路改 **object 接收 + 反射枚举**（同 FeatureAlbum / FeatureDrops 的既有姿势）：
    ///   • 读表：`ShopTable()` 返回 object，`CountOf/ItemOf` 反射遍历；
    ///   • 改表：条目字段内存直读/直写（`mItemID` 0x10 / `mStartProgress` 0x14 / `mEndProgress` 0x18）；
    ///   • 刷列表：**就地 ListClear + ListAdd 窗口自己那个 Il2Cpp 列表对象**（不 new 托管 List）。
    ///
    /// 【重放与判据】开启时每 2 秒校验一次表（读回 start/end，与目标不符才写，写前备份原值）。
    ///   ⚠ 备份只在"用户主动关开关"时写回（那时对象必然还活着）；
    ///     **切场景只清空备份、不写回**——表都要重载了，写回旧指针反而有悬垂风险。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// C6 金钱直写
    /// ─────────────────────────────────────────────────────────────────────
    /// `UserParameter.SetMoney(int)` 0x1ED4F0（实例）反汇编只有 5 条有效指令：
    ///   `test edx,edx; js 退出`  ← ⚠ **负数静默不生效**（不是夹到 0，是直接不写）
    ///   `eax = 0x5F5E0FF`（99999999 = Global.MAX_MONEY，dump:152133）
    ///   `cmp edx,eax; cmovg edx,eax` ← 超上限自动夹
    ///   `mov [rcx+0x128], edx; ret`  ← 写 `mMoney`（dump:173946 偏移 0x128）
    ///   → **不刷 UI**。HUD 上的数字由 `UIGameMainWindow.UpdateCountInfo()` 0xEDF90 刷新，
    ///     而它只有 3 个调用者（UpdateAllInfo / UpdateInfo / DiscardItemPostProcess），
    ///     **不是每帧调用** → 改完钱必须自己调一次，否则数字要等下次开关菜单才变。
    ///     实例路径：`StellaUIManager.Instance.GameMainWindow`（dump:190721，public 属性）。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// C7 任意物品生成
    /// ─────────────────────────────────────────────────────────────────────
    /// `ItemManager.AddItem(eItemID, ref eError, int count = 1)`（实例，dump 里 ItemManager
    /// : SingletonMonoBehaviour&lt;ItemManager&gt;）→ 直接调，不需要反射。
    ///   • `eItemID` 有效范围 **1..411**（0=INVALID，412=MAX，dump:169676 起）
    ///   • `eError`：0 NONE / 1 ILLEGAL_COUNT / 2 NOT_FOUND_PARAM / 3 OVER_COUNT /
    ///     4 OVER_ITEM_COUNT / 5 ILLEGAL_ITEM_TYPE / 6 NO_ITEM（dump:163947）
    ///     已确认数量 4（超背包 99）会**整笔丢弃**，所以数量先夹到 99。
    ///   • 物品名用 `ParameterManager.GetItemName(eItemID)`（0x?，dump:171847）运行时取，
    ///     不走本地化键 → 自动是当前语言，**不会出现营地页那种缺字形变 □ 的问题**。
    ///     结果做缓存（菜单每帧渲染 ValueText，不能每次都跨托管调用）。
    /// </summary>
    public sealed class FeatureEconomy : IFeature
    {
        internal static FeatureEconomy Inst;

        public string Id { get { return "economy"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        // ---- C2 常量 ----
        private const int ShopItemIdOffset = 0x10;   // ConstShopParameter.mItemID（eItemID，4 字节）
        private const int ShopStartOffset = 0x14;
        private const int ShopEndOffset = 0x18;
        /// <summary>把 mEndProgress 顶到的值。剧情进度远小于此，足以让任何商品都在售。</summary>
        private const int ShopEndOpen = 999999;
        private const int ShopReapplyMs = 2000;

        // ---- C6 常量 ----
        /// <summary>Global.MAX_MONEY（dump:152133）。SetMoney 内部也会夹，这里先夹一次只为回读校验能对齐。</summary>
        private const int MaxMoney = 99999999;

        // ---- C7 常量 ----
        internal const int MinItemId = 1;
        internal const int MaxItemId = 411;      // eItemID.MAX = 412（不含）
        internal const int MaxItemCount = 99;    // 背包上限，超过会被整笔丢弃

        private MelonPreferences_Entry<bool> _shopUnlock;
        private MelonPreferences_Entry<int> _spawnId;
        private MelonPreferences_Entry<int> _spawnCount;
        private MelonPreferences_Entry<int> _idStep;

        private bool _shopWasOn;
        private int _lastShopApplyAt;
        private bool _shopWarned;
        /// <summary>r22：上一次"真的写了表"才打日志的时刻（30 秒节流），见 ApplyShopUnlock。</summary>
        private int _lastShopWriteLogAt;

        private readonly Dictionary<IntPtr, int[]> _shopBackup = new Dictionary<IntPtr, int[]>();
        private readonly Dictionary<int, string> _nameCache = new Dictionary<int, string>();

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            var c = ctx.Cfg;
            _shopUnlock = c.Bool("ShopUnlockAll", false, "Unlock all shop items / 商店全解锁");
            _spawnId = c.Int("SpawnItemId", 1, "Item id to spawn / 生成物品 ID");
            _spawnCount = c.Int("SpawnItemCount", 1, "Item count to spawn / 生成数量");
            _idStep = c.Int("SpawnIdStep", 10, "Step when browsing item id / 物品ID 步进");
            // C2 真正生效的挂点（v0.2.4）：商店列表在 InitializeUI 里过滤一次就被缓存，
            // 所以必须在"列表诞生时"（InitializeUI）与"每次开窗时"（OpenWindow）两处补全量。
            // 两处都幂等（件数相等即 return），谁先谁后都安全。
            Safe(ctx.Harmony, typeof(ShopInitPatch), "UIShopItemListWindow.InitializeUI");
            Safe(ctx.Harmony, typeof(ShopWindowPatch), "UIShopItemListWindow.OpenWindow");
            // v0.2.5（第 8 条）：缓存底部 HUD 的 footer 实例，金钱直写后主动刷数字。
            // 反汇编实锤 `UIFooterContent.UpdateCountInfo()`(0xEDF90) 结尾是
            // `jmp 0xEE4C0`（尾调用 `UpdateMoneyCount`）→ 调它就能把精神碎片数字刷新。
            // 之前只靠"在 StellaUIManager 字段里找 footer"，实机证明找不到（且日志在 Debug 级，看不见）。
            Safe(ctx.Harmony, typeof(FooterCachePatch), "UIFooterContent.UpdateCountInfo（缓存 footer 实例）");
            Safe(ctx.Harmony, typeof(FooterCachePatch2), "UIFooterContent.OnUpdate（缓存 footer 实例）");
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("economy: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // 营地「物品生成」子页面用的读写口（v0.2.4）
        // ------------------------------------------------------------------

        internal static int SpawnId
        {
            get { var f = Inst; return f != null && f._spawnId != null ? f._spawnId.Value : 1; }
            set { var f = Inst; if (f != null && f._spawnId != null) f._spawnId.Value = value; }
        }

        internal static int SpawnCount
        {
            get { var f = Inst; return f != null && f._spawnCount != null ? f._spawnCount.Value : 1; }
            set { var f = Inst; if (f != null && f._spawnCount != null) f._spawnCount.Value = value; }
        }

        internal static int IdStep
        {
            get { var f = Inst; return f != null && f._idStep != null ? f._idStep.Value : 10; }
        }

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        public void OnUpdate()
        {
            // C2 开启时周期性校验：回标题/读档重载表后要重新套用。
            // ⚠ 这是"重放"，所以必须有判据——读回字段，与目标不符才写。
            bool on = _shopUnlock != null && _shopUnlock.Value;
            if (on)
            {
                int now = Environment.TickCount;
                if (now - _lastShopApplyAt >= ShopReapplyMs)
                {
                    _lastShopApplyAt = now;
                    ApplyShopUnlock(true);
                }
            }
            else if (_shopWasOn)
            {
                // 开关跳变 → 真正撤销（⚠ IFeature.OnReset() 全项目无调用点，
                // 不能只写在 OnReset 里，见 v0.2.1 的教训）
                _shopWasOn = false;
                ApplyShopUnlock(false);
            }
            if (on) _shopWasOn = true;
        }

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // 表会随场景重载：备份作废，但**不写回**旧指针（对象可能已销毁）。
            // 重载后的表本来就是原始值，下一轮 ApplyShopUnlock(true) 会重新套用并重新备份。
            _shopBackup.Clear();
            _lastShopApplyAt = 0;
        }

        public void OnReset()
        {
            // ⚠ 无调用点（2026-09-11 grep 核实），真正撤销在 OnUpdate 的开关跳变处。
            // 这里保留一份实现，万一以后 ModMain 接上了也不会漏。
            ApplyShopUnlock(false);
        }

        // ------------------------------------------------------------------
        // C2 商店全解锁
        // ------------------------------------------------------------------

        /// <summary>
        /// 取商店**全量表** `List&lt;ConstShopParameter&gt;`（不经过任何进度过滤）—— 返回 **object**。
        ///
        /// ⛔ v0.2.5：**不要再调 `ParameterManager.GetShopList()`** —— 实机日志实锤它在运行时抛
        ///   `MissingMethodException: ... Game.ParameterManager.GetShopList()`
        ///   （2026-09-12 03:42:13 / 03:43:00，每次开商店都失败）。见第十节铁律
        ///   "DummyDll 里有 ≠ 运行时能调用"。
        ///
        /// ⛔⛔ v0.2.7 真根因：**不能用编译期类型接这个列表**。
        ///   v0.2.5/v0.2.6 写的是 `object raw = GetMember(sp,"param"); var list = raw as List&lt;ConstShopParameter&gt;;`
        ///   —— 本工程编译期引用 Il2CppDumper 的 DummyDll，那里的 `List&lt;T&gt;` 是
        ///   `System.Collections.Generic.List&lt;T&gt;`；运行期 `ScriptableShopParameter.param` 实际是
        ///   `Il2CppSystem.Collections.Generic.List&lt;Game.ConstShopParameter&gt;` ⇒ `as` **恒为 null**。
        ///   实机日志实锤（2026-09-12 14:13:19，v0.2.6）：
        ///     `shop.paramtype: ScriptableShopParameter.param 类型不符：
        ///      Il2CppSystem.Collections.Generic.List`1[[Game.ConstShopParameter, ...]]（反射 Count=27）`
        ///   —— 表**是拿得到的**（27 条），只是被类型转换判掉了。这正是 v0.2.4/v0.2.5「C2 无效」的根因
        ///   （改表那一步从未真正执行过）。
        ///
        /// ✅ v0.2.7：整条链路一律 **object 接收 + 反射枚举**（本项目在 FeatureAlbum / FeatureDrops 里
        ///   已验证过的唯一安全姿势）。字段偏移：
        ///   `ParameterManager.mShopParameter` **0xB8**（dump:171514，private）
        ///     → `ScriptableShopParameter.param` **0x18**（dump:173577，**public**）
        ///
        /// 【顺便澄清"每层的商店经营范围不同"】不是的：`ConstShopParameter` 只有
        ///   `mItemID`(0x10) / `mStartProgress`(0x14) / `mEndProgress`(0x18) 三个字段，
        ///   过滤谓词读的就是**每条商品自己的进度区间**（`start&lt;=0` 不设下限、`end&lt;=0` 直接放行、
        ///   否则 `start &lt;= 进度 &lt; end`）。所以"一张全量表 + 逐条目进度门槛"就是全部逻辑。
        /// </summary>
        private static object ShopTable()
        {
            var pm = ParameterManager.Instance;
            if (pm == null) { WarnOnce("shop.pm", "ParameterManager.Instance 为空"); return null; }

            object sp = GetMember(pm, "mShopParameter");
            if (sp == null)
            {
                WarnOnce("shop.field", "读 ParameterManager.mShopParameter(0xB8) 失败");
                return null;
            }

            object raw = GetMember(sp, "param");
            if (raw == null)
            {
                WarnOnce("shop.param", "读 ScriptableShopParameter.param(0x18) 失败");
                return null;
            }

            int n = CountOf(raw);
            if (n <= 0) { WarnOnce("shop.empty2", "ScriptableShopParameter.param 存在但为空（Count=" + n + "）"); return null; }
            return raw;

            // 2) ⛔⛔ v0.2.6：**彻底删掉 `GetShopList()` 兜底调用**（全项目不再有任何引用）。
            //
            // 2026-09-12 实机日志实锤：只要本方法体里**存在**对 `ParameterManager.GetShopList()`
            // （运行期没有方法体/未生成 stub）的调用，整个方法就可能在**入口**就抛
            // `MissingMethodException: ... Game.ParameterManager.GetShopList()`：
            //   • 日志里只有 `shop: 套用失败 -> MissingMethodException: …GetShopList()`（每 2 秒一条，335 条）；
            //   • 而字段直读路径的诊断**一条都没有** → 连 `GetMember(pm,"mShopParameter")` 那步都没跑到。
            // ⇒ 结论：**"引用了运行期缺失的方法"会毒死整个宿主方法**，连不碰它的字段兜底路径一起废。
            //   必须把引用整段删除（try/catch 包住也没用）。静态 IL2CPP 方法在运行期会被裁剪。
        }

        /// <summary>
        /// 套用 / 撤销商店解锁。on=true 时逐个把门槛改成 (0, OPEN_END)；
        /// on=false 时把备份的原值写回。返回实际改动的商品数，-1 = 取不到表。
        ///
        /// v0.2.7：列表走 **object + 反射枚举**（见 ShopTable 注释），条目字段全部**内存直读/直写**
        /// （`mItemID` 0x10 / `mStartProgress` 0x14 / `mEndProgress` 0x18），完全不碰托管类型。
        /// </summary>
        internal static int ApplyShopUnlock(bool on)
        {
            var f = Inst;
            if (f == null) return -1;
            try
            {
                object list = ShopTable();
                if (list == null)
                {
                    if (!f._shopWarned) { f._shopWarned = true; QolLog.Warn("shop: 取不到商店全量表"); }
                    return -1;
                }

                int total = CountOf(list);
                int n = 0;
                for (int i = 0; i < total; i++)
                {
                    try
                    {
                        object p = ItemOf(list, i);
                        if (p == null) continue;
                        var ptr = NativePtr.Of(p);
                        if (ptr == IntPtr.Zero) continue;

                        int start = Marshal.ReadInt32(IntPtr.Add(ptr, ShopStartOffset));
                        int end = Marshal.ReadInt32(IntPtr.Add(ptr, ShopEndOffset));

                        if (on)
                        {
                            // 判据：已经是目标值就什么都不做（幂等，避免每 2 秒重复写）
                            if (start == 0 && end == ShopEndOpen) continue;
                            if (!f._shopBackup.ContainsKey(ptr))
                                f._shopBackup[ptr] = new[] { start, end };
                            Marshal.WriteInt32(IntPtr.Add(ptr, ShopStartOffset), 0);
                            Marshal.WriteInt32(IntPtr.Add(ptr, ShopEndOffset), ShopEndOpen);
                            n++;
                        }
                        else
                        {
                            int[] orig;
                            if (!f._shopBackup.TryGetValue(ptr, out orig)) continue;
                            Marshal.WriteInt32(IntPtr.Add(ptr, ShopStartOffset), orig[0]);
                            Marshal.WriteInt32(IntPtr.Add(ptr, ShopEndOffset), orig[1]);
                            n++;
                        }
                    }
                    catch { }
                }

                // r22（清噪音）：这条重放每 2 秒跑一轮，而**绝大多数轮次是 n=0**
                // （全部条目已经是 (0, OPEN_END)，幂等命中，什么都不写）。
                // 旧代码无条件 Trace ⇒ 一场游戏刷出 130+ 行"解锁 0 / 27 件商品"，全是噪音，
                // 还会把真正的异常（Warn）淹掉。
                // 现在：只有"真的发生写入"（首次开启 / 读档或切场景后表重载）才打日志，
                // 并按 30 秒节流；n=0 属正常稳态 ⇒ 静默。
                // ⚠ 失败路径（取不到表 / 抛异常）另有 WarnOnce 与 Warn，不受影响。
                if (on)
                {
                    if (n > 0)
                    {
                        int nowMs = Environment.TickCount;
                        if (f._lastShopWriteLogAt == 0 || unchecked(nowMs - f._lastShopWriteLogAt) >= 30000)
                        {
                            f._lastShopWriteLogAt = nowMs;
                            QolLog.Info("shop: 已把 " + n + " / " + total
                                + " 件商品的进度门槛改成全解锁（之后每 2s 幂等校验，无变化不落盘）");
                        }
                    }
                }
                else { QolLog.Info("shop: 已还原 " + n + " 件商品的进度门槛"); f._shopBackup.Clear(); }
                return n;
            }
            catch (Exception e)
            {
                QolLog.Warn("shop: " + (on ? "套用" : "还原") + "失败 -> " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        // ------------------------------------------------------------------
        // C2（v0.2.4 真正生效的路径）：开窗时把商品列表补成全量
        // ------------------------------------------------------------------

        /// <summary>
        /// 商店每次开窗 → 把 `mShopParamList`(0x138) 换成"全量表"并重建行。
        ///
        /// 【为什么 v0.2.3 改表没用（实机 + 反汇编）】
        ///  - `ShopManager.GetShopLineupList()`(0x166620) 用谓词过滤进度 —— 但 `callers.py`
        ///    扫出它 **0 个调用者**，是死代码（v0.2.3 就是照着它做的，方向错了）；
        ///  - 真正的过滤是编译器生成的
        ///    `UIShopItemListWindow.&lt;InitializeUI&gt;c__AnonStorey0.&lt;&gt;m__0`(0x16E870)：
        ///      `start = x.mStartProgress(0x14)`、`end = x.mEndProgress(0x18)`，
        ///      再调 `0x2D3D0(gameManager, 捕获的进度, start, end)`；其语义（6 条指令）：
        ///        `start <= 0` → 不设下限；`end <= 0` → 直接放行；否则 `start <= progress < end`
        ///      ⇒ 所以"把 start 写 0、end 写 999999"在语义上**本该**放行全部；
        ///  - 但 `mShopParamList` 只在 `InitializeUI` 里构建**一次**，之后每次开窗
        ///    （`OpenWindow` 0x18D130 只做演出/详情刷新）都复用这份缓存
        ///    → 表改了，界面不动。**这才是"改了没反应"的真正原因。**
        ///
        /// ⇒ v0.2.4 改挂 `UIShopItemListWindow.OpenWindow` 的 **Postfix**：开启时把
        ///   `mShopParamList` 直接换成 `ParameterManager.GetShopList()` 的全量（只收
        ///   `ItemID != 0` 的有效项），再调 `UpdateContentDataList()`（0x18D400，反汇编确认
        ///   它就是按 `[this+0x138]` 逐条造行）重建列表。改表逻辑保留作兜底。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(UIShopItemListWindow), "OpenWindow", new Type[] { })]
        private static class ShopWindowPatch
        {
            private static void Postfix(UIShopItemListWindow __instance)
            {
                try
                {
                    var f = Inst;
                    if (f == null || __instance == null) return;
                    if (f._shopUnlock == null || !f._shopUnlock.Value)
                    {
                        // ★ v0.2.4：开关关掉也要能收回副作用。
                        // mShopParamList 只在 InitializeUI 构建一次，关掉开关后不会自动复原，
                        // 必须在开窗时把之前记下的原版列表写回去。
                        RestoreVanillaList(__instance);
                        return;
                    }

                    ApplyShopUnlock(true);          // 兜底：表也解锁（幂等，已在目标值就跳过）
                    ForceFullList(__instance, true);    // true = 换完还要重建行（窗口已在显示）
                }
                catch (Exception e)
                {
                    QolLog.Warn("shop: 开窗补全列表失败 -> " + e.GetType().Name + ": " + e.Message);
                }
            }

            /// <summary>
            /// 第一次覆盖前记下来的**原版（按进度过滤后的）条目快照**。
            ///
            /// ⛔ v0.2.7：**必须存"条目快照"而不是"原列表对象"**。
            ///   因为补全量用的是"就地清空 + 回填窗口自己的 Il2Cpp 列表对象"（这样类型天然正确），
            ///   若同时又把那个列表对象当"原版"留着，它早就被我们改成全量了 → 关开关时无法复原。
            ///   快照里的元素是**游戏类 `ConstShopParameter` 的代理**，类型映射正确，可安全回填。
            /// </summary>
            internal static List<object> _vanilla;

            /// <summary>
            /// 开关关闭 → 把 <see cref="_vanilla"/> 快照回填进窗口并重建行。
            /// 幂等：当前件数已等于原版件数就不做任何事。
            /// </summary>
            private static void RestoreVanillaList(UIShopItemListWindow win)
            {
                if (_vanilla == null || win == null) return;
                try
                {
                    object winList = GetMember(win, "mShopParamList");
                    if (winList == null) return;
                    if (CountOf(winList) == _vanilla.Count) return;   // 已经是原版

                    if (!ListClear(winList))
                    {
                        WarnOnce("shop.restore", "恢复原版商品列表失败（清空 mShopParamList 失败）");
                        return;
                    }
                    MethodInfo add = FindAddMethod(winList);
                    int back = 0;
                    for (int i = 0; i < _vanilla.Count; i++)
                        if (_vanilla[i] != null && ListAdd(add, winList, _vanilla[i])) back++;

                    win.UpdateContentDataList();
                    try { win.UpdateShowComponent(); } catch { }
                    QolLog.Info("shop: 已恢复原版商品列表（" + back + " 件）");
                }
                catch (Exception e)
                {
                    QolLog.Warn("shop: 恢复原版列表失败 -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // v0.2.4 冗余覆盖：InitializeUI 的过滤结果写入点
        //
        // 反汇编 `UIShopItemListWindow.InitializeUI` 0x18C5B0：
        //   0x18C745 call 0x8D45B0         ; LINQ Where(闭包谓词 <>m__0).ToList()
        //   0x18C74A mov [rdi+0x138], rax  ; ← mShopParamList 的**唯一**写入点
        //   0x18C790 mov rcx,[rdi+0x138]   ; 紧接着的循环按它逐条建数据
        // ⇒ 这里是"过滤后的列表"诞生的地方。Postfix 换成全量后，
        //   游戏自己接着建的就会是全量行。
        //
        // ⛔ 这里**刻意只换列表、不调 UpdateContentDataList()**：InitializeUI 的时序
        //   不保证窗口 / 滚动控制器已就绪，此时重建行有"建一半"的风险。
        //   "换 + 重建"由 OpenWindow 的 Postfix 负责。两者都幂等，谁先谁后都安全。
        // ------------------------------------------------------------------
        [HarmonyLib.HarmonyPatch(typeof(UIShopItemListWindow), "InitializeUI", new Type[] { })]
        private static class ShopInitPatch
        {
            private static void Postfix(UIShopItemListWindow __instance)
            {
                try
                {
                    var f = Inst;
                    if (f == null || __instance == null) return;
                    if (f._shopUnlock == null || !f._shopUnlock.Value) return;
                    ForceFullList(__instance, false);
                }
                catch (Exception e)
                {
                    QolLog.Warn("shop: InitializeUI 换全量列表失败 -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// 把窗口的商品列表补成全量。<paramref name="rebuild"/>=true 时同时重建行。
        ///
        /// v0.2.7 关键改动：**不再 `SetMember(win,"mShopParamList", 托管 List)`**。
        ///   旧写法把一个 `System.Collections.Generic.List&lt;ConstShopParameter&gt;` 塞进运行期
        ///   类型为 `Il2CppSystem...List&lt;ConstShopParameter&gt;` 的字段 → 类型不符（就算侥幸不抛，
        ///   游戏随后按它造行也会错）。现在改为**就地操作窗口自己那个列表对象**：
        ///     ① `ListClear` 清空（反射调 `Clear()`）；
        ///     ② 逐条 `ListAdd` 全量表里的**同一批 `ConstShopParameter` 对象**（反射调 `Add(object)`）。
        ///   元素类型两边都是游戏类 → 天然安全；列表对象本身是游戏建的 → 类型/身份都正确。
        ///
        /// 原版快照：第一次覆盖前把"当前的（过滤后）条目"抄下来（见 _vanilla 注释），
        /// 关掉开关时由 RestoreVanillaList 回填。
        /// </summary>
        private static int ForceFullList(UIShopItemListWindow win, bool rebuild)
        {
            try
            {
                object full = ShopTable();          // object：绝不用编译期 List<T> 接（DummyDll≠运行时）
                if (full == null) { WarnOnce("shop.list", "取不到商店全量表（mShopParameter/param 直读失败）"); return -1; }

                int fullN = CountOf(full);
                object winList = GetMember(win, "mShopParamList");
                if (winList == null) { WarnOnce("shop.winlist", "读 UIShopItemListWindow.mShopParamList(0x138) 失败"); return -1; }

                // 已经是全量就不再重建（避免每次开窗都白刷一遍 UI）
                int curCount = CountOf(winList);

                // 收集有效条目（ItemID != 0）。ItemID 走**内存直读**（0x10），全程不碰托管类型。
                var keep = new List<object>();
                for (int i = 0; i < fullN; i++)
                {
                    try
                    {
                        object p = ItemOf(full, i);
                        if (p == null) continue;
                        IntPtr ptr = NativePtr.Of(p);
                        if (ptr == IntPtr.Zero) continue;
                        if (Marshal.ReadInt32(IntPtr.Add(ptr, ShopItemIdOffset)) == 0) continue;   // eItemID.INVALID
                        keep.Add(p);
                    }
                    catch { }
                }
                if (keep.Count == 0) { WarnOnce("shop.empty", "全量表里没有有效商品（ItemID!=0 共 0 条）"); return -1; }
                if (curCount == keep.Count && curCount > 0) return curCount;

                // ★ 覆盖前先抄下"原版（当前）条目"，供关掉开关时复原
                if (curCount != keep.Count && ShopWindowPatch._vanilla == null && curCount > 0)
                {
                    var snap = new List<object>();
                    for (int i = 0; i < curCount; i++)
                    {
                        object o = ItemOf(winList, i);
                        if (o != null) snap.Add(o);
                    }
                    if (snap.Count > 0) ShopWindowPatch._vanilla = snap;
                }

                // ★ 就地清空 + 回填（复用窗口自己的 Il2Cpp 列表对象）
                //   先解析出 Add 方法再清空：万一反射拿不到 Add，宁可不动列表，也不能清成空表。
                MethodInfo add = FindAddMethod(winList);
                if (add == null) { WarnOnce("shop.add", "mShopParamList 的 Add 反射不到 —— 放弃（未改动列表）"); return -1; }
                if (!ListClear(winList)) { WarnOnce("shop.set", "清空 mShopParamList 失败（Clear 反射不到）"); return -1; }
                int added = 0;
                for (int i = 0; i < keep.Count; i++)
                    if (ListAdd(add, winList, keep[i])) added++;

                if (added != keep.Count)
                {
                    WarnOnce("shop.addpartial", "回填只成功 " + added + "/" + keep.Count + " 件（列表可能不全）");
                    return -1;
                }

                if (rebuild)
                {
                    win.UpdateContentDataList();               // 0x18D400：按新列表重建行
                    try { win.UpdateShowComponent(); } catch { }
                }

                QolLog.Info("shop: 已把商店列表补成全量（原列表 " + curCount + " 件 → 现 "
                            + added + " 件，表内共 " + fullN + "）"
                            + (rebuild ? "" : "（InitializeUI 阶段，行由游戏自己建）"));
                return added;
            }
            catch (Exception e)
            {
                QolLog.Warn("shop: 全量列表失败 -> " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        // ------------------------------------------------------------------
        // 反射列表操作（**一律 object**：DummyDll 的 System.List ≠ 运行期的 Il2CppSystem.List）
        // ------------------------------------------------------------------

        /// <summary>反射读任意 Il2Cpp/托管列表的 Count（失败返回 0）。</summary>
        private static int CountOf(object list)
        {
            if (list == null) return 0;
            try
            {
                var mi = list.GetType().GetMethod("get_Count", Type.EmptyTypes);
                if (mi == null) return 0;
                object v = mi.Invoke(list, null);
                return v is int ? (int)v : 0;
            }
            catch { return 0; }
        }

        /// <summary>反射取列表第 i 项（返回 object）。</summary>
        private static object ItemOf(object list, int i)
        {
            if (list == null) return null;
            try
            {
                var mi = list.GetType().GetMethod("get_Item", new Type[] { typeof(int) });
                return mi == null ? null : mi.Invoke(list, new object[] { i });
            }
            catch { return null; }
        }

        private static bool ListClear(object list)
        {
            try
            {
                var mi = list.GetType().GetMethod("Clear", Type.EmptyTypes);
                if (mi == null) return false;
                mi.Invoke(list, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 取列表的**单参 `Add`**（不按参数类型匹配：unhollower 代理类型与 DummyDll 声明类型
        /// 不一定完全一致，按类型查会漏）。找不到返回 null。
        /// </summary>
        private static MethodInfo FindAddMethod(object list)
        {
            if (list == null) return null;
            try
            {
                var methods = list.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    var m = methods[i];
                    if (m.Name == "Add" && m.GetParameters().Length == 1) return m;
                }
            }
            catch { }
            return null;
        }

        private static bool ListAdd(MethodInfo add, object list, object item)
        {
            if (add == null || list == null || item == null) return false;
            try
            {
                add.Invoke(list, new object[] { item });
                return true;
            }
            catch { return false; }
        }

        private static readonly HashSet<string> _warnedKeys = new HashSet<string>();
        private static void WarnOnce(string key, string msg)
        {
            if (_warnedKeys.Contains(key)) return;
            _warnedKeys.Add(key);
            QolLog.Warn(key + ": " + msg);
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanRead) { try { return p.GetValue(obj, null); } catch { } }
            var fd = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fd != null) { try { return fd.GetValue(obj); } catch { } }
            return null;
        }

        private static bool SetMember(object obj, string name, object value)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            try
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite) { p.SetValue(obj, value, null); return true; }
            }
            catch { }
            try
            {
                var fd = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fd != null) { fd.SetValue(obj, value); return true; }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------
        // C6 金钱
        // ------------------------------------------------------------------

        /// <summary>直写金钱。返回 false = 没生效（取不到 UserParameter 或回读不符）。</summary>
        internal static bool SetMoney(int value)
        {
            try
            {
                var pm = ParameterManager.Instance;
                var up = pm == null ? null : pm.UserParameter;
                if (up == null)
                {
                    QolLog.Warn("money.set: UserParameter 取不到");
                    return false;
                }
                if (value < 0) value = 0;
                if (value > MaxMoney) value = MaxMoney;

                up.SetMoney(value);

                // 铁律：调用成功 ≠ 生效，必须回读校验
                int after = -1;
                try { after = up.Money; } catch { }
                QolLog.Info("money.set: -> " + value + "（回读 " + after + "）");

                RefreshHud();
                return after == value;
            }
            catch (Exception e)
            {
                QolLog.Warn("money.set: 失败 -> " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>在当前金额上加减（夹到 0..MAX）。</summary>
        internal static bool AddMoney(int delta)
        {
            try
            {
                var pm = ParameterManager.Instance;
                var up = pm == null ? null : pm.UserParameter;
                if (up == null) return false;
                int cur = up.Money;
                long v = (long)cur + delta;
                if (v < 0) v = 0;
                if (v > MaxMoney) v = MaxMoney;
                return SetMoney((int)v);
            }
            catch (Exception e)
            {
                QolLog.Warn("money.add: 失败 -> " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// v0.2.5：底部 HUD（精神碎片计数）实例缓存。
        /// `UIFooterContent` 不是单例、也没有静态入口，之前"在 StellaUIManager 字段里翻"
        /// 实机证明翻不到（日志还是 Debug 级，静默失败）。改挂它自己的方法抓 __instance。
        /// </summary>
        private static UIFooterContent _footer;

        /// <summary>缓存 footer 实例（UpdateCountInfo 每次刷新 HUD 都会被调用）。</summary>
        [HarmonyLib.HarmonyPatch(typeof(UIFooterContent), "UpdateCountInfo")]
        private static class FooterCachePatch
        {
            private static void Postfix(UIFooterContent __instance) { _footer = __instance; }
        }

        /// <summary>缓存 footer 实例（OnUpdate 每帧，兜底）。</summary>
        [HarmonyLib.HarmonyPatch(typeof(UIFooterContent), "OnUpdate")]
        private static class FooterCachePatch2
        {
            private static void Postfix(UIFooterContent __instance) { _footer = __instance; }
        }

        /// <summary>
        /// 尽力刷新 HUD 数字。
        ///
        /// `UserParameter.SetMoney` 反汇编只有 5 条指令，**只写字段不刷 UI**；
        /// 而真正刷新金钱文本的是 `UIFooterContent.UpdateCountInfo()`（0xEDF90）——
        /// 它的结尾就是 `jmp 0xEE4C0`（**尾调用 `UpdateMoneyCount`**，反汇编实锤），
        /// 所以调它一次就会把精神碎片数字刷新（用户第 8 条："点一下返回按钮才显示满"，
        /// 就是因为只有返回那条路径才会走到 UpdateAllInfo→UpdateCountInfo）。
        ///
        /// v0.2.5：**先用手上缓存的实例**（FooterCachePatch 抓的），再退回字段搜索；
        /// 两条都不成时按 Info 级打一条日志（旧版是 Debug 级 → 看不见，白丢一轮实测）。
        /// </summary>
        private static void RefreshHud()
        {
            try
            {
                if (_footer != null)
                {
                    try
                    {
                        _footer.UpdateCountInfo();
                        return;
                    }
                    catch (Exception e)
                    {
                        QolLog.Warn("money.hud: 缓存 footer 刷新失败（转字段搜索）-> " + e.Message);
                        _footer = null;
                    }
                }

                var mgr = StellaUIManager.Instance;
                if (mgr == null)
                {
                    QolLog.DebugThrottled("money.hud", "StellaUIManager.Instance 为空，HUD 暂不刷新");
                    return;
                }
                var t = mgr.GetType();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!typeof(UIFooterContent).IsAssignableFrom(f.FieldType)) continue;
                    var footer = f.GetValue(mgr) as UIFooterContent;
                    if (footer == null) continue;
                    _footer = footer;
                    footer.UpdateCountInfo();
                    return;
                }
                if (!_hudWarned)
                {
                    _hudWarned = true;
                    QolLog.Warn("money.hud: 两种方式都拿不到 UIFooterContent —— HUD 数字要等打开一次菜单才变"
                                + "（footer 实例会在 UpdateCountInfo/OnUpdate 时被缓存）");
                }
            }
            catch (Exception e)
            {
                QolLog.DebugThrottled("money.hud", "刷新 HUD 失败: " + e.Message);
            }
        }

        private static bool _hudWarned;

        // ------------------------------------------------------------------
        // C7 物品生成
        // ------------------------------------------------------------------

        /// <summary>物品显示名（运行时向游戏查表，自动跟随当前语言）。查不到返回 null。</summary>
        internal static string ItemName(int id)
        {
            var f = Inst;
            if (f == null) return null;
            string name;
            if (f._nameCache.TryGetValue(id, out name)) return name;
            name = null;
            try
            {
                var pm = ParameterManager.Instance;
                if (pm != null)
                {
                    string s = pm.GetItemName((eItemID)id);
                    if (!string.IsNullOrEmpty(s)) name = s;
                }
            }
            catch { }
            f._nameCache[id] = name;
            return name;
        }

        // 生成结果码（<0 = 我们自己的判定失败，>=0 直接是 ItemManager.eError 的值）
        internal const int SpawnOk = 0;            // = eError.ERROR_NONE
        internal const int SpawnNoInstance = -1;   // ItemManager.Instance 取不到
        internal const int SpawnBadId = -2;        // ID 越界
        internal const int SpawnException = -3;    // 调用抛异常

        /// <summary>
        /// 生成物品。返回错误码（SpawnOk=0 表示成功）。
        /// ⚠ **故意不返回显示文本**：营地页必须用 Disp()（繁体表）否则变 □，
        ///   F2 面板用 Loc.T()。所以这里只给码，文本由各自调用方渲染。
        /// </summary>
        internal static int SpawnItem(int id, int count)
        {
            if (id < MinItemId || id > MaxItemId) return SpawnBadId;
            if (count <= 0) count = 1;
            if (count > MaxItemCount) count = MaxItemCount;   // 超 99 会被整笔丢弃
            try
            {
                var im = ItemManager.Instance;
                if (im == null)
                {
                    QolLog.Warn("item.spawn: ItemManager.Instance 为空");
                    return SpawnNoInstance;
                }

                var err = ItemManager.eError.ERROR_NONE;
                im.AddItem((eItemID)id, ref err, count);

                if (err != ItemManager.eError.ERROR_NONE)
                {
                    QolLog.Warn("item.spawn: id=" + id + " x" + count + " -> eError=" + err
                                + "(" + (int)err + ")");
                    return (int)err;
                }
                QolLog.Info("item.spawn: id=" + id + " x" + count + " -> OK");
                return SpawnOk;
            }
            catch (Exception e)
            {
                QolLog.Warn("item.spawn: 失败 -> " + e.GetType().Name + ": " + e.Message);
                return SpawnException;
            }
        }

        /// <summary>错误码 → Loc key（成功/失败/越界/具体错误）。</summary>
        internal static string SpawnResultKey(int code)
        {
            if (code == SpawnOk) return "f.item.done";
            if (code == SpawnBadId) return "f.item.badid";
            if (code == SpawnNoInstance || code == SpawnException) return "f.item.fail";
            return "f.item.err";
        }

        /// <summary>F2 面板用的结果文本。</summary>
        private static string SpawnResultText(int code)
        {
            string t = Loc.T(SpawnResultKey(code));
            return code > 0 ? t + " " + code : t;
        }

        /// <summary>
        /// 营地页入口：用 F2 面板里选好的 ID / 数量执行生成。
        /// 返回 **Loc key**（不是文本）——营地页必须走 Disp() 拿繁体表。
        /// </summary>
        internal static string SpawnFromCamp()
        {
            var f = Inst;
            if (f == null) return "f.item.fail";
            int id = f._spawnId != null ? f._spawnId.Value : 1;
            int n = f._spawnCount != null ? f._spawnCount.Value : 1;
            return SpawnResultKey(SpawnItem(id, n));
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ------------------------------------------------------------------
        // 菜单（F2 自绘面板）
        // ------------------------------------------------------------------

        public IEnumerable<MenuItem> BuildMenu()
        {
            // ── C2 ──
            yield return Item.Bool(Loc.T("f.shopunlock"), _shopUnlock, false,
                RiskTier.Safe, Effect.ReopenWindow);

            // ── C6 ──
            yield return new MenuItem
            {
                Label = Loc.T("f.money.full"),
                ValueText = () => Loc.T("f.money.full.hint"),
                Confirm = () => SetMoney(MaxMoney),
                Effect = Effect.Immediate
            };
            yield return new MenuItem
            {
                Label = Loc.T("f.money.add"),
                ValueText = () => "+" + MoneyAddStep,
                Confirm = () => AddMoney(MoneyAddStep),
                Adjust = d => { MoneyAddStep = d > 0 ? MoneyAddStep * 10 : MoneyAddStep / 10; },
                Effect = Effect.Immediate,
                Reset = () => MoneyAddStep = 100000
            };

            // ── C7 ──
            yield return new MenuItem
            {
                Label = Loc.T("f.item.id"),
                ValueText = () =>
                {
                    string n = ItemName(_spawnId.Value);
                    return n != null ? (n + " #" + _spawnId.Value) : ("#" + _spawnId.Value);
                },
                Adjust = d =>
                {
                    int st = _idStep.Value;
                    if (st <= 0) st = 1;
                    _spawnId.Value = Clamp(_spawnId.Value + d * st, MinItemId, MaxItemId);
                },
                Reset = () => _spawnId.Value = 1
            };
            yield return new MenuItem
            {
                Label = Loc.T("f.item.step"),
                ValueText = () => "±" + _idStep.Value,
                Adjust = d =>
                {
                    int[] opts = { 1, 10, 50 };
                    int i = Array.IndexOf(opts, _idStep.Value);
                    if (i < 0) i = 1;
                    i = (i + (d > 0 ? 1 : -1) + opts.Length) % opts.Length;
                    _idStep.Value = opts[i];
                },
                Reset = () => _idStep.Value = 10
            };
            yield return new MenuItem
            {
                Label = Loc.T("f.item.count"),
                ValueText = () => "x" + _spawnCount.Value,
                Adjust = d => _spawnCount.Value = Clamp(_spawnCount.Value + d, 1, MaxItemCount),
                Reset = () => _spawnCount.Value = 1
            };
            yield return new MenuItem
            {
                Label = Loc.T("f.item.spawn"),
                ValueText = () => LastSpawnResult != null ? LastSpawnResult : Loc.T("f.item.spawn.hint"),
                Confirm = () => LastSpawnResult = SpawnResultText(SpawnItem(_spawnId.Value, _spawnCount.Value)),
                Effect = Effect.Immediate,
                Reset = () => LastSpawnResult = null
            };
        }

        /// <summary>「金钱+N」的档位（1000 / 1万 / 10万 / 100万 循环）。</summary>
        private int _moneyStep = 100000;
        private int MoneyAddStep
        {
            get { return _moneyStep; }
            set { _moneyStep = Clamp(value, 1000, 10000000); }
        }

        /// <summary>生成结果（显示在「生成」项右侧）。</summary>
        private static string LastSpawnResult;
    }
}
