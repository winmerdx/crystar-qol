using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Game;

namespace CrystarQoL.Features
{
    /// <summary>
    /// v0.3 批次 4 —— A7「手动调整等级数」的数据层（营地子页 FeatureCampConfig 调它）。
    /// 本类**不是 IFeature**、不注册到 ModMain：它只提供"读角色列表 / 读写等级"的纯函数，
    /// UI 行表、页面切换、二次确认全部由 FeatureCampConfig 负责。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 取证（2026-11-06，dump.cs 行号 + GameAssembly.dll 反汇编）
    /// ─────────────────────────────────────────────────────────────────────
    /// 角色列表（复用 C4 已实机跑通 14/14 的主路，FeatureGrowth.cs:1011~1032）：
    ///   ParameterManager.mMainCharacterParameter (dump:171571, 0x280)
    ///     → CharacterParameter[]；兜底 GetMainCharacterParameter(uint) (dump:171895, 0x4FBC0)。
    ///   ⛔ 营地场景下 GM.GetJoinedCharacterList() 元素全 null、CharacterComponent 的
    ///     mCharacterParameter 恒 null ⇒ 只有这条主路可靠（v0.2.10 修正二实机实证）。
    ///
    /// 等级真正存放处：
    ///   CharacterParameter.Parameter (dump:164885)：mLevel 0x10 (uint) / mExp 0x14 /
    ///     mBaseMaxHp 0x18 … 共 8 项 base 属性；
    ///   CharacterParameter.mParam 0x18 (dump:164144) 指向它。
    ///
    /// ★ UpdateLevel(uint) (dump:164697, RVA 0x1CDA00) 反汇编实锤（disasm.py 0x1CDA00）：
    ///   · 0x1CDA0F `mov r12d,edx` ⇒ r12 = 传入的目标等级；
    ///   · 0x1CDB45 `mov rsi,r12` + 0x1CDB48/0x1CDB6F 按 **r12** 在成长表里找区间
    ///     （ConstLevelUpParameter 的上下界），循环 8 次调
    ///     CalcLevelUpValue(growType,start,end,rate) (0x1CC030)，
    ///     结果逐一写回 mParam：0x1CDC71 [rcx+0x18] … 0x1CDFC5 [rcx+0x34]；
    ///   · start 取自**成长表条目 / 上一条目**（0x1CDBF1 `lea edx,[rbx-1]`、
    ///     0x1CDC0A `mov esi,[r14+0x5c]` 取 ConstCharacterParameter 初值），
    ///     ⛔ **与当前 mLevel 完全无关** ⇒ 升/降都是「按目标等级全量重算」，**支持降级**。
    ///     （立项方案 §2.5 / §8.3 把"能否降级"列为唯一待实测点，此处从机器码定案。）
    ///   · 0x1CDFE3 `mov dword ptr [rax+0x10], r12d`（rax = mParam）
    ///     ⇒ **UpdateLevel 自己就会写 mParam.mLevel**，我们提前直写只是保险；
    ///     末尾 0x1CDFE7 `call UpdateSkill`(0x1CE150)，
    ///     0x1CE025 `jmp RecalcParameter`(0x1C0350) ⇒ 属性/技能/HP 全由游戏负责。
    ///   · 0x1CDA9A GetLevelUpParameter(eCharaID) 0x4FA90 /
    ///     0x1CDAF6 GetCharacterParameter(eCharaID) 0x4D820。
    ///
    /// 角色名：CharacterParameter.get_CharaNameID() (dump:164370, 0x1CE990) → string，
    ///   交给 ParameterManager.GetCharacterName(string) (dump:171835) 查当前语言文本；
    ///   备用 get_CharaID() (dump:164367) → GetCharacterName(eCharaID) (dump:171841)。
    ///
    /// ⛔ 铁律适配：字段一律 Marshal 直读/直写（43）；容器与元素 object + 反射枚举（15/18）；
    ///   反射调用游戏方法前后都要回读校验（10）；一次性 Warn 排在可能抛异常的动作之前（19）。
    /// ⚠ 等级写进 CharacterParameter 是**会进存档**的运行时改动 ⇒ UI 侧必须 warn: true
    ///   二次确认，并在使用说明提示先做存档备份（G6），见 FeatureCampConfig.EnsureLevelRows。
    /// </summary>
    internal static class FeatureLevel
    {
        /// <summary>目标等级上限：游戏常量 MAX_LEVEL=99，只夹取不改常量（铁律 10 共享常量池）。</summary>
        public const int MaxLevel = 99;

        private const BindingFlags BF =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly List<object> _actors = new List<object>();
        private static readonly Dictionary<long, string> _nameCache = new Dictionary<long, string>();
        private static readonly HashSet<string> _warned = new HashSet<string>();
        private static int _actorsAt = -100000;
        private const int ActorsTtlMs = 1000;

        private static void WarnOnce(string key, string msg)
        {
            if (_warned.Contains(key)) return;
            _warned.Add(key);
            QolLog.Warn("level: " + msg);
        }

        // ───────────────────────── 反射小工具（与 FeatureGrowth 同构） ─────────────────────────

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

        private static int CountOf(object list)
        {
            if (list == null) return 0;
            try
            {
                foreach (string m in new[] { "get_Count", "get_Length" })
                {
                    var mi = list.GetType().GetMethod(m, Type.EmptyTypes);
                    if (mi == null) continue;
                    object v = mi.Invoke(list, null);
                    if (v is int) return (int)v;
                }
            }
            catch { }
            return 0;
        }

        private static object ItemOf(object list, int i)
        {
            if (list == null) return null;
            try
            {
                var mi = list.GetType().GetMethod("get_Item", new[] { typeof(int) });
                return mi == null ? null : mi.Invoke(list, new object[] { i });
            }
            catch { return null; }
        }

        private static object Invoke0(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var mi = obj.GetType().GetMethod(name, BF);
                return mi == null ? null : mi.Invoke(obj, null);
            }
            catch { return null; }
        }

        /// <summary>按参数类型精确匹配的单参调用（uint 与 int 不能混，否则 Invoke 抛异常）。</summary>
        private static object Invoke1(object obj, string name, object arg)
        {
            if (obj == null) return null;
            try
            {
                var mi = obj.GetType().GetMethod(name, new[] { arg.GetType() });
                return mi == null ? null : mi.Invoke(obj, new[] { arg });
            }
            catch { return null; }
        }

        // ───────────────────────── 角色列表 ─────────────────────────

        /// <summary>强制刷新（进子页时调一次，保证角色数是本次进页的真实值）。</summary>
        public static void Refresh() { LoadActors(true); }

        private static void LoadActors(bool force)
        {
            if (!force && _actors.Count > 0 && Environment.TickCount - _actorsAt < ActorsTtlMs) return;
            _actorsAt = Environment.TickCount;
            _actors.Clear();

            try
            {
                var pm = ParameterManager.Instance;
                if (pm == null) { WarnOnce("pmgr", "ParameterManager.Instance 为空，子页角色列表为空"); return; }

                // 主路：mMainCharacterParameter(0x280) → CharacterParameter[]
                // ⚠ GetProperty 优先（unhollower 把字段生成为属性，GetField 恒 null）
                object arr = GetMember(pm, "mMainCharacterParameter");
                int n = CountOf(arr);
                for (int i = 0; i < n; i++)
                {
                    object cp = ItemOf(arr, i);
                    if (cp != null) _actors.Add(cp);
                }

                // 兜底：数组反射取不到时逐个调 GetMainCharacterParameter(uint)
                if (_actors.Count == 0)
                {
                    for (uint i = 0; i < 8; i++)
                    {
                        object cp = Invoke1(pm, "GetMainCharacterParameter", i);
                        if (cp != null) _actors.Add(cp);
                    }
                    if (_actors.Count == 0)
                        WarnOnce("idx", "GetMainCharacterParameter(0..7) 全部返回 null，子页角色列表为空");
                    else
                        QolLog.Info("level: 角色列表走 GetMainCharacterParameter 兜底，共 " + _actors.Count + " 人");
                }
            }
            catch (Exception e)
            {
                WarnOnce("load", "读取角色列表失败 -> " + e.GetType().Name + ": " + e.Message);
            }
            if (_actors.Count > 0)
                QolLog.Info("level: 角色列表已刷新（" + _actors.Count + " 人）");
        }

        public static int ActorCount()
        {
            LoadActors(false);
            return _actors.Count;
        }

        /// <summary>第 idx 个角色的游戏内名字；取不到退 "#idx"，绝不静默变空（方案 8.3 约束①）。</summary>
        public static string ActorName(int idx)
        {
            LoadActors(false);
            if (idx < 0 || idx >= _actors.Count) return "—";
            object cp = _actors[idx];
            long key = NativePtr.Of(cp).ToInt64();
            string cached;
            if (key != 0 && _nameCache.TryGetValue(key, out cached)) return cached;

            string name = null;
            try
            {
                var pm = ParameterManager.Instance;
                if (pm != null)
                {
                    object id = Invoke0(cp, "get_CharaNameID");
                    string sid = id as string;
                    if (!string.IsNullOrEmpty(sid))
                    {
                        object r = Invoke1(pm, "GetCharacterName", sid);
                        name = r as string;
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        // 备用：eCharaID 重载
                        object cid = Invoke0(cp, "get_CharaID");
                        if (cid != null)
                        {
                            object r = Invoke1(pm, "GetCharacterName", cid);
                            name = r as string;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                WarnOnce("name", "取角色名失败 -> " + e.GetType().Name + ": " + e.Message);
            }
            if (string.IsNullOrEmpty(name)) name = "#" + (idx + 1);
            // 标题框约 9 个全角字宽，角色名最多留 5 字（同 C7 物品名的裁法）
            if (name.Length > 5) name = name.Substring(0, 5);
            if (key != 0) _nameCache[key] = name;
            return name;
        }

        // ───────────────────────── 等级读写 ─────────────────────────

        /// <summary>mParam(0x18) → mLevel(0x10) 直读；失败退回 get_Level() 属性。</summary>
        public static int CurrentLevel(int idx)
        {
            LoadActors(false);
            if (idx < 0 || idx >= _actors.Count) return -1;
            object cp = _actors[idx];
            try
            {
                IntPtr p = NativePtr.Of(cp);
                if (p != IntPtr.Zero)
                {
                    IntPtr param = Marshal.ReadIntPtr(IntPtr.Add(p, 0x18));
                    if (param != IntPtr.Zero)
                    {
                        int lv = Marshal.ReadInt32(IntPtr.Add(param, 0x10));
                        if (lv > 0 && lv <= MaxLevel) return lv;
                    }
                }
            }
            catch (Exception e)
            {
                WarnOnce("read", "mLevel 直读失败 -> " + e.GetType().Name + ": " + e.Message);
            }
            object v = Invoke0(cp, "get_Level");
            if (v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return -1;
        }

        /// <summary>目标等级文本（当前等级 + 目标值），只读信息行用。</summary>
        public static string CurrentLevelText(int idx)
        {
            int lv = CurrentLevel(idx);
            return lv < 0 ? "—" : lv.ToString();
        }

        /// <summary>
        /// 套用等级。返回短结果标签（交给 ActionRow 挂在标题尾部 6 秒）。
        /// 流程：直写 mLevel（保险）→ Invoke UpdateLevel((uint)target) → **回读校验**。
        /// </summary>
        public static string Apply(int idx, int target, Func<bool, string> resultLabel)
        {
            LoadActors(true);   // 动作型：用最新列表，避免中途换人写错对象
            if (idx < 0 || idx >= _actors.Count)
            {
                QolLog.Warn("level: 角色下标越界（" + idx + " / " + _actors.Count + "）⇒ 未改动任何数据");
                return resultLabel(false);
            }
            if (target < 1) target = 1;
            if (target > MaxLevel) target = MaxLevel;

            object cp = _actors[idx];
            string who = ActorName(idx);
            int before = CurrentLevel(idx);
            IntPtr p = NativePtr.Of(cp);
            if (p == IntPtr.Zero)
            {
                QolLog.Warn("level: 角色 " + who + " 原生指针为零 ⇒ 未改动任何数据");
                return resultLabel(false);
            }

            // ① 直写 mParam.mLevel(0x10)：private set_Level 调不到，且 Invoke 不是"生效"的保证（铁律 10/43）
            bool wrote = false;
            try
            {
                IntPtr param = Marshal.ReadIntPtr(IntPtr.Add(p, 0x18));
                if (param != IntPtr.Zero)
                {
                    Marshal.WriteInt32(IntPtr.Add(param, 0x10), target);
                    wrote = true;
                }
            }
            catch (Exception e)
            {
                WarnOnce("write", "mLevel 直写失败 -> " + e.GetType().Name + ": " + e.Message
                                  + "（仍会尝试 UpdateLevel，它自己也会写 mLevel）");
            }

            // ② 让游戏按目标等级全量重算 8 项 base 属性 + UpdateSkill + RecalcParameter
            bool invoked = false;
            try
            {
                // ⛔ 参数必须是 uint；按参数类型精确匹配，避免重载/装箱不匹配
                var mi = cp.GetType().GetMethod("UpdateLevel", BF, null, new[] { typeof(uint) }, null);
                if (mi == null)
                {
                    foreach (var m in cp.GetType().GetMethods(BF))
                    {
                        if (m.Name != "UpdateLevel") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 1) { mi = m; break; }
                    }
                }
                if (mi == null)
                {
                    QolLog.Warn("level: " + cp.GetType().Name + " 上找不到 UpdateLevel(uint)");
                    return resultLabel(false);
                }
                object arg = Convert.ChangeType(target, mi.GetParameters()[0].ParameterType);
                mi.Invoke(cp, new[] { arg });
                invoked = true;
            }
            catch (Exception e)
            {
                QolLog.Warn("level: UpdateLevel 调用失败 -> " + e.GetType().Name + ": " + e.Message);
                return resultLabel(false);
            }

            // ③ 回读校验（铁律 10：Invoke 不抛异常 ≠ 生效）
            int after = CurrentLevel(idx);
            string line = "level: 角色 " + who + " 等级 " + before + "→" + target
                          + " ⇒ UpdateLevel(" + target + ") 回读=" + after
                          + "（直写=" + wrote + " Invoke=" + invoked + "）";
            if (after != target)
            {
                QolLog.Warn(line + " ⇒ 回读不符，未确认生效");
                return resultLabel(false);
            }
            QolLog.Info(line + "（属性已重算）");
            return resultLabel(true);
        }
    }
}
