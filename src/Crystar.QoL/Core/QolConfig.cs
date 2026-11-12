using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;

namespace CrystarQoL
{
    /// <summary>按功能隔离的配置登记器，顺手把「如何恢复默认」一起登记了。</summary>
    public sealed class ConfigRegistrar
    {
        private readonly MelonPreferences_Category _cat;

        internal ConfigRegistrar(MelonPreferences_Category cat) { _cat = cat; }

        public string Category { get { return _cat.Identifier; } }

        public MelonPreferences_Entry<bool> Bool(string key, bool def, string desc)
        {
            var e = _cat.CreateEntry(key, def, desc);
            QolConfig.RegisterEntry(_cat.Identifier, key, typeof(bool), () => e.Value, v => e.Value = (bool)v, e.ResetToDefault);
            return e;
        }

        public MelonPreferences_Entry<int> Int(string key, int def, string desc)
        {
            var e = _cat.CreateEntry(key, def, desc);
            QolConfig.RegisterEntry(_cat.Identifier, key, typeof(int), () => e.Value, v => e.Value = (int)v, e.ResetToDefault);
            return e;
        }

        public MelonPreferences_Entry<float> Float(string key, float def, string desc)
        {
            var e = _cat.CreateEntry(key, def, desc);
            QolConfig.RegisterEntry(_cat.Identifier, key, typeof(float), () => e.Value, v => e.Value = (float)v, e.ResetToDefault);
            return e;
        }

        public MelonPreferences_Entry<string> Str(string key, string def, string desc)
        {
            var e = _cat.CreateEntry(key, def, desc);
            QolConfig.RegisterEntry(_cat.Identifier, key, typeof(string), () => e.Value, v => e.Value = (string)v, e.ResetToDefault);
            return e;
        }
    }

    /// <summary>
    /// 配置中枢 + 恢复默认总线。
    ///
    /// 【v0.1.6 持久化换自有文件】MelonPreferences 的落盘连续两个版本都不可信：
    /// v0.1.3 用 SaveToFile(false)「修」过一次，用户确认过好，但 v0.1.5 实测
    /// UserData/MelonPreferences.cfg 里所有分类仍是空的 `expmoney = { }`（点号嵌套分类
    /// 的落盘路径在 0.5.7 上不可靠），改过的设置重启又回默认。
    /// 现改为：MelonPreferences 只做内存存储 + 菜单绑定，持久化由 mod 自己写
    /// UserData/Crystar.QoL/settings.cfg（catid|key=value，float 用 InvariantCulture），
    /// 启动时在所有 feature 注册完之后读回。不再调用任何 MelonPreferences 落盘 API。
    /// </summary>
    public static class QolConfig
    {
        public const string HarmonyId = "com.crystar.qol";
        public const string CategoryId = "Crystar.QoL";

        private static MelonPreferences_Category _cat;
        private static readonly List<Action> Resets = new List<Action>();

        private sealed class SavedEntry
        {
            public string Cat;
            public string Key;
            public Type Kind;
            public Func<object> Get;
            public Action<object> Set;
        }
        private static readonly List<SavedEntry> Entries = new List<SavedEntry>();

        public static MelonPreferences_Entry<bool> Master;
        public static MelonPreferences_Entry<int> LogLevel;
        public static MelonPreferences_Entry<int> Language;
        // v0.3.1-r13：状态条（ShowOverlay）与其热键 F3（Key.Overlay）已撤除——
        //   它只是"菜单没打开时常显一行 [Crystar.QoL] 开"，没有实际用途。
        //   ⚠ Overlay 类**不能删**：它是 F2 自绘菜单唯一的渲染载体，删了菜单就黑屏。

        public static MelonPreferences_Entry<int> KeyMenu;
        public static MelonPreferences_Entry<int> KeyKillSwitch;
        public static MelonPreferences_Entry<int> KeyPad1;
        public static MelonPreferences_Entry<int> KeyPad2;

        /// <summary>mod 自己的数据目录：UserData/Crystar.QoL</summary>
        public static string Dir;
        private static string OwnFile { get { return Path.Combine(Dir, "settings.cfg"); } }

        public static void Init()
        {
            _cat = MelonPreferences.CreateCategory(CategoryId);
            Master = Reg("Master", true, "Master switch / 总开关");
            LogLevel = Reg("LogLevel", 1, "0=off 1=normal 2=verbose");
            Language = Reg("Language", 0, "0=Chinese 1=English");
            KeyMenu = Reg("Key.Menu", (int)UnityEngine.KeyCode.F2, "Menu hotkey KeyCode");
            KeyKillSwitch = Reg("Key.KillSwitch", (int)UnityEngine.KeyCode.F8, "Kill switch KeyCode");
            KeyPad1 = Reg("Key.Pad1", 0, "Pad combo key 1 (0=none, use learn to bind)");
            KeyPad2 = Reg("Key.Pad2", 0, "Pad combo key 2 (0=none)");

            Dir = Path.Combine(MelonUtils.UserDataDirectory, "Crystar.QoL");
            Directory.CreateDirectory(Dir);
        }

        private static MelonPreferences_Entry<bool> Reg(string k, bool d, string desc)
        {
            var e = _cat.CreateEntry(k, d, desc); RegisterEntry(CategoryId, k, typeof(bool), () => e.Value, v => e.Value = (bool)v, e.ResetToDefault); return e;
        }

        private static MelonPreferences_Entry<int> Reg(string k, int d, string desc)
        {
            var e = _cat.CreateEntry(k, d, desc); RegisterEntry(CategoryId, k, typeof(int), () => e.Value, v => e.Value = (int)v, e.ResetToDefault); return e;
        }

        public static ConfigRegistrar ForFeature(string id)
        {
            var c = MelonPreferences.CreateCategory(CategoryId + "." + id);
            return new ConfigRegistrar(c);
        }

        public static void RegisterEntry(string cat, string key, Type kind, Func<object> get, Action<object> set, Action reset)
        {
            Entries.Add(new SavedEntry { Cat = cat, Key = key, Kind = kind, Get = get, Set = set });
            if (reset != null) Resets.Add(reset);
        }

        /// <summary>在所有 feature 注册完之后调用：把 settings.cfg 里的值应用回各条目。</summary>
        public static void Load()
        {
            try
            {
                string path = OwnFile;
                if (!File.Exists(path))
                {
                    QolLog.Info("cfg: 无 " + path + "，全部使用默认值");
                    return;
                }
                int applied = 0, unknown = 0;
                string[] lines = File.ReadAllLines(path);
                foreach (string raw in lines)
                {
                    string line = raw == null ? "" : raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    SavedEntry hit = null;
                    for (int i = 0; i < Entries.Count; i++)
                    {
                        if (Entries[i].Cat + "|" + Entries[i].Key == key) { hit = Entries[i]; break; }
                    }
                    if (hit == null) { unknown++; continue; }
                    try
                    {
                        object typed;
                        if (hit.Kind == typeof(bool)) typed = (object)(val == "true");
                        else if (hit.Kind == typeof(int)) typed = (object)int.Parse(val, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        else if (hit.Kind == typeof(float)) typed = (object)float.Parse(val, NumberStyles.Float, CultureInfo.InvariantCulture);
                        else typed = val;
                        hit.Set(typed);
                        applied++;
                    }
                    catch (Exception e)
                    {
                        QolLog.Warn("cfg: 应用 " + key + "=" + val + " 失败 " + e.Message);
                    }
                }
                QolLog.Info("cfg: 已从 settings.cfg 恢复 " + applied + " 项" + (unknown > 0 ? "（" + unknown + " 项未知键忽略）" : ""));
            }
            catch (Exception e)
            {
                QolLog.Warn("cfg: 读取 settings.cfg 失败 " + e.Message);
            }
        }

        /// <summary>全部恢复默认：配置值 + 各 feature 的运行时撤销，一次走完。</summary>
        public static void ResetAll()
        {
            int n = 0;
            foreach (var a in Resets)
            {
                try { a(); n++; }
                catch (Exception e) { QolLog.Error("reset: " + e.Message); }
            }
            Save();
            QolLog.Info("reset all defaults (" + n + " actions)");
        }

        /// <summary>落盘到自有 settings.cfg。格式：catid|key=value（UTF-8）。</summary>
        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Crystar.QoL settings (auto-written; safe to delete)");
                int n = 0;
                for (int i = 0; i < Entries.Count; i++)
                {
                    object v;
                    try { v = Entries[i].Get(); }
                    catch { continue; }
                    string s;
                    if (v is bool) s = (bool)v ? "true" : "false";
                    else if (v is float) s = ((float)v).ToString("R", CultureInfo.InvariantCulture);
                    else if (v is double) s = ((double)v).ToString("R", CultureInfo.InvariantCulture);
                    else s = v == null ? "" : v.ToString();
                    sb.Append(Entries[i].Cat).Append('|').Append(Entries[i].Key).Append('=').AppendLine(s);
                    n++;
                }
                File.WriteAllText(OwnFile, sb.ToString(), new UTF8Encoding(false));
                QolLog.Info("cfg: settings.cfg 已写入 " + n + " 项");
            }
            catch (Exception e)
            {
                QolLog.Warn("cfg: 写 settings.cfg 失败 " + e.Message);
            }
        }

        public static bool Enabled
        {
            get { return Master != null && Master.Value; }
        }
    }
}
