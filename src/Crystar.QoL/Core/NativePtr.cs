using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrystarQoL
{
    /// <summary>
    /// Il2Cpp 代理对象 → 原生指针。
    ///
    /// 【为什么必须反射】DummyDll 里的代理类型**没有** `Pointer` 属性（它是
    /// Il2CppObjectBase 的运行时成员）→ 直接 `obj.Pointer` 编译不过（CS1061）。
    /// 而 unhollower 的代理 **每次封送都新建实例、但指向同一原生对象**，
    /// 所以"指针"才是跨代理稳定的身份/写入口（v0.1.14b 实锤，见 FeatureCampConfig.PtrOf）。
    /// 早期 FeatureCampConfig 里有一份同名私有实现，新代码统一走这里。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// ⛔⛔ 反向：原生指针 → 托管代理对象（2026-09-20 v0.3.0-r2 实锤，铁律 42）
    /// ─────────────────────────────────────────────────────────────────────
    /// **unhollower 代理类的实例字段，反射是读不到原生值的**：
    ///   代理对象是"每次封送新建的 CLR 壳"，字段只是 CLR 侧的影子槽，从未被填充
    ///   ⇒ `FieldInfo.GetValue()` 对引用类型恒返回 null，对值类型恒返回 false/0；
    ///     `SetValue()` 写进影子槽，**对原生对象完全无效**（不报错，最阴险）。
    ///   实锤（v0.3.0 首轮实机）：
    ///     - `mapicons: 字段 mAllMap 读不到`（FieldMapManager.mAllMap 0x58）
    ///     - `cryskip: 字段 &lt;IsPlaying&gt;k__BackingField 读不到`（先把 mRoomTimeline 读成 null，
    ///        再把 IntPtr 兜底值装箱当对象用 ⇒ 后续全线崩）
    ///   ⇒ **读引用字段必须**：`Marshal.ReadIntPtr(ptr + offset)` → `ToObj&lt;T&gt;()`。
    ///   ⇒ **写任何字段都必须直写**（Marshal.WriteByte/WriteInt32/WriteIntPtr）。
    ///   反射只保留为"最后兜底"，且**写操作绝不能先试反射**（会静默失效）。
    /// </summary>
    internal static class NativePtr
    {
        public static IntPtr Of(object o)
        {
            if (o == null) return IntPtr.Zero;
            try
            {
                var pi = o.GetType().GetProperty("Pointer");
                return pi == null ? IntPtr.Zero : (IntPtr)pi.GetValue(o, null);
            }
            catch { return IntPtr.Zero; }
        }

        private const BindingFlags CtorBF =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _diag;

        /// <summary>一次性诊断：把"到底哪条路走得通"打出来（铁律 19：诊断排在所有动作之前）。</summary>
        private static void Diag(string how, bool ok, Exception e)
        {
            if (_diag) return;
            _diag = true;
            QolLog.Warn("ptr2obj: 采用 [" + how + "] ok=" + ok
                        + (e == null ? "" : " -> " + e.GetType().Name + ": " + e.Message));
        }

        /// <summary>
        /// 原生指针 → 托管代理对象（例如 `FieldMapManager.mAllMap`、`RoomManager.mRoomTimeline`）。
        ///
        /// 三条路都试（unhollower 版本差异大，不想为它再跑一轮实机）：
        ///   ① 代理类型的 `X(IntPtr)` 构造函数（unhollower 生成的类型都有）
        ///   ② `Activator.CreateInstance(type, ptr)`
        ///   ③ `Il2CppObjectBase.Cast&lt;T&gt;()`
        /// </summary>
        public static T ToObj<T>(IntPtr ptr) where T : class
        {
            return (T)ToObj(ptr, typeof(T));
        }

        public static object ToObj(IntPtr ptr, Type t)
        {
            if (ptr == IntPtr.Zero || t == null) return null;

            try
            {
                var ctor = t.GetConstructor(CtorBF, null, new[] { typeof(IntPtr) }, null);
                if (ctor != null)
                {
                    var o = ctor.Invoke(new object[] { ptr });
                    if (o != null) { Diag("ctor(IntPtr)", true, null); return o; }
                }
            }
            catch (Exception e) { Diag("ctor(IntPtr)", false, e); }

            try
            {
                var o = Activator.CreateInstance(t, CtorBF, null, new object[] { ptr }, null);
                if (o != null) { Diag("Activator(IntPtr)", true, null); return o; }
            }
            catch (Exception e) { Diag("Activator(IntPtr)", false, e); }

            try
            {
                var baseType = t.BaseType;
                while (baseType != null && baseType.Name != "Il2CppObjectBase") baseType = baseType.BaseType;
                if (baseType != null)
                {
                    var raw = Activator.CreateInstance(baseType, CtorBF, null, new object[] { ptr }, null);
                    var mi = baseType.GetMethod("Cast", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        var o = mi.MakeGenericMethod(t).Invoke(raw, null);
                        if (o != null) { Diag("Cast<T>", true, null); return o; }
                    }
                }
            }
            catch (Exception e) { Diag("Cast<T>", false, e); }

            Diag("全部失败", false, null);
            return null;
        }

        /// <summary>读 `owner` 偏移 off 处的**引用类型**字段（反射不可靠 ⇒ 直读 + 转对象）。</summary>
        public static T ObjAt<T>(object owner, int off) where T : class
        {
            if (owner == null) return null;
            IntPtr p = Of(owner);
            if (p == IntPtr.Zero) return null;
            try { return ToObj<T>(Marshal.ReadIntPtr(IntPtr.Add(p, off))); }
            catch { return null; }
        }

        /// <summary>读 `owner` 偏移 off 处的引用字段，先试反射、再直读（反射命中即返回）。</summary>
        public static T ObjField<T>(object owner, string field, int off) where T : class
        {
            if (owner == null) return null;
            try
            {
                var fi = FieldOf(owner.GetType(), field);
                if (fi != null)
                {
                    var v = fi.GetValue(owner) as T;
                    if (v != null) return v;
                }
            }
            catch { }
            return ObjAt<T>(owner, off);
        }

        private static FieldInfo FieldOf(Type t, string name)
        {
            for (Type c = t; c != null; c = c.BaseType)
            {
                try
                {
                    var fi = c.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi != null) return fi;
                }
                catch { }
            }
            return null;
        }

        /// <summary>直写 bool 字段（⛔ 不要先试反射 SetValue —— 写的是 CLR 影子槽，不生效且静默）。</summary>
        public static bool WriteBool(object obj, int off, bool val)
        {
            if (obj == null) return false;
            IntPtr p = Of(obj);
            if (p == IntPtr.Zero) return false;
            try { Marshal.WriteByte(IntPtr.Add(p, off), (byte)(val ? 1 : 0)); return true; }
            catch { return false; }
        }

        /// <summary>直读 bool 字段（反射读到的是影子槽 ⇒ 一律直读）。</summary>
        public static bool ReadBool(object obj, int off)
        {
            if (obj == null) return false;
            IntPtr p = Of(obj);
            if (p == IntPtr.Zero) return false;
            try { return Marshal.ReadByte(IntPtr.Add(p, off)) != 0; }
            catch { return false; }
        }

        /// <summary>把 float 写成 Marshal 可写的 int 位型（避免 unsafe）。</summary>
        public static int FloatBits(float v)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
        }
    }
}
