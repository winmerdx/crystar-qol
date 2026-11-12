using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace CrystarQoL
{
    /// <summary>
    /// 菜单打开时让游戏"停手"，避免调 mod 数值的同时角色还在响应按键。
    ///
    /// ===================================================================
    /// 【背景：为什么不能常驻】
    ///
    /// 2026-09-08 实测：本闸门对 Common.InputManager(82) + Game.StellaInputManager(13)
    /// 共 95 个静态输入查询方法做 Harmony 批量 detour 后，**游戏全部输入失效**——
    /// 连标题画面的"按任意键开始"都无响应，键盘鼠标手柄全死。
    /// 关键判定：该失效与 Suppress 是否为 true 无关（用户从未按 F2，菜单从未打开，
    /// Suppress 恒为 false），即不是"闸门开着掐了输入"，而是 **detour 常驻本身
    /// 破坏了 IL2CPP 侧的调用路径**。去掉闸门 → 按键立即恢复，A/B 结论确凿。
    /// 同期对业务方法（DamageHp 等）的 Harmony patch 一切正常，所以是
    /// "避开静态输入 getter 这个类别"的问题，不是"别用 Harmony"。
    ///
    /// ===================================================================
    /// 【v0.5 方案：按需装卸，而不是常驻】
    ///
    /// 既然常驻会坏，那就只在菜单打开的那一段时间挂着：
    ///   按 F2 开菜单  → 装上拦截（游戏输入被掐，正是我们要的）
    ///   按 F2 关菜单  → 立刻撤销（输入必须恢复原状）
    ///
    /// 提供三种模式，由标记文件选择，便于实机 A/B 出结论：
    ///
    ///   gate.pause  → Pause 模式【推荐】：菜单打开时把 Common.Global.GameSpeed
    ///                 压到极低值，冻结游戏逻辑帧。**完全不碰 Harmony**，零 detour
    ///                 风险；角色几乎不动、敌人也一起停，效果接近且更安全。
    ///   gate.patch  → Patch 模式【实验性】：动态装卸 95 个 detour（拦截最彻底，
    ///                 但依赖"Unpatch 后输入能恢复"这个尚未证实的假设）
    ///   两者都无    → Off（默认）：不做任何拦截，菜单开着时操作会穿透
    ///
    /// 标记文件放 UserData/Crystar.QoL/ 下，改完重启游戏生效。
    /// 静态 IL2CPP 方法禁止 patch/probe（游戏自身从未调用过的方法运行期会被裁剪）。
    /// ===================================================================
    /// </summary>
    public static class GameInputGate
    {
        public enum GateMode { Off, Patch, Pause }

        private const string MarkerPatch = "gate.patch";
        private const string MarkerPause = "gate.pause";

        /// <summary>菜单期间的 GameSpeed。用极小值而非 0：DeltaTime 若为 0，
        /// 任何 "x / DeltaTime" 都会变成 NaN（文档 2.4.1：DeltaTime ≈ SystemDeltaTime × GameSpeed）。</summary>
        private const float PauseSpeed = 0.0001f;

        private static readonly string[] TargetTypes =
        {
            "Common.InputManager",
            "Game.StellaInputManager"
        };

        private static readonly HashSet<string> FloatAxes = new HashSet<string>
        {
            "get_Horizontal", "get_Vertical",
            "get_Horizontal2", "get_Vertical2",
            "get_PadHorizontal", "get_PadVertical",
            "GetCameraHorizon", "GetCameraVertical"
        };

        /// <summary>独立 Harmony 实例：卸载时 UnpatchAll 只清闸门自己的 patch，
        /// 不会误伤各 Feature 打在业务方法（DamageHp 等）上的补丁。</summary>
        private static HarmonyLib.Harmony _gate;

        private static GateMode _mode = GateMode.Off;
        private static bool _active;
        private static PropertyInfo _piGameSpeed;
        private static float _savedGameSpeed = 1f;
        private static bool _gameSpeedSaved;
        private static bool _frozenWarned;   // v0.3.1-r11：冻结残留告警（一次性）

        public static bool Suppress;
        public static int PatchedCount;

        /// <summary>本次会话的模式。</summary>
        public static GateMode Mode { get { return _mode; } }

        /// <summary>闸门当前是否生效（菜单开着）。</summary>
        public static bool Active { get { return _active; } }

        /// <summary>是否启用了任一种拦截模式（Mode != Off）。</summary>
        public static bool Enabled { get { return _mode != GateMode.Off; } }

        /// <summary>
        /// 启动时调用：读标记文件决定模式，并做必要的准备。
        /// 此阶段【不会】安装任何 patch，也不改 GameSpeed。
        /// </summary>
        public static void Init()
        {
            bool wantsPatch = Marker(MarkerPatch);
            bool wantsPause = Marker(MarkerPause);

            if (wantsPatch && wantsPause)
                QolLog.Warn("input gate: both gate.patch and gate.pause exist -> using PATCH mode");

            _mode = wantsPatch ? GateMode.Patch
                  : wantsPause ? GateMode.Pause
                  : GateMode.Off;

            switch (_mode)
            {
                case GateMode.Off:
                    QolLog.Info("input gate: OFF (no gate.* marker) - 菜单打开时游戏操作会穿透。"
                              + " 需要拦截请建 gate.pause(推荐,零patch) 或 gate.patch(实验性)");
                    return;

                case GateMode.Patch:
                    _gate = new HarmonyLib.Harmony(QolConfig.HarmonyId + ".inputgate");
                    QolLog.Warn("input gate: PATCH mode (experimental) - 开菜单时装 95 个 detour，"
                              + "关菜单时 Unpatch。**若关掉菜单后按键无反应，立刻删除 gate.patch**");
                    return;

                case GateMode.Pause:
                    PrepareGameSpeed();
                    QolLog.Info("input gate: PAUSE mode - 开菜单时冻结游戏逻辑帧(GameSpeed=" + PauseSpeed
                              + ")，不涉及任何 Harmony patch"
                              + (_piGameSpeed == null ? "  [WARN] GameSpeed 属性未找到，本模式无效！" : ""));
                    return;
            }
        }

        /// <summary>菜单开关时调用：按需装卸拦截。</summary>
        public static void SetActive(bool on)
        {
            if (_mode == GateMode.Off) return;
            if (on == _active) return;
            _active = on;

            try
            {
                if (on)
                {
                    if (_mode == GateMode.Patch) { InstallPatches(); Suppress = true; }
                    else PauseGame(true);
                    // ⚠ v0.3.1-r11：升级为 Warn（Debug 级在默认 Level 下不落盘，事后查不到是谁冻的）
                    QolLog.Warn("input gate: engaged (" + _mode + ")");
                }
                else
                {
                    if (_mode == GateMode.Patch) { UninstallPatches(); Suppress = false; }
                    else PauseGame(false);
                    QolLog.Warn("input gate: released (" + _mode + ")");
                }
            }
            catch (Exception e)
            {
                QolLog.Error("input gate: " + (on ? "engage" : "release") + " failed: "
                             + e.GetType().Name + ": " + e.Message);
                // 出错时朝"不拦截"的方向收敛：宁可穿透，也不能把输入锁死
                if (!on) { Suppress = false; PauseGame(false); }
            }
        }

        /// <summary>退出/总关时兜底：确保一定释放。</summary>
        public static void ForceRelease()
        {
            if (!_active) return;
            _active = false;
            Suppress = false;
            try { if (_mode == GateMode.Patch) UninstallPatches(); else PauseGame(false); }
            catch { }
        }

        /// <summary>
        /// v0.3.1-r12：只读 GameSpeed（看门狗用）。读不到返回 -1。
        /// ⚠ 这是**取证**用的：标题菜单出不来的那类故障，第一嫌疑就是"游戏逻辑帧被冻结"，
        ///   而 mod 的 OnUpdate 是真实时间驱动、日志照打 ⇒ 只能靠主动读值来区分
        ///   "游戏被冻住" 与 "UI 自己卡住"。
        /// </summary>
        public static float ReadGameSpeed()
        {
            if (_piGameSpeed == null) return -1f;
            try { return Convert.ToSingle(_piGameSpeed.GetValue(null)); }
            catch { return -1f; }
        }

        /// <summary>
        /// v0.3.1-r11：冻结残留自愈（配 ModMain 每 60 帧一次）。
        ///
        /// 【为什么需要】PAUSE 模式靠 `Common.Global.GameSpeed = 0.0001` 冻结游戏逻辑帧。
        /// 一旦"菜单开着时切场景 / 异常打断 / 状态残留"，`_active` 与实际 GameSpeed 就可能脱节，
        /// 结果：**游戏逻辑帧永久冻结** —— 标题菜单的加载/淡入全靠逻辑帧推进 ⇒ 表现为
        /// "菜单加载不出来、按什么都没反应"，只能杀进程。
        /// 而 mod 自己的 `OnUpdate` 是真实时间驱动，照样每 2 秒打心跳 ⇒ **日志里看不出任何异常**，
        /// 这也是这类故障最难查的地方（2026-09-24 用户实机反馈）。
        ///
        /// 【做什么】闸门没生效却读到极小 GameSpeed ⇒ 立刻写回原速（默认 1）并告警。
        /// </summary>
        public static void SelfCheck()
        {
            if (_mode != GateMode.Pause || _piGameSpeed == null) return;
            if (_active) return;                       // 闸门正开着 ⇒ 冻着是应该的
            try
            {
                float cur = Convert.ToSingle(_piGameSpeed.GetValue(null));
                if (cur >= 0.5f) { _frozenWarned = false; return; }

                float back = _gameSpeedSaved ? _savedGameSpeed : 1f;
                _piGameSpeed.SetValue(null, back);
                _gameSpeedSaved = false;
                if (!_frozenWarned)
                {
                    _frozenWarned = true;
                    QolLog.Warn("input gate: 检测到 GameSpeed 残留冻结(" + cur.ToString("0.####")
                                + ") ⇒ 已写回 " + back.ToString("0.####") + "（闸门未开却冻着 = 会卡住 UI）");
                }
            }
            catch (Exception e)
            {
                QolLog.DebugThrottled("gate.selfcheck", "input gate: SelfCheck -> " + e.Message);
            }
        }

        // ---- Patch 模式 ----

        private static void InstallPatches()
        {
            if (_gate == null) return;

            var postBool = typeof(GameInputGate).GetMethod("PostfixBool", BindingFlags.Public | BindingFlags.Static);
            var postFloat = typeof(GameInputGate).GetMethod("PostfixFloat", BindingFlags.Public | BindingFlags.Static);
            if (postBool == null || postFloat == null)
            {
                QolLog.Error("input gate: patch methods missing");
                return;
            }

            int total = 0;
            foreach (var typeName in TargetTypes)
            {
                var t = FindType(typeName);
                if (t == null)
                {
                    QolLog.Warn("input gate: type not found -> " + typeName);
                    continue;
                }

                int n = 0;
                MethodInfo[] methods;
                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static); }
                catch (Exception e) { QolLog.Warn("input gate: " + typeName + " -> " + e.Message); continue; }

                foreach (var m in methods)
                {
                    if (m.IsGenericMethod || m.ContainsGenericParameters) continue;

                    bool isBool = m.ReturnType == typeof(bool);
                    bool isFloat = m.ReturnType == typeof(float);
                    if (!isBool && !isFloat) continue;

                    bool want = isBool ? IsInputQuery(m.Name) : FloatAxes.Contains(m.Name);
                    if (!want) continue;

                    try
                    {
                        _gate.Patch(m, postfix: new HarmonyMethod(isBool ? postBool : postFloat));
                        n++;
                    }
                    catch (Exception e)
                    {
                        QolLog.Debug("input gate: skip " + m.Name + " -> " + e.Message);
                    }
                }

                total += n;
                QolLog.Debug("input gate: " + typeName + " patched " + n + " methods");
            }

            PatchedCount = total;
        }

        private static void UninstallPatches()
        {
            if (_gate == null) return;
            try
            {
                // 只清本实例（独立 Harmony ID），Feature 的补丁不受影响。
                // 注意：不能用静态 UnpatchAll()——那会清掉所有实例（含 Feature）的补丁。
                _gate.UnpatchSelf();
                PatchedCount = 0;
            }
            catch (Exception e)
            {
                QolLog.Error("input gate: UnpatchAll failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---- Pause 模式 ----

        private static void PrepareGameSpeed()
        {
            var t = FindType("Common.Global");
            if (t == null)
            {
                QolLog.Warn("input gate: type Common.Global not found");
                return;
            }
            try
            {
                _piGameSpeed = t.GetProperty("GameSpeed", BindingFlags.Public | BindingFlags.Static);
                if (_piGameSpeed == null)
                    QolLog.Warn("input gate: Common.Global.GameSpeed property not found");
            }
            catch (Exception e)
            {
                QolLog.Warn("input gate: GameSpeed lookup failed: " + e.Message);
            }
        }

        private static void PauseGame(bool on)
        {
            if (_piGameSpeed == null) return;
            try
            {
                if (on)
                {
                    if (!_gameSpeedSaved)
                    {
                        float cur0 = Convert.ToSingle(_piGameSpeed.GetValue(null));
                        // ⛔ v0.3.1-r11：别把"已经冻住的值"当成原速存起来
                        //   （否则恢复时写回的就是 0.0001 ⇒ 永久冻结，表现为标题菜单加载不出、按键无反应）。
                        _savedGameSpeed = cur0 < 0.5f ? 1f : cur0;
                        _gameSpeedSaved = true;
                    }
                    _piGameSpeed.SetValue(null, PauseSpeed);
                }
                else if (_gameSpeedSaved)
                {
                    _piGameSpeed.SetValue(null, _savedGameSpeed);
                    _gameSpeedSaved = false;
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("input gate: GameSpeed " + (on ? "freeze" : "restore") + " failed: " + e.Message);
                _gameSpeedSaved = false;
            }
        }

        // ---- 工具 ----

        private static bool Marker(string name)
        {
            try { return File.Exists(Path.Combine(QolConfig.Dir, name)); }
            catch { return false; }
        }

        private static bool IsInputQuery(string name)
        {
            if (name.StartsWith("get_Is")) return true;
            if (name.StartsWith("GetKey")) return true;   // GetKey / GetKeyDown / GetKeyRelease
            if (name == "IsAny") return true;
            if (name == "IsCameraReset") return true;
            return false;
        }

        private static Type FindType(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            string ns = dot < 0 ? "" : fullName.Substring(0, dot);
            string name = dot < 0 ? fullName : fullName.Substring(dot + 1);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false, false); } catch { continue; }
                if (t != null) return t;

                // 兜底：按 名字+命名空间 扫，防止 unhollowed 程序集里命名空间有出入
                try
                {
                    foreach (var cand in asm.GetTypes())
                    {
                        if (cand.Name != name) continue;
                        if (ns.Length > 0 && cand.Namespace != ns) continue;
                        return cand;
                    }
                }
                catch { }
            }
            return null;
        }

        public static void PostfixBool(ref bool __result)
        {
            if (Suppress) __result = false;
        }

        public static void PostfixFloat(ref float __result)
        {
            if (Suppress) __result = 0f;
        }
    }
}
