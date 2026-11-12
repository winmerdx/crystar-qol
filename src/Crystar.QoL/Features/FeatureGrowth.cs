using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Game;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// v0.2 批次 3「养成深化」：C1 强化零消耗 / C3 炼成无材料 / C4 全技能解锁。
    ///
    /// ════════════════════════════════════════════════════════════════════
    /// 取证结论（2026-09-12，反汇编实锤，RVA 见 docs/反编译代码索引文档.md）
    /// ════════════════════════════════════════════════════════════════════
    ///
    /// 【扣费汇聚点——都是 static，⛔ 禁 patch（铁律 10.8）】
    ///   • 强化：`ItemManager.ItemEnhancement(HaveItemInfo, HaveItemInfo)` static 0x39CC0
    ///     `callers.py` 扫出**唯一**调用者 = UI 包装器
    ///     `UIRoomReadingEnhancementListWindow.ItemEnhancement(consumeItemInfo)`（实例 0x174970，dump:199913）。
    ///     函数体流程（0x39CC0~0x39FF0）：
    ///       ① `CheckItemEnhancement(ConstEnhancementParameter)`(0x37810) 闸门：
    ///          材料 1..4 逐条 `CheckEnoughItem(ID, num)`(0x37410) + 最后 `price &lt;= Money`(0x1ED7F0 get_Money)
    ///       ② 材料 1..4 中 ID≠0 且 ≠ 选定堆 ID 的 → `SubItem(eItemID,int)`(0x3A750) 逐条扣
    ///       ③ `UserParameter.ReduceMoney(price)`(0x1ED230)
    ///       ④ 选定的同名素材堆 → `SubItem(uint uniqueID, 1)`(0x3A570) 扣 1 个
    ///          （前置 `haveItemInfo.mItemID == consumeItemInfo.mItemID`，不等直接返回 null——
    ///           即强化素材＝**同名装备**，这是游戏设计，不是 bug）
    ///       ⑤ `haveItemInfo.mItemID = get_AfterItemID()`(0x1875E0) 原地变身 + SetItemGetFlag
    ///   • 炼成：`ItemManager.ItemCreation(HaveItemInfo, int createCount)` static 0x393B0，
    ///     唯一调用者 = UI 包装器 `UIRoomReadingCreationWindow.ItemCreation()`（实例 0x172460，dump:199805）。
    ///     体内唯一扣料点 = 0x39ADD 的 `SubItem(eItemID,int)`（扣输入物品 × createCount）；
    ///     产出走 AddItem（⇒ 抑制窗口绝不能碰 AddItem，只拦 SubItem）。
    ///   • 思装变容（Grant，能力付与）：`ItemManager.ItemAbilityGranted(HaveItemInfo, int slot)`
    ///     static 0x38D90 —— v0.2.9 漏掉的**第三条扣料路径**（用户实测"思装变容有物品消耗"）。
    ///     唯一调用者 = 确认弹窗回调 `UIRoomGrantSelectEquipmentWindow.<OpenCheckWindow>m__2(int)`
    ///     （实例 0x1AE330，callers.py 0x1AE3AE 一处）；体内 4 处 SubItem(eItemID,int)
    ///     （0x38EE2/0x38F1F/0x38F5C/0x38F99），无金钱扣点；前置闸 CheckItemAbilityGranted 0x37590。
    ///     ⚠ v0.2.10 修正二：**编译器生成方法运行期不可挂**——Harmony 找不到 `<OpenCheckWindow>m__2`
    ///     （debug.log 实锤 `Patching exception in method null`）⇒ 改挂 ItemAbilityGranted static 本体
    ///     （零参 Prefix 置旗 + Finalizer 清旗，不注入参数 ⇒ 不触 10.8 的 MethodInfo* 错位）。
    ///     ⛔ 修正六（r7）：挂 static 本体同样不行（参数封送期 NRE，见 Init 的 Latest.log 实锤）。
    ///     ✅ 修正八（r9）：**它的材料量本来就来自一张表**——反汇编 0x38E04~0x38F99 实锤，
    ///     4 处扣料全部是 `SubItem(grantParam.MaterialItemIDn, grantParam.MaterialItemNumn)`，
    ///     而 `grantParam` 来自 `ParameterManager.GetGrantParameter(haveItemInfo.mItemID)`(0x4EC50)
    ///     ⇒ 改表即可，**与强化完全同款**（不再需要任何扣料期干预）。
    ///     游戏读书房菜单枚举只有 3 项（UIRoomReadingMenuWindow.eMenu）：
    ///     CREATION=0（游戏内中文＝"净化"，净化幽怨出产物）/ ENHANCEMENT=1（强化）/
    ///     GRANT_ABILITY=2（游戏内中文＝"思装变容"）——本类 C3 开关现同时罩 净化+思装变容。
    ///
    /// 【实现形态】双层：
    ///   ① 表层（C1 强化 + C3 思装变容）：改配方表 ⇒ **显示与判定同步变免费**，扣料根本不会发生。
    ///      • C1：`ConstEnhancementParameter`（168 条）`mPrice`(0x18)=0、`mMaterialItemNum1..4`(0x20/0x28/0x30/0x38)=0。
    ///      • C3-a：`ConstGrantParameter` `mMaterialItemNum1..4`(0x18/0x20/0x28/0x30)=0。
    ///        链：`ParameterManager.mGrantParameter`(0x98) → `ScriptableGrantParameter.param`(0x18)
    ///        → `List<ConstGrantParameter>`。游戏体内 4 处扣料是 `if (IDn != 0) SubItem(IDn, Numn)`
    ///        ⇒ Numn=0 时 SubItem 自己的 `test ebx,ebx / jle`（0x3A782，实锤）直接返回，
    ///        而 `CheckItemAbilityGranted` 的 `CheckEnoughItem(IDn, 0)` 恒真 ⇒ 闸门放行、分文不扣。
    ///      ⚠ 表是主表同一批对象引用 → **记录原值 + 可还原**（A2c/C2 同款约束）；
    ///      ⚠ 回标题/读档可能重灌表 → OnUpdate 每 2s 重放（读回不符才写，幂等）。
    ///   ② 扣除层（C1 的"同名素材堆" + C3-b 净化）：**抑制窗口** —— 挂两个 UI 包装器
    ///      （都是实例方法，可 patch）：Prefix 置 `SuppressSubItem=true`，Finalizer 清零；
    ///      再挂 `ItemManager.SubItem` 两个**实例**重载的 Prefix，窗口亮时跳过原方法返回 ERROR_NONE。
    ///      ⇒ C1 连"必选的同名素材装备"也不消耗（这一笔**不在表里**，只能靠窗口拦）；
    ///        C3-b 净化**无表可改**（`ConstCreationParameter` 只有 mItemID/mCreationItemID/mWeight/mStartProgress，
    ///        成本＝你自己投进去的那个幽怨物品 × createCount，见 0x39ADD 实锤）⇒ 只能靠窗口拦。
    ///      产出（变身后的装备 / 炼成结果）**照常入包**——只拦扣，不拦给。
    ///
    /// ════════════════════════════════════════════════════════════════════
    /// 【净化为什么必须配"幽怨槽池扩容"】（2026-09-13 取证，修正九 / r10）
    /// ════════════════════════════════════════════════════════════════════
    ///   用户实测：净化后 UI 不刷新、且"不能再次净化"；并担心
    ///   「幽怨有 10 个上限，净化若不消耗幽怨，后期只会剩早期幽怨 ⇒ 拿不到强力装备」。
    ///   二进制结论 —— **担忧成立**：
    ///   • 幽怨 = `eItemType.DEATHCRY(7)`，14 种（237..250），`mMaxCount` 全 = 1，
    ///     且 `AddItem` 对 DEATHCRY 走"新槽"分支 ⇒ **每种各占 1 独立槽位**。
    ///   • 槽池 = `mFreeItemLists[6]`，容量 = `freeItemCounts[6]`（实机 10）⇒ 同时最多 10 种。
    ///   • 池空 ⇒ `ScoopHaveItemInfo` 返回 null ⇒ `ERROR_OVER_ITEM_COUNT(4)` **整笔丢弃**。
    ///   • 净化产出表 `ConstCreationParameter`（59 条）**按 `mStartProgress` 门槛解锁**且**看用哪种幽怨**：
    ///       prog 0/121000/131000/141000/151000 → DEATHCRY_001/002/004/007..014 的产出（早中期）
    ///       prog 361000 → WEAPON_018/045/068/087、ARMOR_043/048、ACCESSORY_016..020
    ///                     **只有 DEATHCRY_003(239) / DEATHCRY_006(242) 才出**
    ///     ⇒ 早期幽怨占满 10 格 ⇒ 后期幽怨入不了包 ⇒ **后期强力装备永远拿不到**。
    ///   ⇒ 修法见类内〈修正九：幽怨槽池扩容〉：`Reset/Initialize` 后往 `mFreeItemLists[6]` 追加空槽
    ///     到 `DeathcryPoolTarget`（默认 20），**不碰 `freeItemCounts`**（那是 InitializeItem 的累积游标）。
    ///   ⚠ 实机一行取证：`growth.pool: 槽池现状（…）` 会一次性打印 8 个槽池空闲数与 freeItemCounts 原值。
    ///
    /// 【C4 全技能解锁】技能 = 按等级学习：
    ///   `ConstLearningParameter { mCharaID 0x10 / mlevel 0x14 / mSkillID 0x18 }`，
    ///   `CharacterParameter.UpdateSkill()`(实例 0x1CE150) 内 `GetLearningList(charaID)`(0x4F960)
    ///   过滤 + 排序后学进角色（学习判定 `&lt;UpdateSkill&gt;m__0` 0x1CD990 = `myLevel &lt; x.mlevel` 视为未学）。
    ///   调用者只有 `UpdateLevel(uint)`(0x1CDFE7) ⇒ 改表后必须**手动触发一次** UpdateSkill
    ///   （对入队角色；之后升级/读档会自动重算）。实现：`mlevel` 全部改 1（记录原值）。
    ///   ⚠ v0.2.9 实机失效实锤（debug.log："成功 0 / 失败 0 / 共 2"）：编译期
    ///   `as CharacterComponent` + `cc.CharacterParameter` 两条路都被静默 continue。
    ///   v0.2.10 改纯反射链（元素不落类型转换、属性→字段双路取 CharacterParameter）+
    ///   每个跳过点留一次性 Warn + 重算改为每轮重放都触发（后入队角色也覆盖）。
    ///   ⚠ 不写存档、不碰持有标记——还原写回原 level 即可（已学会的技能留在角色身上，属预期）。
    /// </summary>
    public sealed class FeatureGrowth : IFeature
    {
        internal static FeatureGrowth Inst;

        public string Id { get { return "growth"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        // ---- ConstEnhancementParameter 字段偏移（dump:166315）----
        private const int EnhPriceOff = 0x18;
        private const int EnhMatNum1Off = 0x20;   // num1..4 = 0x20/0x28/0x30/0x38，步长 8
        private const int EnhMatSlots = 4;

        // ---- ConstGrantParameter 字段偏移（dump TypeDefIndex 3375，2026-09-13 复核）----
        // mItemID 0x10 / mMaterialItemID1 0x14 / mMaterialItemNum1 0x18 / ID2 0x1C / Num2 0x20
        // / ID3 0x24 / Num3 0x28 / ID4 0x2C / Num4 0x30 ⇒ Num1..4 = 0x18 + 8*s（与强化表同步长）
        private const int GrantNum1Off = 0x18;
        private const int GrantMatSlots = 4;

        // ---- ConstLearningParameter 字段偏移（dump:167186）----
        private const int LearnLevelOff = 0x14;

        /// <summary>学习表压到的等级。UpdateSkill 判定"未学"= myLevel &lt; x.mlevel（严格小于），
        /// 任何 ≥1 级的角色对 mlevel=1 都判"已学到"。</summary>
        private const int LearnLevelMin = 1;

        private const int ReapplyMs = 2000;

        // ---- 修正九（r10）：幽怨槽池（背景见类头【净化为什么必须扩容】）----
        /// <summary>`mFreeItemLists` 里 DEATHCRY 的槽池下标。
        /// 实锤 `GetFreeItemList`(0x37D90)：`eItemType.DEATHCRY(7)` → `mFreeItemLists[6]`
        /// （分派链：`get_ItemType` 0x44CF40 → sub 3 → WEAPON / dec → ARMOR·ACCESSORY(4,5) → 索引 7
        ///  / dec → 索引 1 (6) / dec → 索引 6 (7=DEATHCRY) / default → 索引 0）。</summary>
        private const int DeathcryPoolIndex = 6;
        /// <summary>幽怨槽池目标容量（**可选**扩容用）。原版 = `freeItemCounts[6]`（实机 10）。
        /// ⚠ 修正十（r11）起**默认关闭**扩容：见 <see cref="DeathcryFifoPatch"/> 的说明 ——
        /// `UserParameter.mDeathCryCharaID`（0x60）是**硬编码 10 格**的登记表，
        /// `AddDeathCryCharaID`(0x1EB810) 满了就静默丢弃 ⇒ 池子扩到 10 以上时第 11 个之后的幽怨
        /// 在登记表里无处安放（UI 也只认 10）。**FIFO 顶替才是正解**（保持 10 格 + 满时顶掉最早）。</summary>
        private const int DeathcryPoolTarget = 20;
        /// <summary>一次补槽上限（防 while 变死循环）。</summary>
        private const int DeathcryPoolMaxAdd = 64;
        /// <summary>**原版**幽怨上限。实锤：dump 152142 `Common.Global.MAX_HAVE_ITEM_DEATHCRY = 10`
        /// （编译期常量，游戏代码里被内联成 `cmp reg, 0xa`）。
        /// ⛔⛔ r14 实锤（2026-09-14 玩家复现）：
        ///   r10~r13 的 `GrowDeathcryPool` 盯的是 **空闲槽数** 而不是 **总容量** ——
        ///   `while (CountOf(freePool) &lt; 20) Add(new HaveItemInfo())`。
        ///   于是**每进一个幽怨就补一个空槽** ⇒ 总容量 = 20 + 已持有数，**上限形同虚设**：
        ///   实机日志 36538 行 `槽池空闲[…,20,…]` + `净化前持有幽怨 18 个` ⇒ 总容量 38。
        ///   这就是"打了 15 只怪 + 3 只 = 18 个、净化出 18 件"的全部原因（不是 FIFO 失效，是没上限）。
        /// ⇒ 判据一律改成 **总容量 = 空闲 + 持有**。</summary>
        private const int DeathcryPoolNative = 10;

        // ---- 修正十（r11）：幽怨 FIFO 顶替 ----
        /// <summary>幽怨 `eItemID` 区间。dump 枚举实锤 `DEATHCRY_001 = 237` … `DEATHCRY_025 = 261`
        /// （**共 25 种**，此前按物品表记的"14 种"偏窄，这里用枚举上界求稳）。
        /// `ConstItemParameter.mMaxCount` 全 = 1 ⇒ 每种各占 1 个独立槽位、不可叠加。</summary>
        private const int DeathcryIdMin = 237;
        private const int DeathcryIdMax = 261;

        private MelonPreferences_Entry<bool> _freeEnhance;
        /// <summary>C3-b「净化无消耗」（eMenu.CREATION=0，`UIRoomReadingCreationWindow.ItemCreation`）。
        /// 修正十五（r16）前本开关**同时**罩住净化与思装变容，用户要求拆开；现只管净化：
        /// 抑制窗口（拦 `SubItem`）+ 拦 `ClearDeathCryCharaID` + 触发幽怨槽池同步。</summary>
        private MelonPreferences_Entry<bool> _freeCreation;
        /// <summary>C3-a「思装变容无消耗」（eMenu.GRANT_ABILITY=2，改 `ConstGrantParameter` 表）。
        /// 修正十五（r16）从 `_freeCreation` 里**独立**出来的开关。</summary>
        private MelonPreferences_Entry<bool> _freeGrant;
        private MelonPreferences_Entry<bool> _allSkills;
        /// <summary>修正十（r11）：幽怨槽池满时**自动顶掉最早获取的那个**（默认开）。</summary>
        private MelonPreferences_Entry<bool> _deathcryFifo;
        /// <summary>修正十（r11）：把幽怨槽池从 10 补到 <see cref="DeathcryPoolTarget"/>（默认**关**）。
        /// 与 FIFO 二选一即可；同开时先扩容、满了再顶替。</summary>
        private MelonPreferences_Entry<bool> _deathcryPoolExpand;
        /// <summary>修正十三（r14）：HUD/净化页的**幽怨图标个数**跟随上限（默认开）。
        /// 原版图标是 prefab 里写死的 10 个 `UIDeathCryIconContent`；`UIFooterContent.UpdateDeathCryCount`
        /// (0xEE0A0) 取 `GetHaveItemList(7).Count` 与图标数取小 ⇒ 超过 10 个就显示不下。
        /// 本开关在图标列表里 **Instantiate 补足** 到上限个数。</summary>
        private MelonPreferences_Entry<bool> _deathcryIconExpand;
        /// <summary>修正十三（r14）：把 `UserParameter.GetDeathCryCount()`(0x1EBB00) 的返回值
        /// 按 `ceil(持有数 * 10 / 上限)` 缩放（默认**关**，实验性）。
        /// 目的：游戏里所有"满幽怨"判定都是 `count &gt;= Global.MAX_HAVE_ITEM_DEATHCRY(10)`
        /// （编译期常量，改不了）⇒ 缩放后"满"正好发生在**扩容后的上限**而不是 10。
        /// ⚠ 副作用：依赖该计数的任务/成就判定也会被缩放 ⇒ 默认关，需要时再开。</summary>
        private MelonPreferences_Entry<bool> _deathcryCountScale;

        private bool _enhWasOn;
        private bool _skillWasOn;
        private int _lastEnhApplyAt;
        private int _lastSkillApplyAt;
        private bool _enhWarned;
        private bool _skillWarned;
        // ── 修正十六（r17）：还原重试节流 ──
        // 还原失败要**保留重试**（见 OnUpdate 的三处 `else if (_xxxWasOn)`），但 OnUpdate 是每帧跑的
        // ⇒ 必须节流，否则"表还没载入"的那段时间会变成每帧遍历整张表（168 / 188 / 36 条）。
        // 语义：0 = 还没跑过 ⇒ 立即跑第一次；失败后每 ReapplyMs(2s) 重试一次；成功即清零。
        private int _lastEnhRestoreAt;
        private int _lastSkillRestoreAt;
        private int _lastGrantRestoreAt;

        // v0.2.10-r9（修正八）：思装变容表层免费化（与强化同款）
        private bool _grantWasOn;
        private int _lastGrantApplyAt;
        private bool _grantWarned;

        // v0.2.10-r10（修正九）：幽怨槽池扩容（净化「不消耗」的配套）
        private int _lastPoolAt;
        /// <summary>本会话是否已打印过一次槽池现状（一次性观测，避免刷屏）。</summary>
        private bool _poolLoggedOnce;
        /// <summary>r13：槽池是否**已经由游戏建好过**（`Reset`/`Initialize` 跑过即为真，之后永久为真）。
        /// 必要性（2026-09-14 实机 bug）：原先只凭"空闲池 Count&gt;0"判断槽池是否就绪，而
        /// **幽怨占满时空闲 Count 恰好 = 0** ⇒ 被误判成"还没建好"⇒ 扩容在最该生效的时刻静默跳过
        /// （日志 23:52:44 开扩容 → 23:53:15 仍走 FIFO 顶替，净化仍是 10 件）。
        /// ⛔ 别再用"空闲数 == 0"代表未初始化。</summary>
        private static bool _poolReady;
        /// <summary>r14：我们**曾经**往槽池里补过空槽 ⇒ 即使后来关掉扩容开关，也要继续跑
        /// `SyncDeathcryPool` 把多余的空槽撤掉（否则"关掉扩容"永远是空话）。</summary>
        private static bool _poolEverGrown;
        /// <summary>r14：图标列表补位是否已尝试过（一次性，失败也只报一次）。</summary>
        private static bool _iconExpandTried;
        /// <summary>r14：图标补位时记录的最后一次结果说明（诊断用）。</summary>
        private static string _iconExpandNote;
        // ── r15：图标**布局基准**（修「克隆体全叠在第 1 颗上」） ──
        // ⛔ 根因：`Object.Instantiate(模板 = list[0])` 复制的是**第 1 颗**的
        //    localPosition/localScale ⇒ N 个克隆体全落在第 1 颗的位置上，
        //    发光叠加把第 1 颗烧成亮白，而总数看上去仍然只有 10 颗
        //    （2026-09-15 实机截图 + `growth.icon: 10 -> 20` 日志已确认补位成功）。
        // 修法：以原版 10 颗的「首颗位置 / 等距步长 / 缩放」为基准，
        //      把 cap 颗**重新等分**并**等比缩小**，保证全部落在原容器宽度内。
        //      ⚠ 基准只在**第一次**记录 ⇒ 反复进入也不会越缩越小（幂等）。
        private static bool _iconBaseReady;
        private static UnityEngine.Vector3 _iconBaseFirst;   // 原第 1 颗的 localPosition
        private static UnityEngine.Vector3 _iconBaseStep;    // 原相邻两颗的间距
        private static UnityEngine.Vector3 _iconBaseScale;   // 原图标的 localScale
        /// <summary>r15：已经补过图标的那一个 `UIFooterContent` 实例（换场景重建后会重新补）。</summary>
        private static object _iconExpandedFooter;
        /// <summary>思装变容表原值备份：键 = **表内下标**，值 = [num1..num4]。
        /// ⛔ 修正十六（r17）：键从"对象指针"改成"表内下标"——理由见 <see cref="ApplyEnhanceFree"/> 顶部的实锤。</summary>
        private readonly Dictionary<int, int[]> _grantBackup = new Dictionary<int, int[]>();
        private static bool _skillOkLogged;   // 重算成功只打一次 Info，之后降级 Trace（TriggerSkillRecalc 为静态）

        // v0.2.10-r5：C4 重算收敛（见 TriggerSkillRecalc 内的说明与 2026-09-13 日志实锤）
        private static bool _recalcDone;
        private static string _lastCpSig = "";
        private static int _lastRecalcAt;
        private const int RecalcIntervalMs = 30000;

        /// <summary>强化表原值备份：[price, num1..num4]。键 = **表内下标**（修正十六 r17 起；此前是对象指针）。</summary>
        private readonly Dictionary<int, int[]> _enhBackup = new Dictionary<int, int[]>();
        /// <summary>学习表原等级备份。键 = **表内下标**（修正十六 r17 起；此前是对象指针）。</summary>
        private readonly Dictionary<int, int> _skillBackup = new Dictionary<int, int>();

        /// <summary>
        /// SubItem 抑制旗：只在强化/炼成 UI 包装器执行期间为 true。
        /// 窗口内游戏代码是同步的（callers.py 实锤只有包装器一个入口），不会误伤买卖/丢弃等其它扣物路径。
        /// </summary>
        internal static bool SuppressSubItem;

        /// <summary>r21 诊断：growth 各补丁的命中计数（标题卡死定位用，零成本只 ++）。</summary>
        internal static class Hot
        {
            internal static int Reset, Init, SubItemSkip, ClearSkip, AddItem, FooterIcon, PoolTick, Evict, GetDc;
            internal static string Snapshot()
            {
                return " growth命中 Reset=" + Reset + " Init=" + Init + " SubItemSkip=" + SubItemSkip
                     + " ClearSkip=" + ClearSkip + " AddItem=" + AddItem + " FooterIcon=" + FooterIcon
                     + " PoolTick=" + PoolTick + " Evict=" + Evict + " GetDc=" + GetDc;
            }
        }

        /// <summary>
        /// ⛔ v0.2.10-r9（修正八）**已删除**整套「思装变容扣料窗 / 记账 / 事后返还」机器。
        /// 历史：修正三起走"原样扣料 + 事后用 AddItem 补回"，r7 把窗口改由实例方法链驱动
        /// （OpenCheckWindow → 确认框 OnPress → 3 秒窗 → OnUpdate 收尾返还），r8 又把返还量改成
        /// "持有量差值"以对抗双跑翻倍。**但这条路的每一版都在跟铁律 1（补丁体跑两遍）搏斗**，
        /// 用户实机抓到"思装变容材料强化完反向变多"（1 次真实 SubItem(262,4) 被记成 2 笔 ⇒ 返还 8）。
        /// ⇒ 修正八改为**改表**（与强化同款）：材料数在源头上就是 0，扣料根本不会发生，
        /// 于是"窗口 / 记账 / 快照 / 返还 / AddItem"全部不再需要，整个 bug 类别被连根拔掉。
        /// 详见 ApplyGrantFree 与类头【实现形态】①。
        /// </summary>

        /// <summary>
        /// 强化窗口标记（v0.2.10 修正四诊断用）：与 <c>SuppressSubItem</c> 分开，
        /// 因为净化窗口共用同一个抑制旗，Finalizer 需要能区分这一轮到底是谁开的窗。
        /// </summary>
        private static bool _enhWin;
        /// <summary>本轮强化窗口内被拦下的 <c>SubItem(eItemID,int)</c> 次数（= 材料槽扣料）。</summary>
        private static int _enhSubIdHits;
        /// <summary>
        /// 净化窗口标记（v0.2.10-r5 诊断用）：与 <c>SuppressSubItem</c> 分开，
        /// 因为强化窗口共用同一个抑制旗，Finalizer 需要能区分这一轮到底是谁开的窗。
        /// </summary>
        private static bool _creWin;
        /// <summary>本轮净化窗口内被拦下的 <c>SubItem(eItemID,int)</c> 次数。</summary>
        private static int _creSubIdHits;
        /// <summary>修正九（r10）诊断：被拦下的那批里 <c>count &gt; 0</c> 的笔数与总扣量。
        /// 实机 2026-09-13 16:20 一次净化被拦 16 笔 —— 但 `ItemCreation` 0x393B0 体内那次
        /// `SubItem(输入幽怨, createCount)` 是**无条件**执行的（在产出循环之外），
        /// 所以需要区分"16 笔全 count=0 的探测调用"（⇒ 抑制其实是空转、真扣料在别处）
        /// 与"确实拦下了真扣料"。</summary>
        private static int _creSubIdNonZero;
        private static int _creSubIdSum;
        /// <summary>净化窗口里加装备失败（AddItem 返回 null）的笔数 —— 这是"净化后不能再来一次"的头号嫌疑。</summary>
        private static int _creAddFail;
        /// <summary>修正十一（r12）：净化窗口里 `AddItem` 的总笔数（= 产出的装备件数）。
        /// ⚠ 双跑 ⇒ 实际件数 = 本值 / 2。</summary>
        private static int _creAddTotal;
        /// <summary>r13：净化**开始前**实体持有列表里的幽怨个数（= 本次该产出几件）。取不到 = -1。</summary>
        private static int _creHeldBefore = -1;
        /// <summary>r13：净化**开始前** `UserParameter.GetDeathCryCount()`（登记表口径，**硬上限 10**）。
        /// 与 `_creHeldBefore` 并列打印，用来一眼看出"实体 11 个但登记表只记 10 个"这第二套账的差异。</summary>
        private static int _creRegBefore = -1;
        /// <summary>本轮强化窗口内被拦下的 <c>SubItem(uint,int)</c> 次数（= 选定的同名素材堆扣 1）。</summary>
        private static int _enhSubUidHits;
        /// <summary>修正十（r11）：FIFO 顶替累计次数（日志/实机确认用）。</summary>
        private static int _fifoEvicts;
        /// <summary>修正十一（r12）：拦下 `ClearDeathCryCharaID()` 的次数（日志/实机确认用）。
        /// ⚠ 铁律 1（补丁体跑两遍）⇒ 一次净化会看到 2 次，别当成"净化了两次"。</summary>
        private static int _clearGuardHits;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            var c = ctx.Cfg;
            _freeCreation = c.Bool("FreeCreation", false, "Free purification (Deathcry) / 净化幽怨无消耗");
            _freeEnhance = c.Bool("FreeEnhance", false, "Free blending (no gear/money) / 思装调合无消耗");
            _freeGrant = c.Bool("FreeGrant", false, "Free transmute (no materials) / 思装变容无消耗");
            // 修正十五（r16）：三个开关互不干涉，各自只管一条路径（净化 / 思装调合 / 思装变容）。
            //   键名沿用 `FreeCreation`（配置向后兼容，老 settings.cfg 里的值继续生效）⇒ 现在只代表净化。
            _allSkills = c.Bool("AllSkills", false, "Unlock all skills / 全技能解锁");
            // 修正十（r11）：幽怨满了怎么办 —— FIFO 顶替（默认开）/ 扩容（默认关）
            _deathcryFifo = c.Bool("DeathcryFifo", true,
                "Auto-drop oldest Deathcry when full / 幽怨满时自动顶掉最早的那个");
            _deathcryPoolExpand = c.Bool("DeathcryPoolExpand", false,
                "Enlarge Deathcry slot pool to 20 / 幽怨槽池扩容到 20（上限开关，关掉会回落到 10）");
            // 修正十三（r14）
            _deathcryIconExpand = c.Bool("DeathcryIconExpand", true,
                "Deathcry icon count follows the cap / 幽怨图标数量跟随上限（超过 10 时补足图标）");
            _deathcryCountScale = c.Bool("DeathcryCountScale", false,
                "Scale Deathcry count to the cap / 幽怨计数按上限缩放（让「满」发生在 20 而不是 10，实验性）");

            // ── 扣除层：**只挂实例方法** ──
            // ⛔⛔ v0.2.10-r7 铁律级修正：本环境（MelonLoader 0.5.7 + HarmonyX + Il2Cpp）里
            // **挂在 IL2CPP `static` 方法上的补丁会在执行期抛 NullReferenceException，Prefix 根本不跑**。
            // 实锤（MelonLoader/Latest.log，2026-09-13 04:39:42 / 04:39:52，玩家做思装变容的那两次）：
            //   `[ERROR] Exception in Harmony patch of method static Game.HaveItemInfo
            //    Game.ItemManager::ItemAbilityGranted(Game.HaveItemInfo haveItemInfo, int slot):
            //    UnhollowerBaseLib.Il2CppException: System.NullReferenceException...`
            // 机理：IL2CPP static 方法的**第 1 个寄存器参数是隐藏的 `MethodInfo*`**（反汇编实锤调用点
            // `xor ecx,ecx; call 0x180038D90` ⇒ rcx = NULL），而 Harmony 要按"托管签名"把 rcx 当
            // `HaveItemInfo` 封送 ⇒ 解引用 NULL。这比铁律 10.8（"别按参数注入"）更狠：
            // **哪怕 Prefix 零参，只要该方法有参数，Harmony 的参数封送就会先炸**。
            // 对照：本项目所有**实机生效**的挂点（GetDropList / AddDropItem / GetExp / SetMoney /
            // IncreaseMoney / StartSuction / UpdateCountInfo / SubItem / 各 UI 包装器）**全是实例方法**，
            // 而三个 static 挂点（ItemEnhancement / ItemCreation / ItemAbilityGranted）无一产生过日志。
            Safe(ctx.Harmony, typeof(EnhanceWrapperPatch), "UIRoomReadingEnhancementListWindow.ItemEnhancement");
            Safe(ctx.Harmony, typeof(CreationWrapperPatch), "UIRoomReadingCreationWindow.ItemCreation");
            Safe(ctx.Harmony, typeof(SubItemByIdPatch), "ItemManager.SubItem(eItemID,int)");
            Safe(ctx.Harmony, typeof(SubItemByUniqueIdPatch), "ItemManager.SubItem(uint,int)");

            // ── 思装变容（Grant，C3-a）：**不再需要任何补丁** ──
            // 修正八起走"改表"路线（ApplyGrantFree）⇒ 材料数在源头就是 0，扣料不发生，
            // 于是 r7 的「OpenCheckWindow 亮旗 → 确认框 OnPress 开 3 秒窗 → OnUpdate 收尾返还」
            // 整套机器（含 GrantAskPatch / GrantConfirmPressPatch / GrantConfirmCrossPatch）已删除。
            // 只保留下面的**只读观测点**，用于实机确认玩家确实走到了思装变容那条流程。

            // ── 全链路观测（r6 引入，全部实例方法）──
            Safe(ctx.Harmony, typeof(ReadingMenuProbe), "UIRoomReadingMenuWindow.OnDecision（观测点）");
            Safe(ctx.Harmony, typeof(GrantEquipProbe), "UIRoomGrantSelectEquipmentWindow.OnDecision（观测点）");
            Safe(ctx.Harmony, typeof(GrantSlotProbe), "UIRoomGrantSelectSlotWindow.OnPress(int)（观测点）");
            Safe(ctx.Harmony, typeof(GrantCheckWindowProbe), "UIRoomGrantSelectSlotWindow.OpenCheckWindow（观测点）");

            // ── 修正九（r10）：幽怨槽池扩容（C3-b 净化的**必要配套**，只挂实例方法）──
            //   不扩容 ⇒「净化不消耗幽怨」会把 10 个幽怨槽位永久占满 ⇒ 后期幽怨被 AddItem 整笔丢弃
            //   ⇒ 后期强力装备（只有 DEATHCRY_003/006 才出）永远拿不到。详见类内〈幽怨槽池扩容〉说明块。
            Safe(ctx.Harmony, typeof(ItemPoolResetPatch), "ItemManager.Reset");
            Safe(ctx.Harmony, typeof(ItemPoolInitializePatch), "ItemManager.Initialize");
            // 只读诊断：净化期间 AddItem 失败计数（不改行为）—— 用于判定"净化后不能再来一次"的真因
            Safe(ctx.Harmony, typeof(AddItemFailProbe), "ItemManager.AddItem（只读诊断）");
            // 修正十（r11）：幽怨 FIFO 顶替 —— 同一个 AddItem，另一个 patch 类（Prefix）
            Safe(ctx.Harmony, typeof(DeathcryFifoPatch), "ItemManager.AddItem（幽怨 FIFO 顶替）");
            // 修正十一（r12）：拦下净化收尾的 ClearDeathCryCharaID() —— 不解这一步，净化会被永久锁死
            Safe(ctx.Harmony, typeof(DeathcryClearGuardPatch), "UserParameter.ClearDeathCryCharaID（保留幽怨登记表）");
            // 修正十三（r14）：GetDeathCryCount() 缩放（默认关）—— 让"满幽怨"判定发生在扩容后的上限
            Safe(ctx.Harmony, typeof(DeathcryCountScalePatch), "UserParameter.GetDeathCryCount（上限缩放，默认关）");

            PatchSelfCheck(ctx.Harmony);
        }

        /// <summary>
        /// v0.2.10-r6 **挂载自检（决定性证据）**：列出这个 Harmony 实例实际 patch 到的方法，
        /// 并逐个核对本次会话真正依赖的 6 个挂点是否在表里 —— 这是"补丁到底挂上没有"的唯一可信来源
        /// （`Safe()` 只在 PatchAll **抛异常**时才打日志；已见过 `Patching exception in method null`
        /// 这类明确失败，但"解析成功却静默未生效"是不会报错的）。
        /// ⚠ 不在自检通过前改任何行为（规则 23）。
        /// ⚠ 自检本身也必须 try/catch，且日志排在一切动作之前（规则 21）。
        /// </summary>
        private static void PatchSelfCheck(HarmonyLib.Harmony h)
        {
            try
            {
                var names = new List<string>();
                foreach (var m in h.GetPatchedMethods())
                {
                    string n;
                    try { n = (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + m.Name; }
                    catch { n = "(取不到名字)"; }
                    names.Add(n);
                }
                names.Sort();
                QolLog.Warn("growth: 补丁自检 —— 本 Harmony 实例共 patch " + names.Count + " 个方法：" + string.Join(" / ", names.ToArray()));

                // 逐个核对关键挂点（名字包含匹配足够）。⛔ 只核对**实例方法**挂点：
                // static 挂点（ItemEnhancement / ItemCreation / ItemAbilityGranted）已按 r7 结论全部移除；
                // r9 起思装变容走改表、不再挂确认框，故 OnPress / OnCross 两项也移出清单
                // （只留 OpenCheckWindow —— 那是**只读观测点** GrantCheckWindowProbe 在挂）。
                string[] must = new string[] {
                    "UIRoomReadingEnhancementListWindow.ItemEnhancement",
                    "UIRoomReadingCreationWindow.ItemCreation",
                    "ItemManager.SubItem",
                    "UIRoomGrantSelectSlotWindow.OpenCheckWindow",
                    // 修正九（r10）：幽怨槽池扩容的两个挂点（实例方法）+ AddItem 只读诊断
                    "ItemManager.Reset",
                    "ItemManager.Initialize",
                    "ItemManager.AddItem",
                    // 修正十一（r12）：不拦下它 ⇒ 净化一次之后就再也进不去净化（GetDeathCryCount()==0）
                    "UserParameter.ClearDeathCryCharaID",
                };
                foreach (string key in must)
                {
                    bool found = false;
                    foreach (string n in names) { if (n.Contains(key)) { found = true; break; } }
                    QolLog.Warn("growth: 补丁自检 " + (found ? "OK  " : "缺失") + " - " + key);
                }

                // 缺失即自愈（见 HardPatchIfMissing 的说明）
                HardPatchIfMissing(h, typeof(UIRoomReadingEnhancementListWindow), "ItemEnhancement",
                    null, typeof(EnhanceWrapperPatch), "UIRoomReadingEnhancementListWindow.ItemEnhancement");
                HardPatchIfMissing(h, typeof(UIRoomReadingCreationWindow), "ItemCreation",
                    new Type[] { }, typeof(CreationWrapperPatch), "UIRoomReadingCreationWindow.ItemCreation");
                HardPatchIfMissing(h, typeof(ItemManager), "SubItem",
                    new Type[] { typeof(eItemID), typeof(int) }, typeof(SubItemByIdPatch),
                    "ItemManager.SubItem(eItemID,int)");
                HardPatchIfMissing(h, typeof(ItemManager), "SubItem",
                    new Type[] { typeof(uint), typeof(int) }, typeof(SubItemByUniqueIdPatch),
                    "ItemManager.SubItem(uint,int)");
                HardPatchIfMissing(h, typeof(UIRoomGrantSelectSlotWindow), "OpenCheckWindow",
                    new Type[] { }, typeof(GrantCheckWindowProbe), "UIRoomGrantSelectSlotWindow.OpenCheckWindow（观测点）");
                HardPatchIfMissing(h, typeof(ItemManager), "Reset",
                    new Type[] { }, typeof(ItemPoolResetPatch), "ItemManager.Reset");
                HardPatchIfMissing(h, typeof(ItemManager), "Initialize",
                    new Type[] { }, typeof(ItemPoolInitializePatch), "ItemManager.Initialize");
                HardPatchIfMissing(h, typeof(ItemManager), "AddItem",
                    null, typeof(AddItemFailProbe), "ItemManager.AddItem（只读诊断）");
                HardPatchIfMissing(h, typeof(UserParameter), "ClearDeathCryCharaID",
                    new Type[] { }, typeof(DeathcryClearGuardPatch),
                    "UserParameter.ClearDeathCryCharaID（保留幽怨登记表）");
            }
            catch (Exception e)
            {
                QolLog.Warn("growth: 补丁自检失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// v0.2.10-r6 兜底自愈：自检发现关键挂点不在 Harmony 补丁表里时，改用**反射 MethodInfo +
        /// <c>h.Patch(mi, prefix, finalizer)</c>** 再挂一次 —— attribute 解析路径与手动 Patch 路径
        /// 并不完全等价（前者静默失效时后者可能成功）。
        /// ⚠ 幂等：已在表里就直接返回（不重复挂，避免 Prefix 跑三遍）。
        /// ⚠ 回读校验（铁律 9：调用没抛异常 ≠ 生效）。
        /// </summary>
        private static void HardPatchIfMissing(HarmonyLib.Harmony h, Type target, string methodName,
                                               Type[] args, Type patchClass, string what)
        {
            try
            {
                foreach (var m in h.GetPatchedMethods())
                {
                    try { if (m.Name == methodName && m.DeclaringType == target) return; }
                    catch { }
                }

                MethodInfo mi = (args != null && args.Length > 0)
                    ? target.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, args, null)
                    : target.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (mi == null) { QolLog.Warn("growth: 自愈挂载 —— 反射找不到方法 " + what); return; }

                MethodInfo pre = patchClass.GetMethod("Prefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo fin = patchClass.GetMethod("Finalizer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                // ⚠ 用完整限定名：本项目引用的 Harmony shim 里 HarmonyMethod 不在 using 的顶层作用域下
                HarmonyLib.HarmonyMethod hmPre = pre == null ? null : new HarmonyLib.HarmonyMethod(pre);
                HarmonyLib.HarmonyMethod hmFin = fin == null ? null : new HarmonyLib.HarmonyMethod(fin);
                h.Patch(mi, prefix: hmPre, finalizer: hmFin);

                bool ok = false;
                foreach (var m in h.GetPatchedMethods())
                {
                    try { if (m.Name == methodName && m.DeclaringType == target) { ok = true; break; } }
                    catch { }
                }
                QolLog.Warn("growth: 自愈挂载 " + (ok ? "成功" : "后回读仍缺失") + " - " + what);
            }
            catch (Exception e)
            {
                QolLog.Warn("growth: 自愈挂载异常 " + what + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("growth: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // 生命周期（重放/还原模式与 FeatureEconomy C2 同款）
        // ------------------------------------------------------------------

        public void OnUpdate()
        {
            // C1：表层免费（改表）+ 扣除层由补丁自己看配置
            bool enh = _freeEnhance != null && _freeEnhance.Value;
            if (enh)
            {
                int now = Environment.TickCount;
                if (now - _lastEnhApplyAt >= ReapplyMs)
                {
                    _lastEnhApplyAt = now;
                    ApplyEnhanceFree(true);
                }
            }
            else if (_enhWasOn)
            {
                // ⛔ 修正十六（r17）：**先还原、成功了才收闸**。原先是先把标记置 false 再调用 ⇒
                //   这一轮只要没跑成（表还没载入 / 备份为空），就再也不重试 ⇒ 表永远停在 0 消耗。
                //   失败保留重试 + 2s 节流（OnUpdate 每帧跑，不节流会每帧遍历整张表）。
                int now = Environment.TickCount;
                if (_lastEnhRestoreAt == 0 || now - _lastEnhRestoreAt >= ReapplyMs)
                {
                    _lastEnhRestoreAt = now;
                    if (ApplyEnhanceFree(false) >= 0) { _enhWasOn = false; _lastEnhRestoreAt = 0; }
                }
            }
            if (enh) _enhWasOn = true;

            // C4：学习表 mlevel→1 + 触发重算。
            // v0.2.10：重算改为**每轮重放都触发**（原来只在表实际变动时触发一次）——
            // UpdateSkill 的调用者只有 UpdateLevel，后入队的角色不会再有机会重算。
            bool skill = _allSkills != null && _allSkills.Value;
            if (skill)
            {
                int now = Environment.TickCount;
                if (now - _lastSkillApplyAt >= ReapplyMs)
                {
                    _lastSkillApplyAt = now;
                    ApplyAllSkills(true);
                    TriggerSkillRecalc();
                }
            }
            else if (_skillWasOn)
            {
                // ⛔ 修正十六（r17）：还原失败保留重试（同 C1 / C3-a），带 2s 节流
                int now = Environment.TickCount;
                if (_lastSkillRestoreAt == 0 || now - _lastSkillRestoreAt >= ReapplyMs)
                {
                    _lastSkillRestoreAt = now;
                    if (ApplyAllSkills(false) >= 0) { _skillWasOn = false; _lastSkillRestoreAt = 0; }
                }
            }
            if (skill) _skillWasOn = true;

            // C3-a 思装变容：表层免费化（改 ConstGrantParameter，与 C1 同款重放/还原）
            //   修正十五（r16）：改看**独立的 `FreeGrant`**，不再跟净化共用一个开关。
            bool grant = GrantFreeOn();
            if (grant)
            {
                int now = Environment.TickCount;
                if (now - _lastGrantApplyAt >= ReapplyMs)
                {
                    _lastGrantApplyAt = now;
                    ApplyGrantFree(true);
                }
            }
            else if (_grantWasOn)
            {
                // ⛔ 修正十六（r17）：**先还原、成功了才收闸**。原先是先把标记置 false 再调用 ⇒
                //   02:14:21 玩家关掉「思装变容零消耗」时，备份已被 OnSceneLoaded 抹掉 ⇒ 一条都没还原、
                //   而闸已收死 ⇒ 表永远停在 0（玩家报的"关掉还是 0 消耗"）。详见 ApplyEnhanceFree 说明。
                //   失败保留重试 + 2s 节流。
                int now = Environment.TickCount;
                if (_lastGrantRestoreAt == 0 || now - _lastGrantRestoreAt >= ReapplyMs)
                {
                    _lastGrantRestoreAt = now;
                    if (ApplyGrantFree(false) >= 0) { _grantWasOn = false; _lastGrantRestoreAt = 0; }
                }
            }
            if (grant) _grantWasOn = true;

            // C3-b 净化：无表可改、无重放；抑制窗口由 CreationWrapperPatch 自己看配置。
            //   修正九（r10）/ 修正十（r11）：**幽怨槽池**（观测 + 可选扩容）。
            //   槽池由 ItemManager.Reset/Initialize 建，所以这里按 2s 重放兜"场景切换 / 开关跳变"。
            //   ⚠ 修正十起：扩容默认关（`DeathcryPoolExpand`），FIFO 顶替默认开（`DeathcryFifo`），
            //     但**观测（打一遍槽池现状）任何时候都值得跑一次**，所以三个开关有一个亮就跑。
            //   ⚠ 修正十五：这里跟的是**净化**开关（`FreeCreation`），不是思装变容。
            if (PurifyFreeOn() || FifoOn() || ExpandOn())
            {
                int now = Environment.TickCount;
                if (now - _lastPoolAt >= ReapplyMs)
                {
                    _lastPoolAt = now;
                    Hot.PoolTick++;
                    EnsureDeathcryPool("OnUpdate");
                }
            }
        }

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // ⛔⛔ 修正十六（r17）：**绝不在这里清备份**。
            //   r16 及以前这里写的是"表可能随场景重载：备份作废不写回（对象可能已销毁）"⇒ 直接 Clear，
            //   这正是玩家报的「关掉还是 0 消耗」的根因：开关 ON 期间只要读过档 / 进过战斗 / 回过标题
            //   （必然发生），备份就被抹掉 ⇒ 之后关开关时一条都还原不了 ⇒ 表永远停在 0。
            //   现在备份的键是**表内下标**（不是对象指针）⇒ 对象重建也无所谓，下标照样命中；
            //   原值必须活到"开关被关掉"那一刻为止。
            //   ⚠ 若开关此刻已关而备份还在（上一场景关掉时表没就绪），下一轮 OnUpdate 会继续重试。
            _lastEnhApplyAt = 0;
            _lastGrantApplyAt = 0;
            _lastSkillApplyAt = 0;
            // 修正十六（r17）：让"还原重试"在新场景里立刻跑一次，而不是等满 2s 节流
            _lastEnhRestoreAt = 0;
            _lastGrantRestoreAt = 0;
            _lastSkillRestoreAt = 0;
            // 角色对象随场景重建 ⇒ 签名/收敛闸一并作废，下一轮重放必定重算一次
            _recalcDone = false;
            _lastCpSig = "";
            _lastRecalcAt = 0;
        }

        public void OnReset()
        {
            // ⛔ 修正十六（r17）：本方法**现在真的有调用点了** —— `KillSwitch.Trip()`（紧急关闭 / F8）
            //   会通过 `ModMain.RevokeAllFeatures()` 逐个 feature 调它。此前注释写的是"无调用点"。
            //   调用时机在 `QolConfig.ResetAll()` **之后** ⇒ 这里读到的偏好已是默认值
            //   （`ExpandOn()`=false、`FifoOn()`=true），所以下面这一串是干净的"恢复原版"语义。
            ApplyEnhanceFree(false);
            ApplyGrantFree(false);
            ApplyAllSkills(false);
            // 幽怨槽池：把补过的空槽按"原版上限 10"收缩回去（内部靠 `_poolEverGrown` 判该不该动，
            // 不依赖开关），否则 mod 关掉了、幽怨上限却还留在 20。
            EnsureDeathcryPool("OnReset");
        }

        // ------------------------------------------------------------------
        // C1 强化零消耗 —— 表层
        // ------------------------------------------------------------------

        /// <summary>取强化全量表（object + 反射枚举；⛔ 编译期 List&lt;T&gt; 接不住运行期 Il2Cpp List）。</summary>
        private static object EnhanceTable()
        {
            var pm = ParameterManager.Instance;
            if (pm == null) { WarnOnce("growth.pm", "ParameterManager.Instance 为空"); return null; }
            object sp = GetMember(pm, "mEnhancementParameter");   // 0x90（dump:171509）
            if (sp == null) { WarnOnce("growth.enh.field", "读 mEnhancementParameter(0x90) 失败"); return null; }
            object raw = GetMember(sp, "param");                  // ScriptableEnhancementParameter.param 0x18
            if (raw == null || CountOf(raw) <= 0) { WarnOnce("growth.enh.empty", "强化表 param 为空"); return null; }
            return raw;
        }

        /// <summary>
        /// 强化表「免费化 / 还原」：改 `mPrice`(0x18) + `mMaterialItemNum1..4`(0x20/0x28/0x30/0x38)。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// ⛔⛔ 修正十六（r17）实锤：**备份键必须是"表内下标"，且不许在换场景时丢弃**
        /// ══════════════════════════════════════════════════════════════════
        /// 玩家实测「开了能变成 0 消耗，**关掉还是 0 消耗**」。本机 debug.log 证据链：
        ///   • 02:10:18 开 FreeGrant → `#116 思装变容表免费化 188 / 188 条`（表清零、备份建立）
        ///   • 02:14:21 关 FreeGrant → settings.cfg 确实落盘 `FreeGrant=false`，
        ///     但日志里**从头到尾没有** `growth: 已还原思装变容表 …` ⇒ 一条都没还原。
        /// 唯一解释：还原时备份已经是空的。而唯一会清空它的地方就是 **`OnSceneLoaded`**
        /// （r16 及以前写着"表可能随场景重载：备份作废不写回"，直接 Clear）。
        ///   ⇒ 只要玩家在"开开关"与"关开关"之间**读过档 / 进过战斗 / 回过标题**（必然发生），
        ///     备份就没了 ⇒ 关开关时 `TryGetValue` 全 miss ⇒ 表永远停在 0、"关不掉"。
        /// 附带第二重缺陷：`OnUpdate` 的还原分支**先把标记置 false 再调还原** ⇒ 哪怕这一轮
        /// 还原失败（表尚未载入等），也**再也不重试** ⇒ 同样永远停在 0。
        ///
        /// 修法（三条一起才成立）：
        ///   ① 备份键＝**表内下标**（表是静态数据资源，条数与顺序固定：强化 168 / 思装变容 188）
        ///      ⇒ 即使场景重载把 Il2Cpp 对象重建、指针全变，下标依旧指向同一条目。
        ///   ② `OnSceneLoaded` **不再**清备份（原值必须活到"开关被关掉"那一刻为止）。
        ///   ③ 还原失败**保留重试**：调用方只有拿到非负返回值才收闸；还原命中即从备份移除，
        ///      遍历结束仍有残留（下标对不上表）才丢弃 + Warn，避免每 2s 空转。
        /// 另加回读校验（铁律 9：调用没抛异常 ≠ 生效）。
        /// </summary>
        private static int ApplyEnhanceFree(bool on)
        {
            var f = Inst;
            if (f == null) return -1;
            try
            {
                object list = EnhanceTable();
                if (list == null)
                {
                    if (!f._enhWarned) { f._enhWarned = true; QolLog.Warn("growth: 取不到强化表"); }
                    return -1;   // ① 表还没就绪 ⇒ 调用方保留重试
                }
                int total = CountOf(list);
                int n = 0;
                int mismatch = 0;
                for (int i = 0; i < total; i++)
                {
                    try
                    {
                        object p = ItemOf(list, i);
                        if (p == null) continue;
                        IntPtr ptr = NativePtr.Of(p);
                        if (ptr == IntPtr.Zero) continue;

                        if (on)
                        {
                            int price = Marshal.ReadInt32(IntPtr.Add(ptr, EnhPriceOff));
                            bool allZero = price == 0;
                            int[] cur = new int[1 + EnhMatSlots];
                            cur[0] = price;
                            for (int s = 0; s < EnhMatSlots; s++)
                            {
                                cur[1 + s] = Marshal.ReadInt32(IntPtr.Add(ptr, EnhMatNum1Off + 8 * s));
                                if (cur[1 + s] != 0) allZero = false;
                            }
                            if (allZero) continue;   // 判据：已是目标值就不写（幂等重放）

                            if (!f._enhBackup.ContainsKey(i)) f._enhBackup[i] = cur;
                            // 价格 0x18 与材料数 0x20/0x28/0x30/0x38 在内存里连续（步长 8），
                            // 从 0x18 起逐槽清零正好覆盖 price + num1..4
                            for (int s = 0; s <= EnhMatSlots; s++)
                                Marshal.WriteInt32(IntPtr.Add(ptr, EnhPriceOff + 8 * s), 0);
                            n++;
                        }
                        else
                        {
                            int[] orig;
                            if (!f._enhBackup.TryGetValue(i, out orig)) continue;
                            for (int s = 0; s <= EnhMatSlots; s++)
                                Marshal.WriteInt32(IntPtr.Add(ptr, EnhPriceOff + 8 * s), orig[s]);
                            // 回读校验：备份里的 price 必然 != 0（allZero 的条目压根不入备份）
                            if (Marshal.ReadInt32(IntPtr.Add(ptr, EnhPriceOff)) == orig[0])
                            {
                                f._enhBackup.Remove(i);
                                n++;
                            }
                            else mismatch++;
                        }
                    }
                    catch { }
                }

                if (on)
                {
                    if (n > 0) QolLog.Trace("growth", "强化表免费化 " + n + " / " + total + " 条");
                }
                else
                {
                    if (f._enhBackup.Count > 0)
                    {
                        QolLog.Warn("growth: 强化表还原 —— 还有 " + f._enhBackup.Count
                            + " 条原值对不上表内条目（表结构变了？已丢弃）");
                        f._enhBackup.Clear();
                    }
                    if (n > 0) QolLog.Info("growth: 已还原强化表 " + n + " 条原价/材料");
                    if (mismatch > 0) QolLog.Warn("growth: 强化表还原后有 " + mismatch + " 条回读不符（请回报）");
                }
                return n;
            }
            catch (Exception e)
            {
                QolLog.Warn("growth: 强化表 " + (on ? "套用" : "还原") + "失败 -> " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        // ------------------------------------------------------------------
        // C3-a 思装变容免费 —— 表层（v0.2.10-r9 / 修正八）
        // ------------------------------------------------------------------

        /// <summary>
        /// 取「思装变容」全量表。链与强化表同构：
        ///   `ParameterManager.mGrantParameter`(0x98) → `ScriptableGrantParameter.param`(0x18)
        ///   → `List&lt;ConstGrantParameter&gt;`。
        /// 反汇编取证（2026-09-13）：`ItemAbilityGranted` 0x38E04 用 `ParameterManager.GetGrantParameter`(0x4EC50)
        /// 取到 grantParam（0x38E28 存进 rbx），随后 4 处扣料全是
        /// `SubItem(grantParam.MaterialItemIDn, grantParam.MaterialItemNumn)`（0x38EE2/0x38F1F/0x38F5C/0x38F99），
        /// 且前置闸 `CheckItemAbilityGranted`(0x37590) 也逐条读同一对 ID/Num ⇒ **改表即可全链免费**。
        /// </summary>
        private static object GrantTable()
        {
            var pm = ParameterManager.Instance;
            if (pm == null) { WarnOnce("growth.pm3", "ParameterManager.Instance 为空"); return null; }
            object sp = GetMember(pm, "mGrantParameter");        // 0x98（dump:171510）
            if (sp == null) { WarnOnce("growth.grant.field", "读 mGrantParameter(0x98) 失败"); return null; }
            object raw = GetMember(sp, "param");                  // ScriptableGrantParameter.param 0x18
            if (raw == null || CountOf(raw) <= 0) { WarnOnce("growth.grant.empty", "思装变容表 param 为空"); return null; }
            return raw;
        }

        /// <summary>
        /// 思装变容材料数清零 / 还原（与 <see cref="ApplyEnhanceFree"/> 同款：原值备份 + 幂等重放）。
        /// 只清 `mMaterialItemNum1..4`(0x18/0x20/0x28/0x30)，**不动** mMaterialItemIDn：
        ///   • 游戏体内扣料分支判的是 `IDn != 0`（0x38EB4 `test eax,eax`），Num=0 时仍会调
        ///     `SubItem(IDn, 0)`，但 SubItem 自己的守卫 `test ebx,ebx / jle`（0x3A782，实锤）
        ///     当场返回 ⇒ 分文不扣；这一点与已实机验证通过的强化表完全同构。
        ///   • 保留 ID 的好处：UI 素材栏仍能正常渲染图标（数量显示 0），不会出现空 ID 的渲染异常。
        /// ⛔ 修正十六（r17）：备份键＝**表内下标**（188 条静态数据表），理由与实锤见
        /// <see cref="ApplyEnhanceFree"/> 的说明块 —— 这就是玩家报的「关掉还是 0 消耗」的根因所在。
        /// </summary>
        private static int ApplyGrantFree(bool on)
        {
            var f = Inst;
            if (f == null) return -1;
            try
            {
                object list = GrantTable();
                if (list == null)
                {
                    if (!f._grantWarned) { f._grantWarned = true; QolLog.Warn("growth: 取不到思装变容表"); }
                    return -1;   // 表还没就绪 ⇒ 调用方保留重试
                }
                int total = CountOf(list);
                int n = 0;
                int mismatch = 0;
                for (int i = 0; i < total; i++)
                {
                    try
                    {
                        object p = ItemOf(list, i);
                        if (p == null) continue;
                        IntPtr ptr = NativePtr.Of(p);
                        if (ptr == IntPtr.Zero) continue;

                        if (on)
                        {
                            bool allZero = true;
                            int[] cur = new int[GrantMatSlots];
                            for (int s = 0; s < GrantMatSlots; s++)
                            {
                                cur[s] = Marshal.ReadInt32(IntPtr.Add(ptr, GrantNum1Off + 8 * s));
                                if (cur[s] != 0) allZero = false;
                            }
                            if (allZero) continue;   // 已是目标值就不写（幂等重放）

                            if (!f._grantBackup.ContainsKey(i)) f._grantBackup[i] = cur;
                            for (int s = 0; s < GrantMatSlots; s++)
                                Marshal.WriteInt32(IntPtr.Add(ptr, GrantNum1Off + 8 * s), 0);
                            n++;
                        }
                        else
                        {
                            int[] orig;
                            if (!f._grantBackup.TryGetValue(i, out orig)) continue;
                            for (int s = 0; s < GrantMatSlots; s++)
                                Marshal.WriteInt32(IntPtr.Add(ptr, GrantNum1Off + 8 * s), orig[s]);
                            // 回读校验（铁律 9：调用没抛异常 ≠ 生效）
                            bool ok = true;
                            for (int s = 0; s < GrantMatSlots; s++)
                                if (Marshal.ReadInt32(IntPtr.Add(ptr, GrantNum1Off + 8 * s)) != orig[s]) { ok = false; break; }
                            if (ok) { f._grantBackup.Remove(i); n++; }
                            else mismatch++;
                        }
                    }
                    catch { }
                }

                if (on)
                {
                    if (n > 0) QolLog.Trace("growth", "思装变容表免费化 " + n + " / " + total + " 条");
                }
                else
                {
                    if (f._grantBackup.Count > 0)
                    {
                        QolLog.Warn("growth: 思装变容表还原 —— 还有 " + f._grantBackup.Count
                            + " 条原值对不上表内条目（表结构变了？已丢弃）");
                        f._grantBackup.Clear();
                    }
                    if (n > 0) QolLog.Info("growth: 已还原思装变容表 " + n + " 条材料数");
                    if (mismatch > 0) QolLog.Warn("growth: 思装变容表还原后有 " + mismatch + " 条回读不符（请回报）");
                }
                return n;
            }
            catch (Exception e)
            {
                QolLog.Warn("growth: 思装变容表 " + (on ? "套用" : "还原") + "失败 -> " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        // ------------------------------------------------------------------
        // C4 全技能解锁 —— 表层 + 触发重算
        // ------------------------------------------------------------------

        private static object LearningTable()
        {
            var pm = ParameterManager.Instance;
            if (pm == null) { WarnOnce("growth.pm2", "ParameterManager.Instance 为空"); return null; }
            object sp = GetMember(pm, "mLearningParameter");      // 0xD0（dump:171517）
            if (sp == null) { WarnOnce("growth.learn.field", "读 mLearningParameter(0xD0) 失败"); return null; }
            object raw = GetMember(sp, "param");                  // ScriptableLearningParameter.param 0x18
            if (raw == null || CountOf(raw) <= 0) { WarnOnce("growth.learn.empty", "学习表 param 为空"); return null; }
            return raw;
        }

        private static int ApplyAllSkills(bool on)
        {
            var f = Inst;
            if (f == null) return -1;
            int n;
            try
            {
                object list = LearningTable();
                if (list == null)
                {
                    if (!f._skillWarned) { f._skillWarned = true; QolLog.Warn("growth: 取不到学习表"); }
                    return -1;
                }
                int total = CountOf(list);
                n = 0;
                int mismatch = 0;
                for (int i = 0; i < total; i++)
                {
                    try
                    {
                        object p = ItemOf(list, i);
                        if (p == null) continue;
                        IntPtr ptr = NativePtr.Of(p);
                        if (ptr == IntPtr.Zero) continue;
                        int lv = Marshal.ReadInt32(IntPtr.Add(ptr, LearnLevelOff));

                        if (on)
                        {
                            if (lv == LearnLevelMin) continue;   // 幂等判据
                            if (!f._skillBackup.ContainsKey(i)) f._skillBackup[i] = lv;
                            Marshal.WriteInt32(IntPtr.Add(ptr, LearnLevelOff), LearnLevelMin);
                            n++;
                        }
                        else
                        {
                            int orig;
                            if (!f._skillBackup.TryGetValue(i, out orig)) continue;
                            Marshal.WriteInt32(IntPtr.Add(ptr, LearnLevelOff), orig);
                            // 回读校验（铁律 9）
                            if (Marshal.ReadInt32(IntPtr.Add(ptr, LearnLevelOff)) == orig) { f._skillBackup.Remove(i); n++; }
                            else mismatch++;
                        }
                    }
                    catch { }
                }

                if (on)
                {
                    if (n > 0)
                    {
                        QolLog.Info("growth: 学习表已压到 Lv" + LearnLevelMin + "（改动 " + n + " / " + total + " 条）");
                    }
                }
                else
                {
                    // ⛔ 修正十六（r17）：与强化/思装变容同款 —— 备份键是**表内下标**、换场景不清备份、
                    //   还原失败保留重试；这里的收尾也一样：下标对不上才丢弃。
                    if (f._skillBackup.Count > 0)
                    {
                        QolLog.Warn("growth: 学习表还原 —— 还有 " + f._skillBackup.Count
                            + " 条原等级对不上表内条目（已丢弃）");
                        f._skillBackup.Clear();
                    }
                    if (n > 0) QolLog.Info("growth: 已还原学习表 " + n + " 条原等级（已学会的技能保留，属预期）");
                    if (mismatch > 0) QolLog.Warn("growth: 学习表还原后有 " + mismatch + " 条回读不符（请回报）");
                }
                return n;
            }
            catch (Exception e)
            {
                QolLog.Warn("growth: 学习表 " + (on ? "套用" : "还原") + "失败 -> " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        /// <summary>
        /// 对入队角色触发 <c>CharacterParameter.UpdateSkill()</c>。
        /// UpdateSkill 的调用者只有 UpdateLevel —— 改表后必须手动触发才立刻生效。
        ///
        /// ⛔ v0.2.9 实机实锤（debug.log："成功 0 / 失败 0 / 共 2"）：编译期
        /// <c>ItemOf(...) as CharacterComponent</c> + <c>cc.CharacterParameter</c> 在实机**双双静默
        /// continue**（两个角色一个都没走通，且无任何告警）。v0.2.10 改为**纯反射链**：
        /// 元素不落类型转换，CharacterParameter 属性→字段双路取，UpdateSkill 反射调用；
        /// 每个跳过点都留一次性 Warn（铁律 21：诊断日志前置、Warn 而非 DebugThrottled）。
        /// </summary>
        private static void TriggerSkillRecalc()
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm == null) { WarnOnce("growth.gm", "GameManager.Instance 为空，无法触发技能重算"); return; }

                // ── 收集阶段：多来源兜底拿活的 CharacterParameter ──
                // v0.2.10 实机实证：营地/读书房场景下 GM.GetJoinedCharacterList() 列表非空
                // 但 get_Item(0) 全 null（debug.log 2s 轮询 85 次无一幸免）——该列表只在
                // 战斗装配后可靠 ⇒ v0.2.10 修正二改走 PartyManager（GM.mPartyManager 0x90）主路。
                var cps = new List<object>();

                // A0（主路，**技能数据真正所在**）：ParameterManager.mMainCharacterParameter(0x280)
                //    dump 实锤：`private CharacterParameter[] mMainCharacterParameter`（ParameterManager）
                //    + `public CharacterParameter GetMainCharacterParameter(uint index)` 0x4FBC0；
                //    技能 UI（UISkillListWindow.TargetCharacter）读的就是这批对象。
                //    ⛔ 营地场景下 CharacterComponent.mCharacterParameter 恒为 null（组件参数要进战斗才注入），
                //    GM.GetJoinedCharacterList() 元素全 null —— 这两条都不是技能数据源。
                var pmgr = ParameterManager.Instance;
                if (pmgr == null) { WarnOnce("growth.pmgr", "ParameterManager.Instance 为空"); }
                else
                {
                    AddCharacterParams(cps, GetMember(pmgr, "mMainCharacterParameter"), "PMGR.mMainCharacterParameter");
                    // 兜底：数组反射取不到时，按 index 逐个调 GetMainCharacterParameter(uint)
                    if (cps.Count == 0)
                    {
                        for (uint idx = 0; idx < 8; idx++)
                        {
                            object cp = InvokeMember1(pmgr, "GetMainCharacterParameter", idx);
                            if (cp != null) cps.Add(cp);
                        }
                        if (cps.Count == 0)
                            WarnOnce("growth.pmgr.idx", "PMGR.GetMainCharacterParameter(0..7) 全部返回 null");
                    }
                }

                object pm = GetMember(gm, "mPartyManager");
                if (pm == null) pm = GetMember(gm, "PartyManager");
                if (pm == null) { WarnOnce("growth.pm", "取不到 PartyManager（GM.mPartyManager 0x90 / 属性均空）"); }

                if (pm != null)
                {
                    // A1：GetPlayerCharacterParameter() —— 直接返回 CharacterParameter，最稳（玩家必在）
                    object cpDirect = InvokeMember(pm, "GetPlayerCharacterParameter");
                    if (cpDirect != null) cps.Add(cpDirect);
                    else WarnOnce("growth.pm.cp", "PartyManager.GetPlayerCharacterParameter() 返回 null");

                    // A2：GetCharacterList() → List<CharacterComponent>
                    AddCharacterParams(cps, InvokeMember(pm, "GetCharacterList"), "PM.GetCharacterList");
                    // A3：mPlayer 数组（0x40，CharacterComponent[]）
                    AddCharacterParams(cps, GetMember(pm, "mPlayer"), "PM.mPlayer");
                }

                // B：GM.GetJoinedCharacterList()（战斗场景兜底）
                AddCharacterParams(cps, InvokeMember(gm, "GetJoinedCharacterList"), "GM.GetJoinedCharacterList");
                // C：GM.Player（0xB8，当前操作角色）
                AddCharacterParams(cps, GetMember(gm, "Player"), "GM.Player");

                if (cps.Count == 0)
                {
                    WarnOnce("growth.nocp", "所有来源都没拿到 CharacterParameter，本轮跳过");
                    return;
                }

                // ── v0.2.10-r5 收敛闸 ──
                // C4 打开时 OnUpdate 每 2s 重放一次，原实现**每一轮**都跑一次全量 UpdateSkill：
                // 2026-09-13 04:39:22~04:40:32 的 debug.log 实锤 80 轮重算
                // （"#2..#81 技能重算 ok=14 fail=0 n=14"，14 个角色 × 80 次）。
                // UpdateSkill 的调用者只有 UpdateLevel —— 学会的技能就在角色身上、不会自己掉，
                // 频繁重算纯属浪费（且把日志刷爆）⇒ 只在「角色集合签名变化」或「距上次 ≥30s」时真跑。
                string sig = CpSignature(cps);
                int nowMs = Environment.TickCount;
                if (_recalcDone && sig == _lastCpSig && nowMs - _lastRecalcAt < RecalcIntervalMs)
                {
                    QolLog.Trace("growth", "技能重算跳过（角色集合未变，距上次 "
                                           + (nowMs - _lastRecalcAt) + "ms）");
                    return;
                }
                _lastCpSig = sig;
                _lastRecalcAt = nowMs;

                int ok = 0, fail = 0;
                foreach (object cpObj in cps)
                {
                    try
                    {
                        var mi = cpObj.GetType().GetMethod("UpdateSkill",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mi == null)
                        {
                            fail++;
                            WarnOnce("growth.updskill.miss",
                                "growth: " + cpObj.GetType().Name + " 上找不到 UpdateSkill 方法");
                            continue;
                        }
                        mi.Invoke(cpObj, null);
                        ok++;
                    }
                    catch (Exception e)
                    {
                        fail++;
                        if (fail == 1) QolLog.Warn("growth: UpdateSkill 首次失败 -> "
                                                   + e.GetType().Name + ": " + e.Message);
                    }
                }

                if (ok > 0)
                {
                    _recalcDone = true;
                    // 首次成功打 Info，之后每 2s 一轮的重复触发降级 Trace，防刷屏
                    if (!_skillOkLogged)
                    {
                        _skillOkLogged = true;
                        QolLog.Info("growth: 技能重算完成（成功 " + ok + " / 失败 " + fail + " / 共 " + cps.Count + "）");
                    }
                    else
                    {
                        QolLog.Trace("growth", "技能重算 ok=" + ok + " fail=" + fail + " n=" + cps.Count);
                    }
                }
                else
                {
                    WarnOnce("growth.recalc.allfail",
                        "growth: 技能重算全部未生效（成功 0 / 失败 " + fail + " / 共 " + cps.Count + "），详见上方 growth 告警");
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("growth: 触发技能重算失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 从「容器」里收集 CharacterParameter 并入 cps。容器可为：
        /// CharacterParameter 本身（A1 直通）/ List&lt;CharacterComponent&gt; / CharacterComponent[] / 单个 CharacterComponent。
        /// ⛔ 铁律 20/22：容器与元素一律 object + 反射（get_Count/get_Item），属性 get_CharacterParameter
        /// 失败时回退同名字段 mCharacterParameter(0x170)。来源颗粒度 WarnOnce（键=来源名），不刷屏。
        /// </summary>
        private static void AddCharacterParams(List<object> cps, object container, string srcName)
        {
            if (container == null) { WarnOnce("growth.src." + srcName, srcName + " 为 null"); return; }

            // 直通：容器本身就是 CharacterParameter
            if (container.GetType().Name == "CharacterParameter") { cps.Add(container); return; }

            // 单个 CharacterComponent（GM.Player）
            if (container.GetType().Name == "CharacterComponent")
            {
                object cp = GetMember(container, "CharacterParameter");
                if (cp == null) cp = GetMember(container, "mCharacterParameter");
                if (cp != null) cps.Add(cp);
                else WarnOnce("growth.src." + srcName, srcName + " 的 CharacterParameter 取不到");
                return;
            }

            int n = CountOf(container);
            int got = 0;
            for (int i = 0; i < n; i++)
            {
                object el = ItemOf(container, i);
                if (el == null) continue;
                // 元素本身就是 CharacterParameter（PMGR.mMainCharacterParameter 就是这种情况）
                if (el.GetType().Name == "CharacterParameter") { cps.Add(el); got++; continue; }
                object cp = GetMember(el, "CharacterParameter");
                if (cp == null) cp = GetMember(el, "mCharacterParameter");
                if (cp != null) { cps.Add(cp); got++; }
            }
            if (got == 0)
                WarnOnce("growth.src." + srcName,
                    srcName + " 共 " + n + " 项，但一个 CharacterParameter 都没取到（元素 null / 字段空）");
        }

        /// <summary>
        /// 角色集合签名（v0.2.10-r5）：用**原生对象指针**而不是托管哈希 ——
        /// unhollower 每次封送都产生新的代理实例（GetHashCode/ReferenceEquals 都不稳定，
        /// 见 FeatureCampConfig 的 MARKER 注释），只有 native 指针跨轮次恒定。
        /// </summary>
        private static string CpSignature(List<object> cps)
        {
            var ptrs = new List<long>();
            foreach (object o in cps)
            {
                long v = 0;
                try { v = NativePtr.Of(o).ToInt64(); } catch { }
                ptrs.Add(v);
            }
            ptrs.Sort();
            var sb = new System.Text.StringBuilder();
            foreach (long p in ptrs)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(p.ToString("X"));
            }
            return sb.ToString() + "#" + cps.Count;
        }

        // ------------------------------------------------------------------
        // 补丁：抑制窗口 + SubItem 拦截
        // ------------------------------------------------------------------

        /// <summary>强化 UI 包装器（实例 0x174970，static ItemEnhancement 的唯一调用者）。</summary>
        [HarmonyLib.HarmonyPatch(typeof(UIRoomReadingEnhancementListWindow), "ItemEnhancement")]
        private static class EnhanceWrapperPatch
        {
            private static void Prefix()
            {
                var f = Inst;
                bool on = f != null && f._freeEnhance != null && f._freeEnhance.Value;
                // ⛔ v0.2.10-r6：**无条件日志**（不再包在 if 里）——先分清"补丁没被调用"与"开关没开"。
                // 上一版把日志放在 if(开关开) 里面，导致开关关着时"没被调用"和"被调用但没开开关"
                // 两种完全不同的病因在日志上长得一模一样。
                QolLog.Info("growth: 强化窗口开（UI 包装器已触发；FreeEnhance=" + (on ? "ON" : "OFF") + "）");
                if (!on) return;
                SuppressSubItem = true;   // 补丁体跑两遍也幂等
                _enhWin = true;
                _enhSubIdHits = 0;
                _enhSubUidHits = 0;
            }

            private static Exception Finalizer(Exception __exception)
            {
                if (_enhWin)
                {
                    _enhWin = false;
                    // 拦截次数是判据：0 次 ⇒ 前置闸 CheckItemEnhancement 没过（扣料前就 return 了）
                    QolLog.Info("growth: 强化窗口闭 —— 拦下 SubItem(id)×" + _enhSubIdHits
                                + " / SubItem(uid)×" + _enhSubUidHits);
                }
                SuppressSubItem = false;      // 无论正常返回还是抛异常都清旗，窗口绝不泄漏
                return __exception;           // 原样回抛，不吞游戏异常
            }
        }

        // ==================================================================
        // v0.2.10-r7：**static 挂点全部废弃** + 观测点（全部实例方法）
        //
        // 先前的疑问（用户「我每次都有做思装变容」vs 日志空白）已在 2026-09-13 定案，
        // 铁证在 **MelonLoader/Latest.log**（Harmony 的异常只写这里，**不进我们的 debug.log**）：
        //   04:39:42.397 与 04:39:52.838 各一条
        //   `[ERROR] Exception in Harmony patch of method static Game.HaveItemInfo
        //    Game.ItemManager::ItemAbilityGranted(Game.HaveItemInfo haveItemInfo, int slot):
        //    UnhollowerBaseLib.Il2CppException: System.NullReferenceException`
        // ⇒ ① 玩家**确实**做了两次思装变容（补丁被调到了）；② 但 Prefix 在 Harmony 的参数封送阶段
        //   就炸了，**我们的日志一行都打不出来**（与 debug.log 空白完全吻合）。
        // 机理：IL2CPP static 方法的第 1 个寄存器参数是隐藏 `MethodInfo*`，调用点写的是
        // `xor ecx,ecx`（= NULL，见 0x1AE3AE 前一条）；Harmony 按托管签名把 rcx 当 `HaveItemInfo`
        // 封送 ⇒ 解引用 NULL。**这比铁律 10.8 更狠：不是"别注入参数"，而是"有参 static 方法根本挂不了"**。
        // 对照：项目里所有实机生效的挂点全是实例方法；三个 static 挂点（ItemEnhancement /
        // ItemCreation / ItemAbilityGranted）从未产生过任何日志 —— r7 把这三个全部删除。
        // 观测链（全部实例方法，一次实机即可画出玩家的完整操作路径）：
        //   UIRoomReadingMenuWindow.OnDecision（读书房菜单按确定）
        //   → UIRoomGrantSelectEquipmentWindow.OnDecision（选装备）
        //   → UIRoomGrantSelectSlotWindow.OnPress(int)（选槽位）
        //   → UIRoomGrantSelectSlotWindow.OpenCheckWindow（弹确认框）
        //   → UISystemMessageBoxWindow.OnPress / OnCross（玩家选是 / 否）
        //   → [游戏自己 call ItemManager.ItemAbilityGranted（不可挂）]
        //   → ItemManager.SubItem(eItemID,int)（实例，✅ 记账点）
        // ==================================================================

        /// <summary>观测点：读书房菜单按确定（玩家进的是净化 / 强化 / 思装变容 三条里的哪一条）。</summary>
        [HarmonyLib.HarmonyPatch(typeof(UIRoomReadingMenuWindow), "OnDecision")]
        private static class ReadingMenuProbe
        {
            private static void Prefix() { QolLog.Info("growth.probe: 读书房菜单 —— 按了确定"); }
        }

        /// <summary>观测点：思装变容第 1 步「选装备」窗口按确定。</summary>
        [HarmonyLib.HarmonyPatch(typeof(UIRoomGrantSelectEquipmentWindow), "OnDecision")]
        private static class GrantEquipProbe
        {
            private static void Prefix() { QolLog.Info("growth.probe: 思装变容 —— 选装备窗按了确定"); }
        }

        /// <summary>观测点：思装变容第 2 步「选槽位」窗口按确定。</summary>
        [HarmonyLib.HarmonyPatch(typeof(UIRoomGrantSelectSlotWindow), "OnPress", new Type[] { typeof(int) })]
        private static class GrantSlotProbe
        {
            private static void Prefix() { QolLog.Info("growth.probe: 思装变容 —— 选槽位窗按了确定"); }
        }

        /// <summary>
        /// 观测点：思装变容第 3 步「弹确认框」（UIRoomGrantSelectSlotWindow.OpenCheckWindow 0x1ADB00）。
        /// 弹框之后走的是异步弹窗回调 ⇒ 本行的存在只证明"玩家走到了确认这一步"。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(UIRoomGrantSelectSlotWindow), "OpenCheckWindow")]
        private static class GrantCheckWindowProbe
        {
            private static void Prefix() { QolLog.Info("growth.probe: 思装变容 —— 弹出确认框（等玩家选是/否）"); }
        }

        /// <summary>
        /// 净化 UI 包装器（实例 0x172460，static ItemCreation 的唯一调用者）。
        /// 游戏内中文＝"净化"（eMenu.CREATION=0：净化幽怨出产物），C3 开关罩的就是它。
        /// v0.2.10-r5：加诊断日志（与强化同款）——用户实机反馈"思装变容不扣材料但没赋予属性"，
        /// 而 2026-09-13 04:37/04:38 两次会话的 debug.log 里**连一条 C3 路径日志都没有**
        /// （说明玩家那一次操作根本没进到包装器，或开关没开）⇒ 先把"入口有没有被触发"打出来再谈改行为。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(UIRoomReadingCreationWindow), "ItemCreation", new Type[] { })]
        private static class CreationWrapperPatch
        {
            private static void Prefix()
            {
                // 修正十五（r16）：净化只看 `FreeCreation`，与思装变容（`FreeGrant`）互不干扰
                bool on = PurifyFreeOn();
                // ⛔ v0.2.10-r6：无条件日志（理由同强化包装器）
                QolLog.Info("growth: 净化窗口开（UI 包装器已触发；净化幽怨无消耗=" + (on ? "ON" : "OFF")
                            + " / 思装变容无消耗=" + (GrantFreeOn() ? "ON" : "OFF") + "）");
                if (!on) return;
                SuppressSubItem = true;
                _creWin = true;
                _creSubIdHits = 0;
                _creSubIdNonZero = 0;
                _creSubIdSum = 0;
                _creAddFail = 0;
                _creAddTotal = 0;
                // r13：净化前先量两套账（实体 / 登记表），便于解释"UI 显示 10 却产出 11 件"
                _creHeldBefore = CountDeathcryHeld();
                _creRegBefore = DeathcryRegistryCount();
            }

            private static Exception Finalizer(Exception __exception)
            {
                if (_creWin)
                {
                    _creWin = false;
                    // 0 次 ⇒ 前置闸（材料不足）在扣料前就 return 了，我们的抑制旗没派上用场。
                    // 修正九（r10）：附上槽池现状 —— 用它实机确认"净化到底吃了几个幽怨 / 槽池有没有补开"。
                    string extra = "";
                    try
                    {
                        var im = ItemManager.Instance;
                        if (im != null) { string s = PoolSummary(im); if (s != null) extra = "；" + s; }
                    }
                    catch { }
                    // 修正十一：把"产出件数"直接打出来 —— 净化是**一次处理全部持有幽怨**，
                    // 所以件数 = 本次被净化的幽怨数（双跑 ⇒ 记数会翻倍，这里除以 2 报真值）。
                    int made = _creAddTotal / 2;
                    QolLog.Info("growth: 净化窗口闭 —— 拦下 SubItem(id)×" + _creSubIdHits
                                + "（其中 count>0 的 " + _creSubIdNonZero + " 笔 / Σcount=" + _creSubIdSum + "）"
                                + "；净化前持有幽怨 " + _creHeldBefore + " 个（登记表记 " + _creRegBefore + " 个）"
                                + "；产出 AddItem " + made + " 件（原始计数 " + _creAddTotal + " 已按双跑折半）"
                                + "；AddItem 失败 " + _creAddFail + " 笔" + extra);
                }
                SuppressSubItem = false;
                return __exception;
            }
        }

        // ── v0.2.10-r9（修正八）：思装变容的「窗口 / 记账 / 快照 / 返还」机器已**全部删除** ──
        // 历史：修正三起是"原样扣料 + 事后 AddItem 补回"，r7 把窗口改由实例方法链驱动
        // （OpenCheckWindow 亮旗 → 确认框 OnPress 开 3 秒窗 → OnUpdate 收尾返还），r8 又把返还量
        // 改成"持有量差值"以对抗双跑翻倍 —— 每一版都在跟铁律 1（补丁体跑两遍）搏斗，
        // 而用户在 2026-09-13 15:25 实机抓到"思装变容材料强化完反向变多"（1 次真实
        // SubItem(262,4) 被记成 2 笔 ⇒ 返还 8）。
        // 修正八改为**改表**（ApplyGrantFree，与强化同款）：材料数在源头就是 0、扣料根本不发生，
        // 于是整条补偿链不再需要，这个 bug 类别被连根拔掉。
        // 已删除：GrantAskPatch(OpenCheckWindow 亮旗) / GrantConfirmPressPatch(OnPress 开窗) /
        //         GrantConfirmCrossPatch(OnCross 丢旗) / GrantWindow 状态机 /
        //         RefundGrantMaterials() / ItemCountOf()（r8 为"持有量差值"返还而加）。
        // 保留：本文件后段的只读观测点（ReadingMenuProbe / GrantEquipProbe / GrantSlotProbe /
        //       GrantCheckWindowProbe）—— 根因若有反复，先看它们的四行日志。
        /// <summary>扣物（按 eItemID）：净化的输入物品走这里。窗口亮时跳过。</summary>
        [HarmonyLib.HarmonyPatch(typeof(ItemManager), "SubItem", new Type[] { typeof(eItemID), typeof(int) })]
        private static class SubItemByIdPatch
        {
            private static bool Prefix(eItemID itemID, int count, ref ItemManager.eError __result)
            {
                // r9 起思装变容走改表（材料数源头为 0）⇒ 这里不再有记账分支，只剩净化窗口的抑制。
                if (!SuppressSubItem) return true;
                // 诊断计数（修正四/r5）：按当前是哪个窗口开的来归属，便于分辨强化/净化
                if (_creWin) { _creSubIdHits++; if (count > 0) { _creSubIdNonZero++; _creSubIdSum += count; } }
                else if (_enhWin) _enhSubIdHits++;
                __result = ItemManager.eError.ERROR_NONE;   // 假装扣成功，让游戏流程继续（产出照常入包）
                Hot.SubItemSkip++;
                return false;
            }
        }

        /// <summary>扣物（按 uniqueID）：强化的"选定同名素材堆扣 1 个"走这里。</summary>
        [HarmonyLib.HarmonyPatch(typeof(ItemManager), "SubItem", new Type[] { typeof(uint), typeof(int) })]
        private static class SubItemByUniqueIdPatch
        {
            private static bool Prefix(ref ItemManager.eError __result)
            {
                if (!SuppressSubItem) return true;
                _enhSubUidHits++;                      // 诊断计数（修正四）
                __result = ItemManager.eError.ERROR_NONE;
                Hot.SubItemSkip++;
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 修正九（r10）：幽怨槽池扩容 —— C3-b「净化不消耗幽怨」的**必要配套**
        //
        // 【为什么必须扩容】（本机 GameAssembly.dll 反汇编 + parameter 包取证，2026-09-13）
        //   1) 幽怨 = `eItemType.DEATHCRY(7)`，共 14 种（`eItemID` 237..250 = DEATHCRY_001..014），
        //      `ConstItemParameter.mMaxCount`（0x78）**全 = 1**；而 `AddItem`(0x36F20) 的类型分派
        //      跳转表把 DEATHCRY 归到"走 `ScoopHaveItemInfo` 新槽"那一支（与 CONSUME/MATERIA 的
        //      "复用同 ID 堆"相反）⇒ **每种幽怨各占 1 个独立槽位，不可叠加**。
        //   2) 幽怨槽池 = `ItemManager.mFreeItemLists[6]`（`GetFreeItemList` 0x37D90 实锤），
        //      容量 = `static readonly int[] freeItemCounts[6]`：`Reset()`(0x3A080) 里
        //      `start = 0; for type in 0..7: start = InitializeItem(start, type)`，
        //      而 `InitializeItem`(0x385B0) 体内 `end = startIndex + freeItemCounts[type]`，
        //      再逐槽从**主槽位数组**取对象分派进 `mHaveItemList`（`[slot+0x14]!=0` = 占用）
        //      或 `mFreeItemLists[type]`（空闲）。实机 = 10 ⇒ **同时最多持有 10 种幽怨**。
        //   3) 槽池空 ⇒ `ScoopHaveItemInfo`(0x3A100) 的 `if (Count(list) == 0) return null`
        //      ⇒ `AddItem` 返回 null 且 `[*error] = ERROR_OVER_ITEM_COUNT(4)`（0x371F3）
        //      ⇒ **整笔丢弃**，玩家什么都拿不到。
        //   4) 净化产出表 `ConstCreationParameter`（59 条，16 字节/条 = itemID/creationItemID/weight/
        //      startProgress）**按 `mStartProgress` 门槛解锁**，且**取决于投进去的是哪种幽怨**：
        //        后期强力装备（prog 361000）只有 DEATHCRY_003 / DEATHCRY_006 才出
        //        （WEAPON_018/045/068/087、ARMOR_043/048、ACCESSORY_016..020）。
        //      ⇒ 若早期幽怨永久占满 10 格、后期幽怨进不来，**玩家永远拿不到后期装备**。
        //      （这正是用户 2026-09-13 提出的担忧，此处以二进制证据确认成立。）
        //
        // 【修法】在 `Reset` / `Initialize` **之后**把 DEATHCRY 空闲槽池补到 `DeathcryPoolTarget` 个：
        //   只往游戏的 `List<HaveItemInfo>` 本体 `Add` 空槽 —— 空槽（mItemID=0）由游戏自己判定为"空闲"，
        //   只有被 `ScoopHaveItemInfo` 取走并捞进 `mHaveItemList` 之后才算占用（才会进存档）
        //   ⇒ **不写存档、不影响其它类型**。
        //   ⛔ 绝不改 `freeItemCounts`：它在 `Reset` 里是 InitializeItem 的**累积切片游标**
        //      （0 → +count[0] → +count[1] …）指向主槽位数组，改大 ⇒ InitializeItem 越界
        //      （`jae` → `ThrowHelper`）**直接崩**。
        //   ⛔ 绝不再调一次 `InitializeItem`（那会从别的类型的主槽位切片里"偷"槽，破坏 ARMOR 等）。
        // ------------------------------------------------------------------

        /// <summary>
        /// `ItemManager.Reset()`（0x3A080，实例方法 ✅ 可挂）Postfix —— 槽池刚建完，补槽的正时点。
        /// 只挂实例方法（铁律 24：IL2CPP static 一律不可挂）。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(ItemManager), "Reset", new Type[] { })]
        private static class ItemPoolResetPatch
        {
            private static void Postfix() { Hot.Reset++; _poolReady = true; EnsureDeathcryPool("Reset"); }
        }

        /// <summary>`ItemManager.Initialize()`（0x387C0，实例方法 ✅）Postfix —— 开档/读档兜一次。</summary>
        [HarmonyLib.HarmonyPatch(typeof(ItemManager), "Initialize", new Type[] { })]
        private static class ItemPoolInitializePatch
        {
            private static void Postfix() { Hot.Init++; _poolReady = true; EnsureDeathcryPool("Initialize"); }
        }

        /// <summary>
        /// 只读诊断（修正九）：净化窗口期间 `AddItem` 失败的笔数。
        /// `AddItem`(0x36F20) 在背包满时返回 null + `[*error] = ERROR_OVER_ITEM_COUNT(4)` ⇒ **整笔丢弃**；
        /// "净化一次之后就不能再来一次"的头号嫌疑就是**产出装备把装备包塞满**（与幽怨无关，原版同样存在）。
        /// 只读计数，不改行为（绝不碰 AddItem 的返回值）。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(ItemManager), "AddItem")]
        private static class AddItemFailProbe
        {
            private static void Postfix(ref ItemManager.eError error)
            {
                if (!_creWin) return;
                _creAddTotal++;                                              // 修正十一：到底产了几件
                if (error != ItemManager.eError.ERROR_NONE) _creAddFail++;
            }
        }

        // ==================================================================
        // 修正十（r11）：幽怨 FIFO 顶替
        // ------------------------------------------------------------------
        // 【为什么必须做，而且比"扩容"更对】
        //  1) 玩家诉求原话：「光扩容也不是办法吧？如果幽怨槽池满了能否按先进先出的原则自动顶掉
        //     最一开始获取的幽怨，而不是拿不了新的幽怨？」
        //  2) 拿不到新幽怨的**真闸**已实锤：`BattleManager.AddItem(eItemID, eCharaID, int)`
        //     （**static** 0x1B6A20，⛔ 铁律 24 不可挂）先调 `ItemManager.AddItem`，
        //     返回 null 时跳 0x1B6E5F 分支 → `UIGameMainWindow.AddActionLogGetItemCountOver(itemID)`
        //     （0x201750，"持有数超限"）。而 `AddItem` 返回 null 的唯一原因 = `ScoopHaveItemInfo`
        //     （0x3A100）开头 `if (freeList.Count == 0) return null`。
        //  3) ⛔ 为什么**不推荐扩容**：`UserParameter.mDeathCryCharaID`（0x60，`eCharaID[]`）是**硬编码
        //     10 格**的登记表 —— `GetDeathCryCount`(0x1EBB00)、`AddDeathCryCharaID`(0x1EB810)、
        //     `ClearDeathCryCharaID`(0x1EBA80) 三处循环上界全是 `cmp edx, 0xa`；
        //     `AddDeathCryCharaID` 是"找第一个 0 槽写入"，10 格满了就**静默丢弃**。
        //     ⇒ 池子扩到 20 时第 11 个之后的幽怨在这张表里无处安放，UI（UIDeathCryWindow.OpenWindow
        //     0xE05D3 / UIRoomReadingMenuWindow.OnDecision 0x1789CC 都读 GetDeathCryCount）也只认 10。
        //     ⇒ **保持原生 10 格 + 满时顶掉最早**，UI 与存档才自洽（这也是"UI 还是显示 10 个"的正确答案）。
        //  4) 【幂等】铁律 1：同一个游戏调用补丁体会跑两遍 ⇒ 第一遍顶掉后空闲槽 > 0，第二遍直接返回。
        // ==================================================================

        /// <summary>修正十五（r16）：`FreeCreation` 开关 —— 只管**净化**（默认 false）。</summary>
        private static bool PurifyFreeOn()
        {
            var f = Inst;
            return f != null && f._freeCreation != null && f._freeCreation.Value;
        }

        /// <summary>修正十五（r16）：`FreeGrant` 开关 —— 只管**思装变容**（默认 false）。
        /// 与 <see cref="PurifyFreeOn"/> 彻底解耦：开哪个就只影响哪条路径。</summary>
        private static bool GrantFreeOn()
        {
            var f = Inst;
            return f != null && f._freeGrant != null && f._freeGrant.Value;
        }

        /// <summary>`DeathcryFifo` 开关（默认 true）。</summary>
        private static bool FifoOn()
        {
            var f = Inst;
            return f != null && f._deathcryFifo != null && f._deathcryFifo.Value;
        }

        /// <summary>`DeathcryPoolExpand` 开关（默认 false）。</summary>
        private static bool ExpandOn()
        {
            var f = Inst;
            return f != null && f._deathcryPoolExpand != null && f._deathcryPoolExpand.Value;
        }

        /// <summary>`DeathcryIconExpand` 开关（默认 true）。</summary>
        private static bool IconExpandOn()
        {
            var f = Inst;
            return f != null && f._deathcryIconExpand != null && f._deathcryIconExpand.Value;
        }

        /// <summary>`DeathcryCountScale` 开关（默认 false，实验性）。</summary>
        private static bool CountScaleOn()
        {
            var f = Inst;
            return f != null && f._deathcryCountScale != null && f._deathcryCountScale.Value;
        }

        /// <summary>r14：**当前有效幽怨上限**（不是空闲槽数！）。扩容开 = 20，否则原版 10。</summary>
        private static int DeathcryCap()
        {
            return ExpandOn() ? DeathcryPoolTarget : DeathcryPoolNative;
        }

        // ------------------------------------------------------------------
        // 修正十三（r14）：容量判据 = **空闲 + 持有**，不是"空闲"
        // ------------------------------------------------------------------
        // 【r10~r13 的 bug（2026-09-14 玩家复现 + 日志实锤）】
        //   `GrowDeathcryPool` 的循环是 `while (CountOf(freePool) < 20) Add(new HaveItemInfo())`
        //   —— 它盯的是**空闲槽数**。于是：
        //     拿 1 个幽怨 ⇒ 空闲 20→19 ⇒ 下一轮补回 20 ⇒ **总容量 21**；再拿 ⇒ 22 …
        //   ⇒ 上限**形同虚设**，容量 = 20 + 已持有数。
        //   日志实证（debug.log）：
        //     35653 行 `幽怨槽池 0 -> 20（+20）[Initialize]`  ⇒ 空闲池先被填满 20
        //     36445 行 `幽怨槽池 5 -> 20（+15）[OnUpdate]`   ⇒ 拿走 15 个后又补回 20（容量已 35）
        //     36538 行 `净化前持有幽怨 18 个`                ⇒ 15 + 3 = 18，玩家所见即此
        //   连带后果：空闲池恒 > 0 ⇒ FIFO 判据 `空闲 == 0` **永不成立** ⇒ 一次都没顶替
        //     （23:25 那一整段日志里一条 `growth.fifo` 都没有）。
        //
        // 【铁律（规则 29）】**"容量"必须按 空闲 + 已占用 算，绝不能拿空闲数当容量。**
        //   空闲池补满 = "随时能装"，不等于"只能装这么多"；只有 `ScoopHaveItemInfo` 取走时才占位，
        //   而取走后空闲又少一个 —— 若按空闲数补，补的速度永远跟得上消耗速度。
        // ------------------------------------------------------------------

        /// <summary>r14：取 DEATHCRY 空闲槽池对象（`mFreeItemLists[6]`），取不到返回 null。</summary>
        private static object DeathcryFreePool(ItemManager im)
        {
            object lists = GetMember(im, "mFreeItemLists");   // private List<HaveItemInfo>[] 0x48
            if (lists == null) { WarnOnce("growth.pool.field", "取不到 mFreeItemLists"); return null; }
            object pool = ArrayElem(lists, DeathcryPoolIndex);
            if (pool == null)
            {
                WarnOnce("growth.pool.elem", "mFreeItemLists[6] 取不到（容器 " + TypeNameOf(lists) + "）");
                return null;
            }
            return pool;
        }

        /// <summary>r14：**当前总容量** = 空闲槽数 + 已持有幽怨数（这是唯一正确的容量口径）。
        /// 任一项取不到时返回 -1（诊断用，不要拿它做拦截判据）。</summary>
        private static int DeathcryCapacity(object pool)
        {
            int free = CountOf(pool);
            if (free < 0) return -1;
            int held = CountDeathcryHeld();
            if (held < 0) return -1;
            return free + held;
        }

        /// <summary>
        /// `ItemManager.AddItem`(0x36F20，实例方法 ✅) 的 Prefix：**只在"幽怨已经持有到上限"时**
        /// 顶掉最早获取的那个幽怨，让新幽怨永远进得来。
        ///
        /// 判据：① 入包 ID ∈ [237, 261]（DEATHCRY_001..025）；② **持有数 ≥ 当前上限**
        /// （⛔⛔ r14：原来是"空闲槽池 Count == 0"，那是**错的** —— 空闲池会被 `SyncDeathcryPool`
        /// 补满，`空闲==0` 几乎永不成立 ⇒ FIFO 形同虚设；实证见 <see cref="DeathcryCap"/> 的注释）。
        ///
        /// 其余类型（净化产出的装备、掉落物、购买物）**完全不碰**（ID 区间直接挡掉，零副作用）。
        ///
        /// 【幂等】铁律 1：同一个游戏调用补丁体会跑两遍 ⇒ 第一遍顶掉后 **持有数真的 -1**，
        /// 第二遍 `held &lt; cap` 直接返回（比旧判据更稳：旧判据靠"空闲槽多了"间接成立）。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(ItemManager), "AddItem")]
        private static class DeathcryFifoPatch
        {
            private static void Prefix(eItemID itemID)
            {
                try
                {
                    Hot.AddItem++;
                    if (!FifoOn()) return;
                    int id = (int)itemID;
                    if (id < DeathcryIdMin || id > DeathcryIdMax) return;   // 不是幽怨 ⇒ 原版行为

                    var im = ItemManager.Instance;
                    if (im == null) return;

                    object pool = DeathcryFreePool(im);
                    if (pool == null) return;

                    int cap = DeathcryCap();
                    int held = CountDeathcryHeld();

                    if (held >= 0)
                    {
                        if (held < cap) return;      // 还没到上限 ⇒ 什么都不做（幂等 + 主路径）
                    }
                    else if (CountOf(pool) > 0)
                    {
                        return;                      // 数不出来 ⇒ 退化成旧判据，宁可不动
                    }

                    // r13：**扩容优先于顶替**。开着 `DeathcryPoolExpand` 时先把池子同步到目标容量，
                    // 同步完若仍未到上限（腾出了空位）就不必顶替 —— 免掉"刚在营地打开开关、
                    // 还没到 2s 重放就被顶掉"这种时序坑（2026-09-14 实机）。
                    if (ExpandOn())
                    {
                        SyncDeathcryPool("Fifo");
                        int h2 = CountDeathcryHeld();
                        if (h2 >= 0 && h2 < DeathcryCap()) return;
                    }

                    Hot.Evict++;
                    EvictOldestDeathcry(im, id);
                }
                catch (Exception e)
                {
                    WarnOnce("growth.fifo.ex", "AddItem -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// 在 `mHaveItemList` 里找 **最早获取** 的幽怨（`mLastGetTime` 最小，并列取 `mUniqueID` 最小），
        /// 用游戏自己的 `SubItem(uint uniqueID, int count)` 把它删掉 ⇒ 槽位回到 `mFreeItemLists[6]`。
        /// ⛔ 不直接搬对象回空闲池（`SubItem` 还负责存档与其它账本），⛔ 也不碰 `mLock` 的道具。
        /// </summary>
        private static void EvictOldestDeathcry(ItemManager im, int incomingId)
        {
            object have = GetMember(im, "mHaveItemList");
            int n = CountOf(have);
            if (n <= 0) { WarnOnce("growth.fifo.empty", "mHaveItemList 为空，无法顶替"); return; }

            object best = null;
            ulong bestT = ulong.MaxValue;
            uint bestUid = uint.MaxValue;
            int bestId = 0;

            for (int i = 0; i < n; i++)
            {
                object it = ItemOf(have, i);
                if (it == null) continue;
                int itemId = ToInt(GetMember(it, "mItemID"));
                if (itemId < DeathcryIdMin || itemId > DeathcryIdMax) continue;   // 只看幽怨
                if (ToBool(GetMember(it, "mLock"))) continue;                     // 锁着的跳过
                ulong t = ToULong(GetMember(it, "mLastGetTime"));
                uint uid = ToUInt(GetMember(it, "mUniqueID"));
                if (best == null || t < bestT || (t == bestT && uid < bestUid))
                {
                    best = it; bestT = t; bestUid = uid; bestId = itemId;
                }
            }

            if (best == null)
            {
                WarnOnce("growth.fifo.none", "持有列表里没有可顶替的幽怨（id=" + incomingId + "）");
                return;
            }

            // 走游戏自己的删除路径（0x3A570，实例方法；SellItem 与 ItemEnhancement 都在调它 ⇒ 运行期可用）
            var ret = im.SubItem(bestUid, 1);
            _fifoEvicts++;
            // ⚠ r15：`incomingId == 0` 表示这是**关掉扩容后的回落**（`ShrinkDeathcryOverflow` 传 0），
            //   不是"有新幽怨要进来"。旧日志一律写"给新幽怨 id=0 腾位"，很容易被误读成 bug。
            QolLog.Info("growth.fifo: 幽怨满 → 顶掉最早的一个（id=" + bestId + " / uid=" + bestUid
                        + " / t=" + bestT + "）→ SubItem=" + ret
                        + (incomingId > 0
                            ? "，给新幽怨 id=" + incomingId + " 腾位"
                            : "（关扩容后回落，没有新幽怨）")
                        + "（累计 " + _fifoEvicts + " 次）");
        }

        /// <summary>r13：数一遍**实体**持有列表（`ItemManager.mHaveItemList`）里的幽怨个数。
        /// 只读、不改动任何东西；取不到返回 -1（诊断用，失败不影响主流程）。</summary>
        private static int CountDeathcryHeld()
        {
            try
            {
                var im = ItemManager.Instance;
                if (im == null) return -1;
                object have = GetMember(im, "mHaveItemList");
                int n = CountOf(have);
                if (n <= 0) return 0;
                int c = 0;
                for (int i = 0; i < n; i++)
                {
                    object it = ItemOf(have, i);
                    if (it == null) continue;
                    int id = ToInt(GetMember(it, "mItemID"));
                    if (id >= DeathcryIdMin && id <= DeathcryIdMax) c++;
                }
                return c;
            }
            catch (Exception e)
            {
                WarnOnce("growth.cre.count.ex", e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        /// <summary>r13：`UserParameter.GetDeathCryCount()`（0x1EBB00）—— **登记表**口径，
        /// 循环上界硬编码 10（`cmp edx,0xa`），所以这个值**永远 ≤ 10**，与实体数可以不一致。
        /// ⛔ 铁律 18/20：不信编译期类型，实例与调用都走反射；取不到返回 -1。
        ///
        /// ⛔⛔ r14 修正（实机日志 36536 行 `growth.cre.reg.inst: 取不到 UserParameter 单例`）：
        ///   `UserParameter` **根本没有静态单例**（dump 173887 起只有 // Fields，无 Instance / mInstance）。
        ///   真源是 `ParameterManager.mUserParameter`(0x270) + `get_UserParameter()`(dump 171569/171584/171595)。
        ///   ⇒ 先走 `ParameterManager.Instance`，拿不到再退化成"按类型找静态属性"。</summary>
        private static int DeathcryRegistryCount()
        {
            try
            {
                object up = UserParameterInstance();
                if (up == null) { WarnOnce("growth.cre.reg.inst", "取不到 UserParameter 实例"); return -1; }

                var mi = up.GetType().GetMethod("GetDeathCryCount",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi == null) { WarnOnce("growth.cre.reg.mi", "UserParameter.GetDeathCryCount 不存在"); return -1; }
                return ToInt(mi.Invoke(up, null));
            }
            catch (Exception e)
            {
                WarnOnce("growth.cre.reg.ex", e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        /// <summary>r14：拿 `UserParameter` 运行期实例。
        /// 主路径 `ParameterManager.Instance` → `mUserParameter`(0x270) / `get_UserParameter()`；
        /// 兜底：全程序域里按类型找静态属性（有些版本的 IL2CPP dump 会漏掉字段）。</summary>
        private static object UserParameterInstance()
        {
            try
            {
                var pm = ParameterManager.Instance;
                if (pm != null)
                {
                    object up = GetMember(pm, "mUserParameter");           // 0x270
                    if (up == null)                                        // 属性路径兜底
                    {
                        var g = pm.GetType().GetMethod("get_UserParameter",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (g != null) { try { up = g.Invoke(pm, null); } catch { } }
                    }
                    if (up != null) return up;
                }
            }
            catch (Exception e)
            {
                WarnOnce("growth.cre.up.ex", "ParameterManager 路径 -> " + e.GetType().Name + ": " + e.Message);
            }

            // 兜底（r13 原逻辑，保留以防别的入口）
            var t = typeof(UserParameter);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!t.IsAssignableFrom(p.PropertyType)) continue;
                try { object v = p.GetValue(null); if (v != null) return v; } catch { }
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!t.IsAssignableFrom(f.FieldType)) continue;
                try { object v = f.GetValue(null); if (v != null) return v; } catch { }
            }
            return null;
        }

        // ==================================================================
        // 修正十一（r12）：拦下 `UserParameter.ClearDeathCryCharaID()`
        // ------------------------------------------------------------------
        // 【症状（2026-09-13 23:5x 实机）】
        //   净化完幽怨 UI 仍显示 10/10 满（说明**幽怨确实保留了**），但再点净化 ⇒ 提示「未持有幽怨」，
        //   且读书房菜单按确定后**根本不再打开净化窗**（日志 23:54:13/14 只有
        //   `growth.probe: 读书房菜单 —— 按了确定`，后面没有 `净化窗口开`）。
        //
        // 【真因（本机二进制实锤）】
        //   幽怨有两套账：① `ItemManager.mHaveItemList` 里的**实体道具**（我们靠抑制 SubItem 保留了）；
        //   ② `UserParameter.mDeathCryCharaID`（`eCharaID[]` @0x60，**硬编码 10 格**）这张**登记表**。
        //   · 净化执行体：`UIRoomReadingCreationWindow.ItemCreation()`(0x172460) —— 由 `OpenWindow`
        //     (0x172E80 @0x172EC2) 直接调用；体内遍历 `ItemManager.GetHaveItemList(DEATHCRY)`(0x38190)
        //     并逐个 `ItemManager.ItemCreation(HaveItemInfo, createCount)`(static 0x393B0) ⇒
        //     **一次净化处理你手上的全部幽怨**，每个幽怨产 1 件 ⇒ 10 幽怨 = 10 件装备（原版就如此）。
        //   · 净化演出结束的回调是 `UIRoomReadingCreationWindow.<OpenWindow>m__2`（**static** 0x173490，
        //     ⛔ 铁律 24 不可挂），体内 @0x1734F4 调 `UserParameter.ClearDeathCryCharaID()`(0x1EBA80)
        //     —— 把登记表 10 格全清零。
        //   · 而**净化菜单的门闸读的是登记表**：`UIRoomReadingMenuWindow.OnDecision`(0x178850)
        //     @0x1789CC `GetDeathCryCount()`(0x1EBB00) → `test eax,eax; jle 0x178B60` ⇒ count==0
        //     就**不开净化窗**（提示未持有幽怨）。
        //   ⇒ 「实体还在、登记表被清空」= 永久锁死。这就是"UI 显示满但净化不了"的全部原因。
        //
        // 【为什么是直接拦，而不是事后补登记】
        //   `ClearDeathCryCharaID` 全局**只有 1 个调用者**（callers 扫描：仅 0x1734F4 一处），
        //   就是净化演出的收尾回调 ⇒ 拦掉它对其它系统零影响；而"事后补"需要 charaID 快照，
        //   补回来还是同一批值（等价），却多一层双跑风险（铁律 1）。
        //
        // 【副作用评估】登记表保持满格后，新捡到的幽怨走 `BattleManager.AddItem`(static 0x1B6A20)
        //   → `AddDeathCryCharaID`(0x1EB810) 会"找第一个空位"失败而**静默丢弃** ⇒ 只是图标/角色归属
        //   不再更新（显示层），**不影响入包**（入包唯一闸门是 `ItemManager.AddItem` 返回 null，
        //   已由 FIFO 顶替解决），也不影响净化门闸（count 恒 > 0）。
        //
        // 【幂等】Prefix 直接 `return false` 跳过原方法；双跑两次都跳过，结果一致。
        // ==================================================================

        /// <summary>
        /// `UserParameter.ClearDeathCryCharaID()`(0x1EBA80，**实例方法** ✅) 的 Prefix：
        /// 开着「净化无消耗」时**跳过清表**，让登记表与"幽怨还在包里"保持一致。
        /// 关着开关时完全放行（原版行为：净化完清表、下次重新累积）。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(UserParameter), "ClearDeathCryCharaID", new Type[] { })]
        private static class DeathcryClearGuardPatch
        {
            private static bool Prefix()
            {
                try
                {
                    // 修正十五（r16）：只跟**净化**开关；关着思装变容也照样走原版清表
                    bool on = PurifyFreeOn();
                    if (!on) return true;          // 原版行为：该清就清

                    _clearGuardHits++;
                    if (_clearGuardHits <= 2)      // 双跑只打一次，避免刷屏
                    {
                        QolLog.Info("growth.cre.keep: 拦下 ClearDeathCryCharaID()"
                                    + " —— 幽怨登记表保持不动（净化幽怨无消耗=ON），下次仍可净化"
                                    + "（累计 " + _clearGuardHits + " 次；双跑计 2 属正常）");
                    }
                    Hot.ClearSkip++;
                    return false;                  // 跳过原方法 ⇒ 登记表不清空
                }
                catch (Exception e)
                {
                    QolLog.Warn("growth.cre.keep: 拦 ClearDeathCryCharaID 出错，已放行原版逻辑 -> "
                                + e.GetType().Name + ": " + e.Message);
                    return true;
                }
            }
        }

        // ==================================================================
        // 修正十三（r14）：`UserParameter.GetDeathCryCount()` 上限缩放（默认**关**，实验性）
        // ------------------------------------------------------------------
        // 【为什么要它】游戏里所有"满幽怨"判定最终都落到 `GetDeathCryCount()`(0x1EBB00)，
        //   而它的循环上界是编译期常量 `Global.MAX_HAVE_ITEM_DEATHCRY = 10`（dump 152142，
        //   代码里内联成 `cmp edx, 0xa`）⇒ **登记表口径恒 ≤ 10**，扩容到 20 后"满"仍发生在 10。
        //   ⇒ 把返回值按 `ceil(持有数 * 10 / 上限)` 缩放，让"满"发生在**扩容后的上限**。
        //
        // 【调用者清单（callers 扫描，7 处）】—— 这也是它默认关的原因：
        //   0xE05D3  UIDeathCryWindow.OpenWindow（x2）  开窗判据 `count > 0` ⇒ 缩放无碍
        //   0x15EC27 RoomManager.Update                  `count > 0` ⇒ 无碍
        //   0x1789CC UIRoomReadingMenuWindow.OnDecision  净化门闸 `count > 0` ⇒ 无碍
        //   0x1B6E19 BattleManager.AddItem（static）     入包路径
        //   0x1D0D1E / 0x1D139C  TaskComponent.Initialize / Run   ⚠ **任务/成就判定会被缩放**
        //   ⇒ 副作用主要在这里，所以默认关；玩家手动打开后由自己承担。
        //
        // 【幂等】纯计算 + 只依赖 `CountDeathcryHeld()`（不调 GetDeathCryCount ⇒ 无递归）；
        //   双跑两次结果一致（铁律 1）。
        // ==================================================================

        /// <summary>
        /// `UserParameter.GetDeathCryCount()`(0x1EBB00，**实例方法** ✅) 的 Postfix：
        /// 按 <see cref="DeathcryCap"/> 缩放返回值，使"满"发生在扩容后的上限而不是 10。
        /// 上限就是原版 10（未开扩容）时**直接不改**（零副作用）。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(UserParameter), "GetDeathCryCount", new Type[] { })]
        private static class DeathcryCountScalePatch
        {
            private static void Postfix(ref int __result)
            {
                Hot.GetDc++;
                try
                {
                    if (!CountScaleOn()) return;
                    int cap = DeathcryCap();
                    if (cap <= DeathcryPoolNative) return;      // 上限 = 10 ⇒ 原值就是对的

                    int held = CountDeathcryHeld();
                    if (held < 0) return;                       // 数不出来 ⇒ 用原值

                    int scaled = (held * DeathcryPoolNative + cap - 1) / cap;   // ceil
                    if (scaled > DeathcryPoolNative) scaled = DeathcryPoolNative;
                    if (scaled < 0) scaled = 0;
                    __result = scaled;
                }
                catch (Exception e)
                {
                    WarnOnce("growth.scale.ex", "GetDeathCryCount -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        // ==================================================================
        // 修正十三（r14）：幽怨 **图标数量** 跟随上限（默认开）
        // ------------------------------------------------------------------
        // 【取证】`UIFooterContent.UpdateDeathCryCount()`(0xEE0A0，private 实例方法 ✅)：
        //     r12 = ItemManager.Instance.GetHaveItemList(7)      @0xEE104 `lea edx,[r8+7]`
        //     r13 = r12.Count                                   （持有幽怨数）
        //     r15 = [rsi+0xA0].Count                            （`mDeathCryIconList`，prefab 写死 10）
        //     for (ebx = 0; ebx < r15; ebx++)
        //        ebx <  r13 ⇒ 取 have[ebx] → SetSprite → icon.SetActive(true)     @0xEE24F
        //        ebx >= r13 ⇒ icon 若激活则塞进 [rsi+0xE0] `mAnimationIconList`   @0xEE1D6
        //     循环后 `mAnimationIconList` 非空 ⇒ 对最后一个调 `StartAnimation()` 0xDC7F0
        //        （`UIDeathCryIconContent.CLOSE_ANIMATION_NAME = "purification"`）
        //   ⇒ **图标列表长度就是显示上限**（min(图标数, 持有数)）；
        //     净化动画同样只覆盖到图标列表长度 ⇒ **补足图标 = 显示与动画一起修好**（用户第 2、3 点）。
        //
        // 【⛔ 铁律 16】`Object.Instantiate` 若运行期缺失会毒死宿主方法 ⇒
        //   全部走**反射** + 一次性 Warn + 失败后不再重试（`_iconExpandTried`）。
        //   ⛔ 铁律 15：不 new 托管 List，就地 Add 游戏那个列表对象。
        // ==================================================================

        /// <summary>
        /// `UIFooterContent.UpdateDeathCryCount()`(0xEE0A0，private 实例方法 ✅) 的 Prefix：
        /// 图标不够上限个数时，Instantiate 补出来（默认开 `DeathcryIconExpand`）。
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(UIFooterContent), "UpdateDeathCryCount", new Type[] { })]
        private static class DeathcryIconExpandPatch
        {
            private static void Prefix(UIFooterContent __instance)
            {
                Hot.FooterIcon++;
                try { EnsureDeathcryIcons(__instance); }
                catch (Exception e)
                {
                    WarnOnce("growth.icon.ex", "UpdateDeathCryCount -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// r14：把 `UIFooterContent.mDeathCryIconList`(0xA0) 补足到 <see cref="DeathcryCap"/> 个。
        /// 上限 = 原版 10 时**什么都不做**（prefab 原生 10 个图标刚好够）。
        /// ⚠ 全部反射（铁律 16）；任何一步失败都只留一次性日志，绝不抛给游戏。
        /// </summary>
        private static void EnsureDeathcryIcons(object footer)
        {
            if (footer == null || _iconExpandTried) return;
            if (!IconExpandOn()) return;

            int cap = DeathcryCap();
            if (cap <= DeathcryPoolNative)
            {
                // r15：关掉扩容 ⇒ 把图标布局**还原**成原版 10 颗的样子
                //（否则 10 颗会一直挤在缩小的宽度里，右半边空一片）。
                RestoreIconLayout(footer);
                return;
            }

            if (ReferenceEquals(_iconExpandedFooter, footer)) return;   // r15：这个实例补过了

            object list = GetMember(footer, "mDeathCryIconList");   // 0xA0 List<UIDeathCryIconContent>
            if (list == null)
            {
                WarnOnce("growth.icon.list", "取不到 mDeathCryIconList");
                _iconExpandTried = true; return;
            }
            int n = CountOf(list);
            if (n <= 0) return;                                 // 还没初始化，下次再说
            if (n >= cap) { _iconExpandedFooter = footer; return; }   // 已经够

            object tmpl = ItemOf(list, 0);
            object root = GetMember(footer, "mDeathCryIconRoot");   // 0xA8 GameObject（父节点）
            if (tmpl == null)
            {
                WarnOnce("growth.icon.tmpl", "mDeathCryIconList[0] 取不到，无法作为模板");
                _iconExpandTried = true; return;
            }

            var add = list.GetType().GetMethod("Add", new Type[] { SlotElemType(list) });
            if (add == null) add = list.GetType().GetMethod("Add");
            if (add == null)
            {
                WarnOnce("growth.icon.add", "图标列表无 Add（" + TypeNameOf(list) + "）");
                _iconExpandTried = true; return;
            }

            // ── r15：记录 / 取回**布局基准**（原版 n 颗的首位置、等距步长、缩放）──
            //    ⚠ 只在第一次记录 ⇒ 反复进入也不会越缩越小（幂等）。
            if (!_iconBaseReady)
            {
                UnityEngine.Vector3 first, last;
                if (TryIconPos(ItemOf(list, 0), out first)
                    && n >= 2
                    && TryIconPos(ItemOf(list, n - 1), out last))
                {
                    _iconBaseFirst = first;
                    _iconBaseStep = (last - first) / (n - 1);   // 均匀排列 ⇒ 平均间距
                    if (!TryIconScale(ItemOf(list, 0), out _iconBaseScale))
                        _iconBaseScale = new UnityEngine.Vector3(1f, 1f, 1f);
                    _iconBaseReady = true;
                    QolLog.Info("growth.icon: 布局基准 = 首颗 " + FmtVec(_iconBaseFirst)
                                + " / 间距 " + FmtVec(_iconBaseStep)
                                + " / 缩放 " + FmtVec(_iconBaseScale));
                }
                else
                {
                    WarnOnce("growth.icon.pos",
                        "取不到旧图标的 localPosition/localScale ⇒ 无法算出摆放位置（补位会让图标叠在第 1 颗上）");
                    _iconExpandTried = true;                    // 本机拿不到基准 ⇒ 别反复试
                    return;
                }
            }

            // 目标：cap 颗**塞进原来 n 颗占的宽度** ⇒ 间距与图标一起等比缩小。
            // （容器是固定宽度的圆角框，不缩放就会溢出框外被裁掉。）
            float ratio = (float)(n - 1) / (cap - 1);
            if (ratio <= 0f || ratio > 1f) ratio = 1f;
            UnityEngine.Vector3 newStep = _iconBaseStep * ratio;
            UnityEngine.Vector3 newScale = _iconBaseScale * ratio;

            // ① 把**原有** n 颗重排 + 缩放（位置/缩放都是绝对值 ⇒ 幂等）
            for (int i = 0; i < n; i++)
            {
                object ic = ItemOf(list, i);
                SetIconPos(ic, _iconBaseFirst + newStep * i);
                SetIconScale(ic, newScale);
            }

            // ② 克隆补足到 cap 颗，逐个摆到 i = n .. cap-1
            int need = cap - n;
            int made = 0;
            for (int i = 0; i < need; i++)
            {
                object clone = CloneIcon(tmpl, root);
                if (clone == null) break;                       // 失败 ⇒ 停下（不刷屏）
                SetIconPos(clone, _iconBaseFirst + newStep * (n + i));
                SetIconScale(clone, newScale);
                try { add.Invoke(list, new object[] { clone }); made++; }
                catch (Exception e)
                {
                    WarnOnce("growth.icon.invoke", "List.Add 克隆图标失败 -> " + e.GetType().Name + ": " + e.Message);
                    break;
                }
            }

            _iconExpandNote = "幽怨图标 " + n + " -> " + CountOf(list) + "（目标 " + cap + "，本次补 " + made
                              + "，缩放 " + ratio.ToString("0.###") + "，间距 " + FmtVec(newStep) + "）";
            if (made > 0 || !_iconExpandTried)
            {
                QolLog.Info("growth.icon: " + _iconExpandNote
                            + (made < need ? " ⚠ 未补满，见 growth.icon.* 的一次性 Warn" : ""));
            }
            if (made > 0) _iconExpandedFooter = footer;         // 同一实例只补一次
            if (made <= 0) _iconExpandTried = true;             // 一个都造不出来 ⇒ 别再试
        }

        /// <summary>
        /// r15：关掉扩容时把图标布局还原成原版（位置 / 缩放都回到 <c>_iconBase*</c>）。
        /// ⚠ 只在**这个实例确实被我们扩过容**时执行一次，之后 <c>_iconExpandedFooter</c> 置空 ⇒ 不会每帧重跑。
        /// </summary>
        private static void RestoreIconLayout(object footer)
        {
            if (!_iconBaseReady) return;
            if (!ReferenceEquals(_iconExpandedFooter, footer)) return;   // 没扩过 ⇒ 本来就不用还
            try
            {
                object list = GetMember(footer, "mDeathCryIconList");
                if (list != null)
                {
                    int n = CountOf(list);
                    for (int i = 0; i < n; i++)
                    {
                        object ic = ItemOf(list, i);
                        SetIconPos(ic, _iconBaseFirst + _iconBaseStep * i);
                        SetIconScale(ic, _iconBaseScale);
                    }
                    QolLog.Info("growth.icon: 扩容已关闭 ⇒ 图标布局还原成原版 " + _iconBaseScale.x.ToString("0.###")
                                + " 倍（列表仍保留 " + n + " 个，多出的会按游戏逻辑显示为空槽）");
                }
            }
            catch (Exception e)
            {
                WarnOnce("growth.icon.restore", "还原图标布局失败 -> " + e.GetType().Name + ": " + e.Message);
            }
            _iconExpandedFooter = null;
        }

        /// <summary>r15：诊断用的向量格式化（日志里看得懂就够）。</summary>
        private static string FmtVec(UnityEngine.Vector3 v)
        {
            return "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + ")";
        }

        /// <summary>
        /// r15：取图标（<c>UIDeathCryIconContent</c>）的 <c>Transform</c>。
        /// ⚠ 用 <c>as UnityEngine.Transform</c> 而非全反射 —— 依据：
        ///   `Reparent()` 里 `t.GetType().GetMethod("SetParent", new[]{typeof(UnityEngine.Transform), typeof(bool)})`
        ///   在本机**匹配成功**（日志里从未出现 `growth.icon.parent` 的 Warn）
        ///   ⇒ 运行期 `transform` 属性的类型确实就是编译期可见的 `UnityEngine.Transform`。
        /// </summary>
        private static UnityEngine.Transform IconTransform(object icon)
        {
            if (icon == null) return null;
            try
            {
                var p = icon.GetType().GetProperty("transform",
                    BindingFlags.Public | BindingFlags.Instance);
                object o = p == null ? null : p.GetValue(icon, null);
                return o as UnityEngine.Transform;
            }
            catch { return null; }
        }

        /// <summary>r15：读图标的 `localPosition`（UI 里与 `anchoredPosition` 同步）。</summary>
        private static bool TryIconPos(object icon, out UnityEngine.Vector3 pos)
        {
            pos = new UnityEngine.Vector3(0f, 0f, 0f);
            try
            {
                var t = IconTransform(icon);
                if (t == null) return false;
                pos = t.localPosition;
                return true;
            }
            catch { return false; }
        }

        /// <summary>r15：写图标的 `localPosition`。</summary>
        private static bool SetIconPos(object icon, UnityEngine.Vector3 pos)
        {
            try
            {
                var t = IconTransform(icon);
                if (t == null) return false;
                t.localPosition = pos;
                return true;
            }
            catch { return false; }
        }

        /// <summary>r15：读图标的 `localScale`。</summary>
        private static bool TryIconScale(object icon, out UnityEngine.Vector3 sc)
        {
            sc = new UnityEngine.Vector3(1f, 1f, 1f);
            try
            {
                var t = IconTransform(icon);
                if (t == null) return false;
                sc = t.localScale;
                return true;
            }
            catch { return false; }
        }

        /// <summary>r15：写图标的 `localScale`（等比缩放，让 cap 颗塞进原宽度）。</summary>
        private static bool SetIconScale(object icon, UnityEngine.Vector3 sc)
        {
            try
            {
                var t = IconTransform(icon);
                if (t == null) return false;
                t.localScale = sc;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// r14：用 `UnityEngine.Object.Instantiate` 克隆一个图标组件，挂到 `root` 下。
        /// ⛔ 全反射（铁律 16：直接引用若运行期缺失会毒死宿主方法）；返回 null 表示失败。
        /// </summary>
        private static object CloneIcon(object tmpl, object root)
        {
            try
            {
                var ot = typeof(UnityEngine.Object);
                MethodInfo mi = null;

                // 优先泛型版 Instantiate<T>(T)
                foreach (var m in ot.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Instantiate" || !m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1) { mi = m; break; }
                }
                if (mi != null)
                {
                    var g = mi.MakeGenericMethod(tmpl.GetType());
                    object o = g.Invoke(null, new object[] { tmpl });
                    Reparent(o, root);
                    return o;
                }

                // 退化：非泛型版 Instantiate(Object)
                foreach (var m in ot.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Instantiate" || m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(UnityEngine.Object))
                    {
                        object o = m.Invoke(null, new object[] { tmpl });
                        Reparent(o, root);
                        return o;
                    }
                }

                WarnOnce("growth.icon.inst", "找不到 Object.Instantiate 的可用重载");
                return null;
            }
            catch (Exception e)
            {
                WarnOnce("growth.icon.clone", "Instantiate 失败 -> " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>r14：把克隆出来的对象挂到 `root` 下（保持与 prefab 一致的层级/布局）。失败可容忍。</summary>
        private static void Reparent(object obj, object root)
        {
            if (obj == null || root == null) return;
            try
            {
                var goT = obj.GetType().GetProperty("gameObject",
                    BindingFlags.Public | BindingFlags.Instance);
                object go = goT == null ? null : goT.GetValue(obj, null);
                if (go == null) return;

                var tr = go.GetType().GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                object t = tr == null ? null : tr.GetValue(go, null);
                if (t == null) return;

                // ⚠ 不写 `using UnityEngine`（`Object` 会与 `System.Object` 撞）⇒ 全限定
                var setParent = t.GetType().GetMethod("SetParent",
                                    new Type[] { typeof(UnityEngine.Transform), typeof(bool) })
                                ?? t.GetType().GetMethod("SetParent",
                                    new Type[] { typeof(UnityEngine.Transform) });
                if (setParent == null) return;

                var rootTr = root.GetType().GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                object rt = rootTr == null ? null : rootTr.GetValue(root, null);
                if (rt == null) return;

                var ps2 = setParent.GetParameters();
                if (ps2.Length == 2) setParent.Invoke(t, new object[] { rt, false });
                else setParent.Invoke(t, new object[] { rt });
            }
            catch (Exception e)
            {
                WarnOnce("growth.icon.parent", "SetParent 失败（不影响显示）-> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static int ToInt(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        private static uint ToUInt(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToUInt32(v); } catch { return 0; }
        }

        private static ulong ToULong(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToUInt64(v); } catch { return 0; }
        }

        private static bool ToBool(object v)
        {
            if (v == null) return false;
            try { return Convert.ToBoolean(v); } catch { return false; }
        }

        /// <summary>统一入口：**先观测**（本会话第一次打一遍 8 个槽池的空闲数 + freeItemCounts 原值），
        /// 再按开关决定是否补槽（关着 ⇒ 保持原版 10 格，绝不擅自改原版行为）。</summary>
        private static void EnsureDeathcryPool(string src)
        {
            try
            {
                var im = ItemManager.Instance;
                if (im == null) return;
                var f = Inst;
                bool on = ExpandOn();   // 修正十：扩容独立开关（默认关），不再跟 C3 绑定

                if (f != null && !f._poolLoggedOnce)
                {
                    string s = PoolSummary(im);
                    if (s != null)
                    {
                        f._poolLoggedOnce = true;   // 只认"槽池已建好"的那一次
                        QolLog.Warn("growth.pool: 槽池现状（" + src + "，PoolExpand=" + (on ? "ON" : "OFF")
                                    + "，Fifo=" + (FifoOn() ? "ON" : "OFF")
                                    + "，上限=" + DeathcryCap() + "，持有=" + CountDeathcryHeld() + "）" + s);
                    }
                }

                // r14：**开关关着也要跑同步** —— 只要以前补过槽（`_poolEverGrown`），
                // 关掉扩容就必须把多余空槽撤掉，否则"关掉扩容"永远是空话
                // （用户 2026-09-14 复现：关掉扩容后再次净化依旧产出 18 件）。
                if (on || _poolEverGrown) SyncDeathcryPool(src);

                // r14：持有数超过上限（典型＝刚从 20 关回 10）⇒ 逐轮顶掉超出的，让上限真正生效。
                // 只在 FIFO 开着时做（关掉 FIFO = 玩家明确接受"原版行为"）。
                if (FifoOn()) ShrinkDeathcryOverflow(im, src);
            }
            catch (Exception e)
            {
                WarnOnce("growth.pool.ex", src + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// r14（修正十三）：把 DEATHCRY 的**总容量**（空闲槽 + 已持有）同步到 <see cref="DeathcryCap"/>。
        ///
        /// ⛔⛔ 与 r10~r13 的 `GrowDeathcryPool` 的本质区别：**目标量是"总容量"而不是"空闲槽数"**。
        ///   旧版 `while (CountOf(freePool) &lt; 20) Add(...)` 每消耗一个就补一个 ⇒ 容量 = 20 + 持有数，
        ///   上限形同虚设（详见 <see cref="DeathcryCap"/> 处的日志实证）。
        ///
        /// 三条分支：
        ///   · 容量 == 目标 ⇒ 直接返回 true（幂等，零副作用）
        ///   · 容量 &lt; 目标 ⇒ 往空闲池**追加空** `HaveItemInfo`（空槽 mItemID=0，不写存档）
        ///   · 容量 &gt; 目标 ⇒ 从空闲池**尾部撤掉**多余空槽（已持有的撤不掉，交给 FIFO 顶替）
        ///
        /// ⛔ 绝不改 `freeItemCounts`（累积切片游标，改大越界崩，铁律 25）。
        /// ⛔ 绝不 `new` 托管 List 回写（铁律 15）：一律就地 Add / RemoveAt 游戏那个列表对象。
        /// </summary>
        private static bool SyncDeathcryPool(string src)
        {
            var im = ItemManager.Instance;
            if (im == null) { WarnOnce("growth.pool.im", src + ": ItemManager.Instance 为空"); return false; }

            object pool = DeathcryFreePool(im);
            if (pool == null) return false;

            int free = CountOf(pool);
            // r13：⛔ 不能只凭 free<=0 判"还没建好" —— 幽怨**占满**时空闲池也是 0，
            // 而那恰恰是最需要扩容的时刻（r11/r12 的实机 bug：开了扩容却一条日志都没有）。
            // 正确判据 = "游戏还从没建过槽池"（_poolReady 由 Reset/Initialize 的 Postfix 置真）。
            if (free <= 0 && !_poolReady)
            {
                WarnOnce("growth.pool.notready", src + ": 槽池尚未建好（空闲=0 且未见过 Reset/Initialize），跳过");
                return false;
            }

            int held = CountDeathcryHeld();
            if (held < 0) { WarnOnce("growth.pool.held", src + ": 数不出持有幽怨数，跳过"); return false; }

            int cap = DeathcryCap();
            int total = free + held;
            if (total == cap) return true;

            // ---------------- 收缩：撤掉多余空槽 ----------------
            if (total > cap)
            {
                int remove = total - cap;
                if (remove > free) remove = free;      // 只撤得动空闲的
                if (remove <= 0) return false;         // 全是已持有 ⇒ 交给 FIFO / 玩家自己净化

                var rem = pool.GetType().GetMethod("RemoveAt", new Type[] { typeof(int) });
                if (rem == null) rem = pool.GetType().GetMethod("RemoveAt");
                if (rem == null)
                {
                    WarnOnce("growth.pool.rmat", src + ": 槽池类型无 RemoveAt（" + TypeNameOf(pool) + "）");
                    return false;
                }

                int done = 0;
                for (int i = 0; i < remove; i++)
                {
                    int n = CountOf(pool);
                    if (n <= 0) break;
                    try { rem.Invoke(pool, new object[] { n - 1 }); done++; }
                    catch (Exception e)
                    {
                        WarnOnce("growth.pool.rm", src + ": RemoveAt 失败 -> " + e.GetType().Name + ": " + e.Message);
                        break;
                    }
                }
                int afterR = CountOf(pool) + held;
                QolLog.Info("growth.pool: 幽怨容量 " + total + " -> " + afterR + "（撤空槽 " + done
                            + " / 目标 " + cap + "，持有 " + held + "）[" + src + "]");
                return afterR <= cap;
            }

            // ---------------- 扩张：追加空槽 ----------------
            var add = pool.GetType().GetMethod("Add", new Type[] { SlotElemType(pool) });
            if (add == null) add = pool.GetType().GetMethod("Add");
            if (add == null) { WarnOnce("growth.pool.add", src + ": 槽池类型无 Add（" + TypeNameOf(pool) + "）"); return false; }

            int added = 0;
            while (added < DeathcryPoolMaxAdd && CountOf(pool) + held < cap)
            {
                object slot;
                try { slot = new HaveItemInfo(); }
                catch (Exception e)
                {
                    // 兜底：用运行时类型造（两个 ctor 路径都不通就只能放弃并留证）
                    try { slot = Activator.CreateInstance(SlotElemType(pool), true); }
                    catch
                    {
                        WarnOnce("growth.pool.new", src + ": new HaveItemInfo() 失败 -> "
                            + e.GetType().Name + ": " + e.Message);
                        break;
                    }
                }
                try { add.Invoke(pool, new object[] { slot }); }
                catch (Exception e)
                {
                    WarnOnce("growth.pool.invoke", src + ": List.Add 失败 -> " + e.GetType().Name + ": " + e.Message);
                    break;
                }
                added++;
            }

            if (added > 0) _poolEverGrown = true;
            int after = CountOf(pool) + held;
            if (added > 0)
            {
                QolLog.Info("growth.pool: 幽怨容量 " + total + " -> " + after + "（+" + added
                            + " / 目标 " + cap + "，持有 " + held + "）[" + src + "]");
            }
            return after >= cap;
        }

        /// <summary>
        /// r14：持有幽怨数**超过**当前上限时，逐轮顶掉超出的（配合 <see cref="SyncDeathcryPool"/> 的收缩）。
        /// 典型场景：玩家从「扩容 20」关回「上限 10」后，手上还压着 18 个 ⇒ 这里把它降回 10，
        /// 否则"关掉扩容"看起来毫无效果（用户 2026-09-14 复现：关掉扩容后依旧产出 18 件）。
        /// ⚠ 每轮最多删 4 个（2s 一次重放），既不卡帧也便于在日志里观察过程。
        /// </summary>
        private static void ShrinkDeathcryOverflow(ItemManager im, string src)
        {
            int cap = DeathcryCap();
            int held = CountDeathcryHeld();
            if (held < 0 || held <= cap) return;

            int over = held - cap;
            if (over > 4) over = 4;
            for (int i = 0; i < over; i++) EvictOldestDeathcry(im, 0);

            QolLog.Info("growth.pool: 幽怨持有 " + held + " 个 > 上限 " + cap
                        + " ⇒ 本轮顶掉 " + over + " 个 [" + src + "]");
        }

        /// <summary>只读观测：8 个槽池的空闲数 + 静态 freeItemCounts 原值。槽池未建好返回 null。</summary>
        private static string PoolSummary(object im)
        {
            object lists = GetMember(im, "mFreeItemLists");
            if (lists == null) return null;
            int first = CountOf(ArrayElem(lists, 0));
            int total = 0;
            var parts = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                int c = CountOf(ArrayElem(lists, i));
                total += c;
                parts.Add(c.ToString());
            }
            if (total <= 0 && first <= 0) return null;   // 还没建好

            string fc = "(取不到)";
            try
            {
                var fd = typeof(ItemManager).GetField("freeItemCounts",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                object arr = fd == null ? null : fd.GetValue(null);
                var ia = arr as int[];
                if (ia != null)
                {
                    var s = new List<string>();
                    for (int i = 0; i < ia.Length; i++) s.Add(ia[i].ToString());
                    fc = string.Join("/", s.ToArray());
                }
                else if (arr != null) fc = "(" + TypeNameOf(arr) + ")";
            }
            catch (Exception e) { fc = "(异常 " + e.GetType().Name + ")"; }

            return " 槽池空闲[" + string.Join(",", parts.ToArray()) + "] freeItemCounts=" + fc;
        }

        /// <summary>槽池元素（`List&lt;HaveItemInfo&gt;`）的运行时类型 —— 用它匹配 `Add(T)` 的唯一重载。</summary>
        private static Type SlotElemType(object pool)
        {
            try
            {
                object e0 = ItemOf(pool, 0);
                if (e0 != null) return e0.GetType();
            }
            catch { }
            return typeof(HaveItemInfo);
        }

        /// <summary>从 Il2Cpp 数组代理里取第 i 个元素：① IList → ② get_Item(int) → ③ 原生指针直读
        /// （Il2CppArray = { obj 0x10 / bounds 0x10 / max_length 0x18 / vector 0x20 }）。</summary>
        private static object ArrayElem(object arr, int i)
        {
            if (arr == null || i < 0) return null;
            try
            {
                var il = arr as System.Collections.IList;
                if (il != null && i < il.Count) return il[i];
            }
            catch { }
            object v = ItemOf(arr, i);
            if (v != null) return v;
            try
            {
                IntPtr p = NativePtr.Of(arr);
                if (p == IntPtr.Zero) return null;
                int len = Marshal.ReadInt32(p + 0x18);
                if (i >= len) return null;
                IntPtr ep = Marshal.ReadIntPtr(p + 0x20 + i * IntPtr.Size);
                if (ep == IntPtr.Zero) return null;
                object t = ItemOf(arr, 0) ?? ItemOf(arr, 1);   // 同数组元素类型唯一，借它取运行时类型
                if (t == null) return null;
                return Activator.CreateInstance(t.GetType(), ep);
            }
            catch { return null; }
        }

        private static string TypeNameOf(object o)
        {
            try { return o == null ? "null" : o.GetType().FullName; }
            catch { return "(未知类型)"; }
        }

        // ------------------------------------------------------------------
        // 反射工具（与 FeatureEconomy 同款；List 一律 object）
        // ------------------------------------------------------------------

        private static int CountOf(object list)
        {
            if (list == null) return 0;
            try
            {
                var mi = list.GetType().GetMethod("get_Count", Type.EmptyTypes);
                if (mi != null)
                {
                    object v = mi.Invoke(list, null);
                    if (v is int) return (int)v;
                }
                // Il2Cpp 数组（如 PartyManager.mPlayer CharacterComponent[]）只有 get_Length
                mi = list.GetType().GetMethod("get_Length", Type.EmptyTypes);
                if (mi != null)
                {
                    object v = mi.Invoke(list, null);
                    if (v is int) return (int)v;
                }
                return 0;
            }
            catch { return 0; }
        }

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

        private static object InvokeMember(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var mi = obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return mi == null ? null : mi.Invoke(obj, null);
            }
            catch { return null; }
        }

        /// <summary>单参数方法的反射调用（Il2Cpp 代理类型未知，按参数类型精确匹配）。</summary>
        private static object InvokeMember1(object obj, string name, object arg)
        {
            if (obj == null) return null;
            try
            {
                var mi = obj.GetType().GetMethod(name, new Type[] { arg.GetType() });
                if (mi == null) return null;
                return mi.Invoke(obj, new object[] { arg });
            }
            catch { return null; }
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

        // ------------------------------------------------------------------
        // F2 菜单
        // ------------------------------------------------------------------

        public IEnumerable<MenuItem> BuildMenu()
        {
            // 修正十七（r18）：三条按"游戏本名 + 无消耗"命名，顺序与营地菜单一致 = 净化 → 调合 → 变容。
            // ── C3 炼成无材料：净化幽怨（读书房「净化」）──
            yield return Item.Bool(Loc.T("f.creation.free"), _freeCreation, false,
                RiskTier.Safe, Effect.Immediate);
            // ── C1 强化零消耗：思装调合（装备强化 / 融合）──
            yield return Item.Bool(Loc.T("f.enhance.free"), _freeEnhance, false,
                RiskTier.Safe, Effect.Immediate);
            // ── C3 炼成无材料：思装变容（能力付与）──
            yield return Item.Bool(Loc.T("f.grant.free"), _freeGrant, false,
                RiskTier.Safe, Effect.Immediate);
            // ── C4 全技能解锁 ──
            yield return Item.Bool(Loc.T("f.skill.all"), _allSkills, false,
                RiskTier.Safe, Effect.Immediate);
            // ── 修正十（r11）：幽怨满了怎么办 ──
            //   FIFO 顶替（默认开）：满时自动顶掉最早获取的那个 ⇒ 新幽怨永远拿得到。
            //   槽池扩容（默认关）：把 10 补到 20；⚠ UI/登记表硬编码 10，扩容后第 11 个起显示不出来。
            yield return Item.Bool(Loc.T("f.deathcry.fifo"), _deathcryFifo, false,
                RiskTier.Safe, Effect.Immediate);
            yield return Item.Bool(Loc.T("f.deathcry.expand"), _deathcryPoolExpand, false,
                RiskTier.Safe, Effect.Immediate);
            // ── 修正十三（r14）的两个配套开关；修正十四（r15）补进 F2 面板与营地页 ──
            yield return Item.Bool(Loc.T("f.deathcry.icon"), _deathcryIconExpand, true,
                RiskTier.Safe, Effect.Immediate);
            yield return Item.Bool(Loc.T("f.deathcry.scale"), _deathcryCountScale, false,
                RiskTier.Safe, Effect.Immediate);
        }
    }
}
