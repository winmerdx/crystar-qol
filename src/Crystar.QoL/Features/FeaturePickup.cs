using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// v0.2 批次 2：自动拾取掉落（B5）。**v0.2.7 修掉"整段从未跑起来"的真根因**。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// v0.2.3 / v0.2.4 为什么完全没效果（实机取证，2026-09-12）
    /// ─────────────────────────────────────────────────────────────────────
    /// 两版都挂 `DropItemManager.Update()`（0xA7C60）的 Postfix —— 而**这个方法根本不会被调用**：
    ///   • `callers.py` 全量扫描：0 个调用者（引擎驱动型，本不该 0 才对）；
    ///   • 实机 debug.log 整个会话里 `pickup:` **一行都没有**（我们写了"钩子首触无条件打日志"）；
    ///     ⇒ 结论：**换驱动源**，不跟这个方法纠缠。
    ///
    /// `DropItemComponent`（: FieldComponent，dump.cs:158324）字段：
    ///   • `mState` **0x130**（eState：0 INVALID / 1 APPEARANCE / 2 DROP / 3 EXTINCTION / 4 SUCTION）
    ///   • `mItemID` 0x138、`mCount` 0x140
    ///   • 它自己的每帧逻辑在 **`OnUpdate()`（Slot 9，0xA68F0）**——这游戏用自定义 OnUpdate 驱动。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// v0.2.5 改"自驱"后**仍然一行日志都没有** —— v0.2.7 才找到真根因
    /// ─────────────────────────────────────────────────────────────────────
    /// 2026-09-12 的 debug.log（v0.2.5 + v0.2.6 各若干局，AutoPickup=ON 的会话也在）里
    /// `pickup:` **依然是 0 条**，而 v0.2.6 的代码是"首次扫描**无论成败**都打一条 Info"。
    /// ⇒ 说明 `QolLog.Info(...)` 那一行**根本没被执行到**。为什么：
    ///
    /// ⛔⛔ **本工程编译期引用的是 Il2CppDumper 的 DummyDll，那里 `List&lt;T&gt;` 就是
    ///     `System.Collections.Generic.List&lt;T&gt;`；运行期游戏里的却是
    ///     `Il2CppSystem.Collections.Generic.List&lt;T&gt;` —— 两者不是同一个类型。**
    ///   同一坑本项目已踩过两次并在别处修好：见 `FeatureAlbum` 类注释、`FeatureDrops.DropRatePatch`
    ///   的 `__result` 必须声明成 `object` 那段。而本文件当时写的是：
    ///     `var dm = DropItemManager.Instance;  ...  var list = dm.DropItemList;`
    ///   ① `dm.DropItemList` 的属性 getter 在 DummyDll 里返回 `System.List&lt;DropItemComponent&gt;`，
    ///      运行期返回 `Il2CppSystem.List&lt;DropItemComponent&gt;` → 直接调用/接类型会炸；
    ///   ② 更要命的是**日志那一行自己就带毒**：
    ///      `QolLog.Info("...掉落表=" + (list != null ? list.Count + " 项" : "null") + ...)`
    ///      —— `list.Count` 是 `System.List` 实例方法，对 Il2CppSystem 列表恒抛；
    ///   ③ 而 `_liveSeen = true;` 写在 `QolLog.Info(...)` **之前** ⇒ 异常在求参时抛出、
    ///      `QolLog.Info` 从未被调用，但 `_liveSeen` 已是 true → **此后永不重试、永不打印**；
    ///   ④ 外层 `catch` 用的是 `QolLog.DebugThrottled`（LogLevel=1 时**静默**）⇒ 全程无痕。
    ///   ⇒ 观感就是"功能完全无效 + 日志什么都没有"。**这是"引用了运行期不存在的成员"的又一例**
    ///     （静态 IL2CPP 方法在运行期会被裁剪）。`DropItemManager.Instance` 本身是好的（`Common.SingletonMonoBehaviour&lt;T&gt;`
    ///     的泛型实例有 RVA，和 `ParameterManager.Instance` 同款，后者实机一直在用）。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// v0.2.7 实现：**一律 object 接收 + 反射枚举**（本项目验证过的唯一安全姿势）
    /// ─────────────────────────────────────────────────────────────────────
    ///   • `GetMember(dm, "DropItemList")` → object（不写 `List&lt;T&gt;` 类型）
    ///   • `CountOf` / `ItemOf` 走反射 `get_Count` / `get_Item(int)`（与 FeatureAlbum 同款）
    ///   • 元素本身是**游戏类**（`Game.DropItemComponent`），unhollower 映射正确 →
    ///     `as DropItemComponent` 之后 `State` / `StartSuction` 都能正常用
    ///   • 首扫日志**放在所有危险调用之前**，且抛异常时另走 Warn 级一次性日志（不再静默）
    ///
    /// `StartSuction` 0xA6EF0 全量反汇编（27 条指令）—— **内部没有任何距离判定**：
    /// ```
    /// 0xA6EF6  cmp dword ptr [rdi + 0x130], 2   ; state == DROP ?
    /// 0xA6F1B  jne 0xA6F7C                       ; 不是就直接返回（天然幂等）
    /// 0xA6F29  call 0x9ABD00                     ; new <吸附任务>
    /// 0xA6F36  call 0xA5250                      ;   任务 .ctor
    /// 0xA6F44  call 0x142980                     ; StartTask(this, 任务, null)  ← 起吸附任务
    /// 0xA6F56  call 0x926B0 (this, 2)            ; 字段写入
    /// 0xA6F68  call 0x926B0 (this, 0x20)         ;
    /// 0xA6F72  mov dword ptr [rdi + 0x130], 4    ; state = SUCTION
    /// ```
    /// ⇒ 只要 state 是 DROP，调用它就会把该掉落物**从任何位置**拉向玩家（并保留吸附动画/音效，
    ///   入账仍走原生 `GetItem`→`AddItem` 流程，不会"凭空消失"）。这正是用户要的**全图吸**。
    ///
    /// 【为什么不改 `SUCTION_DISTANCE = 6`】它是 .rdata 常量池里的共享 6.0f（`movss [rip+…]`），
    ///   改地址等于改全程序所有 6.0f → 不动它，改在"谁去调 StartSuction"这一层。
    ///
    /// 【幂等性】`StartSuction` 第一件事就是 `cmp state,2; jne 返回` → 同一帧/连续多帧重复调用
    ///   不会重复入账，所以我们的 100ms 扫描不需要额外去重。
    /// </summary>
    public sealed class FeaturePickup : IFeature
    {
        internal static FeaturePickup Inst;

        public string Id { get { return "pickup"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        /// <summary>玩家角色缓存刷新间隔（毫秒）。</summary>
        private const int PlayerRefreshMs = 2000;
        /// <summary>拾取扫描节流（毫秒）。每帧扫没必要，100ms 足够跟手。</summary>
        private const int ScanIntervalMs = 100;

        /// <summary>
        /// 拾取半径档位（游戏世界单位）。
        /// **0 = 全圖（不限距离）**，这是默认值，也是用户明确要的效果；
        /// 6 = 原生 `SUCTION_DISTANCE`（反汇编实锤的那个 6.0f）。
        /// 档位 ≤4 个 → 营地页是"确定键直接切档"的纯文本行（&gt;4 档会退化成需要先进调整态的滑块）。
        /// </summary>
        internal static readonly int[] RangeOptions = new int[] { 0, 6, 20, 50 };

        /// <summary>档位文本（营地页标题右侧）。0 显示"全圖"——必须用繁体字，游戏字体才有字形。</summary>
        internal static string RangeText(int v)
        {
            return v <= 0 ? "全圖" : v + "m";
        }

        private MelonPreferences_Entry<bool> _auto;
        private MelonPreferences_Entry<int> _range;

        private static CharacterComponent _player;
        private static int _playerAt;
        private static int _lastScanAt;
        private static bool _liveSeen;          // 首次真正执行扫描时打一条"在工作"日志
        private static int _idleTicks;          // 空扫描计数（节流打印，便于诊断）
        private static bool _warned;            // 异常只打一次（Warn 级，绝不静默）

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            // v0.1.3 原则：默认关
            _auto = ctx.Cfg.Bool("AutoPickup", false, "Auto vacuum drops / 自动拾取掉落");
            _range = ctx.Cfg.Int("PickupRange", 0, "Auto pickup radius in game units, 0 = whole map / 自动拾取半径（0=全图）");
        }

        private static bool Enabled
        {
            get { var f = Inst; return f != null && f._auto != null && f._auto.Value; }
        }

        /// <summary>
        /// 当前拾取半径（游戏世界单位）；**0 = 全圖**。
        /// 兼容 v0.2.2~v0.2.4 存下的老值（6/12/20/30，已不在档位表里）→ 吸附到最近档并写回，配置自愈。
        /// </summary>
        internal static int Range
        {
            get
            {
                var f = Inst;
                if (f == null || f._range == null) return 0;
                int v = f._range.Value;
                if (Array.IndexOf(RangeOptions, v) >= 0) return v;
                int best = RangeOptions[0], bestD = int.MaxValue;
                for (int i = 0; i < RangeOptions.Length; i++)
                {
                    int d = Math.Abs(RangeOptions[i] - v);
                    if (d < bestD) { bestD = d; best = RangeOptions[i]; }
                }
                f._range.Value = best;
                return best;
            }
            set { var f = Inst; if (f != null && f._range != null) f._range.Value = value; }
        }

        /// <summary>
        /// 每帧驱动（v0.2.5 起自驱；v0.2.7 改反射访问）。
        /// ModMain 每帧调所有 feature 的 OnUpdate，我们在这里扫掉落表 —— 不依赖任何游戏侧钩子。
        /// </summary>
        public void OnUpdate()
        {
            try
            {
                if (!Enabled) return;

                int now = Environment.TickCount;
                if (now - _lastScanAt < ScanIntervalMs) return;
                _lastScanAt = now;

                // ⛔ 顺序很重要：先把"取对象"的每一步各自 try 住，**任何一步都不能让日志打不出来**。
                //    旧版把 `list.Count` 写进日志参数里 → 它先抛异常 → 日志那行永远执行不到（见类注释）。
                object dm = null, list = null;
                string err = null;
                try { dm = DropItemManager.Instance; }
                catch (Exception e) { err = "DropItemManager.Instance: " + e.GetType().Name + ": " + e.Message; }
                if (dm != null)
                {
                    try { list = GetMember(dm, "DropItemList"); }
                    catch (Exception e) { err = "读 DropItemList: " + e.GetType().Name + ": " + e.Message; }
                }
                int listN = CountOf(list);
                var player = Player();

                // v0.2.7：这一行**必然**打在危险动作之前（v0.2.6 只是"无论成败"，却排在求值之后）。
                if (!_liveSeen)
                {
                    _liveSeen = true;
                    QolLog.Info("pickup: 自驱首次扫描 — DropItemManager=" + (dm != null)
                                + "，掉落表=" + (list == null ? "null" : listN + " 项")
                                + "，玩家=" + (player != null)
                                + "，半径 " + RangeText(Range)
                                + (err != null ? "，⚠ " + err : ""));
                }
                if (err != null && !_warned)
                {
                    _warned = true;
                    QolLog.Warn("pickup: 取对象失败 -> " + err);
                }

                if (list == null || listN <= 0) return;     // 营地/过场没有掉落物 → 什么都不做
                if (player == null) return;                 // 没有玩家就没有吸附目标（不空转）

                int r = Range;
                float r2 = r > 0 ? (float)r * r : -1f;      // r <= 0 → 不限距离
                int n = 0;
                float nearest = float.MaxValue;

                for (int i = 0; i < listN; i++)
                {
                    try
                    {
                        var c = ItemOf(list, i) as DropItemComponent;
                        if (c == null) continue;
                        // 只推 DROP 态（StartSuction 内部还会再查一次，这里查是为了少一次跨托管调用）
                        if (c.State != DropItemComponent.eState.DROP) continue;

                        if (r2 > 0f)
                        {
                            float d2 = DistSqrXZ(player, c);
                            if (d2 < nearest) nearest = d2;
                            if (d2 > r2) continue;          // 限定半径时：超出不吸
                        }

                        c.StartSuction(player);             // 幂等：state!=DROP 时它自己会返回
                        n++;
                    }
                    catch { }
                }

                if (n > 0)
                {
                    QolLog.Trace("pickup", "吸附 " + n + " 个（半径 " + RangeText(r) + "，列表 " + listN + "）");
                }
                else if ((_idleTicks++ % 100) == 0)
                {
                    QolLog.Trace("pickup", "无可吸（半径 " + RangeText(r) + "，列表 " + listN
                                 + (r2 > 0f ? "，最近 " + Fmt(nearest) : "") + "）");
                }
            }
            catch (Exception e)
            {
                // v0.2.7：不再用 Debug 级（LogLevel=1 时静默 → 白丢两轮实测），异常一次性 Warn 报出来
                if (!_warned)
                {
                    _warned = true;
                    QolLog.Warn("pickup: 扫描抛出 -> " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            // 换场景后旧角色对象已失效，必须重取
            _player = null;
            _playerAt = 0;
        }

        /// <summary>无持久副作用：关掉后掉落物恢复"走近才吸"的原生行为（原生其实也不吸，见类注释）。</summary>
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Bool(Loc.T("f.autopickup"), _auto, false, RiskTier.Safe, Effect.Immediate);
            yield return new MenuItem
            {
                Label = Loc.T("f.pickuprange"),
                ValueText = () => RangeText(Range),
                Adjust = d =>
                {
                    int i = Array.IndexOf(RangeOptions, Range);
                    if (i < 0) i = 0;
                    i = (i + (d > 0 ? 1 : -1) + RangeOptions.Length) % RangeOptions.Length;
                    Range = RangeOptions[i];
                },
                Reset = () => Range = 0
            };
        }

        /// <summary>
        /// 取当前操作中的玩家角色。取不到时**沿用旧缓存**（不返回 null），
        /// 避免"某一帧取不到就整个功能停摆"。
        ///
        /// ⛔ `GameManager.GetJoinedCharacterList()` 返回 `List&lt;CharacterComponent&gt;` —— **DummyDll 的
        ///   `System.List` ≠ 运行期的 `Il2CppSystem.List`**（同 DropItemList）⇒ 必须 object + 反射枚举，
        ///   否则这里每帧抛异常（旧版就是被 Debug 级 catch 吞掉，观感"玩家永远取不到"）。
        /// </summary>
        internal static CharacterComponent Player()
        {
            int now = Environment.TickCount;
            if (_player != null && now - _playerAt < PlayerRefreshMs) return _player;
            _playerAt = now;
            try
            {
                var gm = GameManager.Instance;
                if (gm == null) return _player;
                // ★ 用反射调用而不是 `gm.GetJoinedCharacterList()`：
                //   直接调用会把 DummyDll 的返回类型 System.List 编进 IL，运行期对不上（见类注释）。
                object chars = InvokeMember(gm, "GetJoinedCharacterList");
                int n = CountOf(chars);
                for (int i = 0; i < n; i++)
                {
                    var cc = ItemOf(chars, i) as CharacterComponent;
                    if (cc == null) continue;
                    bool isPlayer = false;
                    try { isPlayer = cc.IsPlayerCharacter(); } catch { }
                    if (isPlayer) { _player = cc; return _player; }
                }
            }
            catch (Exception e)
            {
                QolLog.DebugThrottled("pickup.player", "取玩家角色失败: " + e.Message);
            }
            return _player;
        }

        /// <summary>XZ 平面平方距离（与原生 GetDistanceXZSqr 同口径）。失败返回 float.MaxValue。</summary>
        private static float DistSqrXZ(CharacterComponent a, DropItemComponent b)
        {
            try
            {
                var pa = a.transform.position;
                var pb = b.transform.position;
                float dx = pa.x - pb.x;
                float dz = pa.z - pb.z;
                return dx * dx + dz * dz;
            }
            catch { return float.MaxValue; }
        }

        private static string Fmt(float sqr)
        {
            if (sqr == float.MaxValue) return "—";
            return Math.Sqrt(sqr).ToString("F1");
        }

        // ------------------------------------------------------------------
        // 反射工具（**一律 object 接收**，绝不写 List<T> —— DummyDll≠运行时）
        // ------------------------------------------------------------------

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
            {
                try { return p.GetValue(obj, null); } catch { }
            }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { try { return f.GetValue(obj); } catch { } }
            return null;
        }

        private static object InvokeMember(object obj, string method)
        {
            if (obj == null) return null;
            try
            {
                var mi = obj.GetType().GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return mi != null ? mi.Invoke(obj, null) : null;
            }
            catch { return null; }
        }

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

        /// <summary>反射取列表第 i 项（返回 object；调用方再 `as 游戏类型`）。</summary>
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
    }
}
