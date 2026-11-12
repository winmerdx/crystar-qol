using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CrystarQoL
{
    /// <summary>
    /// 隐藏 MelonLoader 的控制台窗口。
    ///
    /// 【v0.1.6 加强】v0.1.5 日志显示「已确认控制台不可见 (frame 2)」但玩家仍看到窗口 ——
    /// GetConsoleWindow/IsWindowVisible 的判定在某些宿主下不可信（伪控制台/终端宿主）。
    /// 现在三路并进：
    ///   1. GetConsoleWindow() → ShowWindow(SW_HIDE)（原方案，保留）；
    ///   2. EnumWindows 扫本进程所有 ConsoleWindowClass 可见窗口 → 逐个 SW_HIDE
    ///      （按真实窗口类找，绕开句柄判定失真）；
    ///   3. 60 帧后只要扫到任何一个可见控制台窗口 → FreeConsole() 兜底。
    ///   console.show 标记文件存在 → 完全不做（要看实时日志时用）。
    /// 调用时机：OnInitializeMelon 一次 + OnUpdate 每帧直到完成。
    /// 彻底不闪窗现已由随包 Bootstrap.dll 的 6 字节补丁实现（2026-11-10，无需启动参数）；
    /// 本类保留为兜底 / 换回原版框架时的兼容层。取证见 docs/MelonLoader-Bootstrap控制台取证.md。
    /// </summary>
    internal static class ConsoleHider
    {
        private const int SW_HIDE = 0;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static int _frame;
        private static bool _done;
        private static bool _disabled;
        private static readonly List<IntPtr> _found = new List<IntPtr>();
        private static StringBuilder _sb = new StringBuilder(256);

        /// <summary>由 ModMain（OnInitializeMelon 一次 + OnUpdate 每帧）调用，直到确认处理完成。</summary>
        public static void Tick()
        {
            if (_done || _disabled) return;

            try
            {
                if (File.Exists(Path.Combine(QolConfig.Dir, "console.show")))
                {
                    _disabled = true;
                    QolLog.Info("console: 检测到 console.show，保持控制台可见");
                    return;
                }

                if (++_frame == 1)
                    QolLog.Info("console: 开始隐藏控制台");

                // 1) 常规路径
                IntPtr h = GetConsoleWindow();
                if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);

                // 2) 按窗口类扫描本进程的可见控制台窗口
                int visible = SweepAndHide();

                if (visible == 0)
                {
                    QolLog.Info("console: 已确认无可见控制台窗口 (frame " + _frame + ")");
                    _done = true;
                    return;
                }

                if (_frame >= 60)
                {
                    // SW_HIDE 压了 1 秒还有可见控制台窗口 → FreeConsole 兜底
                    if (FreeConsole())
                        QolLog.Warn("console: 反复隐藏无效，已 FreeConsole 兜底 (frame " + _frame + ", 可见 " + visible + ")");
                    else
                        QolLog.Warn("console: 隐藏失败且 FreeConsole 失败，放弃 (frame " + _frame + ")");
                    _done = true;
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("console: 隐藏异常 " + e.Message);
                _done = true;
            }
        }

        /// <summary>隐藏并统计本进程所有可见的 ConsoleWindowClass 窗口。</summary>
        private static int SweepAndHide()
        {
            _found.Clear();
            try
            {
                uint pid = GetCurrentProcessId();
                EnumWindows(delegate(IntPtr wnd, IntPtr lp)
                {
                    try
                    {
                        uint wpid;
                        GetWindowThreadProcessId(wnd, out wpid);
                        if (wpid != pid) return true;
                        _sb.Length = 0;
                        if (GetClassName(wnd, _sb, 256) <= 0) return true;
                        if (_sb.ToString() != "ConsoleWindowClass") return true;
                        if (IsWindowVisible(wnd)) { _found.Add(wnd); ShowWindow(wnd, SW_HIDE); }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return _found.Count;
        }

        /// <summary>旧入口：保持兼容（等价于一次 Tick）。</summary>
        public static void Run()
        {
            Tick();
        }
    }
}
