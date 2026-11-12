using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using MelonLoader;

namespace CrystarQoL
{
    /// <summary>
    /// 三档日志 + 环形缓冲 + 双通道。
    /// 环形缓冲常驻内存（最近 2000 条）默认不落盘，零开销；出了 bug 再开调试也能回看到事发前的上下文。
    /// </summary>
    public static class QolLog
    {
        public enum Lv
        {
            Off = 0,
            Normal = 1,
            Verbose = 2
        }

        private const int RingMax = 2000;
        private const long ThrottleMs = 200;

        private static readonly object Sync = new object();
        private static readonly List<string> Ring = new List<string>(RingMax);
        private static readonly Dictionary<string, long> LastHit = new Dictionary<string, long>();

        private static StreamWriter _writer;
        private static string _path;
        private static int _sinceFlush;

        public static string FilePath { get { return _path; } }

        public static Lv Level
        {
            get
            {
                var e = QolConfig.LogLevel;
                if (e == null) return Lv.Normal;
                int v = e.Value;
                return v < 0 ? Lv.Off : (v > 2 ? Lv.Verbose : (Lv)v);
            }
        }

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "debug.log");
                _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                _writer.WriteLine("");
                _writer.WriteLine("==== Crystar.QoL session " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
                _writer.Flush();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[QoL] log file unavailable: " + e.Message);
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (_writer == null) return;
                try { _writer.Flush(); _writer.Dispose(); } catch { }
                _writer = null;
            }
        }

        public static void Error(string msg)
        {
            Write("E", msg);
            MelonLogger.Error("[QoL] " + msg);
        }

        public static void Warn(string msg)
        {
            Write("W", msg);
            MelonLogger.Warning("[QoL] " + msg);
        }

        public static void Info(string msg)
        {
            if (Level < Lv.Normal) return;
            Write("I", msg);
            MelonLogger.Msg("[QoL] " + msg);
        }

        public static void Debug(string msg)
        {
            if (Level < Lv.Verbose) return;
            Write("D", msg);
            MelonLogger.Msg("[QoL] " + msg);
        }

        /// <summary>每帧钩子专用：同类 200ms 内只打一条，否则几分钟就能刷出 GB 级日志。</summary>
        public static void DebugThrottled(string key, string msg)
        {
            if (Level < Lv.Verbose) return;
            long now = Environment.TickCount;
            long last;
            if (LastHit.TryGetValue(key, out last) && now - last >= 0 && now - last < ThrottleMs) return;
            LastHit[key] = now;
            Debug(msg);
        }

        private static readonly Dictionary<string, int> TraceCount = new Dictionary<string, int>();
        private static readonly Dictionary<string, long> TraceTick = new Dictionary<string, long>();

        /// <summary>
        /// 诊断通道：前 120 次全量落盘（够看清楚"一次攻击到底进了几趟"），之后每秒最多一条。
        /// 用于事后确认倍率有没有被重复施加，不需要玩家改日志级别。
        /// </summary>
        public static void Trace(string key, string msg)
        {
            try
            {
                int n;
                TraceCount.TryGetValue(key, out n);
                TraceCount[key] = ++n;

                long now = Environment.TickCount;
                long last;
                TraceTick.TryGetValue(key, out last);
                if (n > 120 && last != 0 && now - last >= 0 && now - last < 1000) return;
                TraceTick[key] = now;
                Info("#" + n + " " + msg);
            }
            catch { }
        }

        private static void Write(string tag, string msg)
        {
            string line = "[" + tag + " " + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg;
            lock (Sync)
            {
                if (Ring.Count >= RingMax) Ring.RemoveAt(0);
                Ring.Add(line);

                if (_writer != null)
                {
                    try
                    {
                        _writer.WriteLine(line);
                        if (++_sinceFlush >= 32 || tag == "E") { _writer.Flush(); _sinceFlush = 0; }
                    }
                    catch { }
                }
            }
        }

        /// <summary>把内存里的最近日志倒出来（诊断包用）。</summary>
        public static string Snapshot()
        {
            lock (Sync)
            {
                var sb = new StringBuilder();
                foreach (var l in Ring) sb.AppendLine(l);
                return sb.ToString();
            }
        }

        /// <summary>开启调试 / 导出诊断包时把缓冲整体落盘。</summary>
        public static void FlushRing(string target)
        {
            try
            {
                File.WriteAllText(target, Snapshot(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[QoL] flush ring failed: " + e.Message);
            }
        }

        public static void MakeZip(string zipPath, IEnumerable<KeyValuePair<string, string>> files)
        {
            string tmp = zipPath + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            try
            {
                foreach (var kv in files)
                {
                    string name = Sanitize(kv.Key);
                    try { File.Copy(kv.Value, Path.Combine(tmp, name), true); }
                    catch { File.WriteAllText(Path.Combine(tmp, name), "(unreadable: " + kv.Value + ")"); }
                }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tmp, zipPath);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        private static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s);
            foreach (var c in bad) sb.Replace(c, '_');
            return sb.ToString();
        }
    }
}
