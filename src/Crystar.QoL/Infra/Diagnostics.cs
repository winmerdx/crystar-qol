using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace CrystarQoL
{
    /// <summary>
    /// 一键导出诊断包。
    /// 不要指望玩家去翻 MelonLoader/Logs/ —— 这是用户唯一可靠的取日志路径。
    /// 产物丢在游戏根目录：CrystarQoL-Diagnostics-<时间戳>.zip
    /// </summary>
    public static class Diagnostics
    {
        public static string Export()
        {
            string gameDir = GameDir();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string zip = Path.Combine(gameDir, "CrystarQoL-Diagnostics-" + stamp + ".zip");

            var files = new List<KeyValuePair<string, string>>();

            // 1) 版本指纹
            string finger = Path.Combine(QolConfig.Dir, "version-fingerprint.txt");
            File.WriteAllText(finger, BuildFingerprint(gameDir), Encoding.UTF8);
            files.Add(new KeyValuePair<string, string>("version-fingerprint.txt", finger));

            // 2) 内存里最近 2000 条日志（默认不落盘，出事时才倒出来）
            string ring = Path.Combine(QolConfig.Dir, "recent-log.txt");
            QolLog.FlushRing(ring);
            files.Add(new KeyValuePair<string, string>("recent-log.txt", ring));

            // 3) mod 自己的日志文件
            if (QolLog.FilePath != null && File.Exists(QolLog.FilePath))
                files.Add(new KeyValuePair<string, string>("debug.log", QolLog.FilePath));

            // 4) 配置
            string cfg = Path.Combine(MelonUtils.UserDataDirectory, "MelonPreferences.cfg");
            if (File.Exists(cfg)) files.Add(new KeyValuePair<string, string>("MelonPreferences.cfg", cfg));

            // 5) MelonLoader 日志：当次 + 最近两份历史
            string latest = Path.Combine(gameDir, "MelonLoader", "Latest.log");
            if (File.Exists(latest)) files.Add(new KeyValuePair<string, string>("MelonLoader-Latest.log", latest));

            string logsDir = Path.Combine(gameDir, "MelonLoader", "Logs");
            if (Directory.Exists(logsDir))
            {
                var logs = new List<string>(Directory.GetFiles(logsDir, "*.log"));
                logs.Sort((a, b) => string.CompareOrdinal(File.GetLastWriteTimeUtc(b).Ticks.ToString(), File.GetLastWriteTimeUtc(a).Ticks.ToString()));
                for (int i = 0; i < logs.Count && i < 2; i++)
                    files.Add(new KeyValuePair<string, string>("MelonLoader-" + Path.GetFileName(logs[i]), logs[i]));
            }

            try
            {
                QolLog.MakeZip(zip, files);
                QolLog.Info("diagnostics -> " + zip);
                return zip;
            }
            catch (Exception e)
            {
                // zip 失败就退化成复制目录，别让玩家空手而归
                QolLog.Error("diagnostics failed: " + e.Message);
                string dir = Path.Combine(gameDir, "CrystarQoL-Diagnostics-" + stamp);
                try
                {
                    Directory.CreateDirectory(dir);
                    foreach (var kv in files) File.Copy(kv.Value, Path.Combine(dir, kv.Key), true);
                    return dir;
                }
                catch { return null; }
            }
        }

        private static string GameDir()
        {
            try
            {
                var d = MelonUtils.GameDirectory;
                if (!string.IsNullOrEmpty(d)) return d;
            }
            catch { }
            return Directory.GetCurrentDirectory();
        }

        private static string BuildFingerprint(string gameDir)
        {
            var sb = new StringBuilder();
            sb.Append("Crystar.QoL diagnostics\n");
            sb.Append("exported: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            try
            {
                sb.Append("mod: ").Append(typeof(Diagnostics).Assembly.GetName().Version).Append('\n');
                sb.Append("melonloader: ")
                  .Append(System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(MelonLoaderDir(gameDir), "MelonLoader.dll")).ProductVersion)
                  .Append('\n');
            }
            catch { }
            sb.Append("game dir: ").Append(gameDir).Append('\n');
            sb.Append("log level: ").Append(Loc.Level((int)QolLog.Level)).Append('\n');
            sb.Append("input gate patched: ").Append(GameInputGate.PatchedCount).Append('\n');
            sb.Append("kill switch: ").Append(KillSwitch.Tripped ? "TRIPPED" : "ok").Append('\n');

            string asm = Path.Combine(gameDir, "GameAssembly.dll");
            if (File.Exists(asm))
            {
                sb.Append("GameAssembly.dll size: ").Append(new FileInfo(asm).Length).Append('\n');
                try
                {
                    using (var md5 = MD5.Create())
                    using (var fs = File.OpenRead(asm))
                    {
                        var hash = md5.ComputeHash(fs);
                        var hex = new StringBuilder(32);
                        foreach (var b in hash) hex.Append(b.ToString("x2"));
                        sb.Append("GameAssembly.dll md5: ").Append(hex).Append('\n');
                    }
                }
                catch (Exception e) { sb.Append("GameAssembly hash failed: ").Append(e.Message).Append('\n'); }
            }

            sb.Append("parameter (数值表) md5: ");
            string param = Path.Combine(gameDir, "CRYSTAR_Data", "StreamingAssets", "parameter");
            if (File.Exists(param))
            {
                try
                {
                    using (var md5 = MD5.Create())
                    using (var fs = File.OpenRead(param))
                    {
                        var hash = md5.ComputeHash(fs);
                        var hex = new StringBuilder(32);
                        foreach (var b in hash) hex.Append(b.ToString("x2"));
                        sb.Append(hex);
                    }
                }
                catch (Exception e) { sb.Append("failed: ").Append(e.Message); }
            }
            else sb.Append("not found");
            sb.Append('\n');

            return sb.ToString();
        }

        private static string MelonLoaderDir(string gameDir)
        {
            return Path.Combine(gameDir, "MelonLoader");
        }
    }
}
