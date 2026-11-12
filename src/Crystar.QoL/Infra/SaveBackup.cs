using System;
using System.IO;
using System.Reflection;

namespace CrystarQoL
{
    /// <summary>
    /// 存档备份。跳章、成就解锁这类不可逆操作之前必须自动跑一次。
    /// 存档目录优先问游戏自己要（Game.SaveManager.GetSaveFolderPath），
    /// 拿不到再退回已知路径 %USERPROFILE%/AppData/LocalLow/Spike Chunsoft/CRYSTAR。
    /// </summary>
    public static class SaveBackup
    {
        private const string FallbackRel = "AppData\\LocalLow\\Spike Chunsoft\\CRYSTAR";

        /// <summary>Crystar 的 Steam AppID（`steamapps/appmanifest_981750.acf` 实锤）。</summary>
        private const string AppId = "981750";

        /// <summary>
        /// 存档目录。三条候选**逐个验证**（目录里必须真有 `*.dat`），不能拿到就信：
        ///
        /// ⛔ 2026-09-22 实锤的坑：v0.1 时代 `EnsureFirstRun()` 已经跑过一次，备份也确实生成了
        ///    （`backup/20260908-014139_first-run/`），但里面只有 `Unity/…Analytics/`
        ///    —— 因为 `Game.SaveManager.GetSaveFolderPath()` 当时没取到，退回的
        ///    `%USERPROFILE%/AppData/LocalLow/Spike Chunsoft/CRYSTAR` **根本不是存档目录**，
        ///    真存档在 Steam 云目录里。备份了个空壳，等于没备份。
        ///    ⇒ 修法：候选必须**验证里面真的有 .dat**，且优先用 Steam 云存档路径。
        /// </summary>
        public static string SaveDir()
        {
            string viaGame = null;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType("Game.SaveManager", false, false); } catch { continue; }
                    if (t == null) continue;

                    var m = t.GetMethod("GetSaveFolderPath", BindingFlags.Public | BindingFlags.Static);
                    if (m == null) continue;

                    var v = m.Invoke(null, null) as string;
                    if (!string.IsNullOrEmpty(v)) { viaGame = v; break; }
                }
            }
            catch (Exception e)
            {
                QolLog.Debug("SaveDir via game failed: " + e.Message);
            }
            if (LooksLikeSave(viaGame)) return viaGame;

            // Steam 云存档：<Steam>/userdata/<任意 SteamID>/981750/remote/
            string steam = SteamSaveDir();
            if (steam != null) return steam;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string fb = string.IsNullOrEmpty(profile) ? FallbackRel : Path.Combine(profile, FallbackRel);
            return LooksLikeSave(fb) ? fb : (viaGame ?? fb);
        }

        /// <summary>目录里真有存档文件才算数（Analytics 目录也会被排掉）。</summary>
        private static bool LooksLikeSave(string dir)
        {
            try
            {
                return !string.IsNullOrEmpty(dir)
                       && Directory.Exists(dir)
                       && Directory.GetFiles(dir, "*.dat").Length > 0;
            }
            catch { return false; }
        }

        private static string SteamSaveDir()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string sp = k == null ? null : k.GetValue("SteamPath") as string;
                    if (string.IsNullOrEmpty(sp)) return null;

                    string ud = Path.Combine(sp, "userdata");
                    if (!Directory.Exists(ud)) return null;

                    foreach (string d in Directory.GetDirectories(ud))
                    {
                        string p = Path.Combine(d, AppId, "remote");
                        if (LooksLikeSave(p)) return p;
                    }
                }
            }
            catch (Exception e)
            {
                QolLog.Debug("steam save dir: " + e.Message);
            }
            return null;
        }

        /// <summary>把存档目录整个复制一份到 UserData/Crystar.QoL/backup/ 下。</summary>
        public static string Backup(string tag)
        {
            string src = SaveDir();
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
            {
                QolLog.Warn("save dir not found: " + src);
                return null;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string name = stamp + (string.IsNullOrEmpty(tag) ? "" : "_" + tag);
            string dst = Path.Combine(QolConfig.Dir, "backup", Sanitize(name));

            // 备份前先看清楚到底要拷几个存档文件 —— 拷了 0 个就是路径又指错了（见 SaveDir 注释）。
            int nDat = 0;
            try { nDat = Directory.GetFiles(src, "*.dat").Length; } catch { }
            if (nDat <= 0)
                QolLog.Warn("save backup: 源目录里没有 .dat（" + src + "）⇒ 疑似路径不对，本次备份可能没意义");

            try
            {
                Directory.CreateDirectory(dst);
                CopyDir(src, dst);
                QolLog.Info("save backup -> " + dst + "（源 " + src + "，" + nDat + " 个 .dat）");
                return dst;
            }
            catch (Exception e)
            {
                QolLog.Error("save backup failed: " + e.Message);
                return null;
            }
        }

        /// <summary>首次启动自动备份一次，给玩家一个"出厂状态"可回退。</summary>
        public static void EnsureFirstRun()
        {
            string marker = Path.Combine(QolConfig.Dir, "backup", ".first-run-done");
            try
            {
                if (File.Exists(marker))
                {
                    // 标记在 ≠ 备份有效：v0.1 那次指错了路径，备份里只有 Analytics。
                    // ⇒ 校验内容，不对就重做一次（旧目录保留，不删玩家文件）。
                    if (FirstRunBackupOk()) return;
                    QolLog.Warn("first-run backup: 已有标记但备份里没有 .dat（旧版路径错误）⇒ 重做一次");
                }
                Directory.CreateDirectory(Path.Combine(QolConfig.Dir, "backup"));
                Backup("first-run");
                File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception e)
            {
                QolLog.Warn("first-run backup: " + e.Message);
            }
        }

        private static bool FirstRunBackupOk()
        {
            try
            {
                string root = Path.Combine(QolConfig.Dir, "backup");
                if (!Directory.Exists(root)) return false;
                foreach (string d in Directory.GetDirectories(root, "*_first-run"))
                {
                    if (Directory.GetFiles(d, "*.dat", SearchOption.AllDirectories).Length > 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); }
                catch (Exception e) { QolLog.Debug("copy " + f + ": " + e.Message); }
            }
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(s);
            foreach (var c in bad) sb.Replace(c, '_');
            return sb.ToString();
        }
    }
}
