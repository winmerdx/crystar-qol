using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// v0.2 批次 1：哭泣槽常满（B2）+ Cry Mode 常驻（B3）。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// B2 哭泣槽常满 —— 挂点取证（2026-09-10 反汇编，GameAssembly.dll）
    /// ─────────────────────────────────────────────────────────────────────
    /// 字段：`CharacterParameter.mCryGauge` **0x74（float）** —— 三条独立证据吻合：
    ///   ① dump.cs:164166 字段表 `private float mCryGauge; // 0x74`；
    ///   ② `set_CryGauge`(0x1CF120) 只有一条 `movss [rcx+0x74], xmm1; ret`；
    ///   ③ `SetCryGauge(float)`(0x1CD820) / `AddCryGauge(float)`(0x1CBCA0) 的夹取写回
    ///      都落在 `[rcx+0x74]`。
    ///
    /// 上限：`Global.MAX_CRYGAUGE = 100`，编译为 float 常量 `0x180B03A0C = 100.0f`
    ///   （`AddCryGauge` 0x1CBD4F 与 `SetCryGauge` 0x1CD828 两条 rip 相对取址都解析到它）。
    ///
    /// 三个写入点（callers.py 全量扫描，覆盖"加 / 夹取设值 / 属性直写"三条路）：
    ///   • `AddCryGauge(float)` 0x1CBCA0 —— 入账；调用者仅 3：CharacterComponent.OnCollisionReceive
    ///     （受击 ×2）、CharacterComponent.AddCryGauge()（无参，0x1C3370）、ChargeTask.Run（0x1D1090）。
    ///     ⛔ 方法开头有 `BitFlag.GetBit(mUniqueStateFlag(0x90), 2)` 早退 —— **CryMode 已开时不入账**
    ///     （见 B3：CryMode 就是 mUniqueStateFlag 的 bit 2）。
    ///   • `SetCryGauge(float)` 0x1CD820 —— 夹取 [0,100] 后写；**唯一调用者 = GuardianTimerTask.Run**
    ///     （0x0338A0），即"守护计时器"那条扣减/复位路径。
    ///   • `set_CryGauge(float)` 属性 setter 0x1CF120 —— 裸写 0x74；3 个调用者：
    ///     StellaSaveManager.OnAutoLoadSuccess（读档）、PartyManager.ResetAll、SpecialAttackTask.OnEndSpecialAttackDirection
    ///     （特殊攻击/哭泣演出结束后的复位）。
    ///
    /// 【为什么不选"每帧写字段"】实机教训：每帧强制写会跟游戏自己的槽变化抢控制权
    /// （v0.1.27 光标事故同源），且我们拿不到"玩家那一个 CharacterParameter"的稳定句柄。
    /// 【为什么选"三处 Prefix 把入参改成 100"】三个方法体内部**自己会夹取到 [0,100]**：
    ///   - AddCryGauge：cur + 100 ≥ 100 → 夹取成 100；
    ///   - SetCryGauge：入参 100 → 夹取成 100；
    ///   - set_CryGauge：入参 100 → 直接写 100。
    /// 全是**幂等**改写（同一值写两遍结果相同），所以不需要 ReentryGate（它只防"乘法被平方"，
    /// 而常数覆盖天然幂等 —— 见 ReentryGate 注释的用法 A/B 说明）。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// ⛔ v0.2.1 修正：CryMode 期间**不得**再顶满（实机事故，2026-09-11）
    /// ─────────────────────────────────────────────────────────────────────
    /// 现象：开 B2 → 槽满 → 按 LB 进 CryMode → 放终结技 → 视线恢复正常（演出结束）**但 CryMode
    /// 实际没退**；再按 LB 又能放终结技，放完**整个画面白屏**（游戏仍在跑，能操作、有音效）。
    /// 根因：CryMode 的退出依赖"槽被耗尽"这条路径，而唯一的扣减调用者就是
    /// `GuardianTimerTask.Run` → `SetCryGauge`（见上）。我们无条件把入参顶成 100，
    /// 槽永远到不了 0 ⇒ CryMode 永远退不出 ⇒ 演出状态与逻辑状态不一致 ⇒ 二次触发终结技
    /// 叠一层全屏演出 ⇒ 白屏。
    /// 修法：三处 Prefix 统一加闸门 —— **CryMode 已开就直接放行**，让游戏自己把槽扣完、正常退出。
    /// 退出后 `SpecialAttackTask.OnEndSpecialAttackDirection` 会 `set_CryGauge`（此时 CryMode 已关）
    /// → 我们又能把它顶回 100，"常满"效果不变，但 CryMode 能正常结束。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// B3 Cry Mode 常驻 —— 只调用，不 patch
    /// ─────────────────────────────────────────────────────────────────────
    /// `EventFunction.CryModeInfinity()`（**static int()**，RVA 0xB3F80）是游戏自带的调试/事件接口，
    /// 反汇编确认它做的事：取单例 → `UserParameter.SetScenarioFlag(9,true)` → 事件演出链路 →
    /// 末尾 `PartyManager.OnCry(fromEvent: true)`（0x16B610）。而 `OnCry` 内部：
    ///   `if (!fromEvent) { if (!IsCryGaugeMax()) return false; } ... set_CryMode(true);`
    /// ⇒ **fromEvent=true 时跳过"槽必须满"的闸门**，直接 `set_CryMode(true)`（= mUniqueStateFlag 置 bit 2）。
    ///
    /// ⛔ 按本项目铁律：**静态 IL2CPP 方法禁止 patch/probe**，但**反射调用不受该铁律约束**
    ///   （铁律针对的是 detour 包装器跳过隐藏首参 `MethodInfo*`）。同理 10.10 对
    ///   `StellaInputManager.GetKeyDown` 的结论："不能 patch，但调用可以"。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// ⛔ v0.2.1 修正：重放必须先判 `IsCryModeInfinity()`（实机事故，2026-09-11）
    /// ─────────────────────────────────────────────────────────────────────
    /// 现象：开 B3 后能无限放终结技、不白屏（正常），但**人物身上的光效逐层叠加，慢慢变成光球**，
    /// 最后看不清人。"慢慢"这个时间特征正好对上 5s 节流 —— 原实现每 5s **无条件**重放一次，
    /// 每次重放都再挂一层光环特效。
    /// 判据（反汇编实锤）：`GameManager.IsCryModeInfinity()` 0x2EE60
    ///   = `mParameterManager(0x58)` → `[rax+0x270]`(UserParameter) → `GetScenarioFlag(9)`，
    ///   正是 `CryModeInfinity()` 自己置的那一位（dump.cs:176806 / 174284）。
    /// 修法：节流窗口到了**先查**，只有确认"无限 CryMode 没开"才重放；已开就什么都不做。
    ///
        /// 【撤销】v0.2.2 重写，见 `UndoCryMode` 的详细取证。要点：
        ///   ⛔ `EventFunction.EndCryModeDirection()`（static，0xB4E10）**不是**退出接口 ——
        ///      它只转调 `PartyManager.EndCryModeDirection(0.1, noFade:false)`（0x1B8290），
        ///      且**既不碰 CryMode 位**（set_CryMode 的 4 个调用者里没有它）也**不清 ScenarioFlag(9)**，
        ///      还会在 flag 9 未清时叠一层全屏 fade —— 是"关掉反而白屏"的头号嫌疑。
        ///   ✅ 正解 = 清 flag 9（`GameManager.SetScenarioFlag(9,false)` 0x30280，转发到
        ///      `UserParameter.SetScenarioFlag` 0x1ED510）+ **把槽写 0**，让原生
        ///      `GuardianTimerTask` 走它自己的退出流程（`set_CryMode(false)` + `Finish()`）。
        /// ⚠ 另注：`IFeature.OnReset()` 全项目**没有任何调用点**（2026-09-11 grep 核实），
        ///   所以"关掉即撤销"不能只写在 OnReset 里 —— 本类把撤销放在 OnUpdate 的开关跳变处。
    /// </summary>
    public sealed class FeatureCry : IFeature
    {
        internal static FeatureCry Inst;

        public string Id { get { return "cry"; } }
        public RiskTier Risk { get { return RiskTier.Risky; } }

        /// <summary>Global.MAX_CRYGAUGE（float 常量 0x180B03A0C = 100.0f）。</summary>
        private const float MaxCryGauge = 100f;
        /// <summary>CharacterParameter.mCryGauge 的字段偏移。</summary>
        private const int CryGaugeOffset = 0x74;
        /// <summary>CryModeInfinity 的重放节流（毫秒；被 ResetAll 复位后靠它自行回来）。</summary>
        private const int CryModeReplayMs = 5000;
        /// <summary>CryModeInfinity 置位的剧情旗标编号（9 = "无限 CryMode"）。</summary>
        private const uint InfinityFlagId = 9u;
        /// <summary>
        /// 撤销后给原生路径的宽限时间：超过就说明 GuardianTimerTask 没在跑，强制兜底。
        /// 3s = 60~180 帧，足够计时器跑很多轮。
        /// </summary>
        private const int UndoTimeoutMs = 3000;

        private MelonPreferences_Entry<bool> _gaugeFull;
        private MelonPreferences_Entry<bool> _cryModeAlways;

        /// <summary>最近一次被改写的 CharacterParameter 原生指针（仅用于 OnReset 撤销）。</summary>
        private IntPtr _lastParam;
        private int _lastCryModeAt;
        private bool _cryModeWasOn;
        private static bool _infinityWarned;
        /// <summary>撤销发起时刻（TickCount）；0 = 无待办。用于看门狗判定原生路径是否真的退出了。</summary>
        private static int _undoStartedAt;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            var c = ctx.Cfg;
            // v0.1.3 原则：玩家没动过 mod 之前，一切行为必须与原生游戏一致 → 全部默认关
            _gaugeFull = c.Bool("CryGaugeFull", false, "Cry gauge always full / 哭泣槽常满");
            _cryModeAlways = c.Bool("CryModeAlways", false, "Cry Mode always on (experimental) / CryMode 常驻（实验）");

            Safe(ctx.Harmony, typeof(AddCryGaugePatch), "CharacterParameter.AddCryGauge(float)");
            Safe(ctx.Harmony, typeof(SetCryGaugePatch), "CharacterParameter.SetCryGauge(float)");
            Safe(ctx.Harmony, typeof(CryGaugePropPatch), "CharacterParameter.set_CryGauge(float)");
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("cry: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Enabled
        {
            get { var f = Inst; return f != null && f._gaugeFull != null && f._gaugeFull.Value; }
        }

        private static bool CryModeEnabled
        {
            get { var f = Inst; return f != null && f._cryModeAlways != null && f._cryModeAlways.Value; }
        }

        /// <summary>
        /// B2 闸门用：该角色当前是否处于 CryMode。
        /// 反汇编 `get_CryMode`(0x1CE9C0) = `mov rcx,[rcx+0x90]` → `BitFlag.GetBit(flag, 2)`。
        /// ⛔ 注意 0x90 存的是 **BitFlag 对象引用**，不是 int —— 不能 Marshal 直读，必须走 getter。
        /// </summary>
        private static bool IsCryModeOn(CharacterParameter p)
        {
            if (p == null) return false;
            try { return p.CryMode; }
            catch { return false; }
        }

        /// <summary>记下被改写对象，供 OnReset 撤销（只在功能开启时留句柄，避免无谓持有）。</summary>
        private static void Remember(CharacterParameter param)
        {
            try
            {
                var f = Inst;
                if (f == null || param == null) return;
                f._lastParam = NativePtr.Of(param);
            }
            catch { }
        }

        /// <summary>B3：节流调用游戏自带的"无限 CryMode"调试接口。返回游戏给的 int（日志用）。</summary>
        private static int InvokeStaticInt(string typeName, string methodName, Type type)
        {
            try
            {
                MethodInfo mi = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mi == null)
                {
                    QolLog.Warn("cry: " + typeName + "." + methodName + " 未找到（运行时被裁？）");
                    return -1;
                }
                object r = mi.Invoke(null, null);
                return r == null ? -1 : Convert.ToInt32(r);
            }
            catch (Exception e)
            {
                QolLog.Warn("cry: 调用 " + typeName + "." + methodName + " 失败 -> "
                            + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        /// <summary>
        /// 读 `GameManager.IsCryModeInfinity()`（= UserParameter.GetScenarioFlag(9)）。
        /// 返回 false = **判不了**（调用方此时不得重放：宁可不生效，也不要再叠一层光效）。
        /// </summary>
        private static bool TryGetInfinity(out bool on)
        {
            on = false;
            try
            {
                var gm = GameManager.Instance;
                if (gm == null)
                {
                    if (!_infinityWarned)
                    {
                        _infinityWarned = true;
                        QolLog.Warn("cry: GameManager.Instance 为空，无法判定 IsCryModeInfinity（暂不重放）");
                    }
                    return false;
                }
                on = gm.IsCryModeInfinity();
                return true;
            }
            catch (Exception e)
            {
                if (!_infinityWarned)
                {
                    _infinityWarned = true;
                    QolLog.Warn("cry: IsCryModeInfinity 调用失败 -> " + e.GetType().Name + ": " + e.Message);
                }
                return false;
            }
        }

        /// <summary>
        /// 撤销 B3 —— v0.2.2 重写（实机事故：关掉后不真退出 / 关闭反而白屏）。
        ///
        /// ⛔ v0.2.1 的写法是错的：只调了 `EventFunction.EndCryModeDirection()` + 清 flag 9，
        ///   结果 CryMode **实际没退**（视线/状态都还在），期间再按 LB 放终结技 → 二次演出 → 白屏。
        ///
        /// 反汇编实锤（2026-09-11）：
        ///   ① `CharacterParameter.set_CryMode`(0x1CF130) 只有 **4 个调用者**（callers.py）：
        ///        `GuardianTimerTask.Run`@0x33A87 / `OnCry`@0x16B6F8 / 一个 MoveNext / 一个 Initialize。
        ///      ⇒ **`EndCryModeDirection` 根本不碰 CryMode 位**，它只是演出。
        ///   ② `GuardianTimerTask.Run` 0x33A40~0x33A8C：
        ///        `槽 -= CONST/[rax+0xa8] * dt` → `comiss 0, 槽; jb 跳过`
        ///        → 槽 ≤ 0 时 `set_CryMode(false)`(0x33A87) → 再 `get_CryMode()` 为假时
        ///          `call Finish()`(0x32D40) 收尾。
        ///      ⇒ **唯一真退出 = 计时器把槽扣到 0**。
        ///   ③ `OnCry`(0x16B610) 的 fromEvent 分支（0x16B67F）之后仍会走到
        ///      `AddAlwaysTask`(0x1428B0, 0x16B6CD) ⇒ **计时器一定在跑**，事件进入也不例外。
        ///   ④ `EndCryModeDirection` 还带一个 fade 块（noFade 门在 0x1B8906），
        ///      在 flag 9 未清时调用 = 往已有演出上再叠一层全屏 fade —— 白屏嫌疑点。
        ///
        /// 修法：① 先清 flag 9（否则"无限"仍在，扣槽被跳过）② 把槽直写 0
        ///       ③ 交给原生 GuardianTimerTask 走完整退出（含 Finish 收尾）
        ///       ④ 看门狗：超时仍未退 → 强制 set_CryMode(false)
        ///      **不再调用任何 Direction 演出接口**（不碰演出层 = 不会白屏）。
        /// </summary>
        private static void UndoCryMode()
        {
            // ① 清 flag 9 —— 必须最先做：它是"无限"开关，不清则原生扣槽不会把 CryMode 关掉。
            try
            {
                var gm = GameManager.Instance;
                if (gm == null)
                {
                    QolLog.Warn("cry: 撤销时 GameManager.Instance 为空，无法清 ScenarioFlag(9)");
                }
                else
                {
                    gm.SetScenarioFlag(InfinityFlagId, false);
                    QolLog.Info("cry: 撤销 -> SetScenarioFlag(9,false)");
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("cry: 清 ScenarioFlag(9) 失败 -> " + e.GetType().Name + ": " + e.Message);
            }

            // ② 排空槽 → 让原生计时器下一帧就把 CryMode 关掉（唯一真退出路径，带 Finish 收尾）
            int drained = DrainCryGauge();
            QolLog.Info("cry: 撤销 -> 哭泣槽写 0（CryMode 中角色 " + drained + " 个），等待原生计时器退出");

            _undoStartedAt = Environment.TickCount;
        }

        /// <summary>把所有处于 CryMode 的角色的哭泣槽直写 0（绕过我们自己的 B2 补丁）。返回被写的角色数。</summary>
        private static int DrainCryGauge()
        {
            int n = 0;
            ForEachJoinedParam(p =>
            {
                try
                {
                    if (!p.CryMode) return;
                    var ptr = NativePtr.Of(p);
                    if (ptr == IntPtr.Zero) return;
                    // 直写：不能走 SetCryGauge（会被 B2 的补丁按闸门处理）
                    Marshal.WriteInt32(IntPtr.Add(ptr, CryGaugeOffset), NativePtr.FloatBits(0f));
                    n++;
                }
                catch { }
            });
            return n;
        }

        /// <summary>
        /// 遍历当前在队角色的 CharacterParameter。
        /// 返回 false = **取不到**（GameManager/列表为空或抛异常），调用方不得据此判定"没人开 CryMode"。
        /// </summary>
        private static bool ForEachJoinedParam(Action<CharacterParameter> act)
        {
            try
            {
                var gm = GameManager.Instance;
                if (gm == null) return false;
                var chars = gm.GetJoinedCharacterList();   // List<CharacterComponent>（dump:176824）
                if (chars == null) return false;
                for (int i = 0; i < chars.Count; i++)
                {
                    try
                    {
                        var cc = chars[i];
                        if (cc == null) continue;
                        var p = cc.CharacterParameter;
                        if (p != null) act(p);
                    }
                    catch { }
                }
                return true;
            }
            catch (Exception e)
            {
                QolLog.DebugThrottled("cry.enum", "枚举在队角色失败: " + e.Message);
                return false;
            }
        }

        /// <summary>是否还有角色在 CryMode。返回 false = 判不了（GameManager/列表取不到）。</summary>
        private static bool TryAnyCryModeOn(out bool any)
        {
            bool found = false;   // CS1628：out 参数不能进 lambda，用局部变量中转
            bool ok = ForEachJoinedParam(p =>
            {
                try { if (p.CryMode) found = true; }
                catch { }
            });
            any = found;
            return ok;
        }

        /// <summary>兜底：直接置 `set_CryMode(false)`（0x1CF130，就是计时器退出时调的同一个接口）。</summary>
        private static int ForceClearCryMode()
        {
            int n = 0;
            ForEachJoinedParam(p =>
            {
                try
                {
                    if (!p.CryMode) return;
                    p.CryMode = false;   // 0x1CF130 —— 与计时器退出时调的是同一个接口
                    n++;
                }
                catch { }
            });
            return n;
        }

        /// <summary>撤销看门狗：确认原生路径真的退出了；超时则强制兜底。</summary>
        private static void TickUndo()
        {
            if (_undoStartedAt == 0) return;

            bool any;
            bool judged = TryAnyCryModeOn(out any);
            if (judged && !any)
            {
                QolLog.Info("cry: 撤销完成 —— CryMode 已由原生 GuardianTimerTask 退出");
                _undoStartedAt = 0;
                return;
            }
            if (Environment.TickCount - _undoStartedAt < UndoTimeoutMs) return;

            int n = ForceClearCryMode();
            QolLog.Warn("cry: 原生退出未生效（" + (judged ? "仍在 CryMode" : "状态判不了")
                        + "，已等 " + UndoTimeoutMs + "ms）-> 强制 set_CryMode(false)，角色数 " + n);
            _undoStartedAt = 0;
        }

        public void OnUpdate()
        {
            if (!CryModeEnabled)
            {
                // v0.2.1：IFeature.OnReset() 全项目无调用点，撤销只能在开关跳变处做，
                // 否则关掉 B3 后 CryMode + flag 9 会一直残留（违反"关闭即原生"）。
                if (_cryModeWasOn)
                {
                    _cryModeWasOn = false;
                    UndoCryMode();
                }
                TickUndo();   // v0.2.2：确认原生路径真的退出了，超时则兜底
                return;
            }

            _undoStartedAt = 0;   // 又开了 → 撤销待办作废

            int now = Environment.TickCount;
            // 刚开启 → 立即进入；之后每 CryModeReplayMs 检查一次（对抗 ResetAll / 演出复位）
            if (!_cryModeWasOn || now - _lastCryModeAt >= CryModeReplayMs)
            {
                _lastCryModeAt = now;   // 节流窗口先推进，避免下一帧重复判定

                // v0.2.1：只有确认"无限 CryMode 没开"才重放 —— 无条件重放会逐层叠光效（实机光球）
                if (_cryModeWasOn)
                {
                    bool on;
                    if (!TryGetInfinity(out on)) return;   // 判不了 → 不重放，下个窗口再试
                    if (on) return;                        // 已开 → 不重放
                }

                _cryModeWasOn = true;
                int ret = InvokeStaticInt("EventFunction", "CryModeInfinity", typeof(EventFunction));
                QolLog.Trace("cry.mode", "CryModeInfinity() -> " + ret);
            }
        }

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // 换场景后旧的撤销待办已无意义（角色对象可能已销毁），直接作废避免误兜底
            _undoStartedAt = 0;
        }

        /// <summary>撤销运行时副作用：把哭泣槽写回 0；若 CryMode 是我们开的，收演出并清 flag。</summary>
        public void OnReset()
        {
            if (_cryModeWasOn)
            {
                _cryModeWasOn = false;
                UndoCryMode();
            }

            try
            {
                if (_lastParam != IntPtr.Zero)
                {
                    // 直写 float 0：不经游戏的 set_CryGauge（那会被我们自己的补丁再夹回 100）
                    Marshal.WriteInt32(IntPtr.Add(_lastParam, CryGaugeOffset), NativePtr.FloatBits(0f));
                    QolLog.Info("cry: OnReset -> mCryGauge(0x74) 已写回 0");
                    _lastParam = IntPtr.Zero;
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("cry: OnReset 写回 mCryGauge 失败 " + e.Message);
            }
        }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Bool(Loc.T("f.crygauge"), _gaugeFull, false, RiskTier.Safe, Effect.Immediate);
            yield return Item.Bool(Loc.T("f.crymode"), _cryModeAlways, false, RiskTier.Risky, Effect.Immediate);
        }

        // ───────────────────────── B2：三处写入点，统一把入参顶到上限 ─────────────────────────
        // 三处都是实例方法（rcx=this, xmm1=入参），按名注入对实例方法有效（v0.1.6 实证）。
        // ⛔ v0.2.1：三处都加了 `IsCryModeOn` 闸门 —— CryMode 期间放行，让槽能被扣完、CryMode 能退出。

        /// <summary>AddCryGauge(float addGauge) 0x1CBCA0：mCryGauge = clamp(cur + addGauge, 0, 100)。</summary>
        [HarmonyPatch(typeof(CharacterParameter), "AddCryGauge", new Type[] { typeof(float) })]
        private static class AddCryGaugePatch
        {
            private static bool _live;
            private static void Prefix(CharacterParameter __instance, ref float __0)
            {
                try
                {
                    if (!Enabled) return;
                    if (IsCryModeOn(__instance)) return;   // v0.2.1：CryMode 期间不顶满（本方法本来也会早退）
                    if (!_live) { _live = true; QolLog.Info("cry: AddCryGauge hook live"); }
                    __0 = MaxCryGauge;   // cur + 100 ≥ 100 → 方法内部夹取成 100
                    Remember(__instance);
                }
                catch { }
            }
        }

        /// <summary>SetCryGauge(float gauge) 0x1CD820：夹取到 [0,100] 后写 0x74（GuardianTimerTask 扣减路径）。</summary>
        [HarmonyPatch(typeof(CharacterParameter), "SetCryGauge", new Type[] { typeof(float) })]
        private static class SetCryGaugePatch
        {
            private static bool _live;
            private static void Prefix(CharacterParameter __instance, ref float __0)
            {
                try
                {
                    if (!Enabled) return;
                    // v0.2.1 关键闸门：这是 CryMode 扣槽的唯一路径（GuardianTimerTask.Run），
                    // 顶满 = CryMode 永远退不出 = 二次终结技白屏。
                    if (IsCryModeOn(__instance)) return;
                    if (!_live) { _live = true; QolLog.Info("cry: SetCryGauge hook live"); }
                    __0 = MaxCryGauge;
                    Remember(__instance);
                }
                catch { }
            }
        }

        /// <summary>属性 setter set_CryGauge(float value) 0x1CF120：裸写 0x74（读档 / ResetAll / 特殊攻击结束复位）。</summary>
        [HarmonyPatch(typeof(CharacterParameter), "set_CryGauge", new Type[] { typeof(float) })]
        private static class CryGaugePropPatch
        {
            private static bool _live;
            private static void Prefix(CharacterParameter __instance, ref float __0)
            {
                try
                {
                    if (!Enabled) return;
                    if (IsCryModeOn(__instance)) return;   // v0.2.1：CryMode 期间放行
                    if (!_live) { _live = true; QolLog.Info("cry: set_CryGauge hook live"); }
                    __0 = MaxCryGauge;
                    Remember(__instance);
                }
                catch { }
            }
        }
    }
}
