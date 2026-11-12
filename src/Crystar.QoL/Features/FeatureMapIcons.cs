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
    /// v0.3 批次 3 —— E3 大地图显示敌人 / 稀有敌人 / BOSS / NPC 标记。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 取证（2026-09-17 / 09-18，GameAssembly.dll 机器码 + dump.cs）
    /// ─────────────────────────────────────────────────────────────────────
    /// 地图图标的**唯一入口**是 `FieldMapManager.AddPlacement`（全 dump 检索
    /// `Radar`/`EnemyDetect`/`MapEnemy`/`NaviArrow` 均 0 命中，`ui_map_icon` 只出现
    /// 在 `UIGameMainMapWindowBase.AddMapIcon` 一处 ⇒ 不存在第二套标记系统）：
    ///
    ///   FieldMapManager : SingletonMonoBehaviour<FieldMapManager>   (dump:163012)
    ///     private UIGameMainAllMapWindow   mAllMap;   // 0x58   ← 大地图
    ///     private UIGameMainMinimapWindow  mMiniMap;  // 0x60   ← 小地图
    ///     public void AddPlacement(FieldComponent fc, StellaUIManager.eIconCacheIndex type)  0x9D9D0
    ///     public void RemovePlacement(FieldComponent fc)                                    0x9DF50
    ///     class IconInfo { mType 0x10; mComponent 0x18; mImage 0x20 }
    ///
    /// ★ 为什么大地图看不到敌人（0x9D9D0 机器码，同一个方法里两套放行条件）：
    ///   ① 注册给小地图（mMiniMap = this+0x60）：**无条件全收**
    ///        0x9DA76  mov rcx,[rsi+0x60] ; 0x9DA90  call [rax+0x230]
    ///   ② 注册给大地图（mAllMap = this+0x58）：**先过白名单**
    ///        0x9DAFA  cmp ebx,0x4D        ; 77 RETURN_WARP → 放行
    ///        0x9DAFF  lea eax,[rbx-0x47] ; cmp eax,1 ; jbe 放行   ; type ∈ {71,72}
    ///        0x9DB07  cmp ebx,0x4E        ; 78 SHOP        → 放行
    ///        0x9DB0A  jne 跳过            ; ★ 73/74/75/76 一律被丢
    ///   被丢的四类正好是 73 BOSS / 74 ENEMY / 75 NPC / 76 RARE_ENEMY —— 也就是玩家
    ///   在小地图上看到的"红箭头（普通敌人）/ 高亮红箭头（稀有敌人）"。
    ///
    /// ⛔ **不能用 Harmony Transpiler / ILManipulator 去改上面那几条 `cmp`**：
    ///    IL2CPP 下 `GameAssembly.dll` 里是原生 x86，**没有 IL 可改**（铁律 38）。
    /// ✅ 正解＝Postfix 里自己 new 一个 `IconInfo` 再补调一次 `mAllMap.AddPlacement`。
    ///
    /// ⚠ **不能复用同一个 IconInfo 对象给两张图**：`AddPlacement` 会把新建的
    ///    `SpriteRenderer` 写回 `icon.mImage(0x20)` ⇒ 后写入的一侧把前一侧的引用顶掉。
    ///    所以本功能必须 **new 一个全新的 IconInfo**，绝不能拿游戏那个。
    ///
    /// ⚠ **注册点藏在 switch 里**（铁律 39 的现场）：`callers.py` 扫 `0x9D9D0` 只有 7 处
    ///    call，但敌人/稀有敌人/BOSS/NPC 的四个分支各自 `mov r8d,常量` 后 `jmp` 到同一个
    ///    汇合点，共用 0x96F39 那**一条** call ⇒ 只看 call 前一条指令会误判"从未注册"。
    ///
    /// ─────────────────────────────────────────────────────────────────────
    /// 本功能的做法
    /// ─────────────────────────────────────────────────────────────────────
    /// 1. Postfix 挂 `FieldMapManager.AddPlacement`：**只记账不干活** —— 把
    ///    type ∈ {73,74,75,76} 的 (fc, type) 记进场景级缓存。
    ///    ⇒ 这样开关中途打开也能立刻补发，不必等场景重新初始化。
    /// 2. 真正的补发在自己的 `OnUpdate` 里做（每 6 帧、每轮最多 8 个）：
    ///    读 `mAllMap`，对缓存里没发过的项 new 一个 IconInfo 后 `AddPlacement`。
    ///    ⇒ 挂在 OnUpdate 而不是 Postfix 里，是为了**不假设 mAllMap 的存在时机**
    ///      （大地图窗口可能晚于场景初始化才建好，游戏自己的代码也是先做
    ///      `op_Implicit(mAllMap)` 空判再放行）。
    /// 3. **生命周期跟着游戏走**：另挂 `FieldMapManager.RemovePlacement` 的 Postfix，
    ///    游戏移除某个 fc 时我们同步从缓存里删掉。`RemovePlacement`(0x9DF50) 的机器码
    ///    显示它**对 mMiniMap 与 mAllMap 两侧都调** `UIGameMainMapWindowBase.RemovePlacement`
    ///    ⇒ 游戏走这条路时，我们补出去的图标会被它一并 Destroy。
    ///    ⛔⛔ **但 r14 取证：敌人死亡根本不走这条路** —— `callers.py` 扫 0x9DF50 只有
    ///    Reset(0x9B862) / UpdateKeepOutComponentPlacement(0x9CF7F) / UpdateNPCComponentPlacement(0x9D686)
    ///    三个调用者。小地图图标"消失"其实是：死亡后组件销毁、`get_Position()` 返回 (0,0)、
    ///    小地图做**视野裁剪**（0x1FE2FF~0x1FE323 四条 comiss）把它裁掉；大地图显示**整张图**，
    ///    (0,0) 在左下角照常渲染 ⇒ 补发图标钉死在左下角不消失（2026-09-24 玩家实机）。
    ///    ⇒ 补 `SweepDead()`：每 2 帧判 `fc` 存活（`UnityEngine.Object.op_Implicit` 0x558030，
    ///      游戏自己的 AddPlacement/Update 全程都在做同样的空判），死了就 Invoke
    ///      `mAllMap.RemovePlacement(fc)`（0x1FDA90：Destroy(mImage.gameObject) @0x1FDC23
    ///      + RemoveAt @0x1FDC3D，与"关开关收图标"同一条已验证路径）。
    ///      ⚠ op_Implicit 拿不到/调用炸 ⇒ **保守放弃清理**（宁可多显示，不可误删活人）。
    /// 4. 开关关掉 ⇒ 对每个已补发的 fc 调一次 `mAllMap.RemovePlacement(fc)` 收干净
    ///    （0x1FDA90 会 `Destroy(mImage.gameObject)` @0x1FDC23 再 `RemoveAt` @0x1FDC3D）。
    ///    ⛔ 不能用 `FieldManager` 侧那个 `RemovePlacement`——它会连小地图的图标一起删，
    ///       那就破坏了原版状态。
    /// 5. 敌人**会移动也不用管**：`UIGameMainAllMapWindow.IconPlacementUpdate()`(0xF0C50)
    ///    每帧按 `mComponent.Position` 刷新全部 placement 的位置 ⇒ 跟随是免费的。
    ///
    /// ⚠ 待实机验证点 #1：**`new FieldMapManager.IconInfo()` 在 IL2CPP 下能否构造**
    ///    （它不派生 UnityEngine.Object，理论上可以）。失败会打一次性 Warn 并停手。
    /// </summary>
    public sealed class FeatureMapIcons : IFeature
    {
        internal static FeatureMapIcons Inst;

        public string Id { get { return "mapicons"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        /// <summary>补发节流：每 N 帧处理一轮。</summary>
        private const int TickEvery = 6;
        /// <summary>每轮最多补发几个（避免一次性 Instantiate 一大票 prefab）。</summary>
        private const int MaxPerTick = 8;

        /// <summary>大地图丢掉的四类（StellaUIManager.eIconCacheIndex）。</summary>
        private const int TypeMin = 73;   // MINIMAP_BOSS
        private const int TypeMax = 76;   // MINIMAP_RARE_ENEMY

        // ⛔ v0.3 批次 4 / E4「大地图宝箱标记」2026-11-06 实机否决并整体移除：
        //    set_FlagIndex hook 只在读档时命中一次、切场景即被清空；兜底 FindObjectsOfType
        //    反射在 IL2CPP 下取不到；游戏原生 eIconCacheIndex 71~78 本就没有宝箱图标。
        //    E3 敌人标记不受影响。
        private MelonPreferences_Entry<bool> _on;

        // ── 场景级状态 ──
        private sealed class Entry
        {
            public IntPtr Ptr;
            public int Type;
            public object Fc;
            /// <summary>我们补发出去的那个 IconInfo 代理对象（位置跟随时要读它的 mImage）。</summary>
            public object Icon;
        }

        private static readonly List<Entry> _cache = new List<Entry>();
        private static readonly HashSet<long> _emitted = new HashSet<long>();
        private static object _mgr;
        private static object _allMap;
        private static IntPtr _lastAllMap;
        private static int _tick;

        // ── 一次性诊断 ──
        private static bool _warnNew;
        private static bool _warnField;
        private static bool _warnInvoke;
        private static bool _warnEmitted;
        private static bool _warnAllMap;

        public void Init(FeatureContext ctx)
        {
            Inst = this;
            _on = ctx.Cfg.Bool("ShowEnemyIcons", false, "Show enemy icons on world map / 大地图显示敌人标记");

            Safe(ctx.Harmony, typeof(AddPlacementPatch), "FieldMapManager.AddPlacement");
            Safe(ctx.Harmony, typeof(RemovePlacementPatch), "FieldMapManager.RemovePlacement");
            SelfCheck(ctx.Harmony);
        }

        private static void Safe(HarmonyLib.Harmony h, Type patchClass, string what)
        {
            try { h.PatchAll(patchClass); }
            catch (Exception e)
            {
                QolLog.Warn("mapicons: 挂 " + what + " 失败 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>挂载自检（铁律 22 / r7）：IL2CPP 下 PatchAll 不报错 ≠ 挂上了。</summary>
        private static void SelfCheck(HarmonyLib.Harmony h)
        {
            try
            {
                bool add = false, rm = false;
                foreach (var m in h.GetPatchedMethods())
                {
                    try
                    {
                        if (m.DeclaringType == typeof(FieldMapManager))
                        {
                            if (m.Name == "AddPlacement") add = true;
                            else if (m.Name == "RemovePlacement") rm = true;
                        }
                    }
                    catch { }
                }
                QolLog.Warn("mapicons: 挂载自检 AddPlacement=" + add + " RemovePlacement=" + rm);
            }
            catch (Exception e)
            {
                QolLog.Warn("mapicons: 挂载自检异常 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Enabled
        {
            get
            {
                var f = Inst;
                if (f == null || f._on == null || !f._on.Value) return false;
                return QolConfig.Enabled && !KillSwitch.Tripped;
            }
        }

        public void OnUpdate()
        {
            try
            {
                if (!Enabled) { if (_emitted.Count > 0) Revoke(); return; }

                _tick++;
                if (_tick % TickEvery == 0) EmitPending();
                if (_tick % 2 == 0) { SweepDead(); RefreshPositions(); }   // 先清死敌再跟随，别给尸体算位置
            }
            catch (Exception e)
            {
                QolLog.Warn("mapicons: OnUpdate 异常 -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 切场景：整个缓存作废（fc 指针失效、placements 也会随窗口重建而清空）。
        /// ⛔ 注意别在这里清游戏自己的东西——我们只清自己的账。
        /// </summary>
        public void OnSceneLoaded(int buildIndex, string sceneName)
        {
            _cache.Clear();
            _emitted.Clear();
            _mgr = null;
            _allMap = null;
            _lastAllMap = IntPtr.Zero;
            _tick = 0;
        }

        /// <summary>F8 紧急关闭 / KillSwitch：把补发出去的图标收回来。</summary>
        public void OnReset()
        {
            Revoke();
        }

        public IEnumerable<MenuItem> BuildMenu()
        {
            // Immediate：补发在 OnUpdate 里做，开关一打开就生效
            yield return Item.Bool(Loc.T("f.mapicons"), _on, false, RiskTier.Safe, Effect.Immediate);
        }

        // ───────────────────────── 补发 / 收回 ─────────────────────────

        private static long KeyOf(Entry e)
        {
            return (((long)e.Ptr) << 8) | (uint)e.Type;
        }

        private static int PendingCount()
        {
            int n = 0;
            for (int i = 0; i < _cache.Count; i++)
                if (!_emitted.Contains(KeyOf(_cache[i]))) n++;
            return n;
        }

        private static void EmitPending()
        {
            var mgr = _mgr;
            if (mgr == null) return;

            // ⛔ 不能反射读（铁律 42）：代理对象的字段是 CLR 影子槽，引用字段恒 null。
            // ✅ 直读 0x58 拿到原生指针，再 ToObj 成托管代理。
            object all = NativePtr.ObjAt<UIGameMainAllMapWindow>(mgr, 0x58);
            if (all == null) { WarnField("mAllMap"); return; }
            _allMap = all;
            if (!_warnAllMap)
            {
                _warnAllMap = true;
                QolLog.Warn("mapicons: mAllMap 已拿到（" + all.GetType().Name
                            + "，缓存 " + _cache.Count + " 项，待补发 "
                            + PendingCount() + "）");
            }

            IntPtr ap = NativePtr.Of(all);
            if (ap == IntPtr.Zero) return;

            // 大地图窗口被重建（换图 / 重新打开）⇒ 之前的补发全部作废，重新来
            if (ap != _lastAllMap)
            {
                _lastAllMap = ap;
                if (_emitted.Count > 0) _emitted.Clear();
            }

            int n = 0;
            for (int i = 0; i < _cache.Count && n < MaxPerTick; i++)
            {
                var e = _cache[i];
                if (_emitted.Contains(KeyOf(e))) continue;
                if (EmitOne(all, e)) { _emitted.Add(KeyOf(e)); n++; }   // e.Icon 由 EmitOne 回填
                else break;   // 构造/Invoke 失败 ⇒ 本轮到此为止，下轮再试（铁律 34ⓒ）
            }
        }

        // ───────────────────────── 位置跟随 ─────────────────────────
        //
        // ★ 为什么必须自己做（2026-09-21 玩家实机："有敌人标识了，但不会像小地图一样动态更新"）：
        //   `UIGameMainAllMapWindow.IconPlacementUpdate()`(0xF0C50) 每帧跑，但反汇编显示它的
        //   循环体里**只有一次 `SetActive`**（0xF0E93），**完全没有位置写入**。
        //   ⇒ 大地图图标的位置是 `AddPlacement`(0xF0540) 里**算一次就固定**的：
        //        get_Position() → ConvertPositionToAllMap(Vector2) → set_localPosition
        //     这对游戏原生的设施图标无所谓（传送点/商店本来就不动），但敌人会跑 ⇒ 钉死在原地。
        //   ⇒ 修法：每帧自己重算一次 localPosition，**换算仍用游戏自己的
        //     `ConvertPositionToAllMap`**（不自己造公式，避免与地图缩放/偏移不一致）。
        //
        // ★ 顺带解决"箭头方向不跟随"（2026-09-21 第三轮实机反馈）：
        //   小地图之所以会转，是因为它的重载 `IconPlacementUpdate(float addAngle)`(0x1FE020)
        //   每帧多做了一步 `RotateIcon`：
        //       0x1FECB6  mComponent.transform
        //       0x1FECCE  get_eulerAngles()
        //       0x1FECEF  movss xmm2,[rsp+0x74]      ← objAngle = eulerAngles.**y**
        //       0x1FECF9  call RotateIcon(imageTrans, objAngle, addAngle)
        //   而 `RotateIcon`(0x1FF630) 本体是：
        //       objAngle - imageTrans.eulerAngles.z + addAngle → Rotate(0,0,那个差值)
        //   ⇒ **增量式**校正，最终把图标的 Z 欧拉角顶到 `objAngle + addAngle`。
        //   大地图**不随视角旋转**（`ConvertPositionToAllMap` 没有旋转项）⇒ addAngle = 0
        //   ⇒ 我们只需要把最终 Z 校正到 `mComponent.transform.eulerAngles.y`。
        //
        // ⚠ 为什么这里可以用反射：**属性是方法**（getter/setter），不受铁律 42 限制
        //   （铁律 42 只针对"字段"——字段是 CLR 影子槽）。所以 `Position` / `transform` /
        //   `localPosition` 走 PropertyInfo 是有效的；`mImage`(0x20) 是字段 ⇒ 仍要直读。

        private static MethodInfo _miConvert;
        private static PropertyInfo _piPosition;
        private static PropertyInfo _piTransform;
        private static bool _warnRefresh;
        private static bool _warnRot;

        private static void RefreshPositions()
        {
            var all = _allMap;
            if (all == null) return;

            for (int i = 0; i < _cache.Count; i++)
            {
                var e = _cache[i];
                if (e.Icon == null) continue;
                if (!_emitted.Contains(KeyOf(e))) continue;
                FollowOne(all, e);
            }
        }

        private static void FollowOne(object allMap, Entry e)
        {
            try
            {
                // ① mImage(0x20)：字段 ⇒ 直读指针 + ToObj（铁律 42）
                IntPtr iconPtr = NativePtr.Of(e.Icon);
                if (iconPtr == IntPtr.Zero) return;
                IntPtr imgPtr = Marshal.ReadIntPtr(IntPtr.Add(iconPtr, 0x20));
                if (imgPtr == IntPtr.Zero) return;
                var img = NativePtr.ToObj<UnityEngine.SpriteRenderer>(imgPtr);
                if (img == null) return;

                // ② mComponent.Position：属性 ⇒ 反射 GetValue 有效
                if (_piPosition == null)
                {
                    _piPosition = typeof(FieldComponent).GetProperty("Position");
                    if (_piPosition == null)
                    {
                        WarnRefresh("取不到 FieldComponent.Position");
                        return;
                    }
                }
                object pv = _piPosition.GetValue(e.Fc, null);
                if (pv == null) return;

                // ③ 换算：游戏自己的 ConvertPositionToAllMap(Vector2 worldPos)（private 实例）
                if (_miConvert == null)
                {
                    _miConvert = allMap.GetType().GetMethod(
                        "ConvertPositionToAllMap",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(UnityEngine.Vector2) }, null);
                    if (_miConvert == null)
                    {
                        WarnRefresh("取不到 ConvertPositionToAllMap");
                        return;
                    }
                }
                var src = (UnityEngine.Vector3)pv;
                object cv = _miConvert.Invoke(allMap,
                    new object[] { new UnityEngine.Vector2(src.x, src.z) });

                // ④ 写回：transform / localPosition 都是属性 ⇒ 直接赋值
                var v = (UnityEngine.Vector2)cv;
                img.transform.localPosition = new UnityEngine.Vector3(v.x, v.y, 0f);

                if (!_warnRefresh)
                {
                    _warnRefresh = true;
                    QolLog.Warn("mapicons: 位置跟随已启用（首次换算 ("
                                + src.x.ToString("0.0") + "," + src.z.ToString("0.0")
                                + ") → (" + v.x.ToString("0.0") + "," + v.y.ToString("0.0") + ")）");
                }

                // ⑤ 箭头朝向（详见本段上方注释）：
                //    TakeMinimap 的公式是「最终 Z = mComponent.transform.eulerAngles.y + addAngle」，
                //    大地图 addAngle 恒为 0 ⇒ 目标角就是 eulerAngles.y。
                //    ⛔ 别嫌区别小就省掉：大地图这一版连 SetActive 之外的逻辑都没有，
                //       朝向不补就是钉死的。
                var compTr = TransformOf(e.Fc);
                if (compTr != null)
                {
                    float yaw = compTr.eulerAngles.y;
                    var iconTr = img.transform;
                    iconTr.Rotate(0f, 0f, yaw - iconTr.eulerAngles.z);

                    if (!_warnRot)
                    {
                        _warnRot = true;
                        QolLog.Warn("mapicons: 朝向跟随已启用（首次目标 yaw="
                                    + yaw.ToString("0.0") + "，校正后 Z="
                                    + iconTr.eulerAngles.z.ToString("0.0") + "）");
                    }
                }
            }
            catch (Exception ex)
            {
                WarnRefresh(ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 取 FieldComponent 的 transform（属性 ⇒ 反射有效，见铁律 42 的边界说明）。
        /// ⛔ 就是失败了也只打印一次，不能每帧刷。
        /// </summary>
        private static UnityEngine.Transform TransformOf(object fc)
        {
            if (fc == null) return null;
            try
            {
                // 先试直接转型——代理类型的继承链是完整的 Component
                var comp = fc as UnityEngine.Component;
                if (comp != null) return comp.transform;
            }
            catch { }
            try
            {
                if (_piTransform == null)
                    _piTransform = fc.GetType().GetProperty("transform");
                if (_piTransform == null) return null;
                return _piTransform.GetValue(fc, null) as UnityEngine.Transform;
            }
            catch { return null; }
        }

        private static void WarnRefresh(string why)
        {
            if (_warnRefresh) return;
            _warnRefresh = true;
            QolLog.Warn("mapicons: 位置跟随不可用 -> " + why);
        }

        private static bool EmitOne(object allMap, Entry e)
        {
            object icon = NewIconInfo();
            if (icon == null) return false;

            // ⛔⛔ mType / mComponent 必须**直写**原生内存（铁律 42）：
            //    `FieldInfo.SetValue` 写进的是 CLR 影子槽，对原生 IconInfo 完全无效，
            //    而且不抛异常 ⇒ v0.3.0 首轮即使解决了 mAllMap，这一步也会静默失败。
            IntPtr ip = NativePtr.Of(icon);
            if (ip == IntPtr.Zero) { WarnField("IconInfo.Pointer"); return false; }
            try
            {
                Marshal.WriteInt32(IntPtr.Add(ip, 0x10), e.Type);                     // mType
                Marshal.WriteIntPtr(IntPtr.Add(ip, 0x18), e.Ptr);                      // mComponent
                Marshal.WriteIntPtr(IntPtr.Add(ip, 0x20), IntPtr.Zero);                // mImage（交给 AddPlacement 自己 new）
            }
            catch (Exception ex)
            {
                WarnField("IconInfo 直写 -> " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            try
            {
                var mi = allMap.GetType().GetMethod(
                    "AddPlacement", new[] { typeof(FieldMapManager.IconInfo) });
                if (mi == null)
                {
                    if (!_warnInvoke)
                    {
                        _warnInvoke = true;
                        QolLog.Warn("mapicons: 找不到 " + allMap.GetType().Name + ".AddPlacement(IconInfo)");
                    }
                    return false;
                }
                mi.Invoke(allMap, new[] { icon });
                e.Icon = icon;                      // 位置跟随时要读它的 mImage
                if (!_warnEmitted)
                {
                    _warnEmitted = true;
                    QolLog.Warn("mapicons: 首次补发成功（type=" + e.Type
                                + "，IconInfo 直写 + AddPlacement Invoke 均通过）");
                }
                return true;
            }
            catch (Exception ex)
            {
                if (!_warnInvoke)
                {
                    _warnInvoke = true;
                    QolLog.Warn("mapicons: AddPlacement Invoke 失败 -> "
                                + ex.GetType().Name + ": " + ex.Message);
                }
                return false;
            }
        }

        private static void Revoke()
        {
            try
            {
                var mgr = _mgr;
                if (mgr != null)
                {
                    object all = NativePtr.ObjAt<UIGameMainAllMapWindow>(mgr, 0x58);
                    if (all != null)
                    {
                        var mi = all.GetType().GetMethod("RemovePlacement", new[] { typeof(FieldComponent) });
                        if (mi != null)
                        {
                            foreach (var e in _cache)
                            {
                                if (!_emitted.Contains(KeyOf(e))) continue;
                                try { mi.Invoke(all, new[] { e.Fc }); }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            _emitted.Clear();
        }

        // ───────────────────────── 死敌图标清理（r14/r15：击败敌人后大地图图标残留） ─────────────────────────
        //
        // 取证（2026-09-24，callers.py + 反汇编，证据见文件头注释 #3）：
        //   ⛔ FieldMapManager.RemovePlacement(0x9DF50) 调用者只有 Reset / UpdateKeepOut /
        //     UpdateNPC 三个 —— 敌人死亡不走它。小地图"消失"是视野裁剪，大地图 (0,0) 在图内。
        // r14 的 op_Implicit 判据实机证伪：敌人死亡后 FieldComponent **并没有被 Destroy**
        //   （日志一条"清理"都没有 ⇒ IsAlive 恒真）。尸体留在场景里是本作的常态。
        // r15 判据（dump 实锤）：
        //   CharacterComponent.mCharacterParameter(0x170) → CharacterParameter.mHp(0x20)，
        //   mHp <= 0 ⇒ 死亡（FeatureCombat 的伤害链就写在这，扣到 0 即死）。
        //   适用于 type ∈ {73 BOSS, 74 ENEMY, 76 RARE_ENEMY}；75(NPC) 非战斗单位跳过 hp 判据。
        //   ⛔ 玩家图标不在补发范围（枚举 73~76 无玩家项），不会误删自己。
        //   ⚠ 保守原则：判不了 = 当活着，绝不误删活人的图标。

        private static MethodInfo _miOpImplicit;
        private static bool _aliveBroken;
        private static int _deadRemovedTotal;

        /// <summary>战斗系图标类型（这些才有 mHp 判据意义）。</summary>
        private static bool IsCombatType(int t) { return t == 73 || t == 74 || t == 76; }

        private static bool IsAlive(Entry e)
        {
            object fc = e.Fc;
            if (fc == null) return false;

            // 第一层：Unity 对象销毁判空（尸体虽不销毁，但切场景等场合仍可能失效）
            if (fc is UnityEngine.Object)
            {
                if (_miOpImplicit == null)
                {
                    if (_aliveBroken) return true;
                    try
                    {
                        _miOpImplicit = typeof(UnityEngine.Object).GetMethod(
                            "op_Implicit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            null, new[] { typeof(UnityEngine.Object) }, null);
                    }
                    catch { }
                    if (_miOpImplicit == null)
                    {
                        _aliveBroken = true;
                        QolLog.Warn("mapicons: 拿不到 Object.op_Implicit ⇒ 死敌清理停用（保守放行）");
                        return true;
                    }
                }
                try
                {
                    object r = _miOpImplicit.Invoke(null, new[] { fc });
                    if (r is bool && !(bool)r) return false;   // 组件已销毁 ⇒ 死
                }
                catch (Exception ex)
                {
                    if (!_aliveBroken)
                    {
                        _aliveBroken = true;
                        QolLog.Warn("mapicons: op_Implicit 调用失败 -> " + ex.GetType().Name + ": "
                                    + ex.Message + " ⇒ 停用第一层判空");
                    }
                    // 不 return：第一层废了还有第二层 mHp 判据
                }
            }

            // 第二层：mHp 判据（r15 新增，战斗系图标专用）
            if (!IsCombatType(e.Type)) return true;            // NPC 等非战斗单位：保守放行
            if (e.Ptr == IntPtr.Zero) return true;             // 拿不到指针：保守放行
            try
            {
                IntPtr paramPtr = Marshal.ReadIntPtr(e.Ptr, 0x170);   // CharacterComponent.mCharacterParameter
                if (paramPtr == IntPtr.Zero) return true;             // 没有参数块（不该发生）：保守放行
                int hp = Marshal.ReadInt32(paramPtr, 0x20);           // CharacterParameter.mHp
                int maxHp = Marshal.ReadInt32(paramPtr, 0x24);        // mMaxHp（sanity 校验用）
                // sanity：hp/maxHp 都得在合理区间，否则视为读数不可信（指针错位/类型不符）⇒ 保守放行
                if (maxHp <= 0 || maxHp > 0x1000000 || hp > maxHp) return true;
                return hp > 0;
            }
            catch (Exception ex)
            {
                if (!_hpBroken)
                {
                    _hpBroken = true;
                    QolLog.Warn("mapicons: mHp 直读失败 -> " + ex.GetType().Name + ": " + ex.Message
                                + " ⇒ hp 判据停用");
                }
                return true;   // 保守：当活着
            }
        }

        private static bool _hpBroken;

        private static void SweepDead()
        {
            if (_emitted.Count == 0) return;
            var all = _allMap;
            if (all == null) return;

            MethodInfo mi = null;
            int removed = 0;
            try
            {
                for (int i = _cache.Count - 1; i >= 0; i--)
                {
                    var e = _cache[i];
                    if (e == null || !_emitted.Contains(KeyOf(e))) continue;
                    if (IsAlive(e)) continue;

                    if (mi == null)
                    {
                        mi = all.GetType().GetMethod("RemovePlacement", new[] { typeof(FieldComponent) });
                        if (mi == null)
                        {
                            _aliveBroken = true;
                            QolLog.Warn("mapicons: 拿不到 mAllMap.RemovePlacement ⇒ 死敌清理停用");
                            return;
                        }
                    }
                    try { mi.Invoke(all, new[] { e.Fc }); } catch { }   // 游戏侧 Destroy+RemoveAt；尸体上失败就算了
                    _emitted.Remove(KeyOf(e));
                    _cache.RemoveAt(i);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                QolLog.Warn("mapicons: SweepDead 异常 -> " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            if (removed > 0)
            {
                _deadRemovedTotal += removed;
                QolLog.Warn("mapicons: 清理死敌大地图图标 " + removed + " 个（累计 " + _deadRemovedTotal + "）");
            }
        }

        // ───────────────────────── 对象 / 字段工具（反射优先 + 直读兜底，规则 15/18） ─────────────────────────

        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>new 一个 IconInfo（IL2CPP 非 UnityEngine 派生类型，理论可行 ⇒ 待实机验证点 #1）。</summary>
        private static object NewIconInfo()
        {
            try
            {
                var t = typeof(FieldMapManager.IconInfo);
                return Activator.CreateInstance(t);
            }
            catch (Exception ex)
            {
                if (!_warnNew)
                {
                    _warnNew = true;
                    QolLog.Warn("mapicons: new IconInfo 失败 -> " + ex.GetType().Name + ": "
                                + ex.Message + " ⇒ 大地图敌人标记不可用");
                }
                return null;
            }
        }

        /// <summary>
        /// 一次性诊断。
        /// ⛔ 铁律 42（2026-09-20 实锤）：unhollower 代理的实例字段**反射拿不到原生值** ——
        ///    引用字段 `GetValue()` 恒 null、值类型恒默认值，`SetValue()` 写的是 CLR 影子槽
        ///    （不报错、不生效）。v0.3.0 首轮 E3/D9 全线失效就是栽在这里。
        ///    ⇒ 读引用字段：`Marshal.ReadIntPtr(ptr+off)` + `NativePtr.ToObj&lt;T&gt;()`；
        ///      写任何字段：`Marshal.Write*` 直写。反射**不得**作为写操作的首选。
        /// </summary>
        private static void WarnField(string what)
        {
            if (_warnField) return;
            _warnField = true;
            QolLog.Warn("mapicons: " + what + " 取不到（直读 0x58 + ToObj 失败）⇒ 本轮不补发");
        }

        // ───────────────────────── 补丁 ─────────────────────────

        [HarmonyPatch(typeof(FieldMapManager), "AddPlacement",
            new[] { typeof(FieldComponent), typeof(StellaUIManager.eIconCacheIndex) })]
        private static class AddPlacementPatch
        {
            private static bool _live;

            private static void Postfix(FieldMapManager __instance,
                                        FieldComponent fc,
                                        StellaUIManager.eIconCacheIndex type)
            {
                try
                {
                    if (__instance == null || fc == null) return;
                    _mgr = __instance;

                    int t = (int)type;
                    if (t < TypeMin || t > TypeMax) return;   // 只关心被大地图丢弃的四类

                    if (!_live)
                    {
                        _live = true;
                        QolLog.Warn("mapicons: AddPlacement hook live  (type=" + t + ")");
                    }

                    IntPtr p = NativePtr.Of(fc);
                    if (p == IntPtr.Zero) return;

                    for (int i = 0; i < _cache.Count; i++)
                    {
                        if (_cache[i].Ptr == p && _cache[i].Type == t) return;   // 已记过
                    }
                    _cache.Add(new Entry { Ptr = p, Type = t, Fc = fc });
                }
                catch (Exception e)
                {
                    QolLog.Warn("mapicons: AddPlacement Postfix 异常 -> "
                                + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>
        /// 游戏移除某个 fc 时同步销账。
        /// ⛔ 这里**不做任何 RemovePlacement 调用**——`FieldMapManager.RemovePlacement`(0x9DF50)
        /// 自己会对 mMiniMap 与 mAllMap 两侧各调一次 `UIGameMainMapWindowBase.RemovePlacement`
        /// ⇒ 我们补出去的那一份会随之被 Destroy，天然不会残留。
        /// </summary>
        [HarmonyPatch(typeof(FieldMapManager), "RemovePlacement", new[] { typeof(FieldComponent) })]
        private static class RemovePlacementPatch
        {
            private static void Postfix(FieldComponent fc)
            {
                try
                {
                    if (fc == null) return;
                    IntPtr p = NativePtr.Of(fc);
                    if (p == IntPtr.Zero) return;

                    for (int i = _cache.Count - 1; i >= 0; i--)
                    {
                        if (_cache[i].Ptr != p) continue;
                        _emitted.Remove(KeyOf(_cache[i]));
                        _cache.RemoveAt(i);
                    }
                }
                catch { }
            }
        }

    }
}
