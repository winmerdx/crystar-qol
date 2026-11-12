using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using HarmonyLib;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>
    /// 回忆录全解锁（死者回忆录 / Memoirs of the Dead）。
    ///
    /// 【机制（2026-09-09 反汇编闭环）】
    /// 每个敌人的 3 条记忆 = UserParameter.mThoughtObtainFlag(BitFlag) 里
    /// bit(charaID*4 + index) 三个槽位。正常流程：击杀 → 死泣落地 → 拾取 →
    /// BattleManager.AddItem 判 eItemType.DEATHCRY(=7) → AddDeathCryCharaID(charaID)
    /// → 把该死泣第一个未设的记忆位置 1。与击杀数完全无关（KillEnemyCount 只喂成就层）。
    ///
    /// 【本功能做什么】
    /// 遍历 ParameterManager.GetAlbumDictionaryParameter()（相册本体的敌人表，
    /// 每敌一条 ConstAlbumDictionaryParameter），对每个 eCharaID 调
    /// SetThoughtObtainFlag + SetThoughtObtainSeenFlag 把 0..3 号槽全置位。
    /// index 上限 4 由 GetThoughtObtainFlag 的 `cmp r8d,3; ja` 实锤；
    /// 全部置位可同时满足 CheckEnemyAlbumAllOfOne / Complete 两种成就口径。
    ///
    /// 【铁律适配】
    /// - SetThoughtObtainFlag 是**实例**方法 → 可以直接调用（Invoke 不受静态禁令限制）。
    /// - GetAlbumDictionaryParameter 返回 List&lt;T&gt;：编译期是 DummyDll 的
    ///   System.Collections.Generic.List，运行时是 Il2CppSystem.Collections.Generic.List
    ///   → 必须 object 接收 + 反射枚举（v0.1.9 踩过的坑）。
    /// - 单例获取：不碰 SingletonMonoBehaviour 的泛型静态 Instance（DummyDll≠运行时类型），
    ///   改用 Postfix 挂 get_UserParameter（游戏内高频 getter）顺手捕获 PM/UP 实例。
    /// </summary>
    public sealed class FeatureAlbum : IFeature
    {
        public string Id { get { return "album"; } }
        public RiskTier Risk { get { return RiskTier.Irreversible; } }

        private Menu _menu;

        // get_UserParameter 捕获的游戏单例（进入任意场景后很快就有值）
        private static ParameterManager _pm;
        private static UserParameter _up;

        public void Init(FeatureContext ctx)
        {
            _menu = ctx.Menu;
            ctx.Harmony.PatchAll(typeof(CapturePatch));
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }
        public void OnReset() { }   // 不可逆写档，不做撤销

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return new MenuItem
            {
                Label = Loc.T("f.album"),
                ValueText = () => "",
                Risk = RiskTier.Irreversible,
                NeedConfirm = true,
                Confirm = () =>
                {
                    int n = UnlockAll();
                    if (_menu == null) return;
                    _menu.ShowStatus(n >= 0
                        ? string.Format(Loc.T("f.album.done"), n)
                        : Loc.T("f.album.fail"));
                }
            };
        }

        /// <summary>营地页动作行入口（营地集成 v0.1.10）。返回解锁的敌人数；-1 = 数据未就绪。</summary>
        internal static int RunUnlockAll()
        {
            return UnlockAll();
        }

        /// <summary>返回解锁的敌人数；-1 = 数据未就绪。重复执行无害（SetBit 幂等）。</summary>
        private static int UnlockAll()
        {
            if (_pm == null || _up == null) return -1;

            // GetAlbumDictionaryParameter() 返回 List<T> → 反射拿，别用编译期类型接
            object list;
            try
            {
                MethodInfo mi = typeof(ParameterManager).GetMethod("GetAlbumDictionaryParameter",
                    BindingFlags.Public | BindingFlags.Instance);
                if (mi == null) return -1;
                list = mi.Invoke(_pm, null);
            }
            catch { return -1; }
            if (list == null) return -1;

            int count;
            try { count = (int)list.GetType().GetProperty("Count").GetValue(list, null); }
            catch { return -1; }

            int done = 0;
            var getItem = list.GetType().GetMethod("get_Item", new Type[] { typeof(int) });
            if (getItem == null) return -1;

            for (int i = 0; i < count; i++)
            {
                object entry;
                try { entry = getItem.Invoke(list, new object[] { i }); }
                catch { break; }
                var p = entry as ConstAlbumDictionaryParameter;
                if (p == null) continue;

                try
                {
                    int id = (int)p.CharaID;
                    for (int idx = 0; idx <= 3; idx++)
                    {
                        _up.SetThoughtObtainFlag(p.CharaID, idx);
                        _up.SetThoughtObtainSeenFlag(p.CharaID, idx);
                    }
                    done++;
                }
                catch (Exception e)
                {
                    QolLog.DebugThrottled("album", "FeatureAlbum: " + e.Message);
                }
            }

            QolLog.Info("album: unlocked memories for " + done + " entries");
            return done;
        }

        /// <summary>
        /// get_UserParameter 是 UserParameter 的高频入口（UI/战斗到处在读），
        /// Postfix 顺手把 ParameterManager / UserParameter 实例缓存下来。
        /// 不碰泛型静态 Instance，不新增任何每帧逻辑。
        /// </summary>
        [HarmonyPatch(typeof(ParameterManager), "get_UserParameter")]
        private static class CapturePatch
        {
            private static void Postfix(ParameterManager __instance, UserParameter __result)
            {
                _pm = __instance;
                _up = __result;
            }
        }
    }
}
