using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 营地菜单集成：把 mod 设置塞进游戏自己的「設定」窗口。
    ///
    /// 【架构 v3（2026-09-10 实机 MissingMethodException 后定稿）】
    /// ✗ v1 向营地主菜单尾插条目 → 实机卡死/全屏被踢后台（行池越界，勿回退！）。
    /// ✗ v2 入口挂设置列表尾部，但行构造用 3 参 .ctor(string,int,string[]) → 实机
    ///   MissingMethodException：编译期引用的 DummyDll 里 string[] 在 unhollower 运行时
    ///   代理中是 Il2CppReferenceArray&lt;string&gt;，签名对不上。这是"DummyDll 里有 ≠ 运行时
    ///   能调用"的构造器形态（数组参数必炸）。
    /// ✓ v3 实锤的行渲染链（0x227B80 UpdateShowComponent 反汇编）：
    ///   UIText.set_TextID(data.mMessageID 0x28) → UpdateText(force) → GetSystemMessage(键)
    ///   → 查本地化表；**查不到返回 null → 标题空白**。mTitleText(0x20) 字段渲染时不读！
    ///   NONE 型行（mContentType=0，原版"按键设置/重置参数"动作行同款）走最简分支，
    ///   由 1 参 .ctor(string messageID) 创建 —— 无数组参数，运行时代理签名一致。
    ///   自定义文本"MOD設定"不是本地化键 ⇒ 显示必须绕开查表：
    ///   - UIText 有 SetText(string)(0x1E4C40) 直接写显示文本（写到 mText/TMP）；
    ///   - Patch：UICampConfigMenuContent.UpdateShowComponent Postfix → 我们的行调
    ///     mTitleText.SetText(标题)；UIText.UpdateText Prefix → TextID 以标记开头时跳过
    ///     （防 OnEnable/重绑路径把标题刷成空）。
    ///   - 行身份识别用 mMessageID 标记字符串（\uE000 私有区前缀，值比较跨代理稳定；
    ///     unhollower 每次封送产生新代理实例，ReferenceEquals 不可靠！）。
    /// 【入口/回路（沿用 v2 实测有效的部分）】
    ///   - 主菜单 7 项原样不动；入口 = 设置列表尾部第 15 行（OpenWindow Postfix 幂等挂载，
    ///     行池安全检查防越界卡死）。
    ///   - 选中入口行按确定 → OnPressDecision Prefix 置 modMode + UpdateContentDataList
    ///     → Postfix 整表换 mod 行（NONE 型标题行）+ UpdateListShowContent 重绑。
    ///   - modMode 按取消 → OnPressCanceled Prefix 按快照恢复原版行（不关窗）；
    ///     再按取消原版关窗。改值/动作后只刷可见行（不 ResetSelectIndex，光标不跳）。
    ///   - modMode 下 UpdateContentData(bool)/(float)、OnPressDecision、OnTab* 全部 Prefix
    ///     拦截 —— 绝不让原方法按行 index 摸到游戏音量/难度/语言。
    /// 【铁律适配】全部补丁为实例方法；每个补丁独立 try/catch；Il2Cpp List 反射操作；
    /// DummyDll 字段 → unhollower 属性：GetMember 先 Property 再 Field。
    /// </summary>
    public sealed class FeatureCampConfig : IFeature
    {
        public string Id { get { return "camp"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }   // UI 壳本身无风险；不可逆动作行自带标注与二次确认

        /// <summary>我们的行 mMessageID 前缀（\uE000 私有区，游戏本地化键不可能命中）。</summary>
        private const string MARKER = "\uE000CQ_";
        private const string MARKER_ENTRY = MARKER + "ENTRY";

        private static bool _modMode;
        /// <summary>
        /// 原版行 data 快照。v0.1.27 起**不再置空**（重启场景才清）：它同时充当
        /// "列表被污染时的纯净重灌源"——见 OpenWindowPatch 的防御性清污。
        /// </summary>
        private static object[] _vanilla;
        /// <summary>
        /// 营地页的页面层级：MOD 設定主页 / 物品生成子页（v0.2.4）/ 角色等级子页（v0.3 批次 4 · A7）。
        /// ⚠ 新增子页必须同时改：Rows、_page 判断（GoBack）、PageRow.Target、Decision2Patch 的分派。
        /// </summary>
        private enum CampPage { Main, Spawn, Level }
        private static CampPage _page = CampPage.Main;
        private static readonly List<CampRow> MainRows = new List<CampRow>();
        private static readonly List<CampRow> SpawnRows = new List<CampRow>();
        private static readonly List<CampRow> LevelRows = new List<CampRow>();
        /// <summary>当前生效的行表（RebuildPatch 按它建行，StepSelected/确认键按它取行）。</summary>
        private static List<CampRow> Rows
        {
            get
            {
                // A7（2026-11-06 用户反馈）：角色索引一旦变化（卡片选中 / 外部改配置），
                // 立刻把「目標等級」重置为新角色的当前等级 ⇒ 未保存的编辑值绝不会串到下一个角色。
                // 放在 getter 里是兜底：任何渲染 / 输入路径都要经过 Rows。
                if (_page == CampPage.Level && _levelChara != null
                    && _levelChara.Value != _levelCharaSeen) ResetLevelTargetToCurrent();
                if (_page == CampPage.Spawn) return SpawnRows;
                if (_page == CampPage.Level) return LevelRows;
                return MainRows;
            }
        }
        private static bool _rowsBuilt;
        private static bool _spawnRowsBuilt;
        /// <summary>A7 子页每次进入都重建（角色数是运行时动态的，方案 8.3 约束①）。</summary>
        private static bool _levelRowsBuilt;

        // A7 子页的三个值。注册在 Crystar.QoL.camp 分类下 ⇒ 自动进 settings.cfg（QolConfig.RegisterEntry）。
        private static MelonPreferences_Entry<int> _levelChara;
        private static MelonPreferences_Entry<int> _levelStep;
        private static MelonPreferences_Entry<int> _levelTarget;
        /// <summary>上次同步「目標等級」时对应的角色下标（脏检查用，-1 = 尚未进过子页）。</summary>
        private static int _levelCharaSeen = -1;
        // ── v0.2.4：确认/取消键去重 ──
        // 2026-09-12 的 debug.log 实锤：本环境（MelonLoader 0.5.7 + HarmonyX）里
        // **同一个补丁方法在一次游戏调用中会被触发两次**：
        //   • "MOD 设置行已重建" 142 次 vs "进入 MOD 设置页" 71 次 = 正好 2:1；
        //   • `f.item.spawn` 在 02:39:02.510 / .511 各执行一遍 → 按一次给两份物品。
        // 人类不可能在同一毫秒按两次确认 → 40ms 内的重复一律吞掉（不影响正常连按）。
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static long _lastDecisionMs = -100000;
        private static long _lastCancelMs = -100000;
        /// <summary>
        /// v0.2.6：返回键去重（**所有**返回路径共用一把闸）。
        ///
        /// 实机问题：一次取消键按下会让**两条**路径都进来 ——
        ///   `UICampConfigMenuListWindow.OnPressCanceled`（CancelPatch）
        ///   与 `UICampConfigWindow.CloseWindow`（ConfigClosePatch），
        ///   两次都调 `GoBack` → 从子页按一次退**两级**
        ///   （2026-09-12 debug.log：04:24:11.150 子页→主页，仅 6ms 后 11.158 主页→原版列表）。
        /// v0.2.5 只在各自的补丁里做了去重，跨补丁的第二次调用照样执行 → 用户观感"返回不按层级"。
        /// 现在把闸设在 GoBack 入口：Dedup 窗口内的第二次调用直接返回 true（＝已消费，
        /// 调用方照样拦掉原方法、窗口不关），保证**一次物理按键 = 退一级**。
        /// 人手连按的间隔远大于 150ms，不受影响。
        /// </summary>
        private static long _lastBackMs = -100000;
        private const long BackDedupMs = 150;
        private const long KeyDedupMs = 40;
        /// <summary>
        /// v0.2.7：滑块"事件簇"聚合闸。
        ///
        /// 实机日志实锤（2026-09-12 14:23，v0.2.6）：**一次物理按键会打出 3 个事件**——
        ///   `norm=NaN delta=±1`（我们自己 OnUpdate 读游戏按键）+
        ///   `norm=∓1.00 delta=0` **×2**（游戏 `UpdateContentData(float)` 的两个重载各跑一遍，
        ///   且本环境补丁体会跑两遍）→ 一次按键实际走了 **3 档**
        ///   （用户原话："滑块的值不是正常加减的，能 +1+1 地去调最好"）。
        /// 这些事件都在同一帧内、相隔 1~6ms 到达；人手连按间隔 ≥250ms → 用 80ms 窗口把它们并成一步。
        /// 长按方向键 = 逐帧重复输入（每个窗口最多一步）≈ 12 档/秒，手感仍然连续。
        /// </summary>
        private static long _lastSliderStepMs = -100000;
        private const long SliderStepDedupMs = 80;
        private static bool _adjust;                            // GAUGE 调整模式（对应原版 eListState.CHANGE_VALUE）
        private static int _pendingRow = -1;                    // 待确认行（原生弹窗打开期间 >=0）
        private static UICampConfigMenuListWindow _pendingWin;  // 弹窗归属窗口（回调里恢复状态用）
        private static int _pendingDelta = 1;                   // 待确认变更的步进（左右键调值可为 -1）
        private static int _pendingAt;                          // 弹窗打开时刻（看门狗超时判定用）
        /// <summary>
        /// v0.1.27：入口行"自动落点"纠正窗口（帧数预算）。
        /// 反汇编实锤：`UpdateContentDataList`(0x22AF70) 会 for(count-1) 调
        /// `MoveNextIndex(true,false)`(0x229860) 把光标**一路走到最后一行**，
        /// `OpenWindow`(0x22AB90) 的循环同样在末尾调用 `SetSelectIndex`——两者结束时
        /// 光标都停在**末行**；而我们的「MOD設定」入口行正是末行 → 每次进/回设置窗
        /// 光标都自动压在入口行上，玩家再按确定就直接又进 mod 页（用户实测的"返回逻辑错乱"）。
        /// 打开/重建后给若干帧预算：一旦发现光标停在入口行就复位到首行，之后交还玩家。
        /// </summary>
        private static int _entryFixFrames;
        /// <summary>光标复位诊断去重（避免刷屏）。</summary>
        private static string _lastCursorDiag = "";
        /// <summary>
        /// v0.1.28：状态卡死看门狗起点（Environment.TickCount）。
        /// mod 页里若发现 mListState(0xF8) 停在 SHOW_DIALOG(2) 而**我们并没有待确认弹窗**，
        /// 连续 2 秒就强制写回 SELECT_ITEM(0) —— 见 GuardStuckDialogState 的反汇编实锤说明。
        /// </summary>
        private static int _stuckStateAt;
        private static UICampConfigMenuListWindow _win;         // 当前设置窗（v0.1.20：滑块左右输入用）

        public void Init(FeatureContext ctx)
        {
            // v0.3 批次 4 / A7：角色等级子页的三个值（铁律 3：注册 ≠ 可见，下面还有三张语言表 + 入口行）。
            // ⚠ A7 是动作型子页，本身没有布尔开关；这三个 int 只是 UI 的行内取值，
            //   真正的"改动游戏数据"只发生在带 warn:true 的「套用等級」动作行上。
            _levelChara = ctx.Cfg.Int("LevelCharaIndex", 0, "A7 level subpage: selected character index");
            _levelStep = ctx.Cfg.Int("LevelStep", 1, "A7 level subpage: target level step");
            // ⚠ 注册默认 1 只是占位：每次进子页 / 切角色卡片都会由 ResetLevelTargetToCurrent
            //   写成该角色当前等级（2026-11-06 用户反馈「不要默认 1 级还得自己点半天」）。
            _levelTarget = ctx.Cfg.Int("LevelTarget", 1, "A7 level subpage: target level (1..99)");

            Safe(ctx.Harmony, typeof(OpenWindowPatch), "UICampConfigMenuListWindow.OpenWindow（进窗复位 modMode+挂入口）");
            Safe(ctx.Harmony, typeof(ReturnMenuPatch), "UICampBaseWindow.ReturnMenu（v0.2.8：mod页拦截 ChangeState(MENU)）");
            Safe(ctx.Harmony, typeof(ConfigClosePatch), "UICampConfigWindow.CloseWindow（清标志+落盘）");
            Safe(ctx.Harmony, typeof(RebuildPatch), "UICampConfigMenuListWindow.UpdateContentDataList（modMode 换行）");
            Safe(ctx.Harmony, typeof(RowTitlePatch), "UICampConfigMenuContent.SetContentData/UpdateShowComponent（mod行标题 SetText）");
            Safe(ctx.Harmony, typeof(ToggleListResetPatch), "UICampConfigMenuContent.UpdateShowComponent Prefix（芯片嵌套数据表清空重建）");
            Safe(ctx.Harmony, typeof(ChipTextPatch), "UICampConfigMenuToggleContent.UpdateShowComponent（芯片文本直写）");
            Safe(ctx.Harmony, typeof(UITextGuardPatch), "UIText.UpdateText（标记行跳过查表）");
            Safe(ctx.Harmony, typeof(TogglePatch), "UpdateContentData(bool)（芯片/滑块行步进）");
            Safe(ctx.Harmony, typeof(GaugePatch1), "UpdateContentData(float)（滑块绝对值路径）");
            Safe(ctx.Harmony, typeof(GaugePatch2), "UpdateContentData(float,bool)（滑块绝对值路径2）");
            Safe(ctx.Harmony, typeof(Decision2Patch), "OnPressDecision（入口行/modMode 动作行）");
            Safe(ctx.Harmony, typeof(CancelPatch), "OnPressCanceled（modMode 返回原版列表）");
            Safe(ctx.Harmony, typeof(TabNextPatch), "OnTabNext（modMode 禁用）");
            Safe(ctx.Harmony, typeof(TabPrevPatch), "OnTabPrev（modMode 禁用）");
            // v0.3.1：上键一次到位（见 VertPrevPatch 的反汇编注释）
            // ⛔⛔ v0.3.1-r12：**注销** SetSelectIndexPatch（原 UIScrollController.SetSelectIndex 补丁）。
            //   它是本 mod 唯一挂在**全局 UI 滚动控件**上的补丁 —— 标题菜单、所有列表都走它。
            //   而玩家报的正是"退出战斗到标题 ⇒ 标题菜单加载不出来"；且该补丁会去访问
            //   `_win`（营地设置窗），场景一换就是已销毁对象，对 il2cpp 侧死对象调用方法
            //   未必抛托管异常（可能是进程级事故）。收益（取证用的一行观测）远小于风险。
            //   VertPrevPatch 挂在营地专用窗上，风险面小得多，保留。
            Safe(ctx.Harmony, typeof(VertPrevPatch), "OnVertListPrev（首行按上 ⇒ 直达入口行）");
            Safe(ctx.Harmony, typeof(MessageBoxPressPatch), "UISystemMessageBoxWindow.OnPress（原生确认弹窗结果）");
            Safe(ctx.Harmony, typeof(MessageBoxCrossPatch), "UISystemMessageBoxWindow.OnCross（弹窗取消）");
        }

        /// <summary>
        /// 每帧（v0.1.20）：滑块调整态的左右输入由我们自己读。
        /// 反汇编实锤 UpdateContentData(bool)(0x22BAB0) 与 UpdateContentData(float)(0x22BAA0)
        /// 都是**零调用者**（原版经委托间接调用，Harmony 挂在上面永不触发）→ 左右键此前毫无响应。
        /// 改在 CHANGE_VALUE 态下直接读输入：键盘方向键 + 手柄十字键（QolInput 已统一映射）。
        /// </summary>
        public void OnUpdate()
        {
            // v0.1.25 看门狗：弹窗打开后若回调一直没来（窗口被抢关/重建/异常），
            // 窗口会永远停在 SHOW_DIALOG → 上下左右全废。超时按"取消"强制收尾。
            if (_pendingRow >= 0 && _pendingAt != 0 && Environment.TickCount - _pendingAt > 20000)
            {
                QolLog.Warn("camp: 弹窗超时未回调，强制恢复（行 " + _pendingRow + "）");
                OnDialogResult(-1);
            }

            // v0.1.28 看门狗（第二条路径）：上面那条只管"弹窗回调一直没来"；这条管"状态确实卡住了"
            // —— 即使 _pendingRow 已经清成 -1，只要 mListState 仍停在 SHOW_DIALOG 就自愈。
            GuardStuckDialogState();

            if (!_modMode)
            {
                GuardEntryCursor();   // v0.1.27：原版页的光标纠偏（见 _entryFixFrames 说明）
                return;
            }
            try
            {
                // v0.1.22：输入源换成**游戏自己的按键查询** StellaInputManager.GetKeyDown：
                //   HORI_LIST_NEXT(10)/PREV(11) + 十字键变体(16/17)。实测（v0.1.21 tick 日志）
                //   游戏轴 Horizontal 在菜单里恒 0.00、UnityEngine.Input.GetKeyDown 也收不到
                //   ——只有游戏自己的输入层（Steam Input 统一后）才知道按键。
                bool right = GameKeyDown(10) || GameKeyDown(16) || QolInput.Down(275);
                bool left = GameKeyDown(11) || GameKeyDown(17) || QolInput.Down(276);

                if ((_tick++ % 600) == 0)
                    QolLog.Info("camp: tick adjust=" + _adjust + " win=" + (_win != null) + " im=" + (_imType != null));

                // v0.1.23：恢复 v0.1.15 的可用交互——左右键**随时**直接调当前行，
                // 不再要求先按确定进入"调整态"（v0.1.20 加的 _adjust 门槛是左右键失效的真凶）。
                if (_win == null) return;
                if (!left && !right) return;
                StepSelected(_win, right ? 1 : -1, float.NaN);
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 滑块输入失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>游戏统一按键查询 StellaInputManager.GetKeyDown(eInputType)（静态，含键盘+手柄映射）。</summary>
        private static bool GameKeyDown(int inputType)
        {
            try
            {
                if (_imType == null)
                {
                    // v0.1.23：dump 实锤该类在 **Game 命名空间**下（dump.cs:163242
                    // "// Namespace: Game"）——之前按裸名 "StellaInputManager" 找，永远找不到
                    // （v0.1.22 日志 tick im=False），游戏按键查询整条死掉 → 左右键无反应。
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _imType = asm.GetType("Game.StellaInputManager") ?? asm.GetType("StellaInputManager");
                        if (_imType != null) break;
                    }
                    // 兜底：从已知 il2cpp 代理类型反查同一程序集（避免遍历/命名空间再踩坑）
                    if (_imType == null)
                        _imType = typeof(UICampConfigMenuListWindow).Assembly
                                    .GetType("Game.StellaInputManager");
                    if (_imType == null) { Diag("camp: 找不到 Game.StellaInputManager（左右键仅键盘可用）"); return false; }
                }
                if (_miKeyDown == null)
                {
                    var et = _imType.GetNestedType("eInputType");
                    if (et == null) return false;
                    _miKeyDown = _imType.GetMethod("GetKeyDown", new Type[] { et });
                    if (_miKeyDown == null) return false;
                }
                object v = _miKeyDown.Invoke(null, new object[] { Enum.ToObject(_miKeyDown.GetParameters()[0].ParameterType, inputType) });
                return v != null && (bool)v;
            }
            catch { return false; }
        }
        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            _vanilla = null;
            _adjust = false;
            _page = CampPage.Main;            // v0.2.4 / v0.3 批次 4：所有子页一并退回主页
            _levelRowsBuilt = false;          // 场景重载后角色对象全变，A7 行表必须重建
            _pendingRow = -1;
            _pendingWin = null;
            _entryPtr = System.IntPtr.Zero;   // 场景重载后 UI 重建，旧指针全部作废
            _entryObj = null;
            _rowByPtr.Clear();
            _entryFixFrames = 0;
            _lastCursorDiag = "";
            _stuckStateAt = 0;
        }
        public void OnReset() { }

        // ------------------------------------------------------------------
        // 营地页显示文本（繁体中文）：游戏字体只含繁中字形，简体字渲染为"□"
        // （2026-09-10 实机截图实锤）。camp 页一律走 Disp()；F2 注入菜单仍走 Loc。
        // ------------------------------------------------------------------

        private static string Disp(string key)
        {
            string v;
            return TW.TryGetValue(key, out v) ? v : Loc.T(key);
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> TW =
            new System.Collections.Generic.Dictionary<string, string>
        {
            { "camp.entry",      "MOD設定" },
            { "camp.hintf2",     "F2 完整面板" },
            { "camp.cat.a",      "核心減肝" },
            { "camp.cat.b",      "戰鬥輔助" },
            { "camp.cat.c",      "經濟" },
            { "camp.cat.d",      "養成" },
            { "camp.cat.e",      "探索" },
            { "camp.cat.f",      "收集圖鑑" },
            { "camp.cat.h",      "流程加速" },
            { "camp.cat.g",      "系統" },
            // v0.1.27：警示后缀压到一个全角字符。原版标题框只放得下约 9 个全角字宽
            // （"恢復初始設定" 6 字两侧还留白），"（不可逆）" 5 字会把标题顶到框外。
            { "camp.warn",       "！" },
            { "camp.confirm",    "再確認" },
            { "camp.ask",        "是否要變更？（此操作不可逆）" },
            { "f.exp",           "經驗倍率" },
            { "f.money",         "金錢倍率" },
            { "f.dropcount",     "掉落倍率" },
            { "f.droprate",      "掉率拉滿" },
            { "f.onehit",        "秒殺" },
            { "f.onehit.prot",   "無視保護強殺" },
            { "f.dmg",           "傷害倍率" },
            { "f.god",           "玩家無敵" },
            // v0.2 批次 1 的四行（v0.2.1 补）：当初只加了 Loc 的简体表，Disp() 查不到 TW 键就
            // 回退 Loc.T() → 简体字在游戏字体里没有字形 → 营地页整行渲染成 □。
            // ⛔ 教训：新增营地行必须**同时**加 Loc.Zh 与这里的 TW，只加一边就是"能编译、能显示英文、中文变方块"。
            { "f.crit",          "必定暴擊" },
            { "f.crygauge",      "哭泣槽常滿" },
            { "f.combo",         "連擊不中斷" },
            { "f.crymode",       "CryMode常駐" },
            // v0.2 批次 2（經濟三件套 + 自動拾取）。⛔ 同 v0.2.1 的教训：简繁两表必须一起加。
            { "f.autopickup",    "自動拾取" },
            { "f.shopunlock",    "商店全開" },
            { "f.money.full",    "金錢全滿" },
            { "f.item.spawn",    "生成物品" },
            { "f.item.done",     "完成" },
            { "f.item.fail",     "失敗" },
            { "f.item.badid",    "ID錯" },
            { "f.item.err",      "錯誤" },
            // v0.2.4：物品生成子页 + 拾取范围（营地页必须走 TW，见 v0.2.1 的 □ 教训）
            { "f.spawn.step",    "步進" },
            { "f.spawn.id",      "物品ID" },
            { "f.spawn.name",    "物品名" },
            { "f.spawn.count",   "數量" },
            { "f.spawn.exec",    "確定生成" },
            { "f.spawn.back",    "返回上一級" },
            { "f.pickuprange",   "拾取範圍" },
            // v0.2 批次 3（養成深化）。⛔ 同 v0.2.1 教训：简繁两表必须一起加，缺一边营地页就变 □。
            // 修正十七（r18）：標題統一用**遊戲本名 + 無消耗**，
            //   • 淨化幽怨（讀書房「淨化」）  • 思裝調合（裝備強化 / 融合）  • 思裝變容（能力付與）
            { "f.creation.free", "淨化幽怨無消耗" },
            { "f.enhance.free",  "思裝調合無消耗" },
            { "f.grant.free",    "思裝變容無消耗" },
            { "f.deathcry.fifo",   "幽怨滿時頂替最早" },
            { "f.deathcry.expand", "幽怨槽池擴容20" },
            // v0.2.10 修正十四（r15）：r14 的兩個配套開關當初漏了營地行 ⇒ 補上（簡繁英三表同步）
            { "f.deathcry.icon",   "幽怨圖標跟隨上限" },
            { "f.deathcry.scale",  "幽怨計數隨上限" },
            { "f.skill.all",     "全技能解鎖" },
            { "camp.back",       "返回上一級" },
            { "f.move",          "移速倍率" },
            { "f.atkspeed",      "攻擊速度倍率" },
            { "f.kill",          "擊殺倍率" },
            { "f.map",           "地圖全開" },
            // v0.3 批次 1（D8）。⛔ 簡繁兩表必須一起加，缺 TW 就是整行 □（v0.2.1 教訓）。
            { "f.openskip",      "開場影片自動跳過" },
            // v0.3 批次 2（D9）。簡繁兩表必須一起加，缺 TW 就是整行 □（v0.2.1 教訓）。
            { "f.cryskip",       "幽怨演出自動跳過" },
            // v0.3 批次 3（E3）。簡繁兩表必須一起加，缺 TW 就是整行 □（v0.2.1 教訓）。
            { "f.mapicons",      "大地圖敵人標記" },
            // ⛔ E4 寶箱標記已於 2026-11-06 實機否決並移除。
            // v0.3 批次 4（A7 手動調整等級數，逐角色子頁）。簡繁英三表齊改，缺 TW 整行 □。
            { "f.level",         "角色等級" },
            { "f.level.chara",   "角色" },
            { "f.level.cur",     "當前等級" },
            { "f.level.step",    "步進" },
            { "f.level.target",  "目標等級" },
            { "f.level.apply",   "套用等級" },
            { "f.album",         "回憶錄全解鎖" },
            { "f.album.done",    "解鎖 {0}" },
            { "f.album.fail",    "需讀檔" },
            { "core.loglevel",   "日誌級別" },
            { "core.backup",     "備份存檔" },
            { "core.resetall",   "全部恢復預設" },
            { "backup.done",     "完成" },
            { "backup.fail",     "失敗" },
            { "reset.done",      "完成" },
        };

        private static string LevelChip(int lv)
        {
            return lv <= 0 ? "關" : (lv == 1 ? "普通" : "詳細");
        }


        public IEnumerable<MenuItem> BuildMenu() { yield break; }   // F2 菜单不新增项；本 feature 只服务营地页

        // ------------------------------------------------------------------
        // 行注册表（按功能分类 A/B/E/F/G 组织，只收已实现功能）
        // ------------------------------------------------------------------

        private static void EnsureRows()
        {
            if (_rowsBuilt) return;
            _rowsBuilt = true;

            // v0.3.1：这里原本是第一行提示「按 F2 開啟完整設定介面」（camp.hintf2），已按用户要求删除。
            // ⚠ 只是不占用营地页的一行了 —— **F2 自绘面板本身仍然存在**（Key.Menu 默认 F2），
            //   它依旧是一条独立退路；营地页不再替它做广告。

            // ── A 核心减肝 ──
            MainRows.Add(CampRow.Header("camp.cat.a"));
            MainRows.Add(CampRow.CycleFloat("f.exp",       "Crystar.QoL.expmoney",    "ExpMult",       new float[] { 0.5f, 1f, 1.5f, 2f, 3f, 4f, 5f, 10f }));
            MainRows.Add(CampRow.CycleFloat("f.money",     "Crystar.QoL.expmoney",    "MoneyMult",     new float[] { 0.5f, 1f, 1.5f, 2f, 3f, 4f, 5f, 10f }));
            MainRows.Add(CampRow.CycleFloat("f.dropcount", "Crystar.QoL.drops",       "DropCountMult", new float[] { 1f, 2f, 3f, 4f, 5f, 8f, 10f }));
            MainRows.Add(CampRow.CycleBool("f.droprate",   "Crystar.QoL.drops",       "DropRateMax"));
            MainRows.Add(CampRow.CycleBool("f.onehit",     "Crystar.QoL.combat",      "OneHit"));
            MainRows.Add(CampRow.CycleFloat("f.dmg",       "Crystar.QoL.combat",      "DamageMult",    new float[] { 0.5f, 1f, 1.5f, 2f, 3f, 5f, 10f }));
            MainRows.Add(CampRow.CycleFloat("f.move",      "Crystar.QoL.movespeed",   "Multiplier",    new float[] { 0.5f, 1f, 1.25f, 1.5f, 2f, 3f }));
            MainRows.Add(CampRow.CycleInt("f.kill", new int[] { 1, 2, 3, 4, 5, 10 }, "Crystar.QoL.killcounter", "Multiplier"));
            // v0.2 批次 2：B5 自动拾取（v0.2.5：驱动改为自驱 OnUpdate —— 全图吸 / 或指定半径都行）
            MainRows.Add(CampRow.CycleBool("f.autopickup", "Crystar.QoL.pickup", "AutoPickup"));
            MainRows.Add(CampRow.CycleInt("f.pickuprange", FeaturePickup.RangeOptions,
                "Crystar.QoL.pickup", "PickupRange", textOf: FeaturePickup.RangeText));

            // ── B 战斗辅助 ──
            MainRows.Add(CampRow.Header("camp.cat.b"));
            MainRows.Add(CampRow.CycleBool("f.god",        "Crystar.QoL.combat",      "GodMode"));
            MainRows.Add(CampRow.CycleBool("f.onehit.prot","Crystar.QoL.combat",      "ForceProtected", warn: true));
            // v0.2 批次 1：B2 哭泣槽常满 / B3 CryMode 常驻 / B4 连击不中断 / B7 必定暴击
            MainRows.Add(CampRow.CycleBool("f.crit",       "Crystar.QoL.combat",      "AlwaysCrit"));
            MainRows.Add(CampRow.CycleBool("f.crygauge",   "Crystar.QoL.cry",         "CryGaugeFull"));
            MainRows.Add(CampRow.CycleBool("f.combo",      "Crystar.QoL.combo",       "NoBreak"));
            // 攻速倍率：改技能序列表 ConstSkillSequenceParameter.mAnimationSpeed(0x44)，
            // 只改 eSkillID 里 PLAYER_ 前缀的技能（敌人不动）。5 档 ⇒ GAUGE 滑块。
            MainRows.Add(CampRow.CycleFloat("f.atkspeed",   "Crystar.QoL.atkspeed",    "Multiplier",     new float[] { 1f, 1.25f, 1.5f, 2f, 3f }));
            // B3 CryMode 常驻：v0.2.10-r5 起**不再弹确认框** —— 用户实机确认它是**可逆**的
            //（关掉开关即可退出 CryMode，退出后 mod 自己 `SetScenarioFlag(9,false)` 收尾；
            //  不像 f.skill.all / f.onehit.prot 那种"开了就回不去"的不可逆项）。
            // ⇒ warn: false：确定键与左右调值都直接生效，不再拦一道「是/否」。
            MainRows.Add(CampRow.CycleBool("f.crymode",    "Crystar.QoL.cry",         "CryModeAlways"));

            // ── C 经济（v0.2 批次 2）──
            MainRows.Add(CampRow.Header("camp.cat.c"));
            // C2 商店全解锁：改的是 ConstShopParameter 表（start/end 进度门槛），不改存档
            MainRows.Add(CampRow.CycleBool("f.shopunlock", "Crystar.QoL.economy", "ShopUnlockAll"));
            // C6 金钱：营地页只给"一键全满"（原生控件表达不了数值输入），精确数值走 F2
            MainRows.Add(CampRow.Action("f.money.full", delegate {
                return FeatureEconomy.SetMoney(99999999) ? Disp("reset.done") : Disp("backup.fail");
            }, warn: true));
            // C7 物品生成：v0.2.4 改成**子页面**（按用户给的样式）——
            // 按确定进入子页：步進 / 物品ID / 物品名 / 數量 / 確定生成，取消键返回。
            // （v0.2.3 是"在 F2 里选 ID、回营地页执行"，用户反馈太绕。）
            MainRows.Add(CampRow.Page("f.item.spawn"));

            // ── D 養成（v0.2 批次 3）──
            // C1 强化零消耗 / C3 炼成无材料 / C4 全技能解锁（FeatureGrowth）
            MainRows.Add(CampRow.Header("camp.cat.d"));
            // 修正十七（r18）：三行统一命名（游戏本名 + 无消耗），顺序按玩家要求 = 净化 → 调合 → 变容。
            //   键名不变（FreeCreation / FreeEnhance / FreeGrant）⇒ 老 settings.cfg 的开关状态照旧生效。
            MainRows.Add(CampRow.CycleBool("f.creation.free", "Crystar.QoL.growth", "FreeCreation"));
            MainRows.Add(CampRow.CycleBool("f.enhance.free",  "Crystar.QoL.growth", "FreeEnhance"));
            MainRows.Add(CampRow.CycleBool("f.grant.free",    "Crystar.QoL.growth", "FreeGrant"));
            // 不可逆（游戏没有"忘技能"机制，学会就退不回去）→ 与 f.onehit.prot / f.money.full /
            // f.album / core.resetall 同款处理：确定键与左右调值都要先过原生「是/否」确认框（warn: true）。
            // ⚠ f.crymode **不在**这一组里（v0.2.10-r5 起已去掉 warn，见上方 375~379 行）。
            MainRows.Add(CampRow.CycleBool("f.skill.all",     "Crystar.QoL.growth", "AllSkills", warn: true));
            // 修正十（r11）：幽怨满时顶替最早（默认开）/ 槽池扩容到 20（默认关）。
            // ⚠ 二者不要同开：扩容后 UI 只认 10（mDeathCryCharaID 硬编码 10 格），FIFO 才是对的解。
            MainRows.Add(CampRow.CycleBool("f.deathcry.fifo",   "Crystar.QoL.growth", "DeathcryFifo"));
            MainRows.Add(CampRow.CycleBool("f.deathcry.expand", "Crystar.QoL.growth", "DeathcryPoolExpand"));
            // 修正十四（r15）：r14 加了配置项却忘了加营地行 —— 游戏里看不到 = 等于没做。
            MainRows.Add(CampRow.CycleBool("f.deathcry.icon",   "Crystar.QoL.growth", "DeathcryIconExpand"));
            MainRows.Add(CampRow.CycleBool("f.deathcry.scale",  "Crystar.QoL.growth", "DeathcryCountScale"));
            // v0.3 批次 4 / A7：手动调整等级数。放在「養成」末尾（等级属养成，铁律 50 按用途归类）。
            // ⚠ warn 不在入口行上：真正不可逆的是子页里的「套用等級」动作行，那里才需要二次确认。
            MainRows.Add(CampRow.Page("f.level", false, CampPage.Level));

            // ── E 探索 ──
            MainRows.Add(CampRow.Header("camp.cat.e"));
            MainRows.Add(CampRow.CycleBool("f.map",        "Crystar.QoL.map",         "RevealAll"));
            // v0.3 批次 3（E3）：把小地图上已有的敌人/稀有敌人/BOSS/NPC 图标补发到大地图。
            // ⚠ 2026-09-24 归类订正：**放「探索」不放「流程加速」**——E3 是看地图用的（跟 f.map 同族），
            //   跟「跳过播片」这类省时间的功能不是一回事（用户原话："不是应该放在探索里面吗"）。
            MainRows.Add(CampRow.CycleBool("f.mapicons", "Crystar.QoL.mapicons", "ShowEnemyIcons"));
            // ⛔ E4 大地图宝箱标记 2026-11-06 实机否决并移除；「探索」分类只剩 f.map / f.mapicons 两项。

            // ── F 收集图鉴 ──
            MainRows.Add(CampRow.Header("camp.cat.f"));
            MainRows.Add(CampRow.Action("f.album", delegate {
                int n = FeatureAlbum.RunUnlockAll();
                // v0.1.27：**必须用 Disp（繁体表）**——Loc 的简中句子既太长（标题折行）
                // 又会被只含繁中字形的游戏字体渲染成 "□"。这里只回一个短标签。
                return n >= 0 ? string.Format(Disp("f.album.done"), n) : Disp("f.album.fail");
            }, warn: true));

            // ── H 流程加速（v0.3）──
            // D8 开场播片自动跳过：播片开始后替游戏调一次 OnEndMovie()，切场景仍由游戏自己完成。
            // ⚠ 只有下一次启动/进入播片时才看得出效果（播片只在开场放一次），故 F2 面板标 RestartGame。
            MainRows.Add(CampRow.Header("camp.cat.h"));
            MainRows.Add(CampRow.CycleBool("f.openskip", "Crystar.QoL.opening", "AutoSkip"));
            MainRows.Add(CampRow.CycleBool("f.cryskip", "Crystar.QoL.cryskip", "AutoSkipCry"));

            // ── G 系统 ──
            MainRows.Add(CampRow.Header("camp.cat.g"));
            MainRows.Add(CampRow.CycleInt("core.loglevel", new int[] { 0, 1, 2 }, textOf: LevelChip, entry: QolConfig.LogLevel));
            // v0.3.1-r13：「螢幕狀態條」開關已隨 F3 一併撤除（見 QolConfig 註解）。
            MainRows.Add(CampRow.Action("core.backup", delegate {
                return SaveBackup.Backup("camp") != null ? Disp("backup.done") : Disp("backup.fail");
            }));
            MainRows.Add(CampRow.Action("core.resetall", delegate {
                QolConfig.ResetAll();
                return Disp("reset.done");
            }, warn: true));

            // v0.2.6（用户原话："我是想让你在我点返回按钮的时候能返回，而不是我还要单独去找一个返回按钮
            // 去点一下确认"）：**删掉显式的「返回上一級」动作行**。
            // 取消/返回键本来就走 GoBack（`OnPressCanceled` 与 `UICampConfigWindow.CloseWindow` 两条路径都接，
            // 实机日志 67 次"退出 MOD 设置页 → 退回原版設定列表（拦截关窗）"实锤有效），
            // 再放一个要滚到列表末尾才能选中的"返回"行，只会让人以为必须去找它点确定。
            // ⇒ 列表末行现在就是「全部恢復預設」；返回一律按取消键（已加跨路径去重：一次按键退一级）。
        }

        /// <summary>
        /// v0.2.4：物品生成子页的行表（按用户给的样式：步進 / ID / 物品名 / 數量 / 確定）。
        /// 只在第一次进入子页时构建；所有值直接读写 FeatureEconomy 的配置项，
        /// 所以 F2 面板与营地子页看到的是同一份数据。
        /// ⛔ 文案必须走 Disp()（繁体表），否则游戏字体没有简体字形 → 变 □（v0.2.1 教训）。
        /// </summary>
        private static void EnsureSpawnRows()
        {
            if (_spawnRowsBuilt) return;
            _spawnRowsBuilt = true;

            SpawnRows.Add(CampRow.Header("f.item.spawn"));
            // 步進：物品 ID 的加减步长（4 档 → NONE 行，档位并入标题）
            SpawnRows.Add(CampRow.CycleInt("f.spawn.step", new int[] { 1, 10, 50, 100 },
                "Crystar.QoL.economy", "SpawnIdStep", textOf: v => "±" + v));
            // 物品 ID：1..411，步长取自上一行（一次左右键 = 一个步進）
            SpawnRows.Add(CampRow.IntRange("f.spawn.id", "Crystar.QoL.economy", "SpawnItemId",
                FeatureEconomy.MinItemId, FeatureEconomy.MaxItemId,
                delegate { return FeatureEconomy.IdStep; }, delegate (int v) { return v.ToString(); }));
            // 物品名：只读（运行时向游戏查表 → 自动跟随语言，不会 □）
            SpawnRows.Add(CampRow.Info("f.spawn.name", delegate {
                string n = FeatureEconomy.ItemName(FeatureEconomy.SpawnId);
                if (string.IsNullOrEmpty(n)) return "—";
                // 标题框约 9 个全角字宽，「物品名：」占 4 → 名字最多 5 字，超出截断
                return n.Length > 5 ? n.Substring(0, 5) : n;
            }));
            // 數量：4 档（≤4 档 = 纯文本循环行，确定键直接切档，不用先进滑块调整态）。
            // 上限 99（背包格上限；超过会被游戏整笔丢弃）。
            SpawnRows.Add(CampRow.CycleInt("f.spawn.count", new int[] { 1, 10, 50, 99 },
                "Crystar.QoL.economy", "SpawnItemCount", textOf: v => "x" + v));
            // 確定生成：ActionRow —— 结果直接挂在标题尾部 6 秒
            // 成功（code==0）时把实际数量一并写出来，失败（code>0）时附上游戏错误码便于排查
            SpawnRows.Add(CampRow.Action("f.spawn.exec", delegate {
                int cnt = FeatureEconomy.SpawnCount;
                int code = FeatureEconomy.SpawnItem(FeatureEconomy.SpawnId, cnt);
                string s = Disp(FeatureEconomy.SpawnResultKey(code));
                if (code == FeatureEconomy.SpawnOk) s += " x" + cnt;
                else if (code > 0) s += " (" + code + ")";
                return s;
            }));
            // v0.2.6：**删掉「返回上一級」动作行**（理由同 MainRows 末尾的注释）——
            // 取消键从子页返回上一级已实机验证有效（日志："物品生成子页 → 返回 MOD 設定页（上一级）"），
            // 不需要再让玩家滚到最后一行去点它。子页现在 6 行。
        }

        /// <summary>
        /// v0.3 批次 4 / A7：角色等级子页（与 C7 生成物品子页同构，方案 §8.3）。
        /// ⚠ **每次进入子页都重建**：角色数是运行时动态的，而 IntRangeRow 的 max 在构造时
        ///   就固定，不重建就选不到新入队的角色（方案 8.3 约束①）；取不到列表要打 Warn，别静默变空。
        /// ⚠ 文案一律 Disp()（繁体表），缺 TW 会渲染成 □（v0.2.1 教训）。
        /// ⚠ 等级写进 CharacterParameter（保存后进存档）⇒ 「套用等級」必须 warn:true（铁律 21）。
        /// </summary>
        private static void EnsureLevelRows()
        {
            if (_levelRowsBuilt) return;
            _levelRowsBuilt = true;
            LevelRows.Clear();

            FeatureLevel.Refresh();                 // 强制刷新角色列表
            int n = FeatureLevel.ActorCount();
            if (n <= 0)
                QolLog.Warn("camp: A7 子页角色列表为空（请先读档）；行表仍会显示，套用会拒绝执行");

            LevelRows.Add(CampRow.Header("f.level"));
            // 卡片模式（2026-11-06 用户反馈）：一行一个角色，显示角色名 + 当前等级 + 选中标记。
            // 去掉了原来单行左右切换的「角色」行 —— 看不到四个角色全貌、也不直观。
            for (int i = 0; i < n; i++) LevelRows.Add(CampRow.Card(i));
            // 当前等级：只读，动态读（DisplayTitle 每轮重算）
            LevelRows.Add(CampRow.Info("f.level.cur", delegate {
                return FeatureLevel.CurrentLevelText(CharaIndex());
            }));
            // 步進：1 / 10 / 20，与 C7 同款
            LevelRows.Add(CampRow.CycleInt("f.level.step", new int[] { 1, 10, 20 },
                "Crystar.QoL.camp", "LevelStep", textOf: v => "±" + v));
            // 目標等級：1..99，MAX_LEVEL 是游戏 const，只夹取不改它。
            // ⚠ 默认值不再固定 1：进页 / 切角色时由 ResetLevelTargetToCurrent 写成该角色当前等级。
            LevelRows.Add(CampRow.IntRange("f.level.target", "Crystar.QoL.camp", "LevelTarget",
                1, FeatureLevel.MaxLevel,
                delegate { return LevelStepValue(); }, delegate (int v) { return v.ToString(); }));
            // 套用：直写 mLevel + Invoke UpdateLevel + 回读校验，全在 FeatureLevel.Apply。
            // ⚠ 只对 CharaIndex() 指向的当前角色动手；切角色未确认的编辑值早已被丢弃。
            LevelRows.Add(CampRow.Action("f.level.apply", delegate {
                return FeatureLevel.Apply(CharaIndex(), TargetLevel(),
                    delegate (bool ok) { return ok ? Disp("reset.done") : Disp("backup.fail"); });
            }, warn: true));

            ResetLevelTargetToCurrent();   // ★ 进页默认 = 当前角色当前等级，不残留上次编辑值
            // v0.2.6 起子页不放「返回上一級」动作行：取消键返回上一级已实机验证有效。
        }

        private static int CharaIndex()
        {
            int i = _levelChara != null ? _levelChara.Value : 0;
            int n = FeatureLevel.ActorCount();
            if (n <= 0) return 0;
            return i < 0 ? 0 : (i >= n ? n - 1 : i);
        }

        private static int LevelStepValue()
        {
            int s = _levelStep != null ? _levelStep.Value : 1;
            return s < 1 ? 1 : (s > 20 ? 20 : s);
        }

        private static int TargetLevel()
        {
            int t = _levelTarget != null ? _levelTarget.Value : 1;
            return t < 1 ? 1 : (t > FeatureLevel.MaxLevel ? FeatureLevel.MaxLevel : t);
        }

        /// <summary>
        /// A7（2026-11-06 用户反馈）：把「目標等級」重置为**当前选中角色的当前等级**。
        /// 调用点：① 每次进入子页（EnsureLevelRows 末尾）② 卡片选中（Decision2Patch）
        ///        ③ Rows getter 的角色索引脏检查（兜住任何外部改值路径）。
        /// ⇒ 进页不再是 1 级、也不残留上次会话的编辑值；切换角色时未保存的编辑直接丢弃，
        ///   **不写入任何角色**；只有「套用等級」经 warn 确认后才会动 CharacterParameter。
        /// ⚠ 顺手清掉 IntRangeRow 的 _preview，避免「調整中」残留把新值盖掉。
        /// </summary>
        private static void ResetLevelTargetToCurrent()
        {
            int idx = CharaIndex();
            if (_levelChara != null && _levelChara.Value != idx) _levelChara.Value = idx;
            _levelCharaSeen = idx;

            int lv = FeatureLevel.CurrentLevel(idx);
            if (lv < 1) lv = 1;
            if (lv > FeatureLevel.MaxLevel) lv = FeatureLevel.MaxLevel;
            if (_levelTarget != null) _levelTarget.Value = lv;

            for (int i = 0; i < LevelRows.Count; i++)
            {
                var ir = LevelRows[i] as IntRangeRow;
                if (ir != null) ir.CancelPreview();
            }
        }

        // ------------------------------------------------------------------
        // CampRow：配置条目（2026-09-10 适配游戏原生控件——用户实机反馈纯文本行
        // 右侧无控制器、长标题重叠）：
        // - NONE    ：头行/动作行/入口行（1 参 ctor，标题 SetText 直写）
        // - TOGGLES ：≤4 档 → 原生芯片行（難易度同款），mToggleContentTitleList
        //             反射填充（避开 string[] 参数的 3 参 ctor——运行时签名错配），
        //             mSelectToggleIndex = 当前档，右侧芯片原生显示当前值
        // - GAUGE   ：>4 档 → 原生滑块行（2 参 ctor(string,float) 无数组参数，
        //             运行时安全），mGaugeValue = 档位归一化 0..1（上下限=档位表）；
        //             原版滑块无数值组件（反汇编实锤 GAUGE 分支只读 0x30 一个 float），
        //             当前值显示在标题尾部
        // ------------------------------------------------------------------

        private enum RowKind { None, Gauge, Toggles }

        private abstract class CampRow
        {
            public string LabelKey;                    // null = 直接用 TitleOverride
            public string TitleOverride;               // 头行用
            public bool Warn;                          // 高危/不可逆标注（用户要求保留）
            public RowKind Kind = RowKind.None;

            public abstract string DisplayTitle();
            public virtual bool SupportsStep { get { return false; } }
            public virtual void Step(int delta) { }
            public virtual float GaugeNorm() { return 0f; }        // GAUGE：归一化值
            public virtual void StepToNorm(float norm) { }         // GAUGE：按归一化值设档
            public virtual int SelectedChip() { return 0; }        // TOGGLES：选中芯片下标
            public virtual string[] Chips() { return null; }       // TOGGLES：芯片文本

            protected string Title()
            {
                if (TitleOverride != null) return TitleOverride;
                string label = Disp(LabelKey);
                return Warn ? label + Disp("camp.warn") : label;
            }

            public static CampRow Header(string locKey)
            {
                // v0.2.5：按用户给的样式做成「尖括号夹中括号」：> [ 标题 ] <
                // ⛔⛔ v0.2.6 重大修正：**绝不能用 ASCII 的 > <**！
                //    所有 SetText 文本都会先过 `Game.TagConverter.ReplaceTagToString()`（见 WriteTitle 注释），
                //    它把 ASCII "<" / ">" 当标签定界符：IndexOf("<")=8、IndexOf(">")=0 →
                //    Substring(left, right-left+1) 长度 = 0-8+1 = **负数** →
                //    `Il2CppException: System.ArgumentOutOfRangeException: Cannot be negative.`
                //    实测后果（2026-09-12 用户截图 + debug.log 5859 条刷屏）：
                //      • 8 个分节标题全部写不进去 → 标题空/显示成上一行的文字（"各层级标题没了"）
                //      • 行组件池化复用 → 失败的行保留上一行文本 → 列表里出现**成对重复的行**
                //      • 入口行标题同样失败 → 原版页末尾的「MOD設定」显示成上一行的「恢復初始設定」
                //        （用户原话："'mod设置'变成'恢复初始'设置了"）
                // 现在改用 **全角** 的 ＞ ＜（U+FF1E / U+FF1C）：视觉与用户要的样式一致，
                //    但**不是** ASCII 定界符 → TagConverter 完全不认；且全角 ASCII 变体是日文字体
                //    最基础的字形（JIS X 0208 第 1 行），不存在 v0.2.4 "▶"那种缺字形变 □ 的风险。
                // ⛔ 铁律：任何送进 UIText.SetText 的字符串都不得含 ASCII '<' 或 '>'（WriteTitle 另有兜底净化）。
                return new HeaderRow { LabelKey = locKey, TitleOverride = "＞ [ " + Disp(locKey) + " ] ＜" };
            }

            /// <summary>只读信息行（物品名等）：标题由委托动态生成，不参与左右调值。</summary>
            public static CampRow Info(string labelKey, Func<string> text)
            {
                return new InfoRow(labelKey, text);
            }

            /// <summary>
            /// A7 角色卡片（2026-11-06 用户反馈）：一行一个角色，确定键选中。
            /// 分派在 Decision2Patch；选中时由 ResetLevelTargetToCurrent 重置目标等级。
            /// </summary>
            public static CampRow Card(int idx)
            {
                return new CardRow(idx);
            }

            /// <summary>
            /// 子页面入口行：按确定进入另一页（不是"执行动作"）。
            /// v0.3 批次 4：A7 带来第二个子页 ⇒ 入口行必须自带 Target，
            /// 旧调用（生成物品）默认 Spawn，保持行为不变。
            /// </summary>
            public static CampRow Page(string labelKey, bool warn = false,
                CampPage target = CampPage.Spawn)
            {
                return new PageRow(labelKey, warn, target);
            }

            /// <summary>整数范围行（物品 ID）：左右键按「步進」加减，落在 [min,max]。</summary>
            public static CampRow IntRange(string labelKey, string cat, string ent, int min, int max,
                Func<int> step, Func<int, string> textOf)
            {
                return new IntRangeRow(labelKey, MelonPreferences.GetEntry<int>(cat, ent), min, max, step, textOf);
            }

            public static CampRow CycleBool(string labelKey, string cat, string ent,
                bool warn = false, MelonPreferences_Entry<bool> entry = null)
            {
                return new EnumRow<bool>(
                    labelKey, warn,
                    entry ?? (cat != null ? MelonPreferences.GetEntry<bool>(cat, ent) : null),
                    new bool[] { false, true },
                    v => v ? "ON" : "OFF");
            }

            public static CampRow CycleFloat(string labelKey, string cat, string ent, float[] opts, bool warn = false)
            {
                return new EnumRow<float>(labelKey, warn, MelonPreferences.GetEntry<float>(cat, ent),
                    opts, v => "x" + v.ToString("0.##"));
            }

            public static CampRow CycleInt(string labelKey, int[] opts, string cat = null, string ent = null,
                bool warn = false, Func<int, string> textOf = null, MelonPreferences_Entry<int> entry = null)
            {
                return new EnumRow<int>(labelKey, warn,
                    entry ?? (cat != null ? MelonPreferences.GetEntry<int>(cat, ent) : null),
                    opts, textOf ?? (v => "x" + v));
            }

            public static CampRow Action(string labelKey, Func<string> run, bool warn = false)
            {
                return new ActionRow(labelKey, warn, run);
            }
        }

        private sealed class HeaderRow : CampRow
        {
            public HeaderRow() { Kind = RowKind.None; }
            public override string DisplayTitle() { return TitleOverride; }
        }

        /// <summary>
        /// 离散档位行（布尔/浮点/整数统一）。≤4 档→TOGGLES 芯片，>4 档→GAUGE 滑块。
        /// 交互适配游戏原生逻辑（用户 02:16 反馈）：芯片=确定键切下一档；
        /// 滑块=确定进入调整（左右改值实时预览但不写配置）→确定保存→取消回滚进调整前值。
        /// </summary>
        private sealed class EnumRow<T> : CampRow, IAdjustableRow
        {
            private readonly MelonPreferences_Entry<T> _entry;
            private readonly T[] _opts;
            private readonly Func<T, string> _text;
            private readonly string[] _chips;
            private int _preview = -1;              // GAUGE 调整中的临时档位（-1=无，用 entry 值）

            public EnumRow(string labelKey, bool warn, MelonPreferences_Entry<T> entry, T[] opts, Func<T, string> text)
            {
                LabelKey = labelKey; Warn = warn;
                _entry = entry; _opts = opts; _text = text;
                // v0.1.22：≤4 档也放弃原生 TOGGLES 芯片。实测（v0.1.21 日志"写入 2，隐藏 4"+
                // 截图）证明芯片文本已能写入，但芯片组的布局/选中态/显隐由嵌套 controller 全权
                // 驱动：屏幕只显示 1 个芯片、OFF 档缺失、显示档位与实际值相反——不可控。
                // 统一改 NONE 纯标题行，档位文本并入标题（显示 100% 自控），交互仍是原生确定键。
                Kind = opts.Length <= 4 ? RowKind.None : RowKind.Gauge;
                _chips = new string[opts.Length];
                for (int i = 0; i < opts.Length; i++) _chips[i] = text(opts[i]);
            }

            public override bool SupportsStep { get { return _entry != null; } }

            private int IndexOf(T v)
            {
                for (int i = 0; i < _opts.Length; i++)
                    if (EqualityComparer<T>.Default.Equals(_opts[i], v)) return i;
                return -1;
            }

            /// <summary>当前生效档位下标。</summary>
            public int CurrentIndex()
            {
                int i = IndexOf(_entry.Value);
                return i < 0 ? 0 : i;
            }

            /// <summary>直接设档位（预览/回滚/确定共用）。</summary>
            public void SetIndex(int i)
            {
                if (_entry == null || i < 0 || i >= _opts.Length) return;
                if (EqualityComparer<T>.Default.Equals(_opts[i], _entry.Value)) return;
                _entry.Value = _opts[i];
                QolConfig.Save();
            }

            /// <summary>芯片行确定键：切下一档（立即生效）。</summary>
            public override void Step(int delta)
            {
                if (_entry == null) return;
                int i = IndexOf(_entry.Value);
                if (i < 0) i = 0;
                i = (i + delta + _opts.Length) % _opts.Length;
                SetIndex(i);
            }

            // ---- GAUGE 调整模式（预览不落盘，确定 Commit / 取消 CancelPreview）----

            public void BeginPreview() { _preview = CurrentIndex(); }

            public void PreviewStep(int delta)
            {
                if (_preview < 0) _preview = CurrentIndex();
                // v0.2.7：调整态**夹紧**不回绕。旧版 `(i+delta+N)%N` 在端点会回绕——
                // 实机日志里 x0.5 按一下左就变成 x10（用户观感"加减不正常"）。
                int i = _preview + delta;
                if (i < 0) i = 0;
                else if (i >= _opts.Length) i = _opts.Length - 1;
                _preview = i;
            }

            /// <summary>
            /// v0.2.6：把"游戏传进来的归一化滑块位置"转成**每事件最多走一档**的步进。
            ///
            /// 实机问题（用户截图标注"现在可以用滑块了，但是只能从最左到最右"）：
            /// 一次左右输入，游戏会连发两个回调 —— `UpdateContentData(bool)` 给 delta=±1，
            /// 紧接着 `UpdateContentData(float)` 给**绝对位置** norm（手柄推到底 = 1.00）。
            /// v0.2.5 直接把 norm 映射成档位 → 后到的绝对位置把刚才的步进**覆盖成最大值**，
            /// 于是 0.5→1→1.5… 的中间档永远看不到，只有最左/最右。
            /// 日志实锤：`norm=NaN delta=1 → [x1.5]` 紧接 `norm=1.00 delta=0 → [x10]`。
            ///
            /// 现在只把 norm 当"方向"用：目标档位比当前大就 +1、小就 -1。长按 = 连续事件 = 逐档步进。
            /// </summary>
            public void PreviewToNorm(float norm)
            {
                if (_preview < 0) _preview = CurrentIndex();
                if (float.IsNaN(norm)) return;
                if (norm < 0f) norm = 0f;
                if (norm > 1f) norm = 1f;
                if (_opts.Length <= 1) return;
                int target = (int)System.Math.Round(norm * (_opts.Length - 1));
                if (target > _preview) _preview++;
                else if (target < _preview) _preview--;
            }

            public void CommitPreview()
            {
                if (_preview >= 0) SetIndex(_preview);
                _preview = -1;
            }

            public void CancelPreview() { _preview = -1; }

            public override float GaugeNorm()
            {
                int i = _preview >= 0 ? _preview : CurrentIndex();
                return _opts.Length <= 1 ? 0f : i / (float)(_opts.Length - 1);
            }

            public override int SelectedChip()
            {
                return _preview >= 0 ? _preview : CurrentIndex();
            }

            public override string[] Chips() { return _chips; }

            public override string DisplayTitle()
            {
                // GAUGE：原版滑块无数值组件 → 标题尾带当前值
                if (Kind == RowKind.Gauge && _entry != null)
                {
                    int i = _preview >= 0 ? _preview : CurrentIndex();
                    string v = _text(_opts[i]);
                    // v0.1.20：调整态必须在标题上给可见反馈——原生 SetGaugeIconMaterial 高亮
                    // 一旦反射不到（v0.1.18/19 就是这么静默丢的），用户至少能看到实时数值与
                    // 「調整中」。符号只用 ASCII 括号（游戏字体无简中字形，繁体可显示）。
                    return _preview >= 0
                        ? Title() + "  [" + v + "] 調整中"
                        : Title() + "  " + v;
                }
                // v0.1.22：NONE 行档位并入标题（原 TOGGLES 芯片显示已放弃——不可控）
                if (_entry != null) return Title() + "：" + _text(_opts[CurrentIndex()]);
                return Title();
            }
        }

        private sealed class ActionRow : CampRow
        {
            private readonly Func<string> _run;
            private bool _armed;
            private string _note;                       // 最近一次执行结果备注
            private int _noteAt;                        // 备注产生时刻（限时显示，防标题无限变长）

            public ActionRow(string labelKey, bool warn, Func<string> run)
            {
                LabelKey = labelKey; Warn = warn; _run = run;
                Kind = RowKind.None;
            }

            /// <summary>内联二次确认的兜底路径（原生弹窗不可用时）：第一次按下待确认，第二次执行。</summary>
            public string Press()
            {
                if (!_armed)
                {
                    _armed = true;
                    return null;
                }
                _armed = false;
                return Execute();
            }

            /// <summary>直接执行（原生弹窗确认"是"之后调用）。</summary>
            public string Execute()
            {
                _note = _run();
                _noteAt = Environment.TickCount;
                return _note;
            }

            public void Disarm() { _armed = false; }

            public override string DisplayTitle()
            {
                if (_armed) return Title() + "（" + Disp("camp.confirm") + "）";   // v0.2.6：删 "◀"(U+25C0 无字形 → □)，改纯文本括注
                // v0.1.25：结果备注只显示 6 秒。之前永久挂在标题上 → 标题越来越长，
                // 是"文字太多放不下变两行"的元凶之一。
                if (_note != null && Environment.TickCount - _noteAt < 6000)
                    return Title() + "（" + _note + "）";
                return Title();
            }
        }

        /// <summary>v0.2.4：纯信息行（物品名）。任何按键都不改值，只显示。</summary>
        private sealed class InfoRow : CampRow
        {
            private readonly Func<string> _text;

            public InfoRow(string labelKey, Func<string> text)
            {
                LabelKey = labelKey; _text = text; Kind = RowKind.None;
            }

            public override string DisplayTitle()
            {
                string v = null;
                try { v = _text(); } catch { }
                return v == null ? Title() : Title() + "：" + v;
            }
        }

        /// <summary>
        /// A7 角色卡片：把四个角色平铺成四行，直观显示「当前是谁 / 各自多少级」。
        /// ⛔ 标题整条自绘，不走 Title()：不能用 ASCII '&lt;' / '&gt;'（TagConverter 会当标签，
        ///    v0.2.6 实机翻车），也不能用 ● / ▶（缺字形变 □）⇒ 只用全角 ＞ 与全角空格。
        /// ⚠ DisplayTitle 每轮重算：套用等级后卡片上的 Lv 立即跟着变，无需重建行表。
        /// </summary>
        private sealed class CardRow : CampRow
        {
            public int Index;

            public CardRow(int idx) { Index = idx; Kind = RowKind.None; }

            public override string DisplayTitle()
            {
                int lv = FeatureLevel.CurrentLevel(Index);
                string lvs = lv < 0 ? "—" : lv.ToString();
                return (Index == CharaIndex() ? "＞ " : "　 ")
                       + FeatureLevel.ActorName(Index) + "　Lv." + lvs;
            }
        }

        /// <summary>v0.2.4：子页面入口行（营地「生成物品」）。确定键进入子页，不做别的。</summary>
        private sealed class PageRow : CampRow
        {
            /// <summary>按确定要切到哪一页（v0.3 批次 4 起不再写死 Spawn）。</summary>
            public CampPage Target;

            public PageRow(string labelKey, bool warn, CampPage target)
            {
                LabelKey = labelKey; Warn = warn; Target = target; Kind = RowKind.None;
            }

            public override string DisplayTitle() { return Title() + "  ＞"; }   // v0.2.6：子页指示符用全角 ＞（ASCII '>' 会被 TagConverter 当标签）
        }

        /// <summary>
        /// v0.2.4：整数范围行（物品 ID 1..411）。左右键按**步进**（取自「步進」行）加减，
        /// 而不是像 EnumRow 那样一档一档走 —— 411 档走不动。
        /// 「確定」= 加一个步进（与原版"加減"列同义），进入调整态后左右预览、確定保存。
        /// </summary>
        private sealed class IntRangeRow : CampRow, IAdjustableRow
        {
            private readonly MelonPreferences_Entry<int> _entry;
            private readonly int _min, _max;
            private readonly Func<int> _stepOf;
            private readonly Func<int, string> _text;
            private int _preview = -1;

            public IntRangeRow(string labelKey, MelonPreferences_Entry<int> entry, int min, int max,
                Func<int> stepOf, Func<int, string> text)
            {
                LabelKey = labelKey; _entry = entry; _min = min; _max = max;
                _stepOf = stepOf; _text = text; Kind = RowKind.None;
            }

            public override bool SupportsStep { get { return _entry != null; } }

            private int Cur()
            {
                int v = _preview >= 0 ? _preview : (_entry != null ? _entry.Value : _min);
                return v < _min ? _min : (v > _max ? _max : v);
            }

            private int StepSize()
            {
                int st = 1;
                try { st = _stepOf != null ? _stepOf() : 1; } catch { st = 1; }
                return st < 1 ? 1 : st;
            }

            public void BeginPreview() { _preview = Cur(); }

            public void PreviewStep(int delta)
            {
                if (_preview < 0) _preview = Cur();
                int v = _preview + delta * StepSize();
                _preview = v < _min ? _min : (v > _max ? _max : v);
            }

            /// <summary>v0.2.6：同 EnumRow.PreviewToNorm —— 绝对位置只当方向，每事件最多走一步进。</summary>
            public void PreviewToNorm(float norm)
            {
                if (_preview < 0) _preview = Cur();
                if (float.IsNaN(norm)) return;
                if (norm < 0f) norm = 0f;
                if (norm > 1f) norm = 1f;
                int target = _min + (int)System.Math.Round(norm * (_max - _min));
                int st = StepSize();
                if (target > _preview) _preview = _preview + st > _max ? _max : _preview + st;
                else if (target < _preview) _preview = _preview - st < _min ? _min : _preview - st;
            }

            public void CommitPreview()
            {
                if (_preview >= 0 && _entry != null)
                {
                    if (_entry.Value != _preview) { _entry.Value = _preview; QolConfig.Save(); }
                }
                _preview = -1;
            }

            public void CancelPreview() { _preview = -1; }

            /// <summary>确定键 = 加一个步进（立即生效）。</summary>
            public override void Step(int delta)
            {
                BeginPreview();
                PreviewStep(delta);
                CommitPreview();
            }

            public override string DisplayTitle()
            {
                int v = Cur();
                string s = _text != null ? _text(v) : v.ToString();
                return _preview >= 0 ? Title() + "  [" + s + "] 調整中" : Title() + "：" + s;
            }
        }

        // ------------------------------------------------------------------
        // 反射工具（unhollower：字段生成为属性 → 先 Property 再 Field）
        // ------------------------------------------------------------------

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanRead) return p.GetValue(obj, null);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f != null ? f.GetValue(obj) : null;
        }

        /// <summary>取 UIScrollController.mContentDataList（运行时 Il2Cpp List）。</summary>
        private static object GetDataList(UIScrollListWindow2 win)
        {
            try
            {
                var ctrl = win.ScrollListController;
                return ctrl == null ? null : GetMember(ctrl, "mContentDataList");
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: mContentDataList 获取失败 " + e.GetType().Name + " " + e.Message);
                return null;
            }
        }

        private static int ListCount(object list)
        {
            if (list == null) return 0;
            try { return Convert.ToInt32(list.GetType().GetMethod("get_Count").Invoke(list, null)); }
            catch { return 0; }
        }

        private static object ListAt(object list, int i)
        {
            return list.GetType().GetMethod("get_Item", new Type[] { typeof(int) }).Invoke(list, new object[] { i });
        }

        private static void ListClear(object list) { list.GetType().GetMethod("Clear").Invoke(list, null); }

        private static void ListAdd(object list, object item)
        {
            list.GetType().GetMethod("Add").Invoke(list, new object[] { item });
        }

        private static bool SetMember(object obj, string name, object value)
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(obj, value, null); return true; }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { f.SetValue(obj, value); return true; }
            return false;
        }

        /// <summary>
        /// 写 mContentType（0x18）。
        /// 反射写属性会失败（实机日志实锤 Set Method not found——ContentType 是只读属性），
        /// 改用原生指针内存直写：IL2CPP 对象头 0x10 字节（klass+monitor），字段在 0x18 起。
        /// GAUGE 行能渲染正是因为 2 参 ctor 在原生侧内部直写了该字段。
        /// </summary>
        private static void SetContentType(object data, int v)
        {
            try
            {
                System.IntPtr p = PtrOf(data);
                if (p == System.IntPtr.Zero) { QolLog.Warn("camp: ContentType 直写失败（零指针）"); return; }
                System.Runtime.InteropServices.Marshal.WriteInt32(p, 0x18, v);
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: ContentType 直写失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>组件显示/隐藏（池化芯片组件数量固定，档位少时必须隐藏多余的）。</summary>
        private static void SetActive(object comp, bool v)
        {
            try
            {
                if (comp == null) return;
                object go = GetMember(comp, "gameObject");
                if (go == null) return;
                var mi = go.GetType().GetMethod("SetActive", new Type[] { typeof(bool) });
                if (mi == null) return;
                mi.Invoke(go, new object[] { v });
            }
            catch { /* 视觉细节，不因它中断主流程 */ }
        }

        /// <summary>
        /// 写 int 字段（v0.1.20）：unhollower 把 IL2CPP **字段**生成为 m 前缀属性、把
        /// **get_/set_ 方法对**生成为去前缀属性（如 mSelectToggleIndex → SelectToggleIndex）。
        /// 三条路依次尝试：m 前缀属性 → 去前缀属性 → 内存直写，全失败返回 false（调用方必须打日志）。
        /// </summary>
        private static bool SetIntField(object obj, string mName, int off, int v)
        {
            if (obj == null) return false;
            try
            {
                var t = obj.GetType();
                string alt = mName.Length > 1 && mName[0] == 'm'
                    ? char.ToUpper(mName[1]) + mName.Substring(2) : mName;
                foreach (string nm in new[] { mName, alt })
                {
                    var p = t.GetProperty(nm, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanWrite) { p.SetValue(obj, v, null); return true; }
                }
                System.IntPtr p2 = PtrOf(obj);
                if (p2 == System.IntPtr.Zero) return false;
                System.Runtime.InteropServices.Marshal.WriteInt32(p2, off, v);
                return true;
            }
            catch { return false; }
        }

        /// <summary>写 float 字段（滑块位置 mGaugeValue 0x30），详见 SetIntField 说明。</summary>
        private static bool SetFloatField(object obj, string mName, int off, float v)
        {
            if (obj == null) return false;
            try
            {
                var t = obj.GetType();
                string alt = mName.Length > 1 && mName[0] == 'm'
                    ? char.ToUpper(mName[1]) + mName.Substring(2) : mName;
                foreach (string nm in new[] { mName, alt })
                {
                    var p = t.GetProperty(nm, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanWrite) { p.SetValue(obj, v, null); return true; }
                }
                System.IntPtr p2 = PtrOf(obj);
                if (p2 == System.IntPtr.Zero) return false;
                byte[] b = System.BitConverter.GetBytes(v);
                System.Runtime.InteropServices.Marshal.Copy(b, 0, p2 + off, 4);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 用同一原生指针把基类代理重建为派生代理（v0.1.21）。
        /// unhollower 铁律：从 List/属性/Harmony 参数读回的对象按**声明类型**构造代理，
        /// 于是派生类私有字段（如芯片的 mTitleText 0x108）一律取不到。
        /// 之前只对这个坑做了"身份识别"的规避，取字段同样中招——必须重建代理。
        /// </summary>
        private static object AsDerived(object obj, Type derived)
        {
            if (obj == null) return null;
            if (derived.IsInstanceOfType(obj)) return obj;
            try
            {
                System.IntPtr p = PtrOf(obj);
                if (p == System.IntPtr.Zero) return obj;
                return System.Activator.CreateInstance(derived, p) ?? obj;
            }
            catch { return obj; }
        }

        private static string TypeName(object o) { return o == null ? "null" : o.GetType().FullName; }

        /// <summary>
        /// 造第 i 行 data（按 Rows[i].Kind 选原生控件类型）：
        /// - GAUGE：2 参 ctor(messageID, gaugeValue)——无数组参数，运行时签名安全；
        /// - TOGGLES：1 参 ctor + 反射填 mToggleContentTitleList/SelectableList + mSelectToggleIndex
        ///   （3 参 ctor 的 string[] 参数在运行时是 Il2CppReferenceArray——MissingMethodException）。
        /// </summary>
        private static UICampConfigMenuContentData MakeRowData(int i)
        {
            CampRow r = Rows[i];
            if (r.Kind == RowKind.Gauge)
            {
                var g = new UICampConfigMenuContentData(MARKER + i, r.GaugeNorm());
                SetContentType(g, 1);   // 保险：ctor 理应已设 GAUGE
                return g;
            }
            if (r.Kind == RowKind.Toggles)
            {
                var d = new UICampConfigMenuContentData(MARKER + i);
                SetContentType(d, 2);
                object titles = GetMember(d, "mToggleContentTitleList");
                object sel = GetMember(d, "mToggleContentSelectableList");
                string[] chips = r.Chips();
                if (titles != null && sel != null && chips != null)
                {
                    for (int k = 0; k < chips.Length; k++)
                    {
                        string chip = chips[k] ?? "";
                        ListAdd(titles, chip);
                        ListAdd(sel, true);
                        _chipTexts.Add(chip);   // 芯片级文本补丁的识别表（见 ChipTextPatch）
                    }
                    if (!SetIntField(d, "mSelectToggleIndex", 0x34, r.SelectedChip()))
                        QolLog.Warn("camp: 初始档位写入失败（" + r.DisplayTitle() + "）");
                }
                else
                {
                    QolLog.Warn("camp: TOGGLES 列表不可达（titles=" + (titles != null) + " sel=" + (sel != null) + "）");
                    SetContentType(d, 0);   // 降级为纯标题行
                }
                return d;
            }
            return new UICampConfigMenuContentData(MARKER + i);
        }

        /// <summary>改值后把当前档位同步回 data 对象（TOGGLES 选中芯片 / GAUGE 滑块位置）。</summary>
        private static void SyncRowData(CampRow row, object data)
        {
            if (data == null) return;
            bool ok;
            if (row.Kind == RowKind.Toggles)
                ok = SetIntField(data, "mSelectToggleIndex", 0x34, row.SelectedChip());
            else if (row.Kind == RowKind.Gauge)
                ok = SetFloatField(data, "mGaugeValue", 0x30, row.GaugeNorm());
            else return;
            if (!ok)
                QolLog.Warn("camp: 档位回写失败（" + row.DisplayTitle() + " kind=" + row.Kind + "）→ 高亮/滑块不会动");
        }

        // ------------------------------------------------------------------
        // 行身份识别：原生指针（IntPtr）登记表。
        // v0.1.14b 实锤：unhollower 从列表 get_Item / 游戏属性 / Harmony 参数读回
        // 对象时按**声明类型**（基类）构造代理，托管 `as 派生类` 恒 null →
        // 基于 mMessageID 的三判定全灭（重复挂行+标题复制+点不进去）。
        // 同一原生对象每次封送的 Pointer 相同 → 指针登记表做身份，零转型零读取。
        // ------------------------------------------------------------------

        private static System.IntPtr _entryPtr;   // 入口行原生指针
        private static UIScrollListContentData _entryObj;   // 入口行托管引用（防 GC 回收致指针悬垂）
        /// <summary>
        /// mod 行指针 → **行对象本身**。
        ///
        /// ⛔ v0.2.5 关键修正：v0.2.4 这里存的是"页内下标(int)"，导致跨页切换**必然越界**——
        ///   实机证据（2026-09-12 debug.log + Latest.log）：
        ///     03:40:16.849 进入 MOD 设置页（主页 32 行）
        ///     03:40:19.918 ×80 ArgumentOutOfRangeException（行标题写入失败 48 / 芯片数据表清空失败 32）
        ///     03:40:19.947 MOD 设置行已重建（子页 7 行）→ 进入物品生成子页
        ///   原因：`EnterSpawnPage` 必须**先**把 `_page` 切成 Spawn 再调 `UpdateContentDataList()`（Postfix 要按新页建行），
        ///   但游戏在这个调用里是**先**遍历旧数据（主页 32 个 data 对象）逐个 bind → 我们的 Postfix
        ///   `WriteTitle`/`ToggleListResetPatch` 拿旧指针查到旧下标 7..31，而 `Rows` 已经是 Spawn 的 7 行
        ///   → `Rows[7..31]` 越界 → 行组件 bind 抛异常**半途而废** → 行宽/文本/交互全废（用户反馈"内容无法点击"）。
        ///   改成存行对象后，跨页查到的永远是**行自己**，与当前页表无关，不可能越界。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<System.IntPtr, CampRow> _rowByPtr
            = new System.Collections.Generic.Dictionary<System.IntPtr, CampRow>();   // mod 行指针 → 行对象
        /// <summary>我们写进芯片 data 的原始文本集合（芯片组件级文本补丁靠它识别自己人）。</summary>
        private static readonly System.Collections.Generic.HashSet<string> _chipTexts
            = new System.Collections.Generic.HashSet<string>();
        private static string _lastChipDiag = "";   // 芯片渲染诊断去重（避免刷屏）
        private static string _lastChipDiag2 = "";  // 芯片写入/隐藏诊断去重（v0.1.20）
        private static bool _clsDiagDone;           // ChangeListState 反射诊断只打一次
        private static bool _chipDiagDone;          // 芯片组件类型诊断只打一次（v0.1.21）
        private static Type _imType;                // StellaInputManager（游戏输入源，反射定位）
        private static System.Reflection.MethodInfo _miKeyDown;  // GetKeyDown(eInputType) 缓存
        private static int _tick;                   // 心跳计数（验证 OnUpdate 真的在跑）
        private static string _wrapLastDiag = "";   // v0.3.1：上键直达 / 观测去重

        /// <summary>取 Il2Cpp 代理的原生对象指针（同一对象跨代理实例稳定）。</summary>
        private static System.IntPtr PtrOf(object o)
        {
            if (o == null) return System.IntPtr.Zero;
            try
            {
                var pi = o.GetType().GetProperty("Pointer");
                return pi == null ? System.IntPtr.Zero : (System.IntPtr)pi.GetValue(o, null);
            }
            catch { return System.IntPtr.Zero; }
        }

        private static bool IsOurRow(UIScrollListContentData d)
        {
            System.IntPtr p = PtrOf(d);
            return p != System.IntPtr.Zero && (_entryPtr == p || _rowByPtr.ContainsKey(p));
        }

        private static bool IsEntryData(UIScrollListContentData d)
        {
            return _entryPtr != System.IntPtr.Zero && PtrOf(d) == _entryPtr;
        }

        /// <summary>
        /// mod 行 data → 行对象（null = 不是我们的 mod 行）。
        /// v0.2.5：不再返回"页内下标"——跨页切换时下标会失效并越界（见 _rowByPtr 注释）。
        /// </summary>
        private static CampRow RowOf(UIScrollListContentData d)
        {
            CampRow r;
            return _rowByPtr.TryGetValue(PtrOf(d), out r) ? r : null;
        }

        private static bool HasEntry(object list, int n)
        {
            if (_entryPtr == System.IntPtr.Zero) return false;
            for (int i = 0; i < n; i++)
                if (PtrOf(ListAt(list, i)) == _entryPtr) return true;
            return false;
        }

        /// <summary>行池 = UIScrollController.ShowComponentList 的行组件数（-1 = 不可知，放行）。</summary>
        private static int PoolCount(UICampConfigMenuListWindow win)
        {
            try
            {
                var ctrl = win.ScrollListController;
                if (ctrl == null) return -1;
                object show = GetMember(ctrl, "mShowComponentList");
                return show == null ? -1 : ListCount(show);
            }
            catch { return -1; }
        }

        /// <summary>入口行在 data 列表里的下标（-1 = 不在）。</summary>
        private static int EntryIndex(object list)
        {
            if (_entryPtr == System.IntPtr.Zero) return -1;
            int n = ListCount(list);
            for (int i = 0; i < n; i++)
                if (PtrOf(ListAt(list, i)) == _entryPtr) return i;
            return -1;
        }

        /// <summary>列表里是否残留我们的 mod 行（换表没做完/异常残留）→ 需要清污。</summary>
        private static bool ContainsModRow(object list)
        {
            if (_rowByPtr.Count == 0) return false;
            int n = ListCount(list);
            for (int i = 0; i < n; i++)
                if (_rowByPtr.ContainsKey(PtrOf(ListAt(list, i)))) return true;
            return false;
        }

        /// <summary>
        /// 光标强制复位到第 0 行。
        /// 反汇编实锤（GameAssembly）：
        ///  - `UIScrollController.ResetSelectIndex()` 0x184190：把 mSelectedShowUIIndex(0xE8)
        ///    与 mSelectedListDataIndex(0xF0) 一并写 0；
        ///  - `UIScrollController.ForceSetSelectIndex(int,int)` 0x182870：直写上面两个字段。
        /// 两步都做（反射 + 内存直写兜底），最后 UpdateListShowContent 让画面跟着走。
        /// </summary>
        private static void ResetCursorToTop(UICampConfigMenuListWindow win, string why)
        {
            try
            {
                var ctrl = win.ScrollListController;
                if (ctrl == null) return;
                try { ctrl.ResetSelectIndex(); }
                catch (Exception e) { QolLog.Warn("camp: ResetSelectIndex 失败 " + e.Message); }

                bool forced = false;
                try
                {
                    var mi = ctrl.GetType().GetMethod("ForceSetSelectIndex",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mi != null) { mi.Invoke(ctrl, new object[] { 0, 0 }); forced = true; }
                }
                catch (Exception e) { QolLog.Warn("camp: ForceSetSelectIndex 失败 " + e.Message); }

                if (!forced)
                {
                    // 兜底：字段直写（0xE8/0xF0 都是 int）
                    System.IntPtr p = PtrOf(ctrl);
                    if (p != System.IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.WriteInt32(p, 0xE8, 0);
                        System.Runtime.InteropServices.Marshal.WriteInt32(p, 0xF0, 0);
                        forced = true;
                    }
                }
                ctrl.UpdateListShowContent(0f, true, true, false);

                if (_lastCursorDiag != why)
                {
                    _lastCursorDiag = why;
                    QolLog.Info("camp: 光标已复位到首行（" + why + "，ForceSet=" + forced + "）");
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 光标复位失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 用**游戏自己的 SetSelectIndex 向下位移**把光标送到指定 data 下标（v0.3.1-r12）。
        /// 与玩家连按 ↓ 完全同一条路径（滚动/高亮/音效都由游戏算）⇒ 不会像 ForceSetSelectIndex
        /// 那样把 showUIIndex 写死而导致"乱跳"。失败（方法不可达 / 没走到目标下标）返回 false。
        /// </summary>
        private static bool MoveDownBy(UICampConfigMenuListWindow win, int target)
        {
            try
            {
                var ctrl = win.ScrollListController;
                if (ctrl == null) return false;

                var mi = ctrl.GetType().GetMethod("SetSelectIndex",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { typeof(int), typeof(bool), typeof(bool) }, null);
                if (mi == null) return false;

                mi.Invoke(ctrl, new object[] { target, false, false });
                return win.SelectedListDataIndex == target;      // 回读校验（铁律 10：Invoke 不抛 ≠ 生效）
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: SetSelectIndex(+" + target + ") 失败 " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 光标强制落到指定 data 下标（v0.3.1-r11：上键直达入口行用）。
        /// 与 ResetCursorToTop 同一套姿势：`ForceSetSelectIndex(idx, 0)`（0x182870 直写
        /// mSelectedShowUIIndex 0xE8 / mSelectedListDataIndex 0xF0）+ 直写兜底 + 刷显示。
        /// </summary>
        private static bool ForceCursorTo(UICampConfigMenuListWindow win, int index)
        {
            try
            {
                var ctrl = win.ScrollListController;
                if (ctrl == null) return false;

                bool forced = false;
                try
                {
                    var mi = ctrl.GetType().GetMethod("ForceSetSelectIndex",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mi != null) { mi.Invoke(ctrl, new object[] { index, 0 }); forced = true; }
                }
                catch (Exception e) { QolLog.Warn("camp: ForceSetSelectIndex(" + index + ") 失败 " + e.Message); }

                if (!forced)
                {
                    System.IntPtr p = PtrOf(ctrl);
                    if (p == System.IntPtr.Zero) return false;
                    System.Runtime.InteropServices.Marshal.WriteInt32(p, 0xE8, index);
                    System.Runtime.InteropServices.Marshal.WriteInt32(p, 0xF0, index);
                    forced = true;
                }

                ctrl.UpdateListShowContent(0f, true, true, false);
                return forced;
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 光标跳转失败 " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 仅在"进窗/换表后的若干帧"内纠正光标：一旦发现它停在我们的入口行（末行）就复位到首行。
        /// 之后不再干预——玩家自己按 ↓ 走到入口行按确定是**正常意图**，不能被抢。
        /// </summary>
        private static void GuardEntryCursor()
        {
            if (_entryFixFrames <= 0) return;
            _entryFixFrames--;
            if (_win == null) return;
            try
            {
                object list = GetDataList(_win);
                if (list == null) return;
                int ei = EntryIndex(list);
                if (ei < 0) return;
                if (_win.SelectedListDataIndex != ei) return;   // 光标不在入口行 → 无需纠偏
                _entryFixFrames = 0;
                ResetCursorToTop(_win, "启动时落在 MOD設定 入口行");
            }
            catch { /* 纠偏失败不影响主流程 */ }
        }

        /// <summary>进 modMode 前快照原版行引用（**不含**我们的入口行，便于纯净重灌）。</summary>
        private static void SnapshotVanilla(object list)
        {
            int n = ListCount(list);
            var buf = new List<object>(n);
            for (int i = 0; i < n; i++)
            {
                object o = ListAt(list, i);
                if (o == null) continue;
                if (_entryPtr != System.IntPtr.Zero && PtrOf(o) == _entryPtr) continue;   // 跳过入口行
                buf.Add(o);
            }
            _vanilla = buf.ToArray();
        }

        /// <summary>
        /// 退出 modMode：清表 → 按快照重灌原版行 → 确保入口行在末位 → 重绑 → 光标回首行。
        /// v0.1.27：快照**不再置空**（它是清污源）；光标必须回首行，否则下一个进窗动作
        /// 会直接落在入口行上（用户反馈的"返回逻辑错乱"）。
        /// </summary>
        private static void RestoreVanilla(UICampConfigMenuListWindow win, object list)
        {
            if (_vanilla == null) return;
            // v0.2.6：**必须在 ListClear 之前**作废行指针登记表。
            // ListClear 会把我们的 mod 行 data 对象全部释放，而 IL2CPP 的地址会被**立即复用** ——
            // 紧接着重灌的原版行 data 很可能拿回同一批地址；登记表若还留着旧映射，
            // `RowOf(原版行)` 就会返回一个 mod 行 → WriteTitle 把 mod 标题写到**原版行**上、
            // ToggleListResetPatch 还会去清空原版芯片行的数据表 → 原版设置页内容错乱/点不动。
            // （v0.2.5 只在 RebuildPatch / OpenWindow 清污处清过，唯独这条"退回原版"路径漏了。）
            _rowByPtr.Clear();
            _chipTexts.Clear();
            ListClear(list);
            for (int i = 0; i < _vanilla.Length; i++)
                if (_vanilla[i] != null) ListAdd(list, _vanilla[i]);
            if (!HasEntry(list, ListCount(list)))
            {
                if (_entryObj == null) _entryObj = new UICampConfigMenuContentData(MARKER_ENTRY);
                ListAdd(list, _entryObj);
                _entryPtr = PtrOf(_entryObj);
            }
            Rebind(win);
            ResetCursorToTop(win, "退回原版设置页");
            QolLog.Info("camp: 已恢复原版设置行（" + ListCount(list) + " 行，含入口行）");
        }

        // ------------------------------------------------------------------
        // 补丁
        // ------------------------------------------------------------------

        /// <summary>
        /// 打开设置窗（进窗必经路径，UpdateContentDataList 在此流程中不被调用——0x22AF70 只有
        /// 虚调用且实机日志证实进设置不触发）：复位 modMode + 幂等挂「MOD設定 ▸」入口行。
        /// 挂行前做行池安全检查（可见行组件数不足则放弃，绝不重蹈主菜单越界卡死）。
        ///
        /// v0.1.27 增补（用户"返回逻辑还是不对"实机反馈）：
        ///  - 防御性清污：列表里若残留我们的 mod 行（上次退出被异常/抢关打断），直接用
        ///    原版行快照重灌，保证**进窗必是原版页**；清污源 `_vanilla` 不再置空；
        ///  - 光标复位：进入即把光标放回首行（反汇编见 _entryFixFrames 注释），
        ///    并留若干帧预算给"游戏开窗后自己把光标走到末行（=我们的入口行）"兜底。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "OpenWindow", new Type[] { })]
        private static class OpenWindowPatch
        {
            private static void Postfix(UICampConfigMenuListWindow __instance)
            {
                try
                {
                    bool wasMod = _modMode;
                    _modMode = false;
                    _adjust = false;
                    _pendingRow = -1;
                    _pendingWin = null;
                    DisarmActions();
                    _win = __instance;

                    object list = GetDataList(__instance);
                    if (list == null) { QolLog.Warn("camp: OpenWindow 数据列表不可达"); return; }

                    int n = ListCount(list);

                    // 1) 清污：残留 mod 行 → 用快照重灌原版行（进窗一定是原版页）
                    if (ContainsModRow(list))
                    {
                        QolLog.Warn("camp: 进窗发现残留 mod 行（登记 " + _rowByPtr.Count + "）→ 清污重灌原版行");
                        _rowByPtr.Clear();
                        _chipTexts.Clear();
                        if (_vanilla != null) RestoreVanilla(__instance, list);
                        else QolLog.Warn("camp: 无原版快照，清污跳过（请重进一次场景）");
                        n = ListCount(list);
                    }
                    else
                    {
                        // 纯原版列表（可能刚被游戏重建）→ 刷新快照，保证清污源始终是当前对象
                        SnapshotVanilla(list);
                    }

                    if (!HasEntry(list, n))
                    {
                        // 行池安全检查：ShowComponentList.Count 必须 >= 追加后的数据行数
                        int pool = PoolCount(__instance);
                        if (pool > 0 && pool < n + 1)
                        {
                            QolLog.Warn("camp: 行池 " + pool + " < 数据 " + (n + 1) + "，入口行未挂（防越界）");
                            return;
                        }

                        var entry = new UICampConfigMenuContentData(MARKER_ENTRY);
                        ListAdd(list, entry);
                        _entryObj = entry;
                        _entryPtr = PtrOf(entry);
                        Rebind(__instance);
                        QolLog.Info("camp: 入口行已挂（数据 " + (n + 1) + " 行，行池 " + pool + "，wasMod=" + wasMod +
                                    "，ptr=0x" + _entryPtr.ToString("X") + (_entryPtr == System.IntPtr.Zero ? "<!零指针>" : "") + "）");
                    }

                    // 2) 光标回首行 + 留纠偏预算（游戏开窗后自己会把光标走到末行=入口行）
                    ResetCursorToTop(__instance, "进入设置窗");
                    _entryFixFrames = 180;
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: 挂入口失败 " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        /// <summary>
        /// ⛔⛔ v0.2.8（用户第 6 条"返回逻辑依旧有问题"的**真根因**，反汇编全链路实锤）：
        ///
        /// 取消键的真实调用链（此前三轮 v0.2.5/0.2.6/0.2.7 全部修在了错误的环节上）：
        ///   ① StellaUI 输入栈 → UICampConfigWindow.OnInput(0x22C830)
        ///   ② 0x22CA88 → **UICampBaseWindow.OnInput 基类实现(0x21EAD0)**：
        ///        `if (GetKeyDown(2)==CANCEL) { PlayCancelKeySE(0x11); this.ReturnMenu(); return true; }`
        ///   ③ **UICampBaseWindow.ReturnMenu(0x21ED60, Slot 17)**（UICampConfigWindow 未覆写 → 走基类实现）：
        ///        `CampWindow.ChangeState(eWindowType.MENU=7)`(0xD3560)
        ///        → 营地状态机**直接切回 MENU**：設定窗被关、营地主菜单打开
        ///        —— ★★"一次按键直接回营地"的真正机关在这里，mod 从未碰过它
        ///   ④ 基类 OnInput 返回 true 后，shell 把输入**继续转发**给 mMenuListWindow.OnInput(0x187BE0)
        ///   ⑤ → OnInputButton(0x187B00)：GetKeyDown(2) → OnPressCanceled(Slot 38 = 0x229A80 = CancelPatch)
        ///        → GoBack() 打出"退出 MOD 设置页（拦截关窗）"——**此时窗口已经在关了**，纯属马后炮。
        ///
        /// 此前挂在 OnPressCanceled / UICampConfigWindow.CloseWindow(0x22C480) 上的拦截全部无效：
        /// CloseWindow 只是 ChangeState(MENU) 内部关旧窗的**第二次**虚调用，拦它拦不住状态机
        /// （v0.2.7 日志实锤：子页取消 34.387 GoBack 正常 → **+1.005s** 35.392 OpenWindowPatch
        /// "进窗发现残留 mod 行"= 窗口被关掉又重开，mod 页被清污 → "返回无效"）。
        ///
        /// 修法：在 ReturnMenu 这一层拦截——mod 页层级返回时 `return false` 拦掉 ChangeState(MENU)，
        /// 窗口就真的不关；原版页 / 其他营地子窗口 / 弹窗期**完全透明放行**。
        /// </summary>
        [HarmonyPatch(typeof(UICampBaseWindow), "ReturnMenu")]
        private static class ReturnMenuPatch
        {
            private static bool Prefix(UICampBaseWindow __instance)
            {
                try
                {
                    // 必须是設定窗本体——ReturnMenu 基类实现被所有营地子窗口共用
                    // （道具/思裝/技能/状态/帮助），拦错窗口 = 其它页面返回键全废。
                    // 所以窗口类型判断必须放在最前。
                    var cfg = AsDerived(__instance, typeof(UICampConfigWindow)) as UICampConfigWindow;
                    if (cfg == null) return true;

                    if (!_modMode) return true;          // 原版設定页：完全透明，原样关窗回营地

                    if (_pendingRow >= 0) return false;  // 弹窗期：拦住（绝不带着确认框整个关窗）

                    var listWin = GetMember(cfg, "mMenuListWindow") as UICampConfigMenuListWindow;
                    if (listWin == null) return true;    // 拿不到列表窗 → 放行兜底（至少能出去）

                    // mod 页层级返回：子页→主页 / 主页→原版列表。成功 → 拦掉 ChangeState(MENU)。
                    if (GoBack(listWin)) return false;

                    QolLog.Warn("camp: ReturnMenu 拦截后 GoBack 失败 → 放行回营地");
                    return true;
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: ReturnMenu 处理失败 " + e.GetType().Name + " " + e.Message);
                    return true;
                }
            }
        }

        /// <summary>
        /// 设置主窗关闭 — ChangeState(MENU) 内部关旧窗时的**第二次**虚调用（非取消键入口）。
        ///
        /// v0.1.26 实锤：日志里取消时只出现本补丁的 `MOD 设置窗关闭`，从来没有
        /// `OnPressCanceled` 的日志（`callers.py` 也确认 `OnPressCanceled` 只有虚表分发），
        /// 说明 mod 页按取消走的是 **UICampConfigWindow.CloseWindow**，而我们在这里先把
        /// `_modMode` 清成 false → 列表窗的取消逻辑被放行 → **直接关窗回营地**
        /// （用户："点返回直接到营地了；再点设置直接到 mod 设置页"）。
        /// 现在改为：mod 页取消 = **不关窗**，退回原版设置列表；再按一次才回营地。
        ///
        /// ⛔ v0.2.8 勘误：本方法**不是**取消键的入口（入口是 UICampBaseWindow.OnInput →
        /// ReturnMenu，见 ReturnMenuPatch 注释）。它只是 ChangeState(MENU) 关旧窗时的过路站。
        /// 保留本补丁作**纵深防御**：万一 ReturnMenu 拦截未生效（异常/漏挂），这里仍会尝试
        /// GoBack 并拦掉关窗体；正常路径下本补丁的 `_modMode` 分支不会被命中。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigWindow), "CloseWindow")]
        private static class ConfigClosePatch
        {
            private static bool Prefix(UICampConfigWindow __instance)
            {
                try
                {
                    // v0.2.6：`!_modMode` 时这里原本直接 return true（原版设置页正常关窗）。
                    // ⛔ v0.2.7 修正（用户第 6 条："返回依旧直接回到营地"）：
                    //   一次取消键会先经 `OnPressCanceled`（CancelPatch）→ GoBack 把 `_modMode` 清成 false，
                    //   紧跟着本路径再进来时 `!_modMode` 成立 → 放行关窗 → **一次按键直接跳回营地**，
                    //   中间那层「原版設定列表」根本站不住（v0.2.6 日志里那句
                    //   "退出 MOD 设置页 → 退回原版設定列表（拦截关窗）" 其实是 CancelPatch 打的，
                    //   窗口随后仍被这条路径关掉了）。
                    //   现在：只要"本次按键的返回刚刚已被消费"，就继续拦掉关窗；真正的新一次取消才放行。
                    if (!_modMode)
                    {
                        if (RecentlyConsumedBack()) return false;   // 同一次按键的残留 → 继续拦，窗口不关
                        return true;                                 // 玩家新按的一次取消：正常关窗回营地
                    }

                    var listWin = GetMember(__instance, "mMenuListWindow") as UICampConfigMenuListWindow;
                    if (listWin != null && GoBack(listWin))
                        return false;             // 拦掉关窗：层级为 子页→主页→原版列表（v0.2.5）

                    // 兜底：拿不到列表窗（异常/结构变化）→ 只把标志清干净并放行关窗，
                    // 保证用户最多再按一次就能出去，绝不停在"按了没反应"的状态。
                    QolLog.Warn("camp: mod 页退出退回原版列表失败（mMenuListWindow 不可达）→ 放行关窗");
                    _modMode = false;
                    _adjust = false;
                    _page = CampPage.Main;
                    _pendingRow = -1;
                    _pendingWin = null;
                    DisarmActions();
                    _rowByPtr.Clear();     // v0.2.6：离开 mod 层必须作废行登记（防原版行被误认成 mod 行）
                    _chipTexts.Clear();
                    QolConfig.Save();
                    return true;
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: 关窗处理失败 " + e.GetType().Name + " " + e.Message);
                    _modMode = false;
                    _page = CampPage.Main;
                    return true;
                }
            }
        }

        /// <summary>
        /// 列表刷新 Postfix（仅在主动调 UpdateContentDataList 时触发，如进/退 modMode）：
        /// modMode → 快照原版行 → 整表替换为 mod 行（NONE 型 + 标记 MessageID）+ 强制重绑。
        /// （入口行的挂载在 OpenWindowPatch——进设置窗不经过这里。）
        ///
        /// v0.1.27：**原版页也会走到这里**（游戏自己的刷新会虚调用本方法）。反汇编实锤
        /// `UpdateContentDataList` 会 for(count-1) 调 MoveNextIndex 把光标一路推到**末行**，
        /// 而末行正是我们的「MOD設定」入口行 → 清完列表后必须把光标拽回首行，否则玩家
        /// 一按确定就又进了 mod 页（用户"返回逻辑还是不对"的真凶）。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "UpdateContentDataList")]
        private static class RebuildPatch
        {
            private static void Postfix(UICampConfigMenuListWindow __instance)
            {
                try
                {
                    if (!_modMode)
                    {
                        // 原版页：游戏刚把光标走到末行（入口行）→ 纠偏交 GuardEntryCursor，
                        // 只在"确实停在入口行"时才拉回首行。
                        // v0.2.6：这里**不再无条件 ResetCursorToTop** —— 游戏在玩家每次确认改值后
                        // 也会刷新列表，无条件复位会把玩家正在操作的光标每次都拽回第 0 行
                        //（用户观感＝"原版设置页点不了"）。遵循 v0.1.27 定的原则：只纠偏自动落点，不抢玩家操作。
                        _win = __instance;
                        _entryFixFrames = 180;
                        GuardEntryCursor();
                        return;
                    }

                    object list = GetDataList(__instance);
                    if (list == null) { QolLog.Warn("camp: 设置数据列表不可达"); return; }

                    if (_vanilla == null) SnapshotVanilla(list);   // 原版行只快照一次
                    EnsureRows();
                    DisarmActions();
                    int pool = PoolCount(__instance);
                    _rowByPtr.Clear();   // 旧 mod 行对象随 ListClear 释放，指针登记一并作废
                    _chipTexts.Clear();
                    ListClear(list);
                    int added = 0;
                    for (int i = 0; i < Rows.Count; i++)
                    {
                        CampRow r = Rows[i];
                        if (r == null) continue;
                        var row = MakeRowData(i);   // 按行类型造 NONE/TOGGLES/GAUGE
                        ListAdd(list, row);
                        System.IntPtr p = PtrOf(row);
                        if (p != System.IntPtr.Zero) _rowByPtr[p] = r;   // v0.2.5：登记**行对象**，不登记下标
                        added++;
                    }
                    Rebind(__instance);   // 内含 ResetSelectIndex（光标回第 0 行）
                    _win = __instance;
                    QolLog.Info("camp: MOD 设置行已重建（" + added + " 行，行池 " + pool +
                                "，登记 " + _rowByPtr.Count + "）");
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: MOD 行重建失败 " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        /// <summary>
        /// mod 行标题显示（反汇编实锤：行标题 = UIText.set_TextID(data.mMessageID) →
        /// UpdateText → GetSystemMessage 查表，未知键返回 null → 空白；mTitleText 字段不参与渲染）。
        /// 我们的行在绑定后用 UIText.SetText 直接写显示文本（SetText 0x1E4C40 实锤直接写
        /// 文本组件立即生效，不经过 UpdateText，不受 GuardPatch 影响）。
        /// 双挂：SetContentData（UpdateListShowContent 绑定必经点，dump 声明了两个重载）
        /// + UpdateShowComponent（防其他刷新路径）。SetText 幂等，重复触发无害。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuContent), "SetContentData", new Type[] { typeof(UIScrollListContentData) })]
        private static class RowTitlePatch
        {
            private static void Postfix(UICampConfigMenuContent __instance, UIScrollListContentData contentData)
            {
                WriteTitle(__instance, contentData);
            }
        }

        [HarmonyPatch(typeof(UICampConfigMenuContent), "UpdateShowComponent")]
        private static class RowTitlePatch2
        {
            private static void Postfix(UICampConfigMenuContent __instance)
            {
                WriteTitle(__instance, GetMember(__instance, "mShowContentData") as UIScrollListContentData);
            }
        }

        /// <summary>
        /// 芯片组件级文本补丁（v0.1.19 主修复路径）：行级 Postfix 依赖"渲染时芯片组件已绑定"
        /// 这一时序假设——嵌套 controller 若延迟绑定（滚动列表动画帧），行级写就是写了个空集合，
        /// 表现就是"只剩纯色方块"。改挂芯片组件自己的 UpdateShowComponent(0x22C2A0) Postfix：
        /// 该方法的最后一步正是 set_TextID(data.mTitleText)→UpdateText→查表失败清空，
        /// 我们紧随其后直接写回真实文本，时序必对。识别自己人靠 _chipTexts（原版芯片是本地化
        /// 键，不在集合里 → 不碰，原版显示不受影响）。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuToggleContent), "UpdateShowComponent")]
        private static class ChipTextPatch
        {
            private static void Postfix(UICampConfigMenuToggleContent __instance)
            {
                try
                {
                    var data = GetMember(__instance, "mShowContentData");
                    if (data == null) return;
                    string s = GetMember(data, "mTitleText") as string;
                    if (string.IsNullOrEmpty(s) || !_chipTexts.Contains(s)) return;
                    var txt = GetMember(__instance, "mTitleText") as UIText;
                    if (txt != null) txt.SetText(s);
                    var mesh = GetMember(__instance, "mTitleTextMesh");   // TMP 直写双保险
                    if (mesh != null)
                    {
                        var pi = mesh.GetType().GetProperty("text");
                        if (pi != null && pi.CanWrite) pi.SetValue(mesh, s, null);
                    }
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: 芯片文本写入失败 " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        /// <summary>
        /// 芯片嵌套数据表清空重建（v0.1.18，反汇编 0x227DDE 实锤的池化陷阱）：
        /// 行组件池复用 → 行内容组件里的嵌套芯片 controller（mToggleContentRoot）的
        /// ContentDataList 非空就整段跳过创建循环（`test eax,eax; jg`）→ 残留原版 4 芯片
        /// data：标题循环按我们的 title 数跑但 selectable 越界读抛异常烂尾，芯片组显示
        /// 原版旧字（截图"慢/普通/快/瞬間"残留实锤）。Prefix 清空 → 原方法走原生创建
        /// 路径 new UICampConfigMenuToggleContentData(标题)（ctor 0x22C0B0 无数组参数，
        /// 运行时安全），完全原版流程重建。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuContent), "UpdateShowComponent")]
        private static class ToggleListResetPatch
        {
            private static void Prefix(UICampConfigMenuContent __instance)
            {
                try
                {
                    var data = GetMember(__instance, "mShowContentData") as UIScrollListContentData;
                    if (data == null) return;
                    // v0.2.5：按**行对象**判定（跨页切换时下标会失效越界，见 _rowByPtr 注释）
                    CampRow row = RowOf(data);
                    if (row == null || row.Kind != RowKind.Toggles) return;
                    var root = __instance.ToggleContentRoot;
                    if (root == null) return;
                    object list = GetMember(root, "mContentDataList");
                    if (list != null && ListCount(list) > 0) ListClear(list);
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: 芯片数据表清空失败 " + e.GetType().Name + " " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(UICampConfigMenuContent), "SetContentData", new Type[] { typeof(UIScrollListContent) })]
        private static class RowTitlePatch3
        {
            private static void Postfix(UICampConfigMenuContent __instance, UIScrollListContent contentData)
            {
                if (contentData != null)
                    WriteTitle(__instance, GetMember(contentData, "mShowContentData") as UIScrollListContentData);
            }
        }

        private static void WriteTitle(UICampConfigMenuContent content, UIScrollListContentData data)
        {
            string title;
            try
            {
                if (data == null) return;
                // v0.1.27：原版行**一律不碰**。v0.1.24~26 为了让长标题不折行去改标题框的
                // sizeDelta，但行组件是**池化复用**的（mod 行与原版行共用同一批 Content）：
                // 拉伸锚点下 sizeDelta.x 本来就是负值（-20 = 比父级窄 20），"基准+260" 会让
                // 文本左边界整体左移 ~130px → 行内前几个字被裁掉（用户截图实锤：入口行只剩
                // "設定"，"▶ MOD" 被裁到框外），并且残留尺寸还会污染原版页。
                // 现在改为**完全不动布局**：只关换行 + Overflow（不折两行、不裁字），
                // 标题长度靠 Rows 里的文案控制（对齐原版 ≤9 字宽）。
                // v0.2.5：先判入口行（它不在 _rowByPtr 登记表里），再按**行对象**取标题。
                // ⛔ 入口行的 "▶ "(U+25B6) 已删除：游戏字体没有该字形 → 显示成 □（用户反馈第 7 条）。
                //    v0.2.5 改用 ASCII 的 "> [ X ] <" —— 结果整行 SetText 抛 ArgumentOutOfRange（见 Header 注释），
                //    标题写不进去，行组件保留上一行的文字 → 原版列表末尾冒出一个「恢復初始設定」（用户截图实锤）。
                //    v0.2.6 改**全角** ＞ ＜（非 ASCII → TagConverter 不认）。
                if (IsEntryData(data)) title = "＞ [ " + Disp("camp.entry") + " ] ＜";
                else
                {
                    CampRow row = RowOf(data);
                    if (row == null) return;              // 原版行：一律不碰
                    title = row.DisplayTitle();
                    WriteChipTexts(content, row);         // 芯片文字同走 SetText 直写（0x22C2A0 实锤 set_TextID+查表）
                }

                var txt = GetMember(content, "mTitleText") as UIText;
                if (txt == null)
                {
                    QolLog.Warn("camp: 行标题 UIText(mTitleText 0x108) 不可达");
                    return;
                }
                txt.SetText(Sanitize(title));   // v0.2.6：兜底净化 ASCII 尖括号（见 Sanitize）
                NoWrap(content, txt);
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 行标题写入失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// v0.2.6 兜底净化：把 ASCII '&lt;' / '&gt;' 换成全角 '＜' / '＞'。
        ///
        /// 为什么必须有这层：`UIText.SetText`(0x1E4C40) 的第一步就是
        /// `Game.TagConverter.ReplaceTagToString(str)`(0x114AD0) —— 它把 ASCII '&lt;' '&gt;' 当富文本
        /// 标签定界符，用 `Substring(IndexOf("&lt;"), IndexOf("&gt;") - IndexOf("&lt;") + 1)` 取值；
        /// 一旦 '&gt;' 出现在 '&lt;' 之前（例如 "&gt; [ X ] &lt;"），长度算成**负数** →
        /// `Il2CppException System.ArgumentOutOfRangeException: Cannot be negative.`
        /// 整行标题写不进去（行组件是池化复用的 → 保留上一行的文字 → 界面出现"重复选项/标题消失"）。
        ///
        /// 实测（2026-09-12）：v0.2.5 的 "&gt; [ X ] &lt;" 让 debug.log 刷了 **5859 条**
        /// `行标题写入失败 ... Cannot be negative.`，8 个分节标题全灭、入口行标题变成「恢復初始設定」。
        /// 加这一层之后，即便以后有人在文案里手滑写了 ASCII 尖括号，也只是显示成全角，不会再炸整个列表。
        /// </summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('<') < 0 && s.IndexOf('>') < 0) return s;
            return s.Replace('<', '＜').Replace('>', '＞');
        }

        /// <summary>
        /// v0.1.27：只关掉自动换行并允许溢出显示，**完全不改 RectTransform**。
        /// 这样长标题不会被 TMP 折成两行（用户最初的需求），也不会像 sizeDelta 加法那样
        /// 把文本整体推移/裁切（v0.1.24~26 的回归）。
        /// </summary>
        private static void NoWrap(UICampConfigMenuContent content, UIText txt)
        {
            try
            {
                object tmp = GetMember(txt, "TextMesh") ?? GetMember(content, "TitleTextMesh");
                if (tmp == null) return;
                var pw = tmp.GetType().GetProperty("enableWordWrapping");
                if (pw != null && pw.CanWrite) pw.SetValue(tmp, false, null);
                var po = tmp.GetType().GetProperty("overflowMode");
                if (po != null && po.CanWrite) po.SetValue(tmp, Enum.ToObject(po.PropertyType, 0), null);   // Overflow=0
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 标题换行设置失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// v0.1.24 的"标题框拉宽"（WidenTitle / GetXY / TitleWidenAdd / _baseW / _rtType）在
        /// v0.1.27 **整体删除**：行组件池化复用 + 拉伸锚点负 sizeDelta 的组合无法安全加宽
        /// （负值哨兵击穿 → 每帧累加；加法增量 → 文本被推出框外裁字；残留尺寸 → 原版页串改）。
        /// 长标题改由文案长度控制 + NoWrap（关换行/允许溢出）解决。
        /// </summary>

        /// <summary>
        /// 芯片文字直写（v0.1.18）：反汇编实锤芯片渲染链 UICampConfigMenuToggleContent.
        /// UpdateShowComponent(0x22C2A0)：UIText.set_TextID(data.mTitleText 0x18) → UpdateText
        /// → GetSystemMessage 查表 → 我们的 "x0.5"/"ON" 非键 → null → 空白（行标题同款坑）。
        /// 嵌套 controller（mToggleContentRoot）的 ShowComponentList 顺序 = 芯片 data 顺序，
        /// 渲染完成后逐个 UIText.SetText 直写真实文本（SetText 0x1E4C40 不走查表）。
        /// </summary>
        private static void WriteChipTexts(UICampConfigMenuContent content, CampRow row)
        {
            try
            {
                if (row.Kind != RowKind.Toggles) return;
                string[] chips = row.Chips();
                if (chips == null) return;
                var root = content.ToggleContentRoot;
                if (root == null) return;
                object show = GetMember(root, "mShowComponentList");
                int n = ListCount(show);
                string diag = row.DisplayTitle() + "|" + n + "/" + chips.Length;
                if (diag != _lastChipDiag)
                {
                    _lastChipDiag = diag;
                    QolLog.Info("camp: 芯片渲染（" + row.DisplayTitle() + "：组件 " + n + "，档位 " + chips.Length + "）");
                }
                int wrote = 0, hid = 0, nulls = 0;
                for (int i = 0; i < n; i++)
                {
                    var raw = ListAt(show, i);
                    if (raw == null) { nulls++; continue; }
                    if (i >= chips.Length)
                    {
                        // v0.1.20：组件池固定 N 个（原版最多 6 档），档位少于池容量时多余的组件
                        // 必须隐藏——之前只把文字写成空串，组件仍激活 → 用户看到的"纯色方块"。
                        SetActive(raw, false);
                        hid++;
                        continue;
                    }
                    SetActive(raw, true);
                    // v0.1.21：mShowComponentList 的声明类型是基类 UIScrollListContent，
                    // unhollower 按声明类型构造代理 → 派生类私有字段 mTitleText(0x108)/
                    // mTitleTextMesh(0x110) 一律取不到（v0.1.20 日志"写入 0"的根因）→ 用同一
                    // 原生指针重建派生代理再读。
                    var comp = AsDerived(raw, typeof(UICampConfigMenuToggleContent));
                    string chip = chips[i] ?? "";
                    var txt = GetMember(comp, "mTitleText") as UIText;
                    if (txt != null) { txt.SetText(chip); wrote++; }
                    // TMP 双写（芯片可见文本实际由 mTitleTextMesh 0x110 承载）
                    var mesh = GetMember(comp, "mTitleTextMesh");
                    if (mesh != null)
                    {
                        var pi = mesh.GetType().GetProperty("text");
                        if (pi != null && pi.CanWrite) pi.SetValue(mesh, chip, null);
                    }
                }
                string diag2 = row.DisplayTitle() + "|w" + wrote + "/h" + hid + "/n" + nulls;
                if (diag2 != _lastChipDiag2)
                {
                    _lastChipDiag2 = diag2;
                    QolLog.Info("camp: 芯片写入（" + row.DisplayTitle() + "：写入 " + wrote +
                                "，隐藏 " + hid + "，空组件 " + nulls + "）");
                    if (wrote == 0 && !_chipDiagDone)
                    {
                        _chipDiagDone = true;
                        QolLog.Warn("camp: 芯片文本组件不可达 原始类型=" + TypeName(ListAt(show, 0)) +
                                    " 重建后=" + TypeName(AsDerived(ListAt(show, 0), typeof(UICampConfigMenuToggleContent))));
                    }
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 芯片文字写入失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 我们的行 UIText.TextID 是标记（非真键），UpdateText 查表会得到 null 把标题刷成空
        /// （OnEnable/重绑路径都会触发）→ 标记行直接跳过原方法。
        /// </summary>
        [HarmonyPatch(typeof(UIText), "UpdateText")]
        private static class UITextGuardPatch
        {
            private static bool Prefix(UIText __instance)
            {
                try
                {
                    string id = __instance.TextID;
                    return !(id != null && id.StartsWith(MARKER, StringComparison.Ordinal));
                }
                catch { return true; }
            }
        }

        /// <summary>只重绑可见行内容（不 ResetSelectIndex，光标不跳）。改值/动作后调用。</summary>
        private static void RebindKeepCursor(UICampConfigMenuListWindow win)
        {
            try
            {
                var ctrl = win.ScrollListController;
                if (ctrl == null) return;
                ctrl.UpdateListShowContent(0f, true, true, false);
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 行刷新失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>池化行按当前 data 重新绑定内容 + 选中复位（换表后必须，否则首屏旧内容/选中越界）。</summary>
        private static void Rebind(UICampConfigMenuListWindow win)
        {
            try
            {
                var ctrl = win.ScrollListController;
                if (ctrl == null) return;
                ctrl.ResetSelectIndex();
                ctrl.UpdateListShowContent(0f, true, true, false);
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 行重绑失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 左右键步进（TOGGLES 芯片行 + GAUGE 滑块行的键盘/手柄路径统一入口）：
        /// modMode 换档后把新档位同步回 data（芯片选中态/滑块位置），重绑刷新；
        /// 非 modMode 时光标在入口行上必须吞掉（防原版按 index 误写游戏配置）。
        /// </summary>
        /// <summary>
        /// 游戏自己的行刷新路径（UpdateContentData 三个重载）——**只隔离，不响应交互**。
        ///
        /// v0.1.26 根因（callers.py 实锤）：`UpdateContentData(float,bool)` 的调用者包含
        /// **`UpdateContentDataList`**(0x22B03E，就是我们在 RebuildPatch 里主动调的那个)、
        /// `OnPressDecision`/`OnTabNext`/`OnTabPrev`/`OnVertListNextPage`/`OnVertListPrevPage`
        /// —— 也就是说**游戏刷新列表时会逐行调用它**，把"这一行的当前值"传进来。
        /// v0.1.23~25 我们把每次调用都当成"用户在调值"（日志里 `左右调值 delta=0` 就是这么
        /// 来的，用户不可能按出 delta=0），于是：
        ///   - 每行都跑一遍 BeginPreview/CommitPreview/刷新（22 次无谓重渲染）；
        ///   - 撞到 warn 行（`f.onehit.prot` 強殺受保護，行 12）就 OpenConfirm
        ///     → **一进 MOD 页就自动弹确认框**（v0.1.25 用户实测）。
        /// 修复：用户输入已由 OnUpdate 自读（左右键），这里只在"用户明确处于滑块调整态"
        /// 时接受游戏传来的值（拖动滑块），其余一律吞掉做隔离（防原版按行号误写游戏配置）。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "UpdateContentData", new Type[] { typeof(bool) })]
        private static class TogglePatch
        {
            private static bool Prefix(UICampConfigMenuListWindow __instance, bool isNext)
            {
                if (!_modMode) return true;   // 原版设置页：完全透明
                return false;                 // mod 页：吞掉（不响应，也不放行给原版按行号误写）
            }
        }

        /// <summary>滑块直接赋值路径（拖动/绝对值，归一化 0..1）。仅在滑块调整态接受。</summary>
        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "UpdateContentData", new Type[] { typeof(float) })]
        private static class GaugePatch1
        {
            private static bool Prefix(UICampConfigMenuListWindow __instance, float value)
            {
                if (!_modMode) return true;
                if (!_adjust) return false;                    // 非调整态（含游戏刷新路径）：只隔离
                return StepSelected(__instance, 0, value);     // 用户已在调整态：拖动预览
            }
        }

        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "UpdateContentData", new Type[] { typeof(float), typeof(bool) })]
        private static class GaugePatch2
        {
            private static bool Prefix(UICampConfigMenuListWindow __instance, float value)
            {
                if (!_modMode) return true;
                if (!_adjust) return false;                    // 同上：刷新路径不响应
                return StepSelected(__instance, 0, value);
            }
        }

        /// <summary>
        /// 统一方向键处理（适配游戏原生交互，用户 02:16 反馈）：
        /// - 弹窗待确认期间：全吞（输入归弹窗）。
        /// - GAUGE 调整模式中：左右=预览改档（不落盘，确定才保存）。
        /// - 其余情况（含芯片行）：方向键不切档（原版芯片靠确定键切换），吞掉。
        /// </summary>
        /// <summary>去重诊断日志（同类消息只打一次，防长按刷屏）。</summary>
        private static void Diag(string msg)
        {
            if (msg == _lastDiag) return;
            _lastDiag = msg;
            QolLog.Info(msg);
        }
        private static string _lastDiag = "";

        private static bool StepSelected(UICampConfigMenuListWindow win, int delta, float norm)
        {
            try
            {
                if (_pendingRow >= 0) return false;             // 弹窗期间输入归弹窗
                if (!_modMode)
                    return !IsEntryData(win.SelectedContentData);

                int idx = win.SelectedListDataIndex;
                if (idx < 0 || idx >= Rows.Count) return false;
                var row = Rows[idx] as IAdjustableRow;
                if (row == null) return false;
                if (!_adjust)
                {
                    // v0.1.24：不可逆项**左右键同样必须先确认**。v0.1.23 在这里直接写配置，
                    // 完全绕过了 warn 检查 → 用户反馈"需要二次确认的项目弹窗不生效了"。
                    if (Rows[idx].Warn)
                    {
                        if (!OpenConfirm(win, idx, delta))
                            QolLog.Warn("camp: 弹窗不可用，行 " + idx + " 左右调值已拒绝（防未确认先改）");
                        return false;
                    }

                    // v0.1.23：v0.1.15 行为——左右键直接改值并立即写配置（不必先按确定进调整态）。
                    // v0.1.20 起这里直接 return（要求先 _adjust），导致玩家按左右毫无反应。
                    row.BeginPreview();
                    row.PreviewStep(delta);
                    row.CommitPreview();
                    Diag("camp: 左右调值（行 " + idx + " delta=" + delta + " → " + Rows[idx].DisplayTitle() + "）");
                    SyncSelectedRow(win, idx);
                    return false;
                }

                // GAUGE 调整中：预览改档（不写配置，确定保存/取消回滚）
                // ★ v0.2.7：一次物理按键会连发 3 个事件（见 SliderStepDedupMs 注释）→ 窗口内只走一档。
                long nowS = Clock.ElapsedMilliseconds;
                if (nowS - _lastSliderStepMs < SliderStepDedupMs) return false;
                _lastSliderStepMs = nowS;
                if (!float.IsNaN(norm)) row.PreviewToNorm(norm);
                else row.PreviewStep(delta);
                Diag("camp: 滑块预览（行 " + idx + "，norm=" + norm.ToString("F2") +
                     " delta=" + delta + " → " + Rows[idx].DisplayTitle() + "）");

                SyncSelectedRow(win, idx);
                return false;
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 方向键处理失败 " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>把当前预览/生效档位同步回选中行的 data（芯片高亮/滑块位置）并重绑刷新。</summary>
        private static void SyncSelectedRow(UICampConfigMenuListWindow win, int idx)
        {
            CampRow row = Rows[idx];
            object list = GetDataList(win);
            if (list != null && idx < ListCount(list))
                SyncRowData(row, ListAt(list, idx));
            RebindKeepCursor(win);
            RefreshShow(win);   // v0.1.22：所有改值路径统一强制重渲染，标题立即反映新档位
        }

        /// <summary>
        /// 强制窗口重渲染（v0.1.22）：切档/预览后原版不会自己重跑 UpdateShowComponent，
        /// 标题（档位文本就在标题里）于是不动——这就是"值其实切了、显示不动"的直接原因。
        /// 窗口级 UpdateShowComponent 是 public override（Slot 25），可直接反射调用；
        /// 它会逐行走 SetContentData/UpdateShowComponent → 我们的标题补丁随之写入新文本。
        /// </summary>
        private static void RefreshShow(UICampConfigMenuListWindow win)
        {
            try
            {
                var mi = typeof(UICampConfigMenuListWindow).GetMethod("UpdateShowComponent");
                if (mi == null) { QolLog.Warn("camp: UpdateShowComponent 反射缺失（标题不会刷新）"); return; }
                mi.Invoke(win, null);
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 重渲染失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>调整模式存取（EnumRow 用）。</summary>
        private interface IAdjustableRow
        {
            void BeginPreview();
            void PreviewStep(int delta);
            void PreviewToNorm(float norm);
            void CommitPreview();
            void CancelPreview();
        }

        /// <summary>
        /// 取选中的行内容组件（UICampConfigMenuContent 代理）。跨代理 as 派生类恒 null
        /// （unhollower 铁律）→ 用原生指针重建派生代理：Activator 走运行时 unhollower
        /// 生成的 ctor(IntPtr)（编译期 DummyDll 无此 ctor，故不走 new）。
        /// </summary>
        private static object GetSelectedContent(UICampConfigMenuListWindow win)
        {
            var ctrl = win.ScrollListController;
            if (ctrl == null) return null;
            var ui = ctrl.SelectedUIContent;
            System.IntPtr p = PtrOf(ui);
            if (p == System.IntPtr.Zero) return null;
            return System.Activator.CreateInstance(typeof(UICampConfigMenuContent), new object[] { p });
        }

        /// <summary>
        /// 滑块行进/出调整态的视觉反馈（v0.1.18）：原版 OnPressDecision（0x229D13 实锤）在
        /// ChangeListState(CHANGE_VALUE/SELECT_ITEM) 之后必调 SetGaugeIconMaterial(selectState)
        /// + UpdateMaterial(selectState)——只切状态不刷材质 = 用户看到的"确定进不了滑块调整"。
        /// SELECTED(1)=进入调整高亮，NORMAL(0)=退出还原。
        /// </summary>
        private static void GaugeVisual(UICampConfigMenuListWindow win, int selectState)
        {
            try
            {
                var content = GetSelectedContent(win);
                if (content == null)
                {
                    QolLog.Info("camp: 滑块视觉跳过（选中行组件不可达）");
                    return;
                }
                var st = Enum.ToObject(typeof(UIScrollListContent.eSelectState), selectState);
                var et = typeof(UIScrollListContent.eSelectState);
                // v0.1.20：取不到方法必须打日志——v0.1.18/19 的静默 if(mi!=null) 让"没高亮"看起来像"没改值"。
                var mi = typeof(UICampConfigMenuContent).GetMethod("SetGaugeIconMaterial", new Type[] { et });
                if (mi == null) QolLog.Warn("camp: SetGaugeIconMaterial 反射缺失（滑块高亮不会有反馈）");
                else mi.Invoke(content, new object[] { st });
                var mi2 = typeof(UICampConfigMenuContent).GetMethod("UpdateMaterial", new Type[] { et });
                if (mi2 == null) QolLog.Warn("camp: UpdateMaterial 反射缺失");
                else mi2.Invoke(content, new object[] { st });
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 滑块视觉状态失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>v0.2.4：进入物品生成子页（MOD 設定 → 生成物品）。</summary>
        private static void EnterSpawnPage(UICampConfigMenuListWindow win)
        {
            EnsureSpawnRows();
            _page = CampPage.Spawn;
            _adjust = false;
            _pendingRow = -1;
            _backRequested = false;
            DisarmActions();
            win.UpdateContentDataList();     // → RebuildPatch 按"当前页"（Spawn）重建行
            // ⛔ v0.1.27 教训：UpdateContentDataList 末尾会把光标推到**最后一行**。
            // 子页末行是「返回」行，往回翻到主页时末行则是 G 分类的动作行——
            // 无论哪个方向都必须把光标拽回首行 + 留帧预算，避免"刚翻页就按确定"误触末行。
            ResetCursorToTop(win, "进入物品生成子页");
            _entryFixFrames = 180;
            QolLog.Info("camp: 进入物品生成子页（ID " + FeatureEconomy.SpawnId
                        + " x" + FeatureEconomy.SpawnCount + "，步進 " + FeatureEconomy.IdStep + "）");
        }

        /// <summary>
        /// v0.3 批次 4 / A7：进入「角色等级」子页。
        /// ⚠ 与 EnterSpawnPage 的关键差别：**每次都重建行表**（_levelRowsBuilt 复位），
        ///   因为角色数随剧情/编队变化，IntRangeRow 的 max 是构造期固定的。
        /// 其余（清调整态/弹窗、重绑列表、光标回首行 + 180 帧预算）与 C7 完全一致。
        /// </summary>
        private static void EnterLevelPage(UICampConfigMenuListWindow win)
        {
            _levelRowsBuilt = false;
            EnsureLevelRows();
            _page = CampPage.Level;
            _adjust = false;
            _pendingRow = -1;
            _backRequested = false;
            DisarmActions();
            win.UpdateContentDataList();     // → RebuildPatch 按"当前页"（Level）重建行
            ResetCursorToTop(win, "进入角色等级子页");
            _entryFixFrames = 180;
            QolLog.Info("camp: 进入角色等级子页（角色 " + FeatureLevel.ActorCount()
                        + " 人，当前 " + CharaIndex() + "，目標 " + TargetLevel() + "）");
        }

        /// <summary>v0.2.5：子页「返回」行动作行请求返回（由 Decision2Patch / OnDialogResult 消费）。</summary>
        private static bool _backRequested;

        /// <summary>
        /// v0.2.5：统一的"返回上一级"（页面层级）。
        ///
        /// 【v0.2.8 勘误 + 反汇编全链路】取消键真实链路 =
        /// UICampBaseWindow.OnInput(0x21EAD0) → ReturnMenu(0x21ED60) → ChangeState(MENU)，
        /// 然后 shell 才把输入转发给列表的 OnPressCanceled（且仅 CHANGE_VALUE 态必转发）。
        /// 也就是说本函数历史上主要由 ConfigClosePatch（ChangeState 关旧窗）调用，
        /// 真正"不关窗"的拦截点在 ReturnMenuPatch（v0.2.8 新增）——本函数只负责换层级。
        ///
        /// 现在的层级（Spawn / Level → Main → 原版設定列表 → 营地）：
        ///   物品生成子页 / 角色等级子页 --取消--> MOD 設定主页 --取消--> 原版設定列表 --取消--> 营地
        /// 多条取消路径都调本函数，行为一致。返回 true = 已消费（调用方必须 return false 拦原方法）。
        /// </summary>
        private static bool GoBack(UICampConfigMenuListWindow win)
        {
            if (!_modMode || win == null) return false;
            try
            {
                // v0.2.6：一次按键会被两条返回路径（CancelPatch / ConfigClosePatch）各调一次 →
                // 窗口内第二次直接吞（仍返回 true = 已消费，调用方拦掉原方法），保证"一次按键退一级"。
                long nowBack = Clock.ElapsedMilliseconds;
                if (nowBack - _lastBackMs < BackDedupMs)
                {
                    Diag("camp: 返回键重复触发已吞（相隔 " + (nowBack - _lastBackMs) + "ms）");
                    return true;
                }
                _lastBackMs = nowBack;

                _backRequested = false;

                if (_page != CampPage.Main)          // Spawn / Level 一律退一级回主页
                {
                    string from = _page == CampPage.Level ? "角色等级子页" : "物品生成子页";
                    _page = CampPage.Main;
                    _adjust = false;
                    _pendingRow = -1;
                    DisarmActions();
                    win.UpdateContentDataList();      // → RebuildPatch 按 Main 重建
                    ResetCursorToTop(win, "子页 → MOD 設定页");
                    _entryFixFrames = 180;
                    QolLog.Info("camp: " + from + " → 返回 MOD 設定页（上一级）");
                    return true;
                }

                // 已在 MOD 設定主页 → 退回原版設定列表（**不关窗**，也不回营地）
                _modMode = false;
                _adjust = false;
                _page = CampPage.Main;
                _pendingRow = -1;
                _pendingWin = null;
                DisarmActions();
                QolConfig.Save();
                _win = win;
                object list = GetDataList(win);
                if (list != null) RestoreVanilla(win, list);   // 内含 Rebind + 光标回首行
                else Rebind(win);
                _entryFixFrames = 180;
                QolLog.Info("camp: 退出 MOD 设置页 → 退回原版設定列表（拦截关窗），配置已落盘");
                return true;
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 返回上一级失败 " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// v0.2.4：按键去重。本环境（MelonLoader 0.5.7 + HarmonyX）一次游戏调用会把同一个
        /// 补丁方法跑两遍（日志实锤，见 Clock 字段注释）→ 动作行会被执行两次。
        /// 40ms 内的重复一律吞掉；人手连续按键（&gt;100ms）不受影响。
        /// </summary>
        private static bool DupKey(ref long last, string what)
        {
            long now = Clock.ElapsedMilliseconds;
            long gap = now - last;
            if (gap < KeyDedupMs)
            {
                Diag("camp: " + what + "重复触发已吞（相隔 " + gap + "ms）");
                return true;
            }
            last = now;
            return false;
        }

        /// <summary>
        /// v0.2.7：本次物理按键的"返回"是否刚刚已经被 GoBack 消费掉。
        ///
        /// 一次取消键会同时打到**两条**路径（`OnPressCanceled` / `UICampConfigWindow.CloseWindow`），
        /// 各自跑两遍（本环境补丁体翻倍）。第一条路径里的 GoBack 会把 `_modMode` 清成 false 并把
        /// `_lastBackMs` 置为当前；之后那条 `CloseWindow` 路径再进来时若只看 `!_modMode`，
        /// 就会**放行关窗 → 一次取消直接从 mod 页跳回营地**（用户第 6 条）。
        /// 用这把闸把"同一次按键的后续路径"继续拦住，保证**一次按键 = 退一级**。
        /// 之后玩家再按一次（>150ms）时 `_lastBackMs` 已过期 → 正常关窗回营地。
        /// </summary>
        private static bool RecentlyConsumedBack()
        {
            return Clock.ElapsedMilliseconds - _lastBackMs < BackDedupMs;
        }

        // ------------------------------------------------------------------
        // v0.3.1：原版设置页「首行按上 ⇒ 一步落到末行的 MOD設定 入口行」（用户第 2 条）
        //
        // 反汇编实锤（GameAssembly.dll）：
        //   OnVertListPrev 0x22AA60（上键）
        //     → MoveNextIndex(false,false) 0x229860
        //       → OnPressAny.IsMovingIndex(onPress,…) 0x183600（会写 UIScrollController+0x120 = 0 或 2）
        //       → UIScrollController.MoveSelectableIndex 0x183980
        //          0x1839CA  mov r15d,[rdi+0xF0]          ; 当前选中下标
        //          0x183A21  call 0x550F20                ; Mathf.Repeat ⇒ 候选下标的环绕换算
        //          0x183A3D  call isSelectableFunc(...)
        //          0x183A97  cmp dword ptr [rdi+0x120], 2
        //          0x183A9E  jne 0x183AA5
        //          0x183AA0  xor r8b,r8b                  ; ← 命中这条 ⇒ **isLoop = false**
        //          0x183AA5  movzx r8d, byte [rdi+0xD4]   ; ← 否则 isLoop = 类的 bool 字段
        //          0x183ABF  call SetSelectIndex(位移, isLoop, false)
        //   ⇒ isLoop=false 时，首行的 "-1" 位移被直接丢弃（不回绕），玩家要按第二次才看得到跳转；
        //     而 [this+0x120] 每次按键都可能不同（IsMovingIndex 拿累积 DeltaTime 与阈值比较来写），
        //     这就是"有时一次到、有时两次"的来源。
        //
        // 修法：**只把 isLoop 扳回 true**，位移与滚动/音效/ResetCursor 全部仍是游戏自己的逻辑
        //   （最小副作用）。限定条件（宁可不动，也不碰别的列表）：
        //   ① 玩家刚按了"上"（VertPrevPatch 置位）② 仍在原版设置页（未进 modMode）
        //   ③ 我们的入口行已挂 ④ 位移正好是 -1 且当前在首行。
        // ------------------------------------------------------------------
        // ⛔⛔ v0.3.1-r11 改版（实机日志推翻了 r9 的判断）：
        //   Latest.log 实锤 —— `OnVertListPrev` **本体**在"首行往上"时抛
        //     `UnhollowerBaseLib.Il2CppException: System.ArgumentOutOfRangeException`
        //   栈帧只到 `DMD<...OnVertListPrev>`，没有任何 mod 的托管帧 ⇒ 是**游戏方法内部**
        //   访问列表越界（Mathf.Repeat 环绕出的候选下标 == Count）。异常会让整次按键作废，
        //   这才是"要按两次"的真因（r9 改的 isLoop 参数根本没走到）。
        // ⇒ 既然这条路径本身会炸，就**别让它走**：首行按上时由 mod 直接把光标放到入口行，
        //   然后跳过原方法（Prefix 返回 false）。位移/音效之外的东西全交给 UpdateListShowContent。
        //   ⚠ 必须同时清 `_entryFixFrames`：否则 GuardEntryCursor 会把玩家刚跳过去的入口行
        //   当成"开窗时落在末行"又拽回首行。
        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "OnVertListPrev")]
        private static class VertPrevPatch
        {
            private static bool Prefix(UICampConfigMenuListWindow __instance)
            {
                try
                {
                    if (_modMode) return true;                                  // mod 页内不干预
                    if (_win == null || _entryPtr == System.IntPtr.Zero) return true;
                    if (PtrOf(_win) != PtrOf(__instance)) return true;          // 不是当前设置窗

                    object list = GetDataList(__instance);
                    if (list == null) return true;

                    int ei = EntryIndex(list);
                    if (ei <= 0) return true;                                   // 入口行不在表里
                    if (__instance.SelectedListDataIndex != 0) return true;      // 只有"首行往上"接管

                    // ⛔ v0.3.1-r12：优先走**游戏自己的"向下位移"**（+ei），不要用 ForceSetSelectIndex。
                    //   r11 实测"乱跳"的原因：ForceSetSelectIndex 把 mSelectedShowUIIndex(0xE8)
                    //   和 mSelectedListDataIndex(0xF0) 一起写成 14，而 showUIIndex 是**可见行组件**
                    //   的下标、由滚动位置决定，直接写死会让列表显示与实际选中错位 ⇒ 视觉乱跳。
                    //   "向下 +ei" 与玩家连按 ↓ 完全同路（滚动/音效/高亮全由游戏算），且不碰那条
                    //   会抛 ArgumentOutOfRangeException 的向上环绕路径。
                    bool jumped = MoveDownBy(__instance, ei);
                    if (!jumped) jumped = ForceCursorTo(__instance, ei);        // 兜底（老写法）
                    if (!jumped) return true;                                   // 都失败 ⇒ 交回原版行为

                    _entryFixFrames = 0;                                        // 玩家主动操作 ⇒ 交还控制权
                    if (_wrapLastDiag != "jump")
                    {
                        _wrapLastDiag = "jump";
                        QolLog.Warn("camp: 首行按上 ⇒ 直达入口行（下标 " + ei + "，已跳过会越界的 OnVertListPrev）");
                    }
                    return false;                                               // 跳过原方法（它就是越界的源头）
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: 上键直达异常 " + e.GetType().Name + " " + e.Message);
                    return true;
                }
            }
        }

        // ⛔ v0.3.1-r12：下面这个补丁已注销（保留代码作取证记录，不再参与 PatchAll）。
        //   原因见 Init() 的注释：它是唯一挂在全局 UIScrollController 上的补丁，风险面
        //   （标题菜单等所有列表）远大于它那点观测收益。需要取证时再临时挂回来。
#if NEVER_COMPILE_SETSELECTINDEX_PATCH
        [HarmonyPatch(typeof(UIScrollController), "SetSelectIndex", new System.Type[] { typeof(int), typeof(bool), typeof(bool) })]
        private static class SetSelectIndexPatch
        {
            // ⚠ v0.3.1-r11：改为**纯观测**（不再改 isLoop）。
            //   理由：实机证明"首行往上"的越界发生在游戏方法内部，把 isLoop 扳成 true
            //   只会让游戏更坚定地走进那条会抛 ArgumentOutOfRangeException 的路径；
            //   真正的修法在 VertPrevPatch（直接跳到入口行，跳过原方法）。
            //   这里留着是为了取证：若玩家反馈"还是要按两次"，下一条日志能看清
            //   游戏实际传的 moveIndex / isLoop / 当前下标。
            private static void Prefix(UIScrollController __instance, int moveIndex, bool isLoop)
            {
                try
                {
                    if (_wrapLastDiag == "jump") return;    // 直达已生效 ⇒ 不再刷观测
                    if (_entryPtr == System.IntPtr.Zero || _win == null) return;
                    var ctrl = _win.ScrollListController;
                    if (ctrl == null) return;
                    if (PtrOf(ctrl) != PtrOf(__instance)) return;

                    object list = GetDataList(_win);
                    int count = list == null ? -1 : ListCount(list);
                    QolLog.Warn("camp: [观测] SetSelectIndex move=" + moveIndex
                                + " isLoop=" + isLoop
                                + " curIndex=" + __instance.SelectedListDataIndex
                                + " count=" + count
                                + " entry=" + (list == null ? -1 : EntryIndex(list))
                                + (_modMode ? " (modMode)" : ""));
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: SetSelectIndex 观测异常 " + e.GetType().Name + " " + e.Message);
                }
            }
        }
#endif

        /// <summary>
        /// 确认键（适配游戏原生交互逻辑，用户 02:16 反馈）：
        /// - 弹窗待确认期间：吞。
        /// - 芯片行（TOGGLES）：确定=切下一档立即生效；warn 行先弹原生是/否框，选"是"才切。
        /// - 滑块行（GAUGE）：确定=进入调整（ChangeListState(CHANGE_VALUE) 与原版一致），
        ///   再按确定=保存退出；方向键在调整中改值（StepSelected 处理）。
        /// - 动作行：warn 行先弹原生是/否框（原版 RESET_PARAMETER 同款流程：
        ///   SHOW_DIALOG 状态 + OpenMessageBoxWindow + 回调 index==0 执行），非 warn 直接执行。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "OnPressDecision")]
        private static class Decision2Patch
        {
            private static bool Prefix(UICampConfigMenuListWindow __instance)
            {
                try
                {
                    _win = __instance;   // v0.1.20：滑块左右输入需要当前窗口引用

                    // ⛔⛔ v0.2.7（用户第 5 条："原生设置菜单无法被点击了 → 点不了确定"）：
                    // **原版页必须是我们完全不碰的透明通道**。
                    //   旧版的去重闸 `DupKey(ref _lastDecisionMs, "确认键")` 写在 `!_modMode` 判断**之前**：
                    //   本环境同一次调用会把补丁体跑两遍（见 Clock 注释），于是原版行按确定时
                    //   第 1 遍 true 放行、第 2 遍（相隔 0ms）被判"重复"→ 返回 **false 拦掉原方法**
                    //   （实机 debug.log 14:23:08.245 `确认键重复触发已吞（相隔 0ms）` 就发生在原版页上）。
                    //   现在：原版行**不参与任何去重**，直接 return true 放行 → 游戏自己的确认逻辑
                    //   与 mod 装不装完全无关。去重只保留在 mod 页 / 我们的入口行上。
                    if (_pendingRow >= 0) return false;         // 弹窗期间吞（只有 mod 页会置位）

                    if (!_modMode)
                    {
                        if (!IsEntryData(__instance.SelectedContentData)) return true;   // ★ 原版行：完全透明
                        if (DupKey(ref _lastDecisionMs, "确认键")) return false;
                        _modMode = true;
                        _adjust = false;
                        _lastSliderStepMs = -100000;
                        DisarmActions();
                        __instance.UpdateContentDataList();   // → RebuildPatch Postfix 换 mod 行
                        QolLog.Info("camp: 进入 MOD 设置页");
                        return false;
                    }

                    if (DupKey(ref _lastDecisionMs, "确认键")) return false;

                    int idx = __instance.SelectedListDataIndex;
                    if (idx < 0 || idx >= Rows.Count) return false;
                    CampRow row = Rows[idx];
                    _backRequested = false;   // v0.2.5：每次按键先清"返回上一级"请求（由动作行置位）

                    // 子页面入口行：按 Target 分派（v0.3 批次 4 起有 Spawn / Level 两个子页）
                    var page = row as PageRow;
                    if (page != null)
                    {
                        if (page.Target == CampPage.Level) EnterLevelPage(__instance);
                        else EnterSpawnPage(__instance);
                        return false;
                    }

                    // A7 角色卡片（2026-11-06）：确定键 = 选中该角色。
                    // ⚠ 不弹确认框、不写游戏数据；只切下标 + 把目标等级重置为该角色当前等级，
                    //   未「套用」的编辑值随之丢弃 ⇒ 绝不会串到下一个角色。
                    var card = row as CardRow;
                    if (card != null)
                    {
                        if (_levelChara != null) _levelChara.Value = card.Index;
                        ResetLevelTargetToCurrent();
                        SyncSelectedRow(__instance, idx);       // → RefreshShow 重画全部卡片的选中标记
                        QolLog.Info("camp: 角色等级卡片选中 " + FeatureLevel.ActorName(card.Index)
                                    + "（#" + card.Index + "），目標等級已重置为 " + TargetLevel());
                        return false;
                    }

                    // 动作行：warn → 原生弹窗（失败兜底内联二次确认）；非 warn → 直接执行
                    var act = row as ActionRow;
                    if (act != null)
                    {
                        if (act.Warn && !OpenConfirm(__instance, idx))
                        {
                            string r = act.Press();   // 内联兜底：第一次待确认，第二次执行
                            SyncSelectedRow(__instance, idx);
                            if (r != null) QolLog.Info("camp: 动作 '" + act.LabelKey + "' 已执行: " + r);
                            return false;
                        }
                        if (!act.Warn)
                        {
                            string result = act.Execute();
                            QolLog.Info("camp: 动作 '" + act.LabelKey + "' 已执行: " + result);
                            SyncSelectedRow(__instance, idx);
                            // v0.2.5：子页「返回」行动作行 → 走页面层级返回上一级
                            if (_backRequested) { GoBack(__instance); return false; }
                        }
                        return false;
                    }

                    var adj = row as IAdjustableRow;
                    if (adj == null) return false;

                    if (_adjust)   // 滑块行：调整中按确定=保存退出
                    {
                        _adjust = false;
                        _lastSliderStepMs = -100000;
                        adj.CommitPreview();
                        ChangeListState(__instance, 0);   // SELECT_ITEM
                        GaugeVisual(__instance, 0);       // NORMAL（原版 0x229D8D 流程）
                        SyncSelectedRow(__instance, idx);
                        QolLog.Info("camp: 滑块调整已保存（行 " + idx + "）");
                        return false;
                    }

                    if (row.Kind == RowKind.Gauge)   // 滑块行：进入调整
                    {
                        _adjust = true;
                        _lastSliderStepMs = -100000;   // v0.2.7：清聚合闸，进调整后第一下就能走一档
                        adj.BeginPreview();
                        ChangeListState(__instance, 1);   // CHANGE_VALUE（与原版一致）
                        GaugeVisual(__instance, 1);       // SELECTED 高亮滑块（原版 0x229D1D 流程）
                        SyncSelectedRow(__instance, idx);
                        QolLog.Info("camp: 进入滑块调整（行 " + idx + " " + Rows[idx].DisplayTitle() + "）");
                        return false;
                    }

                    // 芯片行（TOGGLES）：确定=切下一档；warn 先弹窗（弹窗不可用时保守拒绝，绝不未确认先切）
                    if (row.Warn)
                    {
                        if (!OpenConfirm(__instance, idx))
                            QolLog.Warn("camp: 弹窗不可用，行 " + idx + " 变更已拒绝（防未确认先切）");
                        return false;
                    }
                    row.Step(1);
                    SyncSelectedRow(__instance, idx);
                    return false;
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: 确认键处理失败 " + e.GetType().Name + " " + e.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// 取消键（顺序：弹窗待确认 > 滑块调整中 > 退出 mod 页）。
        /// 调整中取消 = 回滚到进入调整前的值（原版 CHANGE_VALUE 取消同款语义）。
        /// </summary>
        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "OnPressCanceled")]
        private static class CancelPatch
        {
            private static bool Prefix(UICampConfigMenuListWindow __instance)
            {
                try
                {
                    if (_pendingRow >= 0) return false;         // 弹窗期间吞（弹窗自己处理取消）

                    // ⛔⛔ v0.2.7（用户第 5 条）：原版设置页必须**完全透明**。
                    //   旧版把 `DupKey(ref _lastCancelMs,"取消键")` 放在最前，于是原版页上同一次按键的
                    //   第 2 遍补丁体会被判"重复"返回 false → 拦掉游戏自己的 OnPressCanceled
                    //   （v0.2.6 实机日志 14:14~14:18 多次出现原版页上的 `确认键重复触发已吞`）。
                    //   现在原版页不参与去重；唯一例外是"这一次按键的返回**刚刚已被我们消费**"
                    //   （= 从 mod 页退回原版列表的同一按键），那时继续拦，保证一次按键只退一级。
                    if (!_modMode)
                    {
                        if (RecentlyConsumedBack()) return false;   // 同一次按键的残留 → 继续拦
                        return true;                                 // ★ 原版页：完全放行
                    }

                    if (DupKey(ref _lastCancelMs, "取消键")) return false;

                    // 1) 滑块调整中取消 = 回滚（优先于页面返回；原版 CHANGE_VALUE 取消同款语义）
                    int idx = __instance.SelectedListDataIndex;
                    if (_adjust && idx >= 0 && idx < Rows.Count)
                    {
                        var adj = Rows[idx] as IAdjustableRow;
                        if (adj != null) adj.CancelPreview();   // 回滚预览
                        _adjust = false;
                        _lastSliderStepMs = -100000;
                        ChangeListState(__instance, 0);   // SELECT_ITEM
                        GaugeVisual(__instance, 0);       // NORMAL（原版取消同款还原）
                        SyncSelectedRow(__instance, idx);
                        QolLog.Info("camp: 滑块调整已取消（值未变）");
                        return false;
                    }

                    // 2) 其余一律按**页面层级**返回上一级（v0.2.5）：
                    //    子页 → MOD 設定主页 → 原版設定列表（不关窗、不回营地）。
                    //    与 CloseWindow 路径共用 GoBack，两条路径行为一致。
                    if (GoBack(__instance)) return false;
                    return true;   // GoBack 失败（异常）→ 放行原方法，至少能关窗出去
                }
                catch (Exception e)
                {
                    QolLog.Warn("camp: 取消键处理失败 " + e.Message);
                    return true;
                }
            }
        }

        // ------------------------------------------------------------------
        // 原生确认弹窗（StellaUIManager.OpenMessageBoxWindow——截图那种 是/否 框）。
        // 原版 RESET_PARAMETER 流程实锤：ChangeListState(SHOW_DIALOG) → OpenMessageBoxWindow
        // → 回调 index==0 = 是（先确认后生效）；否/取消 → ChangeListState(SELECT_ITEM)。
        // delegate 回调不经托管传（封送风险），改挂 UISystemMessageBoxWindow.OnPress/OnCross
        // Postfix 捕获结果：index==0 是"是"（<OnPressDecision>m__1 反汇编实锤）。
        // ------------------------------------------------------------------

        /// <summary>对第 idx 行弹出原生是/否确认框；成功返回 true（失败时调用方走内联兜底）。</summary>
        private static bool OpenConfirm(UICampConfigMenuListWindow win, int idx, int delta = 1)
        {
            try
            {
                // 静态门面：StellaUIManager.OpenMessageBoxWindow(message, onDecide, onCancel, onClose,
                //               selectIndex=0, isEnableCancel=true, isPlayDecisionSe=true)
                var mi = typeof(StellaUIManager).GetMethod(
                    "OpenMessageBoxWindow",
                    BindingFlags.Public | BindingFlags.Static);
                if (mi == null) { QolLog.Warn("camp: OpenMessageBoxWindow 不可达，改用内联确认"); return false; }
                int n = mi.GetParameters().Length;
                var args = new object[n];
                args[0] = Disp("camp.ask");
                for (int i = 1; i < n; i++)
                {
                    var pt = mi.GetParameters()[i].ParameterType;
                    if (pt == typeof(int)) args[i] = 0;                     // selectIndex
                    else if (pt.Name == "Boolean") args[i] = true;          // isEnableCancel 等
                    else args[i] = null;                                    // delegate 回调（结果由 OnPress patch 捕获）
                }
                mi.Invoke(null, args);

                _pendingRow = idx;
                _pendingWin = win;
                _pendingDelta = delta;
                _pendingAt = Environment.TickCount;
                ChangeListState(win, 2);   // SHOW_DIALOG（与原版一致，窗口输入停摆）
                QolLog.Info("camp: 确认弹窗已打开（行 " + idx + "）");
                return true;
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 弹窗打开失败 " + e.GetType().Name + " " + e.Message + "，改用内联确认");
                return false;
            }
        }

        /// <summary>弹窗按下（index==0 是"是"）：先确认后生效——这里才真正切换/执行。</summary>
        private static void OnDialogResult(int index)
        {
            int idx = _pendingRow;
            var win = _pendingWin;
            int delta = _pendingDelta;
            _pendingRow = -1;
            _pendingWin = null;
            _pendingDelta = 1;
            _pendingAt = 0;

            // v0.1.25：上下文失效时也**必须**恢复窗口状态。原来这里直接 return，
            // 窗口永远停在 SHOW_DIALOG(2) → 上下左右全部失灵（用户反馈"确定后无法操作"）。
            bool ok = idx >= 0 && idx < Rows.Count && win != null;
            if (!ok)
                QolLog.Warn("camp: 弹窗回调上下文失效（idx=" + idx + " rows=" + Rows.Count
                            + " win=" + (win != null) + "），仅恢复状态");

            try
            {
                if (ok)
                {
                    if (index == 0)   // 是（<OnPressDecision>m__1 实锤 0=是）
                    {
                        CampRow row = Rows[idx];
                        var act = row as ActionRow;
                        if (act != null)
                        {
                            string result = act.Execute();
                            QolLog.Info("camp: 动作 '" + act.LabelKey + "' 已确认执行: " + result);
                        }
                        else
                        {
                            // v0.1.24：按触发时的真实步进走（左右键调值可以是 -1），不再是写死的 +1
                            row.Step(delta);
                            QolLog.Info("camp: 行 " + idx + " 已确认变更（delta=" + delta + "）");
                        }
                    }                    else
                    {
                        QolLog.Info("camp: 弹窗选否，未变更（行 " + idx + "）");
                    }
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 弹窗回调处理失败 " + e.GetType().Name + " " + e.Message);
            }
            finally
            {
                // v0.1.25：无论走哪条路径（含异常/上下文失效）都必须复位——
                // 窗口停在 SHOW_DIALOG(2) 会让上下左右全部失灵。
                try
                {
                    DisarmActions();
                    if (win != null)
                    {
                        ChangeListState(win, 0);   // SELECT_ITEM
                        if (ok) SyncSelectedRow(win, idx);
                    }
                }
                catch (Exception e2)
                {
                    QolLog.Warn("camp: 弹窗后状态复位失败 " + e2.GetType().Name + " " + e2.Message);
                }
            }
        }

        [HarmonyPatch(typeof(UISystemMessageBoxWindow), "OnPress")]
        private static class MessageBoxPressPatch
        {
            private static void Postfix(int index)
            {
                if (_pendingRow >= 0) OnDialogResult(index);
            }
        }

        [HarmonyPatch(typeof(UISystemMessageBoxWindow), "OnCross")]
        private static class MessageBoxCrossPatch
        {
            private static void Postfix()
            {
                if (_pendingRow >= 0) OnDialogResult(-1);   // 取消=否
            }
        }

        /// <summary>
        /// 读窗口状态字段 mListState(0xF8)。dump 实锤：
        /// `private UICampConfigMenuListWindow.eListState mListState; // 0xF8`
        /// （枚举 SELECT_ITEM=0 / CHANGE_VALUE=1 / SHOW_DIALOG=2）。失败返回 -1。
        /// </summary>
        private static int ReadListState(UICampConfigMenuListWindow win)
        {
            try
            {
                System.IntPtr p = PtrOf(win);
                if (p == System.IntPtr.Zero) return -1;
                return System.Runtime.InteropServices.Marshal.ReadInt32(p, 0xF8);
            }
            catch { return -1; }
        }

        /// <summary>
        /// 直接写 mListState(0xF8)——只复刻游戏 ChangeListState 的**唯一必要副作用**（状态本身），
        /// 跳过它的材质/语音等周边更新。写后回读校验，用于"游戏方法没把状态写成功"时的兜底。
        ///
        /// r22：这条路径上有件**看似故障、其实是已知常态**的事，先说清楚再改日志级别。
        /// 反汇编（见下 ChangeListState 注释）：游戏 `ChangeListState`(0x228490) 把真正写状态的
        /// `mov [rsi+0xF8], edi` 放在方法**最末尾**，之前的材质/语音调用带空引用检查、失败即抛
        /// ⇒ `mi.Invoke` 必然抛 `TargetInvocationException`，且**那些周边副作用本来也没跑到**。
        /// 所以"直写 0xF8"不是将就，它就是这条路径上唯一能落地的副作用，回读恒等于目标值。
        /// ⇒ 结论：功能正常，不该按"故障"逐条报警。但完全静默又会重蹈铁律 53 的覆辙
        ///   （"日志干净"证明不了"游戏正常"）⇒ 采取"**首次明说 + 后续降噪**"：
        ///   · 回读 ≠ 目标（真没写进去）⇒ **Warn，永远打**；
        ///   · 回读正确 ⇒ 首轮 Info 说明一次这是已知常态；后续同类降到 Debug（默认不落盘），
        ///     取证时把日志级别调到 Verbose 仍能逐条看到。
        /// </summary>
        private static int _fallbackCount;
        private static bool _fallbackNoted;

        private static void ForceListState(UICampConfigMenuListWindow win, int state, string why)
        {
            try
            {
                System.IntPtr p = PtrOf(win);
                if (p == System.IntPtr.Zero)
                {
                    QolLog.Warn("camp: 窗口指针为零，无法直写 mListState（" + why + "）");
                    return;
                }
                System.Runtime.InteropServices.Marshal.WriteInt32(p, 0xF8, state);
                int back = System.Runtime.InteropServices.Marshal.ReadInt32(p, 0xF8);

                if (back != state)
                {
                    // 真的没写进去 —— 这是唯一值得报警的情形（会导致上下左右失灵）
                    QolLog.Warn("camp: 直写 mListState(0xF8)=" + state + " 回读=" + back
                        + " ≠ 目标（" + why + "）——未生效！");
                    return;
                }

                _fallbackCount++;
                if (!_fallbackNoted)
                {
                    _fallbackNoted = true;
                    QolLog.Info("camp: 已知常态 —— 游戏 ChangeListState 在这条路径上会抛托管异常，"
                        + "改由内存直写 mListState(0xF8) 兜底且回读正确（="
                        + state + "）；这不是故障。后续同类事件不再逐条落盘，"
                        + "需要取证请把日志级别调到 Verbose。");
                }
                else
                {
                    QolLog.Debug("camp: 直写 mListState(0xF8)=" + state + "（" + why
                        + "，回读=" + back + "，累计 " + _fallbackCount + " 次）");
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("camp: 直写 mListState 失败 " + e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// ChangeListState（窗口调整状态机，private）：0=SELECT_ITEM 1=CHANGE_VALUE 2=SHOW_DIALOG。
        ///
        /// v0.1.28 关键修复（用户实机反馈"需要二次确认的选项点完就没法控制上下左右了"）：
        ///
        /// 反汇编实锤 —— **mListState(0xF8) 就是上下左右导航的输入闸门**：
        ///   `OnVertListNext`(0x22A890) 的第一条语义检查就是
        ///     0x22A896  cmp dword ptr [rcx+0xF8], 0
        ///     0x22A8A0  jne 0x18022A99A        ← 状态 != 0 就整段跳过，光标一动不动
        /// 只要状态停在 SHOW_DIALOG(2)，上下左右全部失灵。
        ///
        /// 而 `ChangeListState`(0x228490) 把 `mov dword ptr [rsi+0xF8], edi` 放在方法**最末尾**
        /// (0x228546)，之前要先做材质/语音等副作用调用；这些调用带 `test rbx,rbx / je` 空引用
        /// 检查，失败就跳到 `call 0x998890`(il2cpp 抛异常桩) + `int3` —— 抛出的异常让末尾那次
        /// 状态写入**永远执行不到**（另有 0x228601 的静默早退路径，同样不写状态）。
        /// 我们此前 `mi.Invoke` 的 catch 只打日志、**没有任何兜底** → 状态卡在 2 → 用户看到的现象。
        ///
        /// 日志实锤（用户本次实测）：
        ///   `camp: 弹窗选否，未变更（行 16）`
        ///   `[W] camp: ChangeListState 调用失败 TargetInvocationException ...`
        ///   此后只剩 `tick`，再没有任何 `左右调值` / 行移动日志；而走"是"的那些次
        ///   （同样会调 ChangeListState(win,0)）没有报警、输入依旧正常。
        ///
        /// 现在：调用成功 → 回读校验；调用抛异常 / 回读不符 → 直写 0xF8 兜底（幂等，可重复调）。
        /// </summary>
        private static void ChangeListState(UICampConfigMenuListWindow win, int state)
        {
            _win = win;   // v0.1.20：滑块左右输入需要当前窗口引用
            if (win == null) { QolLog.Warn("camp: ChangeListState 窗口为空"); return; }
            try
            {
                var mi = typeof(UICampConfigMenuListWindow).GetMethod(
                    "ChangeListState", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (mi == null)
                {
                    if (!_clsDiagDone)   // 一次性诊断：把真实方法名列出来，别再猜
                    {
                        _clsDiagDone = true;
                        var names = new System.Collections.Generic.List<string>();
                        foreach (var m in typeof(UICampConfigMenuListWindow).GetMethods(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            if (m.Name.IndexOf("ListState", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                names.Add(m.Name);
                        QolLog.Warn("camp: ChangeListState 反射缺失；含 ListState 的方法=["
                            + string.Join(",", names.ToArray()) + "]");
                    }
                    // v0.1.19：private 方法反射不到时**不再静默**（v0.1.18 就是这样白丢一轮实测）
                    ForceListState(win, state, "反射缺失");
                    return;
                }
                var p = mi.GetParameters()[0].ParameterType;
                mi.Invoke(win, new object[] { Enum.ToObject(p, state) });

                // v0.1.28：游戏方法可能"调用成功但没写状态"（0x228601 静默早退）→ 回读校验
                int back = ReadListState(win);
                if (back != state)
                    ForceListState(win, state, "调用后回读 " + back + "≠" + state);
            }
            catch (Exception e)
            {
                // v0.1.28：异常时**必须兜底**（旧版只打日志 → 状态卡在 SHOW_DIALOG → 上下左右失灵）
                // r22：游戏方法有可能在抛异常**之前**已经把 0xF8 写好了（取决于它在哪一步炸）；
                //     那种情况下状态本来就是对的 ⇒ 先看再写，省掉一次无谓直写和一条日志。
                int cur = ReadListState(win);
                if (cur == state)
                {
                    QolLog.Debug("camp: ChangeListState 抛 " + e.GetType().Name
                        + "，但 mListState 已是 " + state + " ⇒ 无需兜底");
                    return;
                }
                ForceListState(win, state, "调用抛异常 " + e.GetType().Name);
            }
        }

        /// <summary>
        /// v0.1.28 状态卡死看门狗：mod 页里若发现 mListState 停在 SHOW_DIALOG(2) 而
        /// **我们并没有待确认弹窗**（_pendingRow &lt; 0，说明弹窗回调早已走完），
        /// 连续 2 秒都是这样 → 强制写回 SELECT_ITEM(0)。
        ///
        /// 这是"不依赖任何回调的退出路径"（延续 v0.1.25 立下的铁律），用来兜住我们没预料到的
        /// 路径（游戏自己弄丢状态 / 反射调用以其它方式失败等）。安全性：
        ///  - 弹窗**真的**开着时 _pendingRow >= 0 → 本看门狗自动让位，绝不抢正常弹窗；
        ///  - CHANGE_VALUE(1) 滑块调整态不在判定范围内，不会误复位；
        ///  - 只在 _modMode 下生效，原版设置页的状态机一概不碰。
        /// </summary>
        private static void GuardStuckDialogState()
        {
            try
            {
                if (!_modMode || _win == null || _pendingRow >= 0) { _stuckStateAt = 0; return; }
                if (ReadListState(_win) != 2) { _stuckStateAt = 0; return; }
                if (_stuckStateAt == 0) { _stuckStateAt = Environment.TickCount; return; }
                if (unchecked(Environment.TickCount - _stuckStateAt) < 2000) return;
                _stuckStateAt = 0;
                QolLog.Warn("camp: 看门狗——mListState 卡在 SHOW_DIALOG 且无待确认弹窗 → 强制复位 SELECT_ITEM");
                ForceListState(_win, 0, "看门狗超时自愈");
            }
            catch { /* 看门狗自身异常不得影响主流程 */ }
        }

        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "OnTabNext")]
        private static class TabNextPatch
        {
            private static bool Prefix() { return !_modMode; }
        }

        [HarmonyPatch(typeof(UICampConfigMenuListWindow), "OnTabPrev")]
        private static class TabPrevPatch
        {
            private static bool Prefix() { return !_modMode; }
        }

        private static void DisarmActions()
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                var a = Rows[i] as ActionRow;
                if (a != null) a.Disarm();
            }
        }

        private static void Safe(HarmonyLib.Harmony h, Type patch, string label)
        {
            try { h.PatchAll(patch); QolLog.Info("camp: patch ok - " + label); }
            catch (Exception e) { QolLog.Error("camp: patch FAILED - " + label + " : " + e.GetType().Name + " " + e.Message); }
        }
    }
}
