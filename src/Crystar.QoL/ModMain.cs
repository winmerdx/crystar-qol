using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CrystarQoL.Features;
using Game;
using HarmonyLib;
using MelonLoader;
using UnityEngine.SceneManagement;

namespace CrystarQoL
{
    /// <summary>只做编排：建配置 → 注册功能 → 转发生命周期。具体逻辑全在各 Feature 里。</summary>
    public class ModMain : MelonMod
    {
        private readonly List<IFeature> _features = new List<IFeature>();
        private readonly StringBuilder _sb = new StringBuilder();
        private HarmonyLib.Harmony _harmony;
        private Menu _menu;
        private Overlay _overlay;
        private bool _booted;
        private bool _menuWasOpen;
        private int _gateTick;
        private bool _overlayEnabled = true;

        // ── r17 诊断包：冻结现场自证（r10 起"返回标题卡死"已复现 5 次，r16 证伪了扫表假设）──
        /// <summary>最近一次 OnUpdate 的 Time.realtimeSinceStartup（帧间隔 stall 探测用）。</summary>
        private float _lastFrameRealtime;
        private int _stallWarnAt;
        /// <summary>最近一次场景加载的时刻 / 名字（心跳里打"载入后经过秒数"）。</summary>
        private float _sceneRealtime = -1000f;
        private string _sceneName = "?";
        /// <summary>给 feature 读的"当前场景名"（atkspeed 在 Title 后首轮 Sync 打一条可见日志）。</summary>
        public static string LastSceneName = "?";

        /// <summary>
        /// 逃生开关：在 UserData/Crystar.QoL/ 下放一个 overlay.off 标记文件即可关掉
        /// 屏幕覆盖层（怀疑 HUD 导致异常时用来二分定位），无需重编译。
        ///
        /// 注：输入闸门的模式选择（gate.pause / gate.patch）在 GameInputGate.Init() 里读，
        /// 不在这里。
        /// </summary>
        private static bool MarkerOff(string name)
        {
            try { return File.Exists(Path.Combine(QolConfig.Dir, name)); }
            catch { return false; }
        }

        /// <summary>r18：读 `features.skip.txt`（feature id，逗号/空白/换行分隔）作为"不加载清单"。</summary>
        private static HashSet<string> SkippedIds()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = Path.Combine(QolConfig.Dir, "features.skip.txt");
                if (!File.Exists(p)) return set;
                string[] toks = File.ReadAllText(p).Split(
                    new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < toks.Length; i++) set.Add(toks[i].Trim());
            }
            catch { }
            return set;
        }

        public override void OnInitializeMelon()
        {
            try
            {
                QolConfig.Init();
                QolLog.Init(QolConfig.Dir);

                // 尽早隐藏控制台：OnInitializeMelon 比 OnUpdate 早很多（引擎循环还没跑起来），
                // 能把可见窗口压缩到 MelonLoader 初始化那一两秒。每帧验证逻辑仍在 OnUpdate 兜底。
                ConsoleHider.Run();

                _harmony = new HarmonyLib.Harmony(QolConfig.HarmonyId);
                _menu = new Menu();
                _overlay = new Overlay();
                _overlayEnabled = !MarkerOff("overlay.off");

                // 安全模式（r14 加）：UserData/Crystar.QoL/features.off 存在 ⇒ 一个 feature 都不加载
                //   （不挂任何 Harmony 补丁、不注册营地页），热键 / F2 面板 / 看门狗照常。
                //   用途：卡标题这类"游戏原生 UI 卡住、mod 侧零异常"的故障二分——
                //   放上标记文件重启复现，不卡 ⇒ 是 feature 的；仍卡 ⇒ 游戏自身的路径问题。
                bool safeMode = MarkerOff("features.off");
                if (safeMode) QolLog.Warn("安全模式：检测到 features.off ⇒ 本轮全部 feature 停用（仅热键/菜单/看门狗生效）");
                _features.Add(new FeatureCore());
                // 减肝最小集（v0.1）
                _features.Add(new FeatureExpMoney());     // 经验/金钱倍率 —— 本 mod 的核心
                _features.Add(new FeatureDrops());        // 道具掉落数量倍率 + 掉率一键拉满（v0.1.9）
                _features.Add(new FeatureCombat());       // 秒杀 / 强杀 / 伤害倍率 / 玩家无敌
                // v0.2 批次 1「哭泣与战斗手感」（B2/B3/B4/B7；B7 并入 FeatureCombat）
                _features.Add(new FeatureCry());          // 哭泣槽常满 + CryMode 常驻（v0.2）
                _features.Add(new FeatureCombo());        // 连击不中断（v0.2）
                // v0.2 批次 2「自动拾取与经济」（B5/C2/C6/C7）
                _features.Add(new FeaturePickup());       // 自动拾取掉落（B5）
                _features.Add(new FeatureEconomy());      // 商店全解锁 / 金钱直写 / 物品生成（C2/C6/C7）
                _features.Add(new FeatureGrowth());       // 强化零消耗 / 炼成无材料 / 全技能解锁（C1/C3/C4，批次3）
                _features.Add(new FeatureMoveSpeed());    // 移速倍率
                _features.Add(new FeatureAttackSpeed());  // 攻速倍率（改技能序列表 mAnimationSpeed 0x44，只改玩家技能）
                _features.Add(new FeatureKillCounter());  // 击杀计数倍率
                _features.Add(new FeatureAlbum());        // 回忆录全解锁（v0.1.9）
                _features.Add(new FeatureMapReveal());    // 全图去迷雾（v2 方案）
                // v0.3 批次 1「流程加速」（D8 开场播片自动跳过）
                _features.Add(new FeatureOpeningSkip());  // D8：自动结束开场播片（Invoke OnEndMovie）
                _features.Add(new FeatureCrySkip());      // D9：自动跳过幽怨演出（置 IsPlaying=false）
                _features.Add(new FeatureMapIcons());     // E3：把敌人/BOSS/NPC 图标补发到大地图
                _features.Add(new FeatureCampConfig());   // 营地菜单 MOD設定 页（复用游戏设置窗口）

                var ctx = new FeatureContext { Harmony = _harmony, Menu = _menu };

                // r19 诊断：标题场景探针。它与"二分"无关、常驻生效——即使所有 feature 都停用，
                // 也要能回答"TitleScene 自己还在不在跑"（铁律 22：挂没挂上要看日志）。
                // ⛔ 注意：core 之外的 feature 可以被停用，但**不能**因此把诊断 patch 也停掉，
                //    否则"停用后正常"这件事就失去了可比性。
                // ⛔ 三个 patch 类各挂一次：PatchAll(Type) 只认该类型上的标注，
                //    绝不能把同一个方法标两次（否则 Postfix 跑两遍、计数翻倍）。
                foreach (var pt in new[]
                        { typeof(TitleSceneProbeUpdate), typeof(TitleSceneProbeEnter), typeof(TitleSceneProbeExit) })
                {
                    try
                    {
                        _harmony.PatchAll(pt);
                        QolLog.Warn("diag: TitleScene 探针 patch ok —— " + pt.Name);
                    }
                    catch (Exception e)
                    {
                        QolLog.Warn("diag: TitleScene 探针挂载失败 " + pt.Name + " -> "
                                    + e.GetType().Name + ": " + e.Message);
                    }
                }

                // 二分模式（r18）：`UserData/Crystar.QoL/features.skip.txt` 里写 feature id
                // （逗号/空白/换行分隔）⇒ 这些 feature 一个都不加载。用于"卡标题"这类
                // 每轮复现都要重开游戏的故障，把嫌疑范围按半砍，不用重编译。
                var skip = SkippedIds();
                if (skip.Count > 0)
                {
                    var names = new List<string>();
                    for (int i = _features.Count - 1; i >= 0; i--)
                        if (skip.Contains(_features[i].Id)) { names.Add(_features[i].Id); _features.RemoveAt(i); }
                    QolLog.Warn("二分模式：features.skip.txt ⇒ 已停用 " + names.Count + " 个 feature："
                                + string.Join(", ", names.ToArray()));
                }

                if (safeMode)
                {
                    // ⛔ 必须把非 core 的 feature **从列表里移除**，而不是只跳过 Init：
                    //   它们还会被 _menu.Build / OnUpdate / RevokeAllFeatures 枚举，
                    //   未 Init 的 feature 持有空配置项 ⇒ 菜单构建直接 NRE。
                    for (int i = _features.Count - 1; i >= 0; i--)
                        if (_features[i].Id != "core") _features.RemoveAt(i);
                }
                foreach (var f in _features)
                {
                    // r20：逐个点名"真的 Init 了谁"。二分结论的前提是"停用 = 不加载"，
                    //   而 r19 实机显示：被停用的 opening / map / mapicons 的**补丁日志照打**
                    //   ⇒ 必须能直接看到 Init 名单，否则"停了还不卡"到底排除了谁说不清。
                    QolLog.Warn("init: " + f.Id);
                    try
                    {
                        ctx.Cfg = QolConfig.ForFeature(f.Id);
                        f.Init(ctx);
                    }
                    catch (Exception e)
                    {
                        QolLog.Error("feature '" + f.Id + "' init failed: " + e.Message);
                    }
                }

                // 修正十六（r17）：紧急关闭（F8 / 总开关）时，除了恢复偏好值，还必须**逐个 feature
                // 撤销运行时副作用** —— 否则"改过数据表"的 feature（FeatureGrowth 的强化表 /
                // 思装变容表 / 学习表）会因为 `Master=false` 之后 OnUpdate 不再跑而永远停在 0
                // （与玩家报的「关掉还是 0 消耗」同源）。这里注入回调，Trip() 时调用。
                KillSwitch.RevokeAll = RevokeAllFeatures;

                // 自有配置文件回读：必须在所有 feature 注册完之后、菜单构建之前
                // （v0.1.6 起持久化不再依赖 MelonPreferences，见 QolConfig 注释）
                QolConfig.Load();

                // 输入闸门：只读标记文件决定模式并做好准备工作，
                // 【此刻不装任何 patch、也不改 GameSpeed】——实际的装卸发生在菜单开关时。
                // 见 GameInputGate 类注释：常驻 detour 会让游戏输入全失效。
                GameInputGate.Init();

                _menu.Build(_features);
                SaveBackup.EnsureFirstRun();

                _booted = true;
                MelonLogger.Msg("[QoL] ready - " + _features.Count + " features, " + _menu.Count + " menu items, "
                                + "input gate: " + GameInputGate.Mode.ToString().ToUpperInvariant());
                MelonLogger.Msg("[QoL] " + Loc.T("core.keymenu") + ": " + QolInput.Name(QolConfig.KeyMenu.Value)
                                + "   " + Loc.T("core.keykill") + ": " + QolInput.Name(QolConfig.KeyKillSwitch.Value));
            }
            catch (Exception e)
            {
                MelonLogger.Error("[QoL] boot failed: " + e);
            }
        }

        /// <summary>
        /// 修正十六（r17）：逐个 feature 调 <c>OnReset()</c> 撤销运行时副作用（由 <c>KillSwitch.Trip()</c> 调用）。
        /// 背景：`IFeature.OnReset()` 此前**全项目没有任何调用点**（2026-09-11 核实），各 feature 的还原
        /// 全挤在 `OnUpdate` 的"开关跳变"处 —— 而紧急关闭之后 `Master=false` ⇒ ModMain 不再调 OnUpdate
        /// ⇒ "改数据表"类功能（FeatureGrowth 的强化表 / 思装变容表 / 学习表）会留下一个**再也不会被撤销**
        /// 的 0 值表，玩家看到的就是"关掉了还是 0 消耗"。这里把它接上。
        /// ⚠ 每个调用都 try/catch：一个 feature 抛异常不能拦住其它 feature 的撤销。
        /// </summary>
        /// <summary>
        /// v0.3.1-r12 冻结看门狗（每秒一次，**只在异常时**打日志，正常游戏 0 噪音）。
        ///
        /// 背景：玩家报"战斗中退出到标题 ⇒ 标题菜单加载不出来、只能杀进程"，而两个日志
        /// 都干净（mod 心跳每 2 秒照打、Latest.log 无异常）。r11 怀疑是 gate.pause 闸门，
        /// 但实机日志里**没有** `input gate: engaged` ⇒ 假设被证伪，必须换取证手段。
        ///
        /// ⛔ 关键认知：MelonMod 的 OnUpdate 走真实时间，游戏冻死了它也照跑
        ///   ⇒ "日志在打"证明不了"游戏没卡"。所以这里主动读 `Time.timeScale` /
        ///   `Global.GameSpeed`，一旦明显偏低就告警 —— 有了这一行，下次复现就能一眼区分：
        ///     · 出现 ⇒ 游戏逻辑帧被冻结（去查谁写小了 GameSpeed/timeScale）；
        ///     · 不出现 ⇒ 冻结假设排除，是 UI 层自己卡住（另查补丁/场景）。
        /// </summary>
        private void FreezeWatchdog()
        {
            try
            {
                float ts = UnityEngine.Time.timeScale;
                float gs = GameInputGate.ReadGameSpeed();
                bool bad = ts < 0.5f || (gs >= 0f && gs < 0.5f);
                if (bad)
                {
                    QolLog.Warn("watchdog: 游戏可能被冻结 —— timeScale=" + ts.ToString("0.####")
                                + " GameSpeed=" + gs.ToString("0.####")
                                + " 菜单开=" + _menu.Open + " 闸门=" + GameInputGate.Mode
                                + " 闸门生效=" + GameInputGate.Active);
                }
                else if (_frozenDiag)
                {
                    _frozenDiag = false;
                    QolLog.Warn("watchdog: 已恢复正常（timeScale=" + ts.ToString("0.####")
                                + " GameSpeed=" + gs.ToString("0.####") + "）");
                }
                _frozenDiag = bad;
            }
            catch { }
        }

        private bool _frozenDiag;
        private int _keyProbeAt;

        // ── r19：标题场景自身的心跳（TitleScene.OnUpdate 命中计数）──
        /// <summary>`Game.TitleScene.OnUpdate` 累计命中次数 —— 涨 ⇒ 标题场景自己在跑，卡在 UI 层；停 ⇒ 卡在更上层。</summary>
        public static int TitleUpdateHits;
        public static int TitleEnterHits;
        public static int TitleExitHits;
        private int _titleUpdLast;
        private int _titleLogAt;
        private int _titleSnapCount;
        private float _titleSnapRealtime = -1000f;

        /// <summary>
        /// r18：标题场景探针。心跳已证明"主线程活着"，接下来要回答的是**游戏逻辑层卡在哪**：
        /// · 按键探针 —— 玩家的键到底有没有进引擎（区分"没按键"与"游戏收不到"）；
        /// · 停留 12s 一次性快照 —— 各场景 loaded 状态 + `GameSceneManager` 状态机
        ///   （`mSceneState` 0x40 / `mNextSceneType` 0x48）：若 state 卡在非 DEFAULT，
        ///   就是铁律 45 那类"场景切换请求被门禁吃了 / 异步加载没完成"，菜单自然永远不出。
        /// </summary>
        private void TitleProbe()
        {
            try
            {
                // ⚠ IL2CPP 的 UnityEngine 桩没有 Input.anyKeyDown（编译不过），只探三个常用键。
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Return)
                    || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Space)
                    || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape))
                {
                    int t = Environment.TickCount;
                    if (t - _keyProbeAt > 2000)
                    {
                        _keyProbeAt = t;
                        QolLog.Warn("title: 检测到确认/取消键（Enter/Space/Esc）—— 键盘输入能进引擎");
                    }
                }
            }
            catch { }

            // r19：标题场景自己的"心跳" —— 每 5 秒报一次 TitleScene.OnUpdate 的命中增量。
            //   · 持续增长 ⇒ 标题场景本体在跑，卡的是它内部的 UI/演出；
            //   · 归零不涨  ⇒ 上层（GameSceneManager / 场景对象）已经不再驱动它。
            try
            {
                int tk = Environment.TickCount;
                if (tk - _titleLogAt > 5000)
                {
                    int d = TitleUpdateHits - _titleUpdLast;
                    _titleUpdLast = TitleUpdateHits;
                    _titleLogAt = tk;
                    QolLog.Warn("title.scene: OnUpdate 本 5s 命中 " + d + " 次（累计 " + TitleUpdateHits
                                + " / OnEnter " + TitleEnterHits + " / OnExit " + TitleExitHits + "）");
                }
            }
            catch { }

            // r19：快照改为每 12s 重复一次（最多 10 次）—— 一次性快照看不出状态是否在变化。
            if (_titleSnapCount >= 10) return;
            if (UnityEngine.Time.realtimeSinceStartup - _titleSnapRealtime < 12f) return;
            _titleSnapRealtime = UnityEngine.Time.realtimeSinceStartup;
            _titleSnapCount++;

            var sb = new StringBuilder();
            try
            {
                // ⚠ IL2CPP 的 UnityEngine 桩缺 sceneCount/GetSceneAt（编译不过）⇒ 走反射，取不到就记一行。
                var tSM = typeof(SceneManager);
                object oc = StaticCall(tSM, "sceneCount");
                if (oc is int)
                {
                    int n = (int)oc;
                    sb.Append("场景数=").Append(n);
                    var mi = tSM.GetMethod("GetSceneAt", new[] { typeof(int) });
                    for (int i = 0; i < n && mi != null; i++)
                    {
                        object s = mi.Invoke(null, new object[] { i });
                        sb.Append(" [").Append(s == null ? "?" : s.ToString())
                          .Append(" loaded=").Append(InstVal(s, "isLoaded"))
                          .Append(']');
                    }
                }
                else sb.Append("场景数=(取不到)");
            }
            catch (Exception e) { sb.Append(" 场景读取失败:").Append(e.GetType().Name); }

            try
            {
                var gsm = Common.GameSceneManager.Instance;
                if (gsm == null) sb.Append(" GSM=null");
                else
                {
                    IntPtr p = NativePtr.Of(gsm);
                    IntPtr cs = Marshal.ReadIntPtr(IntPtr.Add(p, 0x50));
                    sb.Append(" GSM state=").Append(Marshal.ReadInt32(IntPtr.Add(p, 0x40)))
                      .Append(" cur=").Append(Marshal.ReadInt32(IntPtr.Add(p, 0x44)))
                      .Append(" next=").Append(Marshal.ReadInt32(IntPtr.Add(p, 0x48)))
                      .Append(" curScene=").Append(cs != IntPtr.Zero)
                      .Append("/0x").Append(cs.ToInt64().ToString("X"))
                      .Append(" nextScene=").Append(Marshal.ReadIntPtr(IntPtr.Add(p, 0x58)) != IntPtr.Zero);
                }
            }
            catch (Exception e) { sb.Append(" GSM 读取失败:").Append(e.GetType().Name).Append(":").Append(e.Message); }

            try
            {
                var gsm = Common.GameSceneManager.Instance;
                if (gsm != null)
                {
                    object cur = InstVal(gsm, "CurrentScene");   // 属性 = getter 方法 ⇒ 反射有效（铁律 43）
                    if (cur == null) sb.Append(" 当前场景对象=null");
                    else
                    {
                        IntPtr cp = NativePtr.Of(cur);
                        byte b10 = Marshal.ReadByte(IntPtr.Add(cp, 0x10));
                        byte b11 = Marshal.ReadByte(IntPtr.Add(cp, 0x11));
                        sb.Append(" 当前场景对象=").Append(cur.GetType().Name)
                          .Append(" ptr=0x").Append(cp.ToInt64().ToString("X"))
                          .Append(" isTS=").Append(cur is TitleScene)
                          .Append(" mTitleSkip=").Append(b10 != 0)
                          .Append(" mSkiped=").Append(b11 != 0)
                          .Append(" raw=0x").Append(b10.ToString("X2")).Append(b11.ToString("X2"));
                    }
                }
            }
            catch (Exception e) { sb.Append(" TitleScene 读取失败:").Append(e.GetType().Name); }

            sb.Append(" ts=").Append(UnityEngine.Time.timeScale.ToString("0.###"))
              .Append(" gs=").Append(GameInputGate.ReadGameSpeed().ToString("0.###"))
              .Append(" 菜单开=").Append(_menu.Open);
            // r23：标题菜单「有没有出现」的客观读数 —— 之前所有二分都缺这个锚点
            AppendTitleUiSnapshot(sb);
            // growth 命中计数只作旁证；r21「凶手 = growth」已被 11-08 / 11-09 日志证伪
            try { sb.Append(FeatureGrowth.Hot.Snapshot()); }
            catch { }
            QolLog.Warn("title: 【标题快照 #" + _titleSnapCount + "】" + sb.ToString());
        }

        /// <summary>
        /// r23（2026-11-09）：把「标题菜单到底有没有出现」变成日志里可判定的客观量。
        ///
        /// 为什么必须加：11-08 12:42（卡 107s）/ 12:46（玩家判「不卡」）/ 11-08 14:23 /
        /// 11-09 22:52（新 DLL 仍卡 107s）四段回标题日志**逐行同构**，
        /// 现有快照只有 GSM / 场景基类字段 / mod 自己的菜单开合，没有任何游戏标题 UI 的读数
        /// ⇒ 历次二分只能靠「看起来能不能退出」当判据，`shutdown` 又不能作数（Alt+F4 也走它）。
        ///
        /// 反编译证据（`crystar-mod/dump/dump.cs`）：
        ///   · `Game.StellaUIManager`（dump:190667，SingletonMonoBehaviour）
        ///       字段 `mEnableLayerFlag` 0xB8 / `mLastEnableLayerID` 0xC0 /
        ///       `mEnableLayerIDSumValue` 0xC8 / **`mTitleWindow` 0x100**（属性 TitleWindow）；
        ///       `SetActiveLayer(eLayerID,bool)` 0x110540、`IsActiveLayer` 0x10F340。
        ///   · `UILayerSetting.eLayerID.LAYER_TITLE = 8`（dump:150599）
        ///       ⇒ `TitleScene.OnEnter` 0x1154E0 / `OnDeactiveScene` 0x115460 在
        ///         `mTitleSkip==0` 时调 `SetActiveLayer(8, true/false)`。
        ///   · `UITitleWindow`（dump:203348）：`mLogoWindow` 0xA0 / **`mMenuWindow` 0xA8** /
        ///       `mBackground` 0xB0 / `mIsFadeOutEnd` 0xB8；
        ///       `LogoClose()` 0x1E92C0 = 关 logo 演出、放菜单，`OpenWindow()` 0x1E9570。
        ///   · `TitleScene`（dump:176202）：`mTitleSkip` 0x10 / `mSkiped` 0x11；
        ///       `IsStartEnterScene` 0x115200 判 `StellaUIManager[+0x100]` 是否为空；
        ///       `OnUpdate` 0x1158A0 在 mTitleSkip!=0 && mSkiped==0 且 7 个标志位全 0 时
        ///       才 `OpenChangeLanguageWindow` 0x1E2270。
        ///
        /// ⛔ 全程只读：`Marshal` 直读 + `InstVal` 反射属性，不调用任何游戏方法、
        ///    不写内存、不改状态；每段独立 try/catch，读不到只记一行，绝不让探针影响游戏。
        /// </summary>
        private static void AppendTitleUiSnapshot(StringBuilder sb)
        {
            try
            {
                var ui = StellaUIManager.Instance;
                if (ui == null) { sb.Append(" UI=(null)"); return; }
                IntPtr q = NativePtr.Of(ui);
                if (q == IntPtr.Zero) { sb.Append(" UI.ptr=null"); return; }

                IntPtr win = Marshal.ReadIntPtr(IntPtr.Add(q, 0x100));   // mTitleWindow
                IntPtr flag = Marshal.ReadIntPtr(IntPtr.Add(q, 0xB8));   // mEnableLayerFlag (BitFlag)
                int lastLayer = Marshal.ReadInt32(IntPtr.Add(q, 0xC0));  // mLastEnableLayerID
                int layerSum = Marshal.ReadInt32(IntPtr.Add(q, 0xC8));    // mEnableLayerIDSumValue

                sb.Append(" UI.Win=").Append(win == IntPtr.Zero ? "null" : "0x" + win.ToInt64().ToString("X"))
                  .Append(" LayerLast=").Append(lastLayer)      // 8 = LAYER_TITLE
                  .Append(" LayerSum=").Append(layerSum)
                  .Append(" LayerFlag=").Append(flag == IntPtr.Zero ? "null" : "0x" + flag.ToInt64().ToString("X"));

                if (win != IntPtr.Zero)
                {
                    sb.Append(" LogoWin=").Append(Marshal.ReadIntPtr(IntPtr.Add(win, 0xA0)) != IntPtr.Zero)
                      .Append(" MenuWin=").Append(Marshal.ReadIntPtr(IntPtr.Add(win, 0xA8)) != IntPtr.Zero)
                      .Append(" BgWin=").Append(Marshal.ReadIntPtr(IntPtr.Add(win, 0xB0)) != IntPtr.Zero)
                      .Append(" FadeOutEnd=").Append(Marshal.ReadByte(IntPtr.Add(win, 0xB8)) != 0);
                }
            }
            catch (Exception e) { sb.Append(" UI读取失败:").Append(e.GetType().Name); }

            // 反射读窗口 GameObject.activeSelf —— 「菜单是否真的显示」的直接判据
            object titleWinObj = null;
            try
            {
                var ui = StellaUIManager.Instance;
                titleWinObj = InstVal(ui, "TitleWindow");
                object go = InstVal(titleWinObj, "gameObject");
                object act = InstVal(go, "activeSelf");
                sb.Append(" WinActive=").Append(act == null ? "?" : act.ToString());
            }
            catch (Exception e) { sb.Append(" WinActive读取失败:").Append(e.GetType().Name); }

            // r24：三个子窗口 activeSelf / 菜单状态机 / 任务系统（见 AppendTitleChildSnapshot）
            try
            {
                var ui = StellaUIManager.Instance;
                if (ui != null)
                {
                    IntPtr q = NativePtr.Of(ui);
                    if (q != IntPtr.Zero)
                    {
                        IntPtr win2 = Marshal.ReadIntPtr(IntPtr.Add(q, 0x100));
                        if (win2 != IntPtr.Zero) AppendTitleChildSnapshot(sb, titleWinObj, win2);
                    }
                }
            }
            catch (Exception e) { sb.Append(" r24读取失败:").Append(e.GetType().Name); }
        }

        /// <summary>
        /// r24（2026-11-09 第二轮）：r23 读数证明窗口对象 / 层 8 / 父 GameObject 全都正常，
        /// 但菜单仍不显示 ⇒ 断点只能在标题窗口**内部**：logo 淡出任务、菜单窗口自己的
        /// 状态机、以及任务系统是否还在推进。本方法把这三层变成可读的客观量。
        ///
        /// 反编译证据（`crystar-mod/dump/dump.cs`）：
        ///   · `UITitleWindow`（dump:203348）：`mLogoWindow` 0xA0 / `mMenuWindow` 0xA8 /
        ///     `mBackground` 0xB0 / `mIsFadeOutEnd` 0xB8。
        ///   · `UITitleRootWindow`（dump:203013，即 mMenuWindow）：**`mState` 0xD0**，
        ///     枚举 `eState`（dump:203190）`OPEN_WAIT=0 … MENU=5 … STORE_WAIT=11`；
        ///     `AddWaitFlagTask(Func&lt;bool&gt;,Action)` 0x1E59B0 / `AddOpenWaitTask()` 0x1E5930
        ///     ⇒ 菜单显示本身就是一条 Task 等待链。
        ///   · `StellaUIComponent`（dump:159344）：`mReceiveInput` 0x90（输入接收闸）。
        ///   · `UILogoWindow`（dump:196956）：`mShowIndex` 0xA8（logo 播到第几张）。
        ///   · `UIComponent : TaskComponent`（dump:148164 / 148073）⇒ 窗口自身就有
        ///     `TaskKicker` 属性（dump:148079）；`TaskKicker`（dump:154569）持有
        ///     `mTaskList` 0x18 / `mAddTaskList` 0x28 等 5 个列表，
        ///     `GetTotalTaskCount()` 0x142EC0 / `TaskComponent.IsExistTask()` 0x142B40。
        ///   · `OpenWindow()` 0x1E9570 按全局状态分派（==2 播 logo / ==3 开菜单 / 其他空跑）；
        ///     真正显示菜单的是回调 `<OpenWindow>m__1` 0x1E9910 的
        ///     `mMenuWindow.gameObject.SetActive(true)`；`LogoClose()` 0x1E92C0 会清零 0xB8。
        ///
        /// ⛔ 仍全程只读：指针转代理走项目既有的 `NativePtr.ToObj`（铁律 42），
        ///    子窗口 active 走 `InstVal` 反射属性；任务总数用无参方法反射（每 12s 一次、
        ///    独立 try/catch，读不到只记 `?`）。不写内存、不改状态、不挂新 Harmony patch。
        /// </summary>
        private static void AppendTitleChildSnapshot(StringBuilder sb, object winObj, IntPtr win)
        {
            AppendWindowActive(sb, "Logo", win, 0xA0, typeof(UILogoWindow));
            AppendWindowActive(sb, "Menu", win, 0xA8, typeof(UITitleRootWindow));
            AppendWindowActive(sb, "Bg", win, 0xB0, typeof(UITitleBackgroundWindow));

            try
            {
                IntPtr menu = Marshal.ReadIntPtr(IntPtr.Add(win, 0xA8));
                if (menu != IntPtr.Zero)
                    sb.Append(" MenuState=").Append(Marshal.ReadInt32(IntPtr.Add(menu, 0xD0)))
                      .Append(" MenuRecvIn=").Append(Marshal.ReadByte(IntPtr.Add(menu, 0x90)) != 0);
                IntPtr logo = Marshal.ReadIntPtr(IntPtr.Add(win, 0xA0));
                if (logo != IntPtr.Zero)
                    sb.Append(" LogoIdx=").Append(Marshal.ReadInt32(IntPtr.Add(logo, 0xA8)));
            }
            catch (Exception e) { sb.Append(" 子窗口状态读取失败:").Append(e.GetType().Name); }

            try
            {
                var ui = StellaUIManager.Instance;
                if (ui != null)
                {
                    // 任务系统：先直读 TaskKicker 的 5 个列表指针（纯直读、零调用）
                    object kicker = InstVal(winObj, "TaskKicker");
                    if (kicker != null)
                    {
                        IntPtr kp = NativePtr.Of(kicker);
                        if (kp != IntPtr.Zero)
                            sb.Append(" TaskLists=")
                              .Append(Marshal.ReadIntPtr(IntPtr.Add(kp, 0x18)) != IntPtr.Zero ? "1" : "0")
                              .Append(Marshal.ReadIntPtr(IntPtr.Add(kp, 0x20)) != IntPtr.Zero ? "1" : "0")
                              .Append(Marshal.ReadIntPtr(IntPtr.Add(kp, 0x28)) != IntPtr.Zero ? "1" : "0")
                              .Append(Marshal.ReadIntPtr(IntPtr.Add(kp, 0x30)) != IntPtr.Zero ? "1" : "0")
                              .Append(Marshal.ReadIntPtr(IntPtr.Add(kp, 0x38)) != IntPtr.Zero ? "1" : "0");
                    }
                    // 再反射无参方法拿任务总数（游戏自己的方法，失败只记 ?）
                    object total = InstVal(kicker, "GetTotalTaskCount");
                    object exist = InstVal(winObj, "IsExistTask");
                    sb.Append(" Tasks=").Append(total == null ? "?" : total.ToString())
                      .Append(" TaskExist=").Append(exist == null ? "?" : exist.ToString());
                }
            }
            catch (Exception e) { sb.Append(" Tasks读取失败:").Append(e.GetType().Name); }
        }

        /// <summary>读某个子窗口的 gameObject.activeSelf（指针 → 运行时代理 → 反射属性）。</summary>
        private static void AppendWindowActive(StringBuilder sb, string tag, IntPtr win, int off, Type proxy)
        {
            try
            {
                IntPtr p = Marshal.ReadIntPtr(IntPtr.Add(win, off));
                if (p == IntPtr.Zero) { sb.Append(" ").Append(tag).Append("Active=null"); return; }
                object child = NativePtr.ToObj(p, proxy);
                object act = InstVal(InstVal(child, "gameObject"), "activeSelf");
                sb.Append(" ").Append(tag).Append("Active=").Append(act == null ? "?" : act.ToString());
            }
            catch (Exception e)
            {
                sb.Append(" ").Append(tag).Append("Active读取失败:").Append(e.GetType().Name);
            }
        }

        private const System.Reflection.BindingFlags RBF =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

        /// <summary>r18 诊断：静态属性 / 无参静态方法的反射取值（IL2CPP 桩缺成员时返回 null）。</summary>
        private static object StaticCall(Type t, string name)
        {
            try
            {
                var p = t.GetProperty(name, RBF);
                if (p != null) return p.GetValue(null, null);
                var m = t.GetMethod(name, RBF, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(null, null);
            }
            catch { }
            return null;
        }

        /// <summary>r18 诊断：实例属性 / 无参实例方法的反射取值。</summary>
        private static object InstVal(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var t = obj.GetType();
                var p = t.GetProperty(name, RBF);
                if (p != null) return p.GetValue(obj, null);
                var m = t.GetMethod(name, RBF, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(obj, null);
            }
            catch { }
            return null;
        }

        private void RevokeAllFeatures()
        {
            for (int i = 0; i < _features.Count; i++)
            {
                try { _features[i].OnReset(); }
                catch (Exception e)
                {
                    QolLog.Warn("kill: " + _features[i].Id + ".OnReset -> " + e.GetType().Name + ": " + e.Message);
                }
            }
            QolLog.Info("kill: 已撤销 " + _features.Count + " 个 feature 的运行时副作用");
        }

        public override void OnUpdate()
        {
            if (!_booted) return;

            try
            {
                // r17 诊断①：帧间隔 stall 探测。OnUpdate 走真实时间——若上一帧距今超过 0.5s，
                //   说明主线程曾被阻塞后恢复（死循环 / 长 GC / interop 卡顿）。永久卡死时本行
                //   不会打（也打不出来），由心跳的"消失"来证明。
                float nowRt = UnityEngine.Time.realtimeSinceStartup;
                if (_lastFrameRealtime > 0f)
                {
                    float gap = nowRt - _lastFrameRealtime;
                    int t = Environment.TickCount;
                    if (gap > 0.5f && t - _stallWarnAt > 2000)
                    {
                        _stallWarnAt = t;
                        QolLog.Warn("stall: 帧间隔 " + gap.ToString("0.##") + "s —— 主线程曾被阻塞后恢复（scene=" + _sceneName + "）");
                    }
                }
                _lastFrameRealtime = nowRt;

                // 手柄轮询（XInput）：必须在所有输入判断之前，保证一帧只轮询一次
                QolInput.NewFrame();

                // 隐藏 MelonLoader 控制台窗口。放在 Update 而不是启动时，
                // 是因为要确保控制台已经创建完（0.5.7 没有 Loader.cfg 可用）。
                ConsoleHider.Run();

                // 热键与菜单必须独立于总开关，否则关掉总开关就再也开不回来了
                if (QolInput.KillPressed()) KillSwitch.Toggle();
                if (QolInput.MenuPressed()) _menu.Toggle();

                // 关菜单时立即落盘：改完设置如果直接关游戏（非正常退出），
                // 靠 OnApplicationQuit 保存会丢。v0.1.3 顺带修了 Save 只写主分类的问题。
                if (_menuWasOpen && !_menu.Open) QolConfig.Save();
                _menuWasOpen = _menu.Open;

                // 菜单开关 = 闸门装卸：开菜单时装上拦截（掐游戏输入），
                // 关菜单时立刻撤销（输入必须恢复）。常驻 detour 会让输入全失效，故按需装卸。
                GameInputGate.SetActive(_menu.Open);

                _menu.Update();
                if (_overlayEnabled) _overlay.Tick();

                // v0.3.1-r13：状态条撤除 ⇒ 覆盖层只在 F2 菜单打开时露面，平时屏幕完全干净。
                //   ⚠ Overlay 本身保留：它是 F2 自绘菜单唯一的渲染载体。
                bool show = _menu.Open && !KillSwitch.Tripped;
                _overlay.SetVisible(show);

                _sb.Length = 0;
                if (show) _menu.Render(_sb);
                _overlay.SetText(_sb.ToString());

                // ⛔ 闸门自愈必须排在总开关 return **之前**：紧急关闭（F8）后 OnUpdate 直接返回，
                //    放在后面就等于永远跑不到，冻结残留照样卡死。
                _gateTick++;
                if ((_gateTick % 60) == 0)
                {
                    try
                    {
                        if (KillSwitch.Tripped) GameInputGate.ForceRelease();
                        GameInputGate.SelfCheck();   // 仅 PAUSE 闸门模式有实际动作，其余模式立即返回
                        FreezeWatchdog();            // v0.3.1-r12：只在"真的被冻住"时才打日志
                    }
                    catch { }
                }
                // r17 诊断②：无条件心跳（2 秒一条，不受总开关 / 级别影响，走 Warn 通道保证落盘）。
                //   【为什么必须有】此前"标题后日志静默"被误读成主线程死亡——但标题画面本来就
                //   没有任何事件型日志（r10.48 的翻版）。心跳在 ⇒ 主线程活着、逻辑层卡住；
                //   心跳消失 ⇒ 主线程真死了。下一个"卡标题"现场就此一锤定音。
                if ((_gateTick % 120) == 0)
                {
                    float ts2 = 1f, gs2 = -1f;
                    try { ts2 = UnityEngine.Time.timeScale; } catch { }
                    try { gs2 = GameInputGate.ReadGameSpeed(); } catch { }
                    QolLog.Warn("hb: 场景 " + _sceneName + " 已 +" + ((int)(UnityEngine.Time.realtimeSinceStartup - _sceneRealtime)).ToString()
                                + "s ts=" + ts2.ToString("0.###") + " gs=" + gs2.ToString("0.###")
                                + (KillSwitch.Tripped ? " [紧急关闭]" : ""));
                    if (_sceneName == "Title") TitleProbe();
                }

                if (!QolConfig.Enabled || KillSwitch.Tripped) return;

                for (int i = 0; i < _features.Count; i++)
                {
                    try { _features[i].OnUpdate(); }
                    catch (Exception e) { QolLog.DebugThrottled("upd." + _features[i].Id, _features[i].Id + ".OnUpdate: " + e.Message); }
                }
            }
            catch (Exception e)
            {
                QolLog.DebugThrottled("modmain.update", "OnUpdate: " + e.Message);
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (!_booted) return;
            // r17 诊断②配套：记下场景名 / 载入时刻，心跳据此打"已 +Ns"。
            _sceneName = string.IsNullOrEmpty(sceneName) ? "#" + buildIndex : sceneName;
            _sceneRealtime = UnityEngine.Time.realtimeSinceStartup;
            _titleSnapRealtime = _sceneRealtime;   // r19：快照计时器跟着场景一起重置 ⇒ 每 12s 一次、可重复
            LastSceneName = _sceneName;
            // ⛔ v0.3.1-r11：切场景一律先解冻。
            //   PAUSE 闸门若"菜单开着时退出战斗/切场景"残留，GameSpeed 会停在 0.0001 ⇒
            //   新场景的 UI（标题菜单等）永远加载不出来，玩家只能杀进程。
            try { GameInputGate.ForceRelease(); } catch { }
            // v0.3.1-r12：每次场景加载记一行时间状态 —— 标题菜单加载不出来这类故障，
            // 第一件事就是确认游戏逻辑帧有没有被冻住（对比进场景前后的 timeScale / GameSpeed）。
            try
            {
                QolLog.Info("scene: " + sceneName + "(#" + buildIndex + ") timeScale="
                            + UnityEngine.Time.timeScale.ToString("0.####")
                            + " gameSpeed=" + GameInputGate.ReadGameSpeed().ToString("0.####")
                            + " menuOpen=" + _menu.Open);
            }
            catch { }
            if (_overlayEnabled) _overlay.OnSceneChanged(); // 旧场景字体可能已销毁，强制重借
            if (!QolConfig.Enabled || KillSwitch.Tripped) return;

            // r17 诊断③：逐个 feature 计时。冻结窗口总是从 overlay font 之后开始，而这里正是
            //   OnSceneWasLoaded 里最后一段同步 mod 代码——若某个 feature 的 OnSceneLoaded 卡住，
            //   主线程就永远回不到游戏。慢于 30ms 的逐个点名。
            for (int i = 0; i < _features.Count; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { _features[i].OnSceneLoaded(buildIndex, sceneName); }
                catch (Exception e) { QolLog.Debug("scene." + _features[i].Id + ": " + e.Message); }
                finally
                {
                    sw.Stop();
                    if (sw.ElapsedMilliseconds > 30)
                        QolLog.Warn("scene.slow: " + _features[i].Id + ".OnSceneLoaded 耗时 " + sw.ElapsedMilliseconds + "ms");
                }
            }
        }

        // ── r25 最小修复（2026-11-09）：回标题时标题窗口状态机未复位 ──────────────
        private static int _titleFixCount;

        /// <summary>
        /// 【问题】r24 实机快照（会话 2026-11-09 23:35:15）：回标题后
        /// `UI.Win` 与冷启动成功那次是**同一指针**（uiSceneTitle 未重载、窗口对象复用），
        /// `LogoActive/MenuActive/BgActive`、`LayerLast=8`、`WinActive`、`FadeOutEnd` 全相同，
        /// 唯一差异是 `UITitleRootWindow.mState`：冷启动 =1(SKIP_WAIT)，回标题 =9(CONTINUE_WAIT)，
        /// 且 `Tasks` 1→2、60 秒读数一动不动。⇒ 窗口对象在、状态机却停在上次进游戏的终态。
        ///
        /// 【反编译证据】`crystar-mod/dump/dump.cs`
        ///   · `UITitleRootWindow.eState`（dump:203190）：0 OPEN_WAIT / 1 SKIP_WAIT / … /
        ///     5 MENU / … / **8 NEW_GAME_WAIT / 9 CONTINUE_WAIT / 10 LOAD_WAIT / 11 STORE_WAIT**。
        ///   · `UITitleRootWindow.OpenWindow()` **0x1E8170** 末尾
        ///     `0x1E82D3 mov dword ptr [rsi+0xd0], 0` ⇒ 游戏自己的复位入口，
        ///     同时 `0x1E82D9 mov [rsi+0xcc], 0` 清 mNoInputTime，并挂 AddAnimWaitTask。
        ///   · `OnInput()` **0x1E78B0** 按 `[rbx+0xd0]`(mState) 走 jump table 分派
        ///     ⇒ 状态 9 不接收任何键；`OnUpdate()` 0x1E8150 只调基类 + AddNoInputTime()，
        ///     状态推进完全依赖 OpenWindow / Task 回调。
        ///   · `UITitleWindow.OnUpdate()` **0x1E9480** 的吸引模式兜底（mNoInputTime 超阈值
        ///     → `ChangeScene(7)`）也因 `AddNoInputTime()` 0x1E6410 按 mState 分派、
        ///     状态 9 不累加而不触发 ⇒ 永久停住。
        ///   · `UITitleWindow.OpenWindow()` **0x1E9570** 只在全局状态 ==3(TITLE) 时才
        ///     虚调 `mMenuWindow`(0xA8) 的 OpenWindow（0x1E9756），其他值整段空跑
        ///     ⇒ 回标题时这条重置链没走到，正是本修复要补的缺口。
        ///
        /// 【修法】在 `TitleScene.OnEnter` 的 Postfix 里，**仅当 mState 落在 8..11 终态**
        /// （正常演出/菜单态 0..7 一律不碰，冷启动时窗口刚建、mState=0/1 天然不触发）
        /// 才调一次游戏自己的 `UITitleRootWindow.OpenWindow()` 把状态机复位；
        /// 若代理方法不可用或调用后仍未复位，降级为**直写原生字段**
        /// （铁律 42：写字段绝不能先试反射 SetValue，那只会写进 CLR 影子槽且静默失效）。
        /// 复位后 mState=0，下一次 OnEnter 条件不再成立 ⇒ 天然防重入。
        ///
        /// ⛔ 全程独立 try/catch；不挂新 Harmony patch、不动任何其他 feature、
        ///    不碰冷启动链路、不改版本号。修复前后读数写进日志当实机判据。
        /// </summary>
        private static void TryResetTitleStateMachine()
        {
            try
            {
                var ui = StellaUIManager.Instance;
                if (ui == null) return;
                IntPtr q = NativePtr.Of(ui);
                if (q == IntPtr.Zero) return;

                IntPtr win = Marshal.ReadIntPtr(IntPtr.Add(q, 0x100));   // mTitleWindow
                if (win == IntPtr.Zero) return;
                IntPtr menu = Marshal.ReadIntPtr(IntPtr.Add(win, 0xA8));  // mMenuWindow = UITitleRootWindow
                if (menu == IntPtr.Zero) return;

                int before = Marshal.ReadInt32(IntPtr.Add(menu, 0xD0));   // mState
                if (before < 8 || before > 11) return;                    // 只处理终态残留

                bool viaOpenWindow = false;
                try
                {
                    object menuObj = NativePtr.ToObj(menu, typeof(UITitleRootWindow));
                    if (menuObj != null)
                    {
                        var mi = menuObj.GetType().GetMethod("OpenWindow", RBF, null, Type.EmptyTypes, null);
                        if (mi != null) { mi.Invoke(menuObj, null); viaOpenWindow = true; }
                    }
                }
                catch (Exception e)
                {
                    QolLog.Warn("title.fix: OpenWindow 调用失败，转直写兜底 " + e.GetType().Name);
                }

                int after = Marshal.ReadInt32(IntPtr.Add(menu, 0xD0));
                if (after < 0 || after >= 8)
                {
                    // 兜底：直写原生内存（0xCC 是 float mNoInputTime，位型 0 = 0.0f）
                    Marshal.WriteInt32(IntPtr.Add(menu, 0xD0), 0);
                    Marshal.WriteInt32(IntPtr.Add(menu, 0xCC), 0);
                    after = Marshal.ReadInt32(IntPtr.Add(menu, 0xD0));
                }

                _titleFixCount++;
                QolLog.Warn("title.fix: 回标题状态机复位 #" + _titleFixCount
                            + " mState " + before + " → " + after
                            + "（" + (viaOpenWindow ? "OpenWindow" : "直写兜底")
                            + "，OnEnter 第 " + TitleEnterHits + " 次）");
            }
            catch (Exception e)
            {
                QolLog.Warn("title.fix: 复位失败 " + e.GetType().Name + "：" + e.Message);
            }
        }

        /// <summary>
        /// r19 诊断：标题场景自身的生命周期探针。
        /// 心跳（mod 的 OnUpdate）只能证明"主线程活着"；要定位卡在 TitleScene 内部还是上层，
        /// 必须看**游戏自己的 TitleScene.OnUpdate 还在不在被调用**（铁律 55 的延伸：
        /// 观测对象要直接对准被怀疑的那一层，不能用邻近层的日志代替）。
        /// 三条都是 Postfix：OnUpdate / OnExit 纯计数；OnEnter 额外挂 r25 的条件性状态机复位
        /// （只在回标题且 mState 落在 8..11 终态时触发，见 TryResetTitleStateMachine）。
        /// </summary>
        [HarmonyPatch(typeof(TitleScene), "OnUpdate")]
        private static class TitleSceneProbeUpdate
        {
            private static void Postfix() { TitleUpdateHits++; }
        }

        [HarmonyPatch(typeof(TitleScene), "OnEnter")]
        private static class TitleSceneProbeEnter
        {
            private static void Postfix()
            {
                TitleEnterHits++;
                QolLog.Warn("title.scene: OnEnter 第 " + TitleEnterHits + " 次");
                // r25：原生 OnEnter 跑完后再判定；条件只命中「回标题 + mState 终态残留」。
                TryResetTitleStateMachine();
            }
        }

        [HarmonyPatch(typeof(TitleScene), "OnExit")]
        private static class TitleSceneProbeExit
        {
            private static void Postfix()
            {
                TitleExitHits++;
                QolLog.Warn("title.scene: OnExit 第 " + TitleExitHits + " 次");
            }
        }

        public override void OnApplicationQuit()
        {
            // 兜底：万一退出时菜单还开着，必须把拦截撤销（尤其是 Pause 模式的 GameSpeed）
            try { GameInputGate.ForceRelease(); } catch { }
            try { QolConfig.Save(); QolLog.Info("shutdown"); QolLog.Shutdown(); } catch { }
        }
    }
}
