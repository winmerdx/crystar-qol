using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Game;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 攻击速度倍率：改写技能序列表 <c>ConstSkillSequenceParameter.mAnimationSpeed</c>(0x44)。
    ///
    /// ════════════════════════════════════════════════════════════════
    /// 【取证结论（2026-09-24）】攻速不是某个 float 字段，是「动画播放速率 + 帧号事件」
    /// ════════════════════════════════════════════════════════════════
    /// • `CharacterParameter`（dump:164140）到 0x80 为止**没有攻速字段** ⇒ 别在角色参数里找。
    /// • 真正的时间轴是表：`ConstSkillSequenceParameter`（dump:168172，玩家敌人共用）
    ///     mSkillID       0x10   eSkillID
    ///     mSequence      0x14   eSequence（ANIMATION=1 / COLLISION=2 / SHOOT=3 / EFFECT=4 …）
    ///     **mAnimationSpeed 0x44  float ← 攻击动画播放倍率，攻速本体**
    ///     mStartFrame    0x48   mEndFrame 0x4C   判定/位移起止**帧号**
    ///     mCancelFrame   0x54   int，连击可取消帧
    ///     mHitIntervalFrame 0x58 / mHitStopFrame 0x5C（float）多段命中间隔 / 顿帧
    /// • 取表入口（两条，都可用）：
    ///     ①（本实现走这条，不调用任何游戏方法）
    ///        `ParameterManager.Instance` → `mSkillSequenceParameter`(0x60, ScriptableSkillSequenceParameter)
    ///        → `param`(0x18, List&lt;ConstSkillSequenceParameter&gt;) —— 全表一次拿到。
    ///     ② `ParameterManager.GetSkillSequenceParameter(eSkillID)` RVA 0x51560（实例方法，
    ///        每次实时查字典、返回同一批对象引用）。⛔ 本实现**不用**它：铁律「dump 有 ≠ 运行时能调」
    ///        （`GetShopList()` 曾整条路径被 MissingMethodException 毒死）。
    /// • 下发链路：`AttackTask.InitializeSequence`(0x12C810) → `AnimationTask.AddAnimation`(0x31EC10)
    ///   → `AnimationParameter.speed`(+0x1C)。玩家所有技能 Task 继承 AttackTask ⇒ 改表即全包。
    /// • **表对象不被拷贝、不缓存派生值** ⇒ 内存直写 0x44 支持热开关，关掉写回原值即可。
    ///
    /// 【为什么要过滤"只改玩家技能"】
    ///   这张表玩家与敌人（CM_/CP_/CN_ 前缀）共用 ⇒ 全表加速会把敌人一起变快。
    ///   过滤依据：`mSkillID`(0x10) 是否落在 `eSkillID` 里 `PLAYER_` 前缀那 100 个值
    ///   （dump 实锤：PLAYER 1..145、GUARDIAN 26..308、CM 161..318、CP/CN 251..347）。
    ///   兜底：枚举名解析失败 ⇒ 从持有表 `mSkillPossessionParameter`(0x70)
    ///   取 `mCharaID`(0x10) ∈ [1,4]（= PLAYER_0001..0004）的 `mSkillID`(0x18)。
    ///   两条都失败 ⇒ 退化全表 + Warn（功能仍生效，但敌人也会加速）。
    ///
    /// 【幂等 / 还原】照 FeatureGrowth 的"表内对象指针 → 原值"备份：
    ///   每次都从**记录的原始值**重算目标值（不是在上一次结果上再乘）⇒ 扫多少次都不累乘；
    ///   ⛔ 换场景**不清备份**（否则关开关时没东西可还原，表永远停在改后状态）；
    ///   ⛔ 还原只写"当前表里还活着"的指针 —— 旧对象可能已随场景销毁，写野指针会直接崩进程；
    ///   写回必回读校验；成/败都打日志。
    /// </summary>
    public sealed class FeatureAttackSpeed : IFeature
    {
        internal static FeatureAttackSpeed Inst;

        public string Id { get { return "atkspeed"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        // ConstSkillSequenceParameter（dump:168172）
        private const int OffSkillID = 0x10;      // eSkillID
        private const int OffAnimSpeed = 0x44;    // float mAnimationSpeed ← 攻速本体
        // ConstSkillPossessionParameter（dump:168116）兜底用
        private const int OffCharaID = 0x10;
        private const int OffPossSkillID = 0x18;

        private MelonPreferences_Entry<float> _mult;

        private int _tick;
        private float _loggedMult = -999f;
        private int _restoreWarnStamp = -99999;

        /// <summary>原生指针 → 原始 mAnimationSpeed（还原的唯一依据，⛔ 不得在中途丢弃）。</summary>
        private static readonly Dictionary<long, float> _orig = new Dictionary<long, float>();

        private static readonly HashSet<int> _playerSkills = new HashSet<int>();
        private static bool _setDone;

        private static bool _warnedNoTable;
        private static bool _warnedWriteback;
        private static bool _warnedGarbage;
        private static bool _warnedDegrade;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            _mult = ctx.Cfg.Float("Multiplier", 1f, "Player attack animation speed multiplier / 攻击速度倍率");
        }

        public void OnUpdate()
        {
            // 表是静态资源，不用每帧扫；全表 2243 条要反射枚举，v0.3.1-r11 由 15 帧放宽到
            // 60 帧（≈1s）：改倍率后最慢 1 秒生效，手感无感，但把周期性开销降到 1/4。
            if (_cooldownFrames > 0)
            {
                // r16：场景切换后表容器有"半失效"窗口（22:45 Boot 时出现过"表未就绪"），
                // 此窗口内扫表可能读到垃圾 count/元素 ⇒ 主线程死循环（22:47 卡标题的现场）。
                // ⇒ 切场景后一律先冷却 3 秒再恢复扫描。
                _cooldownFrames--;
                return;
            }
            _tick++;
            if ((_tick % 60) != 0) return;
            Sync();
        }

        /// <summary>r16：切场景后冷却 3 秒再恢复表扫描（OnSceneLoaded 不再立即 Sync）。</summary>
        private static int _cooldownFrames;

        /// <summary>r17 诊断：标题场景冷却结束后的首轮 Sync 是否已打过日志。</summary>
        private static bool _titleSyncLogged;

        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            _cooldownFrames = 180;
            _titleSyncLogged = false;
        }

        public void OnReset() { Restore("OnReset"); }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Item.Float(Loc.T("f.atkspeed"), _mult, 1f, 0.25f, 0.25f, 5f);
        }

        // ------------------------------------------------------------------
        // 主逻辑
        // ------------------------------------------------------------------

        private void Sync()
        {
            float m = _mult == null ? 1f : _mult.Value;
            if (m <= 0f || Math.Abs(m - 1f) < 0.001f) { Restore("倍率=1"); return; }
            Apply(m);
        }

        /// <summary>按倍率改写玩家技能的 mAnimationSpeed。改的条数写日志；取不到表则一次性 Warn。</summary>
        private static void Apply(float m)
        {
            var pm = ParameterManager.Instance;
            if (pm == null) { WarnOnce(ref _warnedNoTable, "atkspeed: ParameterManager.Instance 为空"); return; }

            object list = SeqTable();
            if (list == null) { WarnOnce(ref _warnedNoTable, "atkspeed: 取不到技能序列表（mSkillSequenceParameter 未就绪）"); return; }

            int n = CountOf(list);
            if (n <= 0) { WarnOnce(ref _warnedNoTable, "atkspeed: 技能序列表为空（Count=" + n + "）"); return; }
            // r16：真实表 2243 条；读到远超此数的 count ⇒ 容器悬空、数值是垃圾 ⇒ 本轮放弃。
            //   （此前这里会直接 for 循环到垃圾上限 ⇒ 主线程死循环 ⇒"卡标题"现场）
            if (n > 5000)
            {
                WarnOnce(ref _warnedGarbage, "atkspeed: Count=" + n + " 超出合理上限（真实 2243）⇒ 容器疑似悬空，本轮跳过");
                return;
            }

            EnsurePlayerSet(pm);

            int scanned = 0, changed = 0, fail = 0, bogus = 0;
            for (int i = 0; i < n; i++)
            {
                object p = ItemOf(list, i);
                if (p == null) continue;
                IntPtr ptr = NativePtr.Of(p);
                // r16：野指针/垃圾元素防御——真实对象指针不可能低于 64K
                if (ptr == IntPtr.Zero || ptr.ToInt64() < 0x10000) { bogus++; continue; }

                int skillId = Marshal.ReadInt32(IntPtr.Add(ptr, OffSkillID));
                if (!IsPlayerSkill(skillId)) continue;      // ⛔ 敌人技能不动，否则敌人一起加速
                scanned++;

                float cur = ReadF(ptr, OffAnimSpeed);
                if (!(cur > 0f)) continue;                  // 非动画条目（mAnimationSpeed=0）不碰

                long key = ptr.ToInt64();
                float orig;
                if (!_orig.TryGetValue(key, out orig)) { orig = cur; _orig[key] = orig; }

                float want = orig * m;
                if (want < 0.01f) want = 0.01f;
                if (want > 100f) want = 100f;
                if (Math.Abs(cur - want) < 0.0001f) continue;   // 已是目标值 ⇒ 不写（幂等重放）

                WriteF(ptr, OffAnimSpeed, want);
                float back = ReadF(ptr, OffAnimSpeed);
                if (Math.Abs(back - want) < 0.0001f) changed++;
                else { fail++; WarnOnce(ref _warnedWriteback, "atkspeed: 写回校验失败（期望 " + want.ToString("0.###") + "，实读 " + back.ToString("0.###") + "）"); }
            }

            var f = Inst;
            if (f != null && (changed > 0 || Math.Abs(m - f._loggedMult) > 0.001f || fail > 0 || bogus > 0))
            {
                f._loggedMult = m;
                QolLog.Info("atkspeed: 表 " + n + " 条 / 玩家技能 " + scanned + " 条，倍率 x" + m.ToString("0.##")
                            + " ⇒ 改写 " + changed + (fail > 0 ? "（失败 " + fail + "）" : "")
                            + (bogus > 0 ? "（垃圾条目 " + bogus + "）" : "")
                            + "，备份 " + _orig.Count + " 条");
                if (ModMain.LastSceneName == "Title") _titleSyncLogged = true;
            }

            // r17 诊断：标题场景冷却（180 帧 ≈ 3s）结束后的首轮 Sync **必打一条**，哪怕零变化。
            //   "卡标题"现场若出现本行 ⇒ 主线程至少活到场景载入 +3s 之后（冻结在更晚/别处）；
            //   不出现 ⇒ 主线程在 +3s 之前就没了（配合 ModMain 的 hb 心跳定性）。
            if (!_titleSyncLogged && ModMain.LastSceneName == "Title")
            {
                _titleSyncLogged = true;
                QolLog.Warn("atkspeed: Title 冷却后首轮 Sync 完成（扫描 " + n + " 条 / 玩家 " + scanned
                            + " 条，改写 " + changed + "，备份 " + _orig.Count + "）—— 主线程此时仍活着");
            }
        }

        /// <summary>
        /// 还原。**只写"当前表里还活着"的指针**（旧对象可能已随场景销毁 ⇒ 写野指针会直接崩进程），
        /// 表没就绪则**保留重试**（不清备份），配合 2s 节流告警。
        /// </summary>
        private static void Restore(string why)
        {
            if (_orig.Count == 0) return;

            object list = SeqTable();
            HashSet<long> active = ActivePointers(list);
            if (active == null || active.Count == 0)
            {
                int now = Environment.TickCount;
                var f0 = Inst;
                if (f0 != null && now - f0._restoreWarnStamp > 2000)
                {
                    f0._restoreWarnStamp = now;
                    QolLog.Warn("atkspeed: 还原时表未就绪 ⇒ 保留重试（待还原 " + _orig.Count + " 条，" + why + "）");
                }
                return;
            }

            int ok = 0, fail = 0;
            var done = new List<long>();
            var stale = new List<long>();
            foreach (var kv in _orig)
            {
                if (!active.Contains(kv.Key)) { stale.Add(kv.Key); continue; }
                IntPtr ptr = new IntPtr(kv.Key);
                WriteF(ptr, OffAnimSpeed, kv.Value);
                float back = ReadF(ptr, OffAnimSpeed);
                if (Math.Abs(back - kv.Value) < 0.0001f) { ok++; done.Add(kv.Key); }
                else fail++;
            }
            for (int i = 0; i < done.Count; i++) _orig.Remove(done[i]);
            // 已不在当前表里的条目：对象多半已销毁，留着只会越积越多且永远写不回去
            for (int i = 0; i < stale.Count; i++) _orig.Remove(stale[i]);

            var f = Inst;
            if (f != null) f._loggedMult = -999f;   // 允许下次开开关重新打一条日志
            QolLog.Info("atkspeed: 已还原 " + ok + " 条（" + why + "）"
                        + (fail > 0 ? "，失败 " + fail + " 条保留重试" : "")
                        + (stale.Count > 0 ? "，作废失效条目 " + stale.Count + " 条" : ""));
        }

        /// <summary>技能序列表全量（object + 反射枚举；⛔ 编译期 List&lt;T&gt; 接不住运行期 Il2Cpp List）。</summary>
        private static object SeqTable()
        {
            var pm = ParameterManager.Instance;
            if (pm == null) return null;
            object sp = GetMember(pm, "mSkillSequenceParameter");   // 0x60（dump:171503）
            if (sp == null) return null;
            object raw = GetMember(sp, "param");                    // ScriptableSkillSequenceParameter.param 0x18
            return raw;
        }

        /// <summary>当前表里"玩家技能条目"的指针集合（与 Apply 的过滤完全一致）。</summary>
        private static HashSet<long> ActivePointers(object list)
        {
            var set = new HashSet<long>();
            if (list == null) return set;
            int n = CountOf(list);
            if (n > 5000) return set;                     // r16：容器悬空防御，与 Apply 同款
            for (int i = 0; i < n; i++)
            {
                object p = ItemOf(list, i);
                if (p == null) continue;
                IntPtr ptr = NativePtr.Of(p);
                if (ptr == IntPtr.Zero || ptr.ToInt64() < 0x10000) continue;   // r16：野指针防御
                int skillId = Marshal.ReadInt32(IntPtr.Add(ptr, OffSkillID));
                if (IsPlayerSkill(skillId)) set.Add(ptr.ToInt64());
            }
            return set;
        }

        // ------------------------------------------------------------------
        // 玩家技能集合
        // ------------------------------------------------------------------

        private static bool IsPlayerSkill(int id)
        {
            return _playerSkills.Count == 0 || _playerSkills.Contains(id);   // 空 ⇒ 退化全表
        }

        private static void EnsurePlayerSet(ParameterManager pm)
        {
            if (_setDone) return;
            _setDone = true;

            // ① eSkillID 枚举名：PLAYER_ 前缀（dump:169320，100 个值 1..145）
            try
            {
                foreach (string nm in Enum.GetNames(typeof(eSkillID)))
                {
                    if (nm.StartsWith("PLAYER_", StringComparison.Ordinal))
                        _playerSkills.Add((int)Enum.Parse(typeof(eSkillID), nm));
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("atkspeed: 枚举名解析失败 -> " + e.GetType().Name + ": " + e.Message);
            }

            // ② 兜底：持有表 mCharaID ∈ [1,4]（= eCharaID.PLAYER_0001..0004，dump:168937）的技能
            if (_playerSkills.Count == 0)
            {
                try
                {
                    object sp = GetMember(pm, "mSkillPossessionParameter");   // 0x70
                    object list = sp == null ? null : GetMember(sp, "param");
                    int n = CountOf(list);
                    if (n > 5000) n = 0;   // r16：容器悬空防御
                    for (int i = 0; i < n; i++)
                    {
                        object p = ItemOf(list, i);
                        if (p == null) continue;
                        IntPtr ptr = NativePtr.Of(p);
                        if (ptr == IntPtr.Zero || ptr.ToInt64() < 0x10000) continue;
                        int chara = Marshal.ReadInt32(IntPtr.Add(ptr, OffCharaID));
                        if (chara < 1 || chara > 4) continue;
                        _playerSkills.Add(Marshal.ReadInt32(IntPtr.Add(ptr, OffPossSkillID)));
                    }
                }
                catch (Exception e)
                {
                    QolLog.Warn("atkspeed: 持有表兜底失败 -> " + e.GetType().Name + ": " + e.Message);
                }
            }

            if (_playerSkills.Count == 0)
            {
                WarnOnce(ref _warnedDegrade, "atkspeed: 玩家技能集合解析失败 ⇒ 退化全表（敌人也会一起加速）");
            }
            else
            {
                QolLog.Info("atkspeed: 玩家技能集合 " + _playerSkills.Count + " 条（只改这些技能的 mAnimationSpeed）");
            }
        }

        // ------------------------------------------------------------------
        // 小工具（与 FeatureGrowth 同一套姿势）
        // ------------------------------------------------------------------

        private static float ReadF(IntPtr p, int off)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(IntPtr.Add(p, off))), 0);
        }

        private static void WriteF(IntPtr p, int off, float v)
        {
            Marshal.WriteInt32(IntPtr.Add(p, off), BitConverter.ToInt32(BitConverter.GetBytes(v), 0));
        }

        private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var p = t.GetProperty(name, BF);
            if (p != null && p.CanRead) { try { return p.GetValue(obj, null); } catch { } }
            var fd = t.GetField(name, BF);
            if (fd != null) { try { return fd.GetValue(obj); } catch { } }
            return null;
        }

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

        private static void WarnOnce(ref bool flag, string msg)
        {
            if (flag) return;
            flag = true;
            QolLog.Warn(msg);
        }
    }
}
