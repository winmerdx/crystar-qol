using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Common;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// v0.3 批次 1 —— D8 开场播片自动跳过。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 取证（2026-09-20，GameAssembly.dll 机器码；dump.cs L176001 / L176047）
    /// ─────────────────────────────────────────────────────────────────────
    /// 场景族：
    ///   LogoScene                 : StellaScene        L175962 （OnUpdate = 0x11FF70 空体，无跳过逻辑）
    ///   OpeningMovieScene         : StellaScene        L176001
    ///       private   bool mPlayMovie;    // 0x10
    ///       protected bool mDisableSkip;  // 0x11
    ///       protected override void OnUpdate()     0x4C490   ← 本次挂点
    ///       protected virtual  void OnEndMovie()   0x4C2E0   → ChangeScene(3)
    ///       protected override void OnEnter()      0x4C350   → PlayMovie(1920x816) + mPlayMovie=1
    ///   PrologueOpeningMovieScene : OpeningMovieScene L176047
    ///       protected override void OnEndMovie()   0x152100  → ChangeMap(eMapID=11)
    ///       protected override void OnInitialize() 0x152190  → base() 之后 **mDisableSkip = true**
    ///       （它**没有**覆写 OnUpdate ⇒ 运行时走基类 0x4C490）
    ///
    /// `OpeningMovieScene.OnUpdate`(0x4C490) 的真实分支（这就是"能不能跳过"的全部逻辑）：
    ///   0x4C555  cmp byte [rbx+0x10],0 ; je return        ← mPlayMovie==false 就整个跳过处理
    ///   0x4C55B  cmp byte [rbx+0x11],0 ; jne 0x4C573      ← mDisableSkip==true 就**不看按键**
    ///   0x4C56A  call GetKeyDown(eInputType=1)            ← 玩家按跳过键
    ///   0x4C571  jne 0x4C578                              ← 按了 → 去 OnEndMovie
    ///   0x4C573  test dil,dil ; jne return                ← 还在播/暂停 → 不结束
    ///   0x4C578  mov rax,[rbx] ; mov rdx,[rax+0x228] ; call [rax+0x220]   ← **vtable 调 OnEndMovie**
    ///
    /// ⇒ 三条硬结论（2026-09-20 玩家实机反馈后订正）：
    ///   ① **跳过键是 `StellaInputManager.eInputType.DECISION`(=1)** —— 即手柄 A / 键盘回车，
    ///      **不是 LB**（dump:163449）。玩家实机确认"启动播片按确认键可跳过"，与本分支吻合。
    ///      ⛔ 上一版注释写成 LB 是误判，已订正。
        ///   ② 基类 `mDisableSkip` 默认 false ⇒ **启动 OP 本来就能按确认键跳过**；
        ///      只有序章 OP 在 `OnInitialize`(0x152190) 里把它写成 true（`mov byte[rbx+0x11],1`）
        ///      ⇒ 序章那段**不能**按。
        ///
        /// ⛔⛔ **2026-09-22 玩家要求：序章一律不跳；但"倍速"照做（规则 47 的准确边界）。**
        ///   序章 OP 是**剧情**——跳掉玩家就看不懂在演什么。游戏自己用 `mDisableSkip=true`
        ///   声明了"不可跳"，mod 必须尊重这个声明。
        ///   v0.3.0~r4 的实现缺这道闸（`OnUpdate` 序章未覆写 ⇒ 一并被跳）⇒ **已修**：
        ///   主补丁里读到 `mDisableSkip` 为真即直接放行，本功能**只**对启动 OP 生效。
        ///
        ///   ⚠⚠ **"不代劳"只针对「跳过」，不包括「倍速」**（2026-09-22 玩家订正）：
        ///     跳过 = **丢内容**（这段没了）；倍速 = **内容一个不少，只是播得快**
        ///     （A8 倍速，v0.3 顺延）。玩家要倍速的理由正是"想看剧情但不想傻等"。
        ///     ⇒ 判据只有一条：**会不会让玩家看不到内容**。
        ///       会 ⇒ 不做（跳过序章）；不会 ⇒ 照做（倍速）。
        ///     ⇒ 后续做 A8 时，`mDisableSkip` 段落**同样适用倍速**，本闸不得拦它。
    ///   ③ 无论是"按了键"还是"播完了"，游戏自己都是**同一个动作 = 虚调用 OnEndMovie()**。
    ///
    /// 场景归属实锤（`eSceneType` dump:150488）：BOOT=1 / LOGO=2 / **TITLE=3** /
    /// OPENING_MOVIE=7 / PROLOGUE_OPENING_MOVIE=8。基类 `OnEndMovie` 里是
    /// `lea edx,[r8+3]` → `ChangeScene(eSceneType.TITLE)` @0x4C33E
    /// ⇒ **`OpeningMovieScene` 就是「启动 → 播片 → 标题画面」这一段**（即玩家所说的
    ///   "打开游戏后进入游戏前的播片"），挂点选择正确。
    ///   `OnEnter`(0x4C350) 里 `PlayMovie(ref MovieParameter)` @0x33CC60（1920×816）、
    ///   随后 `mov byte[rbx+0x10],1` ⇒ **`mPlayMovie` 偏移 0x10 确认**。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 本功能的做法（与规则 33 一致：别用一个开关罩两条路径）
    /// ─────────────────────────────────────────────────────────────────────
    /// 玩家要的是 **自动**跳过 ⇒ 走"补一次 OnEndMovie()"，**不是**写 mDisableSkip=false
    /// （后者只是"允许手动按 LB 跳过"，是另一种语义，不在本功能范围内）。
    ///
    /// ⛔ 不碰输入层（10.10：本作走 Steam Input，mod 监听按键必翻车）。
    /// ⛔ 不自己驱动场景切换（曾因 ReturnMenu → ChangeState 直接把状态机打乱）；
        ///    我们只是**替游戏按了那个已经存在的"结束播片"动作**，切场景由游戏自己完成：
        ///      基类 OnEndMovie → ChangeScene(3)  ← 本功能**只会**走到这一支（序章已被 mDisableSkip 闸拦掉）。
        ///      （保留知识：序章 OnEndMovie → ChangeMap(11)，但正常路径下不会触发。）
    ///
    /// ⚠ 派生类覆写了 OnEndMovie ⇒ 必须按**运行时真实类型**取 MethodInfo（规则 18 同款：
    ///    别信编译期类型）。若误按基类取，序章会走成 ChangeScene(3) 而不是 ChangeMap(11)。
    ///
    /// ⚠ `OnUpdate` 是 protected override、由引擎/场景管理器经 vtable 派发 ⇒
    ///    `callers.py` 扫不出调用者属正常（铁律 22 同款），是否真的跑起来只能看实机日志
    ///    ⇒ 首次进入必打一行 `hook live`（铁律 19：诊断用 Warn，不要 DebugThrottled）。
    /// </summary>
    public sealed class FeatureOpeningSkip : IFeature
    {
        internal static FeatureOpeningSkip Inst;

        public string Id { get { return "opening"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        /// <summary>mPlayMovie(0x10) 变 true 后再等几帧才触发：OnEnter 里刚 PlayMovie，立刻切场景风险大。</summary>
        private const int DelayFrames = 3;

        private MelonPreferences_Entry<bool> _skip;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            _skip = ctx.Cfg.Bool("AutoSkip", false, "Auto skip opening movie / 开场播片自动跳过");

            Safe(ctx.Harmony, typeof(OpeningUpdatePatch), "OpeningMovieScene.OnUpdate");
            Safe(ctx.Harmony, typeof(OpeningEnterPatch), "OpeningMovieScene.OnEnter（仅诊断）");
            Safe(ctx.Harmony, typeof(OpeningEndMoviePatch), "OpeningMovieScene.OnEndMovie（仅诊断）");
            Safe(ctx.Harmony, typeof(ChangeScenePatch), "StellaSceneManager.ChangeScene（仅诊断）");
            Safe(ctx.Harmony, typeof(LogoEnterPatch), "LogoScene.OnEnter（仅观测）");
            Safe(ctx.Harmony, typeof(LogoExitPatch), "LogoScene.OnExit（仅观测）");
            SelfCheck(ctx.Harmony);
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("opening: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 挂载自检（铁律 22 / r7 的教训）：IL2CPP 下 PatchAll 不报错 ≠ 挂上了。
        /// 只在自检表里找不到目标方法时才报警 —— 少了这一步，"没反应"又要靠猜。
        /// </summary>
        private static void SelfCheck(HarmonyLib.Harmony h)
        {
            try
            {
                bool onUpdate = false, onEnter = false;
                foreach (var m in h.GetPatchedMethods())
                {
                    try
                    {
                        if (m.DeclaringType != typeof(OpeningMovieScene)) continue;
                        if (m.Name == "OnUpdate") onUpdate = true;
                        else if (m.Name == "OnEnter") onEnter = true;
                    }
                    catch { }
                }
                QolLog.Warn("opening: 挂载自检 OnUpdate=" + onUpdate + " OnEnter=" + onEnter);
            }
            catch (Exception e)
            {
                QolLog.Warn("opening: 挂载自检异常 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Enabled
        {
            get
            {
                var f = Inst;
                if (f == null || f._skip == null || !f._skip.Value) return false;
                // 总开关 / 紧急关闭：Harmony 补丁不受 ModMain.OnUpdate 的闸门约束，必须自己判
                return QolConfig.Enabled && !KillSwitch.Tripped;
            }
        }

        /// <summary>
        /// 每帧轮询：只服务 **Logo 段跳过**（播片那段有自己的 OnUpdate 挂点，不需要这里）。
        ///
        /// ⚠ 为什么 Logo 必须靠这里：`LogoScene.OnUpdate` 的 RVA 0x11FF70 是**空体**，
        ///   而且与 `OnStartChangeScene` / `OnDeactiveScene` **共享同一函数体** ⇒ 挂它没有产出。
        ///   ⇒ 改成"进段时布防（OnEnter）+ 这里每帧找空闲窗口发请求 + 出段撤防（OnExit）"。
        /// </summary>
        public void OnUpdate()
        {
            try
            {
                if (!_logoArmed) return;
                if (!Enabled) { _logoArmed = false; return; }
                TryLogoSkip();
            }
            catch (Exception e)
            {
                QolLog.Warn("opening: Logo 跳过轮询异常 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>启动后的前若干个场景全部打印（之后只打疑似播片/标题的）。</summary>
        private static int _sceneSeq;

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // 纯诊断：确认启动流程到底进了哪些场景（OP 是不是真的走了 OpeningMovieScene）。
            //
            // ⚠ v0.3.0 首轮的教训：原来只打印名字含 movie/opening/logo/title 的场景，
            //   结果**根本看不出"玩家说的播片"对应哪一段**（播片那段 Unity 场景名就叫
            //   "Title"，与切过去的标题画面同名 ⇒ 两条日志长得一模一样，无从分辨）。
            //   ⇒ 启动阶段（前 20 个场景）一律全打，把完整链路还原出来。
            if (string.IsNullOrEmpty(sceneName)) return;
            _sceneSeq++;
            if (_sceneSeq > 20)
            {
                string s = sceneName.ToLowerInvariant();
                if (s.IndexOf("movie", StringComparison.Ordinal) < 0
                    && s.IndexOf("opening", StringComparison.Ordinal) < 0
                    && s.IndexOf("logo", StringComparison.Ordinal) < 0
                    && s.IndexOf("title", StringComparison.Ordinal) < 0) return;
            }
            QolLog.Info("opening: 场景 #" + _sceneSeq + " " + sceneName
                        + " (buildIndex " + buildIndex + ")");
        }

        /// <summary>无运行时副作用要收回（只在播片那一帧替游戏调了一次 OnEndMovie）。</summary>
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Bool(Loc.T("f.openskip"), _skip, false, RiskTier.Safe, Effect.RestartGame);
        }

        // ───────────────────────── 读 mPlayMovie(0x10) ─────────────────────────

        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _fieldWarned;

        /// <summary>最近一次 Invoke OnEndMovie 的时刻（用于衡量"Invoke → 真正切场景"的耗时）。</summary>
        private static DateTime _invokeAt;

        /// <summary>Logo 段进入时刻（用于量 `OnEnter → OnExit` 的真实停留时长）。</summary>
        private static DateTime _logoAt;

        private static bool PlayMovieOn(OpeningMovieScene s, IntPtr p)
        {
            // ⛔ 直读优先（铁律 42）：unhollower 代理的字段是 CLR 影子槽，
            //    `GetValue()` 恒返回 false ⇒ 反射若排在前面，"永远没在播"会把功能整个卡死。
            if (p != IntPtr.Zero)
            {
                try { return Marshal.ReadByte(IntPtr.Add(p, 0x10)) != 0; }
                catch { }
            }
            try
            {
                var fi = typeof(OpeningMovieScene).GetField("mPlayMovie", BF);
                if (fi != null) return Convert.ToBoolean(fi.GetValue(s));
            }
            catch { }
            if (!_fieldWarned)
            {
                _fieldWarned = true;
                QolLog.Warn("opening: mPlayMovie 读不到（反射与直读都失败）→ 不会自动跳过");
            }
            return false;
        }

        /// <summary>读 mDisableSkip(0x11)（同样只能直读，见铁律 42）。</summary>
        private static bool DisableSkip(OpeningMovieScene s)
        {
            IntPtr p = NativePtr.Of(s);
            if (p == IntPtr.Zero) return false;
            try { return Marshal.ReadByte(IntPtr.Add(p, 0x11)) != 0; }
            catch { return false; }
        }

        // ───────────────────────── 触发 OnEndMovie ─────────────────────────

        private const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static object _sceneMgr;
        private static bool _warnNoMgr;

        /// <summary>
        /// 拿 `GameSceneManager` 单例（`SingletonMonoBehaviour&lt;GameSceneManager&gt;`）。
        /// 静态属性 `Instance` 声明在泛型基类上 ⇒ 自基类向上一路找，别只查自己类型。
        /// </summary>
        private static object SceneMgr()
        {
            if (_sceneMgr != null) return _sceneMgr;
            try
            {
                for (Type t = typeof(GameSceneManager); t != null; t = t.BaseType)
                {
                    var pi = t.GetProperty("Instance", SF | BindingFlags.FlattenHierarchy | BindingFlags.DeclaredOnly);
                    if (pi == null) continue;
                    object v = pi.GetValue(null, null);
                    if (v != null) { _sceneMgr = v; return v; }
                }
            }
            catch { }
            if (!_warnNoMgr)
            {
                _warnNoMgr = true;
                QolLog.Warn("opening: 取不到 GameSceneManager 单例 ⇒ 无法判断场景状态机是否空闲");
            }
            return null;
        }

        /// <summary>
        /// 读 `mSceneState`(0x40)：
        ///   GameSceneManager.eSceneState → DEFAULT=0 / EXIT_CURRENT=1 / EXIT=2 /
        ///   ENTER_NEXT=3 / WAIT_ENTER=4 / ENTER=5（dump:151169）
        /// ⛔ 必须直读（铁律 42）：反射读字段恒 0，会误判成"空闲"。
        /// 返回 &lt;0 表示读不到（当作未知处理）。
        /// </summary>
        private static int SceneStateRaw()
        {
            var m = SceneMgr();
            if (m == null) return -1;
            IntPtr q = NativePtr.Of(m);
            if (q == IntPtr.Zero) return -2;
            try { return Marshal.ReadInt32(IntPtr.Add(q, 0x40)); }
            catch { return -3; }
        }

        /// <summary>
        /// 按运行时真实类型取 OnEndMovie 并调用一次。
        /// 序章（PrologueOpeningMovieScene）覆写了它 ⇒ 取错类型会走成基类的 ChangeScene(3)。
        /// </summary>
        private static void TryEndMovie(OpeningMovieScene s)
        {
            bool prologue = s is PrologueOpeningMovieScene;
            Type t = prologue ? typeof(PrologueOpeningMovieScene) : typeof(OpeningMovieScene);
            string which = prologue ? "PrologueOpeningMovieScene" : "OpeningMovieScene";
            try
            {
                MethodInfo mi = t.GetMethod("OnEndMovie", BF);
                if (mi == null)
                {
                    QolLog.Warn("opening: 取不到 " + which + ".OnEndMovie（运行时被裁？）");
                    return;
                }
                _invokeAt = DateTime.Now;
                mi.Invoke(s, null);
                // 铁律 10：Invoke 没抛异常 ≠ 真生效。这里没法直接回读（切场景是异步的）
                // ⇒ 除了一行"调过了"，另由 `OpeningUpdatePatch` 里 90 帧后的跟踪行来证伪。
                QolLog.Info("opening: 已 Invoke " + which + ".OnEndMovie()"
                            + " (mPlayMovie=" + PlayMovieOn(s, NativePtr.Of(s))
                            + ", mDisableSkip=" + DisableSkip(s) + ")");
            }
            catch (Exception e)
            {
                QolLog.Warn("opening: Invoke " + which + ".OnEndMovie 失败 -> "
                            + e.GetType().Name + ": " + e.Message);
            }
        }

        // ───────────────────── Logo 段跳过（2026-09-22 实测 12 秒） ─────────────────────

        private static bool _logoArmed;
        private static int _logoReq;
        private static int _logoLastState = -99;

        /// <summary>
        /// r26 恢复：TITLE 直跳请求发出 8 秒后仍没离开 Logo 段 ⇒ 退让回 OPENING_MOVIE(7)。
        /// 这是 r7 原有的自保（04fc71d 曾当「死状态」删掉），恢复它是"不会卡在 Logo"的第二道网。
        /// </summary>
        private static bool _logoFallback;

        /// <summary>
        /// 为什么值得做：**实测 Logo 段停留 12084ms / 13229ms**（r5 探针，两次启动一致），
        /// 不是先前误判的 64ms —— 那 64ms 只是两个 `sceneLoaded` **事件**的间隔（叠加加载的假象）。
        ///
        /// ⚠ Logo 段**不走 MovieManager**（`PlayMovie` 只有 3 个调用者：
        ///   `OpeningMovieScene.OnEnter` / 事件任务 0x8669A / Timeline 行为 0x5C8CE5，**都不在 Logo 里**）
        ///   ⇒ 不能用 Stop() 结束它，只能走场景管理器本身。
        ///   实测启动链：`STARTUP_CONFIG(9) → LOGO(2) → OPENING_MOVIE(7) → TITLE(3)`
        ///
        /// ⭐ **2026-11-10 r26：恢复 r7 的「Logo 直跳 TITLE(3)」，省回 OP 场景约 6 秒准备开销。**
        ///   ⚠ 背景订正（别再照抄 04fc71d 的旧结论）：
        ///   · 11-08 曾据两轮对照判定「跳过 OP 场景 = 卡标题根因」，于是 04fc71d 把目标
        ///     改回 OPENING_MOVIE(7)。**11-09 实机证伪**：新 DLL 确实完整进了 OP 场景
        ///     （OnEnter / OnUpdate hook live、Stop()=True、原生 OnEndMovie ×3），
        ///     回标题仍卡 67s / 107s ⇒ 跳过 OP 场景不是这次 bug 的原因。
        ///   · 反汇编事实依然成立：`OpeningMovieScene`（dump.cs:176001）确有整条生命周期
        ///     0x4C210 OnActiveScene / 0x4C350 OnEnter / 0x4C490 OnUpdate /
        ///     0x4C260 OnDeactiveScene / 0x4C2E0 OnEndMovie ⇒ r7 那句「跳过 OP 场景不丢东西」
        ///     在**初始化完整性**意义上是错的；只是丢掉的东西并非本次卡标题的成因。
        ///   · 真正根因由 r23 / r24 客观探针定位（见 docs/标题UI链与卡标题诊断.md 第八节）：
        ///     回标题时 uiSceneTitle 未重载、`UITitleWindow` 对象复用，
        ///     `UITitleRootWindow.mState`(0xD0) 残留 **9 = CONTINUE_WAIT**；
        ///     `OpenWindow()`(0x1E8170) 本该 `mov [rsi+0xd0],0` 复位却没跑 ⇒
        ///     `OnInput()`(0x1E78B0) 在状态 9 不收任何键、吸引模式兜底也不触发。
        ///   · **r25 修复（commit 604d6fa）** 在 `TitleScene.OnEnter` Postfix 里做条件性复位
        ///     （`ModMain.TryResetTitleStateMachine()`：仅 mState∈[8,11] 时调游戏自己的
        ///      OpenWindow，失败则直写原生字段）。11-10 00:05 实机日志确认
        ///      `title.fix: 回标题状态机复位 #1 mState 9 → 0`，玩家确认菜单恢复。
        ///
        /// ⛔⛔ 为什么现在恢复直跳 TITLE 不会让问题复发：
        ///   · `_logoArmed` 只在 `LogoScene.OnEnter` 布防、`OnExit` 撤防，
        ///     `TryLogoSkip` 只在**冷启动 Logo 段**发 ChangeScene；回标题路径是
        ///     `stage_0101 → Title(#5)`，完全不经过 Logo / OpeningMovieScene。
        ///   · r25 挂在 `TitleScene.OnEnter` Postfix，与本次会话的启动链无关，
        ///     只要 `mState` 落在终态就会复位 ⇒ **护栏在恢复后依然完整生效，本提交不动它**。
        ///   · 一并恢复 r7 的「8 秒退让」：TITLE 请求被门禁丢弃时改发 OPENING_MOVIE(7)，
        ///     避免卡在 Logo 段；原有三条自保全部保留。
        ///   · 影片秒停 `OpeningEnterPatch.Stop()` 代码保留，但正常路径不再进 OP 场景，
        ///     只在 8 秒退让时才会用到；动画依旧看不到。
        ///
        /// ⚠ 历史组合（直跳 TITLE + 无 r25）确实会卡标题，且此前所有「不卡」对照的判据
        ///    都被证实不可靠（日志同构、只差等待时长）⇒ 恢复后**必须实机跑一次
        ///    进游戏→回标题**，判据见 docs 第十节；有回归直接用备份 DLL 回滚。
        ///
        /// ⛔⛔ 四条自保（一旦请求没被接受却反复发，会把玩家一路踹回标题）：
        ///   ① 只在 `LogoScene.OnEnter` 之后布防、`OnExit` 之后立刻撤防；
        ///   ② 只在 `mSceneState == DEFAULT(0)` 时发（非空闲发了也白搭，见 ChangeScene 门禁）；
        ///   ③ 8 秒仍没离开 Logo ⇒ 从 TITLE 退让到 OPENING_MOVIE(7)；
        ///   ④ 20 秒兜底超时 ⇒ 无论如何撤防。
        /// </summary>
        private static void TryLogoSkip()
        {
            if (_logoAt != DateTime.MinValue
                && (DateTime.Now - _logoAt).TotalSeconds > 20.0)
            {
                _logoArmed = false;
                QolLog.Warn("opening: Logo 跳过超时（20s 未成功）⇒ 撤防，交回游戏自己播");
                return;
            }

            int st = SceneStateRaw();
            if (st != 0)
            {
                if (st >= 0 && st != _logoLastState)
                {
                    _logoLastState = st;
                    QolLog.Warn("opening: Logo 段 mSceneState=" + st + "（非 DEFAULT）⇒ 暂不发请求");
                }
                return;
            }

            // r26：恢复 r7 的「先 TITLE(3) 直跳；8 秒没离开 Logo ⇒ 退让 OPENING_MOVIE(7)」。
            //     退让是防止 ChangeScene 请求被门禁丢弃时卡在 Logo 段的自保，务必保留。
            if (!_logoFallback
                && _logoAt != DateTime.MinValue
                && (DateTime.Now - _logoAt).TotalSeconds > 8.0)
            {
                _logoFallback = true;
                QolLog.Warn("opening: Logo 段请求 TITLE 后 8s 仍未离开 ⇒ 退让为 OPENING_MOVIE(7)");
            }
            var target = _logoFallback ? Common.eSceneType.OPENING_MOVIE : Common.eSceneType.TITLE;

            var mgr = SceneMgr();
            if (mgr == null) return;
            try
            {
                MethodInfo mi = typeof(GameSceneManager).GetMethod(
                    "ChangeScene", new[] { typeof(Common.eSceneType) });
                if (mi == null)
                {
                    QolLog.Warn("opening: 取不到 GameSceneManager.ChangeScene ⇒ Logo 跳过放弃");
                    _logoArmed = false;
                    return;
                }
                mi.Invoke(mgr, new object[] { target });
                _logoReq++;
                // 请求被接受时 ChangeScene 会立刻把 mSceneState 写成 EXIT_CURRENT(1)
                // ⇒ 下一帧这里读到非 0，不会重复发（配合 ChangeScenePatch 的日志可验证）。
                QolLog.Warn("opening: Logo 段已请求 ChangeScene(" + target + "="
                            + (int)target + ")（第 " + _logoReq + " 次，进段 "
                            + (DateTime.Now - _logoAt).TotalMilliseconds.ToString("0") + "ms）");
            }
            catch (Exception e)
            {
                QolLog.Warn("opening: Logo 段 ChangeScene 失败 -> " + e.GetType().Name + ": " + e.Message);
                _logoArmed = false;
            }
        }

        // ─────────────────── 立刻停掉影片（OP 从"播 1 秒"压到"几乎不播"） ───────────────────

        private static bool _movieWarned;
        private static IntPtr _stoppedPtr;

        /// <summary>
        /// `MovieManager.Stop()`(0x33CE50，`Common` 命名空间单例)。
        ///
        /// ★ 这是把 OP 从"播 1 秒"压到"几乎不播"的关键（r5 实测：OnEnter 05.733 →
        ///   已 Invoke 06.265，532ms；而这 1 秒里**画面一直在播**，玩家看到的就是"还会播一小会"）。
        ///
        /// 原理：`OpeningMovieScene.OnUpdate`(0x4C490) 每帧判 `IsPlaying() || IsPause()`，
        ///   **影片一停，游戏自己就会去调 OnEndMovie**（0x4C578），而且**是每帧调**
        ///   ⇒ `ChangeScene(3)` 的请求只要遇上状态机空闲就一定被接受，不需要我们掐时机。
        ///   ⇒ 先 Stop 让画面立刻消失，收尾交回游戏自己的逻辑（比我们替它 Invoke 更原生）。
        /// </summary>
        private static bool StopMovie()
        {
            try
            {
                var mm = MovieManager.Instance;
                if (mm == null)
                {
                    if (!_movieWarned)
                    {
                        _movieWarned = true;
                        QolLog.Warn("opening: MovieManager.Instance 为空 ⇒ 无法提前停影片（退化为按帧跳过）");
                    }
                    return false;
                }
                mm.Stop();
                return true;
            }
            catch (Exception e)
            {
                if (!_movieWarned)
                {
                    _movieWarned = true;
                    QolLog.Warn("opening: MovieManager.Stop() 失败 -> " + e.GetType().Name + ": " + e.Message);
                }
                return false;
            }
        }

        // ───────────────────────── 补丁 ─────────────────────────

        /// <summary>
        /// ★ 决定性观测（2026-09-21 第二轮实机后加）：
        ///   上一轮日志是 `已 Invoke OnEndMovie()` → 262ms 后 `Invoke 后 90 帧仍在 OnUpdate
        ///   （mPlayMovie=True）` → 4.6 秒后才切到 Title。这有三种可能，行为上必须区分：
        ///     A. `MethodInfo.Invoke` **根本没执行到原生方法**（unhollower 反射调用的坑）
        ///        ⇒ 本 Postfix **不会**被触发；
        ///     B. 原生 OnEndMovie 执行了、ChangeScene(3) 也发了，只是**场景加载慢**（4.6 秒）
        ///        ⇒ 本 Postfix 会触发，且随后会看到 ChangeScene 的日志；
        ///     C. 执行了但被场景状态机挡住 ⇒ 本 Postfix 触发、但没有后续切换。
        ///   ⇒ 挂在与 Invoke 同一个方法上：只要原生方法体跑了一次，这里就一定留痕。
        /// </summary>
        [HarmonyPatch(typeof(OpeningMovieScene), "OnEndMovie")]
        private static class OpeningEndMoviePatch
        {
            private static int _n;
            private static void Postfix(OpeningMovieScene __instance)
            {
                try
                {
                    _n++;
                    if (_n <= 3)
                    {
                        QolLog.Warn("opening: ★ 原生 OnEndMovie 被执行（第 " + _n + " 次，"
                                    + (__instance == null ? "(null)" : __instance.GetType().Name) + "）");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 只在进入播片场景时打一行（诊断用）：确认 OnEnter 到底有没有被派发。
        /// 注意 PrologueOpeningMovieScene.OnEnter(0x152170) 方法体就是一条 `jmp 0x4C350`
        /// ⇒ 挂基类这一个地址，两个类都覆盖得到。
        /// ★ r6 起这里不再是"只诊断"：**影片刚起播就 Stop()**，这才是本功能真正的跳过动作
        ///   （r5 之前靠"替游戏调 OnEndMovie"，画面要等轮询到状态机空闲才消失，实机约 1 秒）。
        /// </summary>
        /// <summary>
        /// 观测 `GameSceneManager.ChangeScene(eSceneType)`(0x32B060)：
        /// `OnEndMovie` 的全部内容就是这个调用（反汇编 0x4C33E `jmp ChangeScene`）。
        ///   ⇒ 这里一有日志，就证明"请求确实发给了场景管理器"。
        /// ⚠ **有这行 ≠ 会切场景**（第三轮根因）：`ChangeScene` 内部还判了
        ///   `mSceneState != DEFAULT ⇒ ret`，非空闲时请求被静默丢弃
        ///   ⇒ 所以真正要看的是这行**之后有没有新场景**。
        /// ⚠ 打印上限从 8 放宽到 40：第三轮就是因为只打了前 8 次，
        ///    `ChangeScene(3)` 到底有没有被调用过完全看不到，白猜了一轮。
        /// </summary>
        [HarmonyPatch(typeof(GameSceneManager), "ChangeScene", new[] { typeof(Common.eSceneType) })]
        private static class ChangeScenePatch
        {
            private static int _n;
            private static void Postfix(Common.eSceneType sceneType)
            {
                try
                {
                    _n++;
                    if (_n > 40) return;
                    string gap = "";
                    if (_invokeAt != DateTime.MinValue)
                        gap = "，距上次 Invoke " + (DateTime.Now - _invokeAt).TotalMilliseconds.ToString("0") + "ms";
                    QolLog.Warn("opening: ChangeScene(" + sceneType + " = " + (int)sceneType
                                + ") 第 " + _n + " 次" + gap);
                }
                catch { }
            }
        }

        /// <summary>
        /// Logo 段跳过（2026-09-22）：先观测，**实测后已改为真跳过**。
        ///
        /// 背景：玩家要求"D8 补 logo 段跳过"。当时实机场景链显示
        ///   `02:13:12.712 场景 #5 Logo` → `02:13:12.776 场景 #6 uiSceneLoading` —— **仅 64ms**，
        ///   我据此判断"跳过它零收益"。⚠ **这个判断错了**（铁律 20 反过来也成立：
        ///   观测方法不可靠时，观测结论同样不可信）：那 64ms 是两个 `sceneLoaded` **事件**的间隔，
        ///   叠加式加载下**下一场景会在上一场景还没退出时就开始加载** ⇒ 它不等于真实停留时长。
        ///   r5 的 `OnEnter → OnExit` 实测：**12084ms / 13229ms（两次启动一致，约 12 秒）**
        ///   ⇒ 玩家是对的，Logo 段**必须做跳过**。
        ///
        /// ⛔ **别挂 `LogoScene.OnUpdate`**：RVA 0x11FF70 是**空体**，与
        ///   `OnStartChangeScene`(Slot 6) / `OnDeactiveScene`(Slot 7) **共享同一函数体** ⇒ 挂了也不会有产出。
        ///   ✔ 可用：`OnEnter` 0x3C220 / `OnExit` 0x1BDA70 / `OnActiveScene` 0x3C1D0 / `IsExit` 0x4C150。
        /// </summary>
        [HarmonyPatch(typeof(LogoScene), "OnEnter")]
        private static class LogoEnterPatch
        {
            private static bool _live;
            private static void Postfix(LogoScene __instance)
            {
                try
                {
                    _logoAt = DateTime.Now;
                    if (!_live)
                    {
                        _live = true;
                        QolLog.Warn("opening: Logo 段 hook live（OnEnter，enabled=" + Enabled + "）");
                    }
                    if (!Enabled) return;

                    // 布防（真正的请求在 OnUpdate 里按状态机空闲窗口发，见 TryLogoSkip）
                    _logoArmed = true;
                    _logoReq = 0;
                    _logoLastState = -99;
                    _logoFallback = false;
                    QolLog.Warn("opening: Logo 段进入 ⇒ 已布跳过（目标 TITLE=3，"
                                + "8s 未离开则退让 OPENING_MOVIE=7；r26 恢复直跳，r25 护栏在位；"
                                + "实测本段原长 ~12s）");
                }
                catch (Exception e)
                {
                    QolLog.Warn("opening: Logo OnEnter Postfix 异常 -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(LogoScene), "OnExit")]
        private static class LogoExitPatch
        {
            private static void Postfix(LogoScene __instance)
            {
                try
                {
                    double ms = _logoAt == DateTime.MinValue
                        ? -1.0
                        : (DateTime.Now - _logoAt).TotalMilliseconds;
                    // ⛔ 撤防必须无条件执行（开关中途关掉也要撤），否则进游戏后还会继续发切场景请求。
                    bool wasArmed = _logoArmed;
                    _logoArmed = false;
                    QolLog.Warn("opening: Logo 段 OnExit（本段停留 " + ms.ToString("0")
                                + "ms，请求 " + _logoReq + " 次"
                                + (wasArmed ? "" : "，出段时已撤防") + "）");
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(OpeningMovieScene), "OnEnter")]
        private static class OpeningEnterPatch
        {
            private static bool _live;
            private static void Postfix(OpeningMovieScene __instance)
            {
                try
                {
                    bool first = !_live;
                    _live = true;
                    if (first)
                        QolLog.Warn("opening: OnEnter hook live - " +
                                    (__instance == null ? "(null)" : __instance.GetType().Name)
                                    + "（enabled=" + Enabled + "）");

                    if (__instance == null || !Enabled) return;

                    // ⛔⛔ 序章一律不碰（规则 47）：那是剧情，不是"省时间"能替玩家决定的东西。
                    if (DisableSkip(__instance))
                    {
                        QolLog.Warn("opening: OnEnter 检测到 mDisableSkip=true ⇒ 不跳（完整播放）");
                        return;
                    }

                    // ★ 画面立刻消失的关键：OnEnter 里刚 PlayMovie，这里紧接着 Stop()。
                    //   之后游戏自己的 OnUpdate 每帧都会去调 OnEndMovie ⇒ 状态机一空闲就切 TITLE。
                    //   ⚠ 幂等（铁律 1）：同一实例只停一次。
                    IntPtr p = NativePtr.Of(__instance);
                    if (p != IntPtr.Zero && p == _stoppedPtr) return;
                    _stoppedPtr = p;

                    bool ok = StopMovie();
                    QolLog.Warn("opening: 播片已起播 ⇒ 立刻 MovieManager.Stop()=" + ok
                                + "（画面即时结束，切场景交给游戏自己的收尾逻辑）");
                }
                catch (Exception e)
                {
                    QolLog.Warn("opening: OnEnter Postfix 异常 -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// 主逻辑：播片开始（mPlayMovie=true）后，等到**场景状态机空闲**再替游戏调一次 OnEndMovie()。
        ///
        /// ★★ 这才是本功能真正的突破口（2026-09-21 第三轮取证，前两版全错在这里）：
        ///   `GameSceneManager.ChangeScene(eSceneType)`(0x32B060) 全文**只有 5 条指令**：
        ///        0x32B060  cmp dword [rcx+0x40], 0 ; jne ret   ← mSceneState != DEFAULT ⇒ **直接丢弃**
        ///        0x32B066  mov dword [rcx+0x48], edx           ← mNextSceneType = sceneType
        ///        0x32B069  mov dword [rcx+0x40], 1             ← mSceneState = EXIT_CURRENT
        ///     它根本不切场景，只是**提交一个请求**，真正的切换由 `Update` 状态机消费；
        ///     而且**只要当前不是 DEFAULT 状态，这次请求连排队都不排，直接丢掉**。
        ///   ⇒ 而 `OnEndMovie()` 的全部内容就是这一句 ChangeScene(3)。
        ///     我们在进场后第 3 帧就调用它，那时场景还在 ENTER/WAIT_ENTER（第三轮日志里
        ///     游戏自己也在同几帧调了 3 次，同样被丢）⇒ 之后再也没有人补刀，
        ///     于是 97 秒的 OP 原样播完。这不是"Invoke 没生效"，是**请求被门禁吃了**。
        ///   ⇒ 修法：别急着那一帧，改成轮询——每 30 帧看一次状态机，空了才出手
        ///     （最多 8 次；成功切换后 OnUpdate 不再被调用，自然就停了）。
        ///
        /// ⚠ 幂等（铁律 1）：MelonLoader 0.5.7 下同一调用补丁体会跑两遍，
        ///    这里"同一实例最多触发 N 次"由 _attempts 上限保证。
        /// </summary>
        [HarmonyPatch(typeof(OpeningMovieScene), "OnUpdate")]
        private static class OpeningUpdatePatch
        {
            private const int MaxAttempts = 24;
            private static bool _live;
            private static IntPtr _handled;
            private static int _frames;
            private static IntPtr _invokedPtr;
            private static int _after;
            private static int _attempts;
            private static int _lastState = -99;
            private static bool _noSkipWarned;

            private static void Postfix(OpeningMovieScene __instance)
            {
                try
                {
                    if (__instance == null) return;

                    IntPtr p = NativePtr.Of(__instance);

                    // ⚠ 诊断必须排在 Enabled 判定**之前**（铁律 19）：
                    //   v0.3.0 首轮把 `if (!Enabled) return;` 放在最前面，结果开关没开时
                    //   一行日志都没有 ⇒ 无法区分"没进挂点"与"开关没开"，白猜一轮。
                    if (!_live)
                    {
                        _live = true;
                        QolLog.Warn("opening: OnUpdate hook live - " + __instance.GetType().Name
                                    + " (enabled=" + Enabled + ", prologue="
                                    + (__instance is PrologueOpeningMovieScene) + ")");
                    }

                    if (!Enabled) return;
                    if (p == IntPtr.Zero) return;

                    // ⛔⛔ 游戏自己声明"不可跳"的段落一律放行（2026-09-22 玩家要求）
                    //
                    //   序章 `PrologueOpeningMovieScene.OnInitialize`(0x152190) 里
                    //   `mov byte[rbx+0x11],1` ⇒ **mDisableSkip(0x11) = true**，
                    //   这是官方"这段不许跳"的声明 —— 序章 OP 是**剧情**，跳掉玩家就看不懂演了什么。
                    //   v0.3.0~r4 的实现**没有**这道闸，导致序章被一并跳过（玩家 2026-09-22 指出）。
                    //
                    //   ⚠ 判据用"读游戏自己的状态"而不是硬编码 `is PrologueOpeningMovieScene`：
                    //     将来若再出现别的不可跳段落也自动覆盖
                    //     （规则 33 同款：别在一个开关底下罩死两条语义不同的路）。
                    if (DisableSkip(__instance))
                    {
                        if (!_noSkipWarned)
                        {
                            _noSkipWarned = true;
                            QolLog.Warn("opening: 检测到 mDisableSkip=true（游戏声明不可跳，如序章 OP）"
                                        + " ⇒ 本段**不**自动跳过，完整播放");
                        }
                        return;
                    }

                    // Invoke 之后的跟踪：还在同一实例上跑 ⇒ 大概率请求又被丢了 ⇒ 放开再试。
                    if (_invokedPtr == p)
                    {
                        _after++;
                        if (_after == 60)
                        {
                            QolLog.Warn("opening: Invoke 后 60 帧仍在 OnUpdate"
                                        + "（mPlayMovie=" + PlayMovieOn(__instance, p)
                                        + "，当前 mSceneState=" + SceneStateRaw()
                                        + "）⇒ 请求没被消费，准备重试");
                        }
                        if (_after >= 20 && _attempts < MaxAttempts)
                            _invokedPtr = IntPtr.Zero;     // 放闸，下一轮再来一次
                        return;
                    }

                    if (p != _handled) { _handled = p; _frames = 0; _attempts = 0; _lastState = -99; }
                    if (_attempts >= MaxAttempts) return;

                    // 还没开始播（mPlayMovie=false）→ 不计数、直接等下一帧。
                    // ⚠ 不能在这里"判定失败就永久放弃"：OnEnter 里才 PlayMovie + 置位，
                    //    早于它的那几帧 mPlayMovie 本来就该是 false。
                    if (!PlayMovieOn(__instance, p)) return;

                    _frames++;
                    if (_frames < DelayFrames) return;
                    // r5 是 `% 30`（≈0.5s 一次）⇒ 光等轮询就吃掉半秒，玩家看到的正是"还会播一小会"。
                    // 现在影片已经在 OnEnter 那里停掉了，这里只是兜底补刀 ⇒ 每 2 帧看一次即可。
                    if (_frames % 2 != 0) return;

                    int st = SceneStateRaw();
                    if (st != 0)
                    {
                        // 未知(<0)也算"不可用"之外的情形：给一次机会，避免卡死在永远不行
                        if (st >= 0 && st != _lastState)
                        {
                            _lastState = st;
                            QolLog.Warn("opening: 第 " + _frames + " 帧 mSceneState=" + st
                                        + "（非 DEFAULT）⇒ 此刻 ChangeScene 会被丢弃，继续等");
                        }
                        if (st >= 0) return;
                    }

                    _invokedPtr = p;                    // 先落闸，再干活（防重入）
                    _after = 0;
                    _attempts++;
                    TryEndMovie(__instance);
                }
                catch (Exception e)
                {
                    QolLog.Warn("opening: OnUpdate Postfix 异常 -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }
}
