using System;
using System.Collections.Generic;

namespace CrystarQoL
{
    /// <summary>
    /// 中英双语文本表。默认中文。
    /// 找不到 key 时原样返回 key——宁可显示 "menu.foo" 也不要抛异常，方便立刻定位漏翻的地方。
    /// </summary>
    public static class Loc
    {
        private static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            { "core.master",      "总开关" },
            { "core.loglevel",    "日志级别" },
            { "core.language",    "语言" },
            { "core.keymenu",     "菜单热键" },
            { "core.keykill",     "紧急总关" },
            { "core.bindpad",     "绑定手柄组合（按键学习）" },
            { "core.diag",        "导出诊断包" },
            { "core.backup",      "备份存档" },
            { "core.resetall",    "全部恢复默认" },
            { "core.killswitch",  "紧急总关（停用全部功能）" },

            { "level.off",    "关" },
            { "level.normal", "常规" },
            { "level.verbose","详细" },
            { "lang.zh",      "中文" },
            { "lang.en",      "English" },
            { "state.on",     "开" },
            { "state.off",    "关" },

            { "menu.title",       "Crystar.QoL" },
            { "menu.nav",         "↑↓ 选择　←→ 调整　Enter 确认　R 本项复位　Q/E 翻页　Esc 关闭" },
            { "menu.none",        "（无功能）" },
            { "menu.reopenitem",  "本项恢复默认" },
            { "menu.confirm",     "再按一次 Enter 确认（不可逆）" },
            { "menu.confirming",  "▶ 确认？" },
            { "menu.risk",        "⚠ 高风险" },
            { "menu.irrev",       "✖ 不可逆" },
            { "eff.immediate",    "立即" },
            { "eff.reopen",       "重开窗口" },
            { "eff.restart",      "重启游戏" },
            { "menu.page",        "页" },
            { "menu.item",        "项" },

            { "capture.wait",     "请按下要绑定的按键/手柄键…（Esc 取消）" },
            { "capture.wait2",    "再按一个作为组合第二键，或按 Enter 只用单键" },
            { "capture.done",     "已绑定" },
            { "capture.cancel",   "已取消" },
            { "capture.none",     "未捕获到按键" },

            { "diag.done",        "诊断包已导出" },
            { "diag.fail",        "诊断包导出失败" },
            { "backup.done",      "存档已备份" },
            { "backup.fail",      "存档备份失败" },
            { "reset.done",       "已全部恢复默认" },
            { "kill.tripped",     "紧急总关已触发：全部功能停用" },
            { "kill.restored",    "已恢复" },

            { "f.onehit",         "秒杀" },
            { "f.onehit.prot",    "强杀受保护敌人" },
            { "f.dmg",            "伤害倍率" },
            { "f.crit",           "必定暴击" },
            { "f.god",            "玩家无敌" },
            { "f.crygauge",       "哭泣槽常满" },
            { "f.crymode",        "CryMode常驻" },
            { "f.combo",          "连击不中断" },
            { "f.autopickup",     "自动拾取掉落" },
            { "f.shopunlock",     "商店全解锁" },
            { "f.money.full",     "金钱全满" },
            { "f.money.full.hint","按 Enter 置顶" },
            { "f.money.add",      "金钱增加" },
            { "f.item.id",        "生成物品" },
            { "f.item.step",      "物品ID步进" },
            { "f.item.count",     "生成数量" },
            { "f.item.spawn",     "执行生成" },
            { "f.item.spawn.hint","按 Enter 生成" },
            // v0.2.4：物品生成子页面（营地页用 TW 表，这里给 F2 面板/兜底用）
            { "f.spawn.step",     "步进" },
            { "f.spawn.id",       "物品ID" },
            { "f.spawn.name",     "物品名" },
            { "f.spawn.count",    "数量" },
            { "f.spawn.exec",     "确定生成" },
            { "f.spawn.back",     "返回上一级" },
            { "f.pickuprange",    "自动拾取范围" },
            // v0.2 批次 3（养成深化）
            // 修正十七（r18）：三条标题统一采用**游戏本名 + 无消耗**写法，顺序＝营地菜单顺序
            //   • 净化幽怨（读书房「净化」）  • 思装调合（装备强化/融合）  • 思装变容（能力付与）
            { "f.creation.free",  "净化幽怨无消耗" },
            { "f.enhance.free",   "思装调合无消耗" },
            { "f.grant.free",     "思装变容无消耗" },
            { "f.skill.all",      "全技能解锁" },
            // v0.2.10 修正十（r11）：幽怨满了怎么办
            // r27（2026-11-14）：三个开关收敛为「扩容主开关 + FIFO 独立」。
            //   图标跟随已并入主开关、计数缩放整体移除 ⇒ 两个死键同步删除（简/繁/英三表齐改）。
            { "f.deathcry.fifo",  "幽怨满时顶替最早" },
            { "f.deathcry.expand","幽怨槽池扩容20" },
            { "camp.back",        "返回上一级" },
            { "f.item.done",      "已生成" },
            { "f.item.fail",      "取不到数据" },
            { "f.item.badid",     "ID 越界" },
            { "f.item.err",       "错误" },
            { "f.exp",            "经验倍率" },
            { "f.money",          "金钱倍率" },
            { "f.move",           "移速倍率" },
            // 攻速倍率（改 ConstSkillSequenceParameter.mAnimationSpeed 0x44）。⛔ 新增开关五处齐改：
            // ①Cfg ②本表 Zh ③下方 En ④FeatureCampConfig 的 TW ⑤MainRows。
            { "f.atkspeed",       "攻击速度倍率" },
            { "f.kill",           "击杀计数倍率" },
            { "f.dropcount",      "掉落倍率(数量/金额)" },
            { "f.droprate",       "掉率一键拉满" },
            { "f.map",            "全图去迷雾" },
            { "f.map.hint",       "只改显示，存档里的探索记录不动" },
            // v0.3 批次 1（D8 开场播片自动跳过）。⛔ 新增开关五处齐改：
            // ①Cfg.Bool ②本表 Zh ③下方 En ④FeatureCampConfig 的 TW ⑤MainRows。
            { "f.openskip",       "开场播片自动跳过" },
            { "f.cryskip",        "幽怨演出自动跳过" },
            // v0.3 批次 3（E3）。⛔ 新增开关五处齐改：
            // ①Cfg.Bool ②本表 Zh ③下方 En ④FeatureCampConfig 的 TW ⑤MainRows。
            { "f.mapicons",       "大地图敌人标记" },
            // ⛔ E4 大地图宝箱标记 2026-11-06 实机否决并移除。
            // v0.3 批次 4（A7 手动调整等级数，逐角色营地子页）。PageRow 也要三表齐全，否则入口行是空的。
            { "f.level",          "角色等级" },
            { "f.level.chara",    "角色" },
            { "f.level.cur",      "当前等级" },
            { "f.level.step",     "步进" },
            { "f.level.target",   "目标等级" },
            { "f.level.apply",    "套用等级" },
            { "f.album",          "回忆录全解锁" },
            { "f.album.done",     "已解锁全部敌人的记忆（{0} 项），打开游戏内回忆录界面即可同步成就" },
            { "f.album.fail",     "尚未读到游戏数据，请在读档后再试一次" },
            { "camp.entry",       "MOD設定" },
            { "camp.hintf2",      "按 F2 開啟完整設定介面" },
            { "camp.cat.a",       "核心减肝" },
            { "camp.cat.b",       "战斗辅助" },
            { "camp.cat.c",       "经济" },
            { "camp.cat.e",       "探索" },
            { "camp.cat.f",       "收集图鉴" },
            // v0.3 新增分类：流程加速（D 系列）。放在 F 之后、G 系统之前（系统那组保持最后）。
            { "camp.cat.h",       "流程加速" },
            { "camp.cat.g",       "系统" },
            { "camp.warn",        "（不可逆，建议先备份存档）" },
            { "camp.confirm",     "再按一次确认执行" },
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            { "core.master",      "Master switch" },
            { "core.loglevel",    "Log level" },
            { "core.language",    "Language" },
            { "core.keymenu",     "Menu hotkey" },
            { "core.keykill",     "Kill switch" },
            { "core.bindpad",     "Bind pad combo (learn)" },
            { "core.diag",        "Export diagnostics" },
            { "core.backup",      "Backup save" },
            { "core.resetall",    "Reset all to defaults" },
            { "core.killswitch",  "Kill switch (disable all)" },

            { "level.off",    "Off" },
            { "level.normal", "Normal" },
            { "level.verbose","Verbose" },
            { "lang.zh",      "中文" },
            { "lang.en",      "English" },
            { "state.on",     "ON" },
            { "state.off",    "OFF" },

            { "menu.title",       "Crystar.QoL" },
            { "menu.nav",         "Up/Dn select  L/R adjust  Enter apply  R reset  Q/E page  Esc close" },
            { "menu.none",        "(no features)" },
            { "menu.reopenitem",  "Reset this item" },
            { "menu.confirm",     "Press Enter again to confirm (irreversible)" },
            { "menu.confirming",  "Confirm?" },
            { "menu.risk",        "RISKY" },
            { "menu.irrev",       "IRREVERSIBLE" },
            { "eff.immediate",    "now" },
            { "eff.reopen",       "reopen UI" },
            { "eff.restart",      "restart" },
            { "menu.page",        "pg" },
            { "menu.item",        "it" },

            { "capture.wait",     "Press the key/button to bind... (Esc to cancel)" },
            { "capture.wait2",    "Press a second key for the combo, or Enter for single key" },
            { "capture.done",     "Bound" },
            { "capture.cancel",   "Cancelled" },
            { "capture.none",     "No key captured" },

            { "diag.done",        "Diagnostics exported" },
            { "diag.fail",        "Diagnostics export failed" },
            { "backup.done",      "Save backed up" },
            { "backup.fail",      "Save backup failed" },
            { "reset.done",       "All reset to defaults" },
            { "kill.tripped",     "Kill switch tripped: all features off" },
            { "kill.restored",    "Restored" },

            { "f.onehit",         "One-hit kill" },
            { "f.onehit.prot",    "Kill protected too" },
            { "f.dmg",            "Damage multiplier" },
            { "f.crit",           "Always critical" },
            { "f.god",            "Player invincible" },
            { "f.crygauge",       "Cry gauge always full" },
            { "f.crymode",        "Cry Mode always on" },
            { "f.combo",          "Combo never breaks" },
            { "f.autopickup",     "Auto vacuum drops" },
            { "f.shopunlock",     "Unlock all shop items" },
            { "f.money.full",     "Max out money" },
            { "f.money.full.hint","press Enter" },
            { "f.money.add",      "Add money" },
            { "f.item.id",        "Spawn item" },
            { "f.item.step",      "Item id step" },
            { "f.item.count",     "Spawn count" },
            { "f.item.spawn",     "Spawn now" },
            { "f.item.spawn.hint","press Enter" },
            { "f.spawn.step",     "Step" },
            { "f.spawn.id",       "Item ID" },
            { "f.spawn.name",     "Item name" },
            { "f.spawn.count",    "Count" },
            { "f.spawn.exec",     "Spawn" },
            { "f.spawn.back",     "Back" },
            { "f.pickuprange",    "Auto pickup range" },
            // 修正十七（r18）：沿用游戏本名的英文写法（blending = 思装调合），顺序同营地菜单
            { "f.creation.free",  "Free purification (Deathcry)" },
            { "f.enhance.free",   "Free blending (no gear/money)" },
            { "f.grant.free",     "Free transmute (no mats)" },
            { "f.skill.all",      "Unlock all skills" },
            { "f.deathcry.fifo",  "Drop oldest Deathcry when full" },
            // r27：图标跟随并入主开关 / 计数缩放废弃 ⇒ 两行死键删除（简表 / 繁体表同步）
            { "f.deathcry.expand","Enlarge Deathcry pool to 20" },
            { "camp.back",        "Back" },
            { "f.item.done",      "spawned" },
            { "f.item.fail",      "no data" },
            { "f.item.badid",     "bad id" },
            { "f.item.err",       "err" },
            { "f.exp",            "EXP multiplier" },
            { "f.money",          "Money multiplier" },
            { "f.move",           "Move speed multiplier" },
            { "f.atkspeed",       "Attack speed multiplier" },
            { "f.kill",           "Kill count multiplier" },
            { "f.dropcount",      "Drop multiplier (count/amount)" },
            { "f.droprate",       "Max out all drop rates" },
            { "f.map",            "Reveal whole map" },
            { "f.map.hint",       "Display only; save exploration data untouched" },
            { "f.openskip",       "Auto-skip opening movie" },
            { "f.cryskip",        "Auto-skip cry scene" },
            { "f.mapicons",       "Enemy icons on world map" },
            { "f.level",          "Character level" },
            { "f.level.chara",    "Character" },
            { "f.level.cur",      "Current level" },
            { "f.level.step",     "Step" },
            { "f.level.target",   "Target level" },
            { "f.level.apply",    "Apply level" },
            { "f.album",          "Unlock all memoirs" },
            { "f.album.done",     "All memories unlocked ({0} entries). Open the in-game Memoirs screen to sync the achievement" },
            { "f.album.fail",     "Game data not captured yet — load a save first and try again" },
            { "camp.entry",       "MOD Settings" },
            { "camp.cat.a",       "Core QoL" },
            { "camp.cat.b",       "Combat assist" },
            { "camp.cat.c",       "Economy" },
            { "camp.cat.d",       "Growth" },
            { "camp.cat.e",       "Exploration" },
            { "camp.cat.f",       "Collection" },
            { "camp.cat.h",       "Flow" },
            { "camp.cat.g",       "System" },
            { "camp.warn",        " (irreversible — backup save first)" },
            { "camp.confirm",     "press again to confirm" },
        };

        public static bool Chinese
        {
            get { return QolConfig.Language == null || QolConfig.Language.Value == 0; }
        }

        public static string T(string key)
        {
            var table = Chinese ? Zh : En;
            string v;
            if (table.TryGetValue(key, out v)) return v;
            // 中文缺失时退回英文，英文也缺失才返回 key
            if (table != En && En.TryGetValue(key, out v)) return v;
            return key;
        }

        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch { return T(key); }
        }

        public static string OnOff(bool b) { return b ? T("state.on") : T("state.off"); }

        public static string Level(int lv)
        {
            return lv <= 0 ? T("level.off") : (lv == 1 ? T("level.normal") : T("level.verbose"));
        }

        public static string EffectTag(Effect e)
        {
            switch (e)
            {
                case Effect.ReopenWindow: return "[" + T("eff.reopen") + "]";
                case Effect.RestartGame: return "[" + T("eff.restart") + "]";
                default: return "[" + T("eff.immediate") + "]";
            }
        }
    }
}
