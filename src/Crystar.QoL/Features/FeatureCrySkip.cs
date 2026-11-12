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
    /// v0.3 批次 2 —— D9 幽怨演出（净化 / 调合 / 强化 / 变容）自动跳过。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 取证（2026-09-20，GameAssembly.dll 机器码 + dump.cs）
    /// ─────────────────────────────────────────────────────────────────────
    /// 演出链路（唯一入口，callers.py 扫 `UIRoomCryTask.Set` 只有 2 个调用者）：
    ///   UIRoomCryTask.Set(eCryType, callback)          0x1A5A50   (dump:183742)
    ///     └─ UIRoomCryTask.Initialize(TaskComponent)   0x1A56D0
    ///          └─ RoomManager.PlayTimelineToCry(type)  0x15C660   ← 演出入口（public 实例）
    ///               ├─ mIsCry(0x90) = true
    ///               ├─ mIsCrySkip(0xC4) = false         ← 每场演出重置
    ///               ├─ mCryType(0x84) = type
    ///               └─ mUpdateState(0x9C) = WaitFade(1)
    ///                    └─ … → PlayTimeline(5) → RoomManager.PlayTimeline() 0x15C790
    ///
    /// ★ 游戏自己「跳过」的全部逻辑就在 `PlayTimeline()` 0x15C790 里：
    ///   0x15C8EF  cmp byte [rbx+0x90],0 ; je 退出      ← 不是 Cry 演出 → 不处理
    ///   0x15C906  lea edx,[r8+2]                       ← ★ 参数 = 2
    ///   0x15C90A  call GetKeyDown(eInputType)           ← ★ **eInputType.CANCEL（取消键）**
    ///   0x15C917  cmp byte [rbx+0xC4],0 ; jne 退出      ← 已跳过 → 不再处理
    ///   0x15C9CC  call StartFade(...)                   ← 淡出
    ///   0x15C9E1  mov byte [rax+0x18],0                 ← ★ mRoomTimeline.IsPlaying = false
    ///   0x15C9E5  mov byte [rbx+0xC4],1                 ← ★ mIsCrySkip = true
    ///   0x15CA26  call StopBgm(...)
    ///
    /// ⛔⛔ **与玩家先前的认知不符，已实锤订正**：跳过键是 **`eInputType.CANCEL`(=2)**
    ///     （dump:163450 `DECISION=1 / CANCEL=2`），**不是 LB**。玩家「按 LB 没反应」
    ///     的真实原因是按错了键，而非这段演出不能跳过。
    ///     （对照 D8：`OpeningMovieScene` 用的是 DECISION=1＝确认键。）
    ///
    /// ★ 为什么只写两个 bool 就够（不必复制 StartFade / StopBgm）：
    ///   `RoomManager.get_IsEndCryTimeline()` 0x1600A0 的判定是
    ///       mUpdateState(0x9C) == PlayTimeline(5)
    ///    && mRoomTimeline.IsPlaying(0x18) == false
    ///    && !FadeManager.IsFade()
    ///   ⇒ **把 IsPlaying 写成 false，游戏自己的状态机就会判定「演出已结束」**，
    ///     后续收尾（回调、状态迁移）全部走游戏原生路径，不会卡死。
    ///   ⛔ 不能改用 `RoomTimelineController.StopTimeline()`(0x162B90)：它除了
    ///      `mTimeline.Stop()` 还会 **Destroy(mParentObject)**（0x162CF3），是硬清理。
    ///   ⛔ `RoomTimelineController.SkipCry()`(0x192E80) 是空壳（铁律 22 同款：
    ///      RVA 与自动属性 setter 共享空函数体，callers 扫出 0 处调用）。
    ///
    /// `mIsCrySkip=true` 的额外作用：`FadeBehaviour.OnBehaviourPlay`(0x13AE00) 在
    ///   0x13AEAD 读 `get_IsCrySkip()`，为真则 **跳过演出里的淡入**（0x13AEB4 jne 直接 return）
    ///   ⇒ 与游戏原生跳过语义一致。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 本功能的做法
    /// ─────────────────────────────────────────────────────────────────────
    /// 玩家要的是 **自动**跳过（不用按键）⇒ Postfix 挂 `PlayTimeline()`（private 实例方法），
    /// 第一次观察到「演出正在播」时置 `IsPlaying=false` + `mIsCrySkip=true`。
    /// ⛔ 不碰输入层（10.10 Steam Input），不伪造按键（GetKeyDown 是 static，铁律 22 不可挂）。
    /// ✅ 幂等天然成立：触发后 mIsCrySkip=true ⇒ 后续帧条件不满足；下一场演出入口
    ///    `PlayTimelineToCry` 会把它重置为 false ⇒ 自动重新武装。
    /// ⚠ 待实机：无淡出直接收尾是否观感突兀（若明显，再补一次 StartFade）。
    /// </summary>
    public sealed class FeatureCrySkip : IFeature
    {
        internal static FeatureCrySkip Inst;

        public string Id { get { return "cryskip"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        /// <summary>观察到 IsPlaying=true 后再等几帧再收，避免打断 PlayTimeline 自身的初始化。</summary>
        private const int DelayFrames = 2;

        private MelonPreferences_Entry<bool> _skip;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            _skip = ctx.Cfg.Bool("AutoSkipCry", false, "Auto skip cry scene / 幽怨演出自动跳过");

            Safe(ctx.Harmony, typeof(CryPlayTimelinePatch), "RoomManager.PlayTimeline");
            SelfCheck(ctx.Harmony);
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("cryskip: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>挂载自检（铁律 22 / r7）：IL2CPP 下 PatchAll 不报错 ≠ 挂上了。</summary>
        private static void SelfCheck(HarmonyLib.Harmony h)
        {
            try
            {
                bool ok = false;
                foreach (var m in h.GetPatchedMethods())
                {
                    try
                    {
                        if (m.DeclaringType == typeof(RoomManager) && m.Name == "PlayTimeline") ok = true;
                    }
                    catch { }
                }
                QolLog.Warn("cryskip: 挂载自检 PlayTimeline=" + ok);
            }
            catch (Exception e)
            {
                QolLog.Warn("cryskip: 挂载自检异常 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Enabled
        {
            get
            {
                var f = Inst;
                if (f == null || f._skip == null || !f._skip.Value) return false;
                return QolConfig.Enabled && !KillSwitch.Tripped;
            }
        }

        public void OnUpdate() { }

        public void OnSceneLoaded(int buildIndex, string sceneName) { }

        /// <summary>无运行时副作用要收回（只在演出那一帧改了两个 bool）。</summary>
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            // Immediate：演出是游戏中随时触发的，不需要像 D8（只在启动时出现）那样要求重启
            yield return Item.Bool(Loc.T("f.cryskip"), _skip, false, RiskTier.Safe, Effect.Immediate);
        }

        // ───────────────────────── 字段读写（反射优先 + 直读兜底，规则 18） ─────────────────────────

        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _fieldWarned;
        private static bool _tlWarned;

        /// <summary>
        /// 读 bool 字段 —— **只能直读**。
        /// ⛔ 铁律 42：unhollower 代理的字段是 CLR 影子槽，`FieldInfo.GetValue()` 永远
        ///    返回默认值 false（不是真值）；v0.3.0 首轮就是靠直读兜底才过了 mIsCry 门闸。
        /// </summary>
        private static bool ReadBool(object obj, IntPtr p, string field, int off)
        {
            if (p != IntPtr.Zero)
            {
                try { return Marshal.ReadByte(IntPtr.Add(p, off)) != 0; }
                catch { }
            }
            if (!_fieldWarned)
            {
                _fieldWarned = true;
                QolLog.Warn("cryskip: 字段 " + field + " 直读失败（指针无效）→ 不会自动跳过");
            }
            return false;
        }

        /// <summary>
        /// 写 bool 字段 —— **只能直写**。
        /// ⛔⛔ 绝不能先试 `FieldInfo.SetValue`：它写进 CLR 影子槽，对原生对象**无效且
        ///    不抛异常**，于是"写成功了"的假象会让整个功能静默失效（v0.3.0 首轮实锤）。
        /// </summary>
        private static bool WriteBool(object obj, IntPtr p, string field, int off, bool val)
        {
            if (p != IntPtr.Zero)
            {
                try { Marshal.WriteByte(IntPtr.Add(p, off), (byte)(val ? 1 : 0)); return true; }
                catch { }
            }
            if (!_fieldWarned)
            {
                _fieldWarned = true;
                QolLog.Warn("cryskip: 字段 " + field + " 直写失败（指针无效）");
            }
            return false;
        }

        private static int ReadInt(IntPtr p, int off)
        {
            if (p == IntPtr.Zero) return -1;
            try { return Marshal.ReadInt32(IntPtr.Add(p, off)); }
            catch { return -1; }
        }

        // ───────────────────────── 补丁 ─────────────────────────

        [HarmonyPatch(typeof(RoomManager), "PlayTimeline")]
        private static class CryPlayTimelinePatch
        {
            private static bool _live;
            private static int _frames;

            private static void Postfix(RoomManager __instance)
            {
                try
                {
                    if (!Enabled) return;
                    if (__instance == null) return;
                    if (!_live)
                    {
                        _live = true;
                        QolLog.Warn("cryskip: PlayTimeline hook live");
                    }

                    IntPtr p = NativePtr.Of(__instance);
                    if (p == IntPtr.Zero) return;

                    // 门闸 1：必须是 Cry 演出（mIsCry 0x90）
                    if (!ReadBool(__instance, p, "mIsCry", 0x90)) { _frames = 0; return; }
                    // 门闸 2：本场还没跳过（mIsCrySkip 0xC4）—— 触发后天然幂等
                    if (ReadBool(__instance, p, "mIsCrySkip", 0xC4)) return;

                    // ⛔ 反射读 mRoomTimeline 恒为 null（铁律 42），⛔ 也不能把 ReadIntPtr
                    //    的结果当对象用（v0.3.0 首轮 bug：装箱的 IntPtr 不是代理对象，
                    //    后续 NativePtr.Of 拿到 Zero ⇒ IsPlaying 永远读不到）。
                    // ✅ 直读 0x88 的原生指针 → ToObj&lt;RoomTimelineController&gt;。
                    object tl = NativePtr.ObjAt<RoomTimelineController>(__instance, 0x88);
                    if (tl == null)
                    {
                        if (!_tlWarned)
                        {
                            _tlWarned = true;
                            QolLog.Warn("cryskip: mRoomTimeline 取不到（直读 0x88 + ToObj 失败）");
                        }
                        return;
                    }

                    IntPtr tp = NativePtr.Of(tl);
                    if (tp == IntPtr.Zero) return;

                    // ★ 门闸 3 已放宽（v0.3.0-r2）：IsPlaying 只作诊断，**不再据此拦截**。
                    //   两条理由：
                    //   ① `PlayTimeline()`(0x15C790) 是**一次性调用**（进入 PlayTimeline 状态时
                    //      调一次），不是每帧 ⇒ v0.3.0 首轮那套"连续 DelayFrames 帧"的等待
                    //      _frames 最多到 1，**永远不会触发**；
                    //   ② IsPlaying 是 PlayTimeline 内部异步置位的，Postfix 里读到的未必是 true，
                    //      用它当判据会静默拦掉全部演出。
                    //   ⇒ 安全性由门闸 1（mIsCry）+ 门闸 2（本场未跳过）保证，足够。
                    bool wasPlaying = ReadBool(tl, tp, "<IsPlaying>k__BackingField", 0x18);

                    // 先落闸再干活（防重入 / 铁律 1：同一调用补丁体会跑两遍）
                    bool okSkip = WriteBool(__instance, p, "mIsCrySkip", 0xC4, true);
                    bool okPlay = WriteBool(tl, tp, "<IsPlaying>k__BackingField", 0x18, false);
                    _frames++;
                    QolLog.Info("cryskip: 幽怨演出自动跳过 -> mIsCrySkip=" + okSkip
                                + " IsPlaying " + wasPlaying + "→false(" + okPlay + ")"
                                + " mUpdateState=" + ReadInt(p, 0x9C));
                }
                catch (Exception e)
                {
                    QolLog.Warn("cryskip: PlayTimeline Postfix 异常 -> "
                                + e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }
}
