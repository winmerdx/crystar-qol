using System;
using System.Runtime.InteropServices;

namespace CrystarQoL
{
    /// <summary>
    /// 手柄输入：P/Invoke 直读 XInput，不经过 UnityEngine.Input，也不经过游戏输入层。
    ///
    /// 【为什么必须绕开 Unity Input】本作手柄走 StellaInputManager（原生层读 XInput），
    /// UnityEngine.Input 的 JoystickButton 在本作收不到任何手柄事件（v0.1 实测）。
    /// 旧的"Unity KeyCode 学习/组合键"对手柄永远是瞎的 —— v0.1.6 用户实测确认。
    ///
    /// 【为什么必须绕开游戏输入层】菜单打开时 gate.pause 把 GameSpeed 冻到 0.0001，
    /// StellaInputManager 的状态停在冻结帧，读它拿不到新按键。
    ///
    /// 【虚键码约定】XInput 按钮编码为 5000+bitIndex，可与 Unity KeyCode 数值共存
    /// （Unity KeyCode 上限约 350，不会冲突）。按键学习捕获手柄时返回虚键码，
    /// 存进 settings.cfg 与键盘绑定走同一套管道。
    ///
    /// 【v0.1.8 修复：P/Invoke 压根没生效】
    ///   v0.1.7 日志实锤 `pad: XInput 不可用（缺 dll）` —— 但 dll 是存在的。真因是
    ///   `[DllImport("xinput1_4.dll")] static extern int XIGet4(...)` **没写 EntryPoint**，
    ///   P/Invoke 按**方法名**找导出函数，dll 里根本没有 `XIGet4` → 三个 dll 全抛
    ///   EntryPointNotFoundException → 本层静默停用（手柄完全没反应）。
    ///   对策：三个 DllImport 全部显式 `EntryPoint = "XInputGetState"`。
    ///   **铁律：给 P/Invoke 方法起别名时，必须显式写 EntryPoint。**
    ///   另：改为轮询 0~3 全部槽位并合并（手柄不一定在槽 0），增加摇杆方向位与连接诊断日志。
    /// </summary>
    internal static class PadInput
    {
        public const int VirtualBase = 5000;

        // XInput 原生按钮位（XINPUT_GAMEPAD_*）
        private const uint DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
        private const uint Start = 0x0010, Back = 0x0020;
        private const uint LeftThumb = 0x0040, RightThumb = 0x0080;
        public const uint BtnLB = 0x0100;
        public const uint BtnRB = 0x0200;
        private const uint BtnA = 0x1000, BtnB = 0x2000, BtnX = 0x4000, BtnY = 0x8000;

        // 摇杆四个方向：本层合成的虚拟位（不与任何 XInput 原生位冲突，见 0x10000 起）
        private const uint StickUp = 0x10000, StickDown = 0x20000, StickLeft = 0x40000, StickRight = 0x80000;
        private const int StickDeadZone = 16000;   // XInput 摇杆量程 ±32767

        // 供 QolInput 做菜单按键映射用的只读别名（方向键 = 十字键 或 左摇杆）
        public const uint DPadUpBit = DPadUp;
        public const uint DPadDownBit = DPadDown;
        public const uint DPadLeftBit = DPadLeft;
        public const uint DPadRightBit = DPadRight;
        public const uint BtnABit = BtnA;
        public const uint BtnBBit = BtnB;
        public const uint UpBit = DPadUp | StickUp;
        public const uint DownBit = DPadDown | StickDown;
        public const uint LeftBit = DPadLeft | StickLeft;
        public const uint RightBit = DPadRight | StickRight;

        // 数组下标 i 对应虚键码 VirtualBase+i（前 14 个可捕获，摇杆方向不进 Capture）
        private static readonly uint[] Bits =
        {
            DPadUp, DPadDown, DPadLeft, DPadRight, Start, Back, LeftThumb, RightThumb,
            BtnLB, BtnRB, BtnA, BtnB, BtnX, BtnY
        };
        private static readonly string[] Names =
        {
            "PadUp", "PadDown", "PadLeft", "PadRight", "Start", "Back", "LStick", "RStick",
            "LB", "RB", "PadA", "PadB", "PadX", "PadY"
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        // 【必须显式 EntryPoint】P/Invoke 默认按 C# 方法名找导出函数，dll 里只有 XInputGetState
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XIGet4(uint idx, ref XINPUT_STATE s);
        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        private static extern int XIGet3(uint idx, ref XINPUT_STATE s);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern int XIGet9(uint idx, ref XINPUT_STATE s);

        private delegate int GetStateFn(uint idx, ref XINPUT_STATE s);
        private static GetStateFn _fn;
        private static bool _fnTested;
        private static string _dll;

        private static uint _cur, _prev;
        private static bool _polledThisFrame;
        private static int _slot = -2;

        private static GetStateFn Fn()
        {
            if (_fn != null) return _fn;
            if (_fnTested) return null;
            _fnTested = true;
            XINPUT_STATE s = default(XINPUT_STATE);
            // 依次尝试三个常见 XInput dll；失败（缺 dll / 缺导出）就换下一个
            try { XIGet4(0, ref s); _fn = XIGet4; _dll = "xinput1_4"; }
            catch (Exception e4)
            {
                try { XIGet3(0, ref s); _fn = XIGet3; _dll = "xinput1_3"; }
                catch (Exception e3)
                {
                    try { XIGet9(0, ref s); _fn = XIGet9; _dll = "xinput9_1_0"; }
                    catch (Exception e9)
                    {
                        QolLog.Info("pad: XInput 不可用，手柄功能停用 | "
                                    + e4.GetType().Name + "/" + e3.GetType().Name + "/" + e9.GetType().Name);
                    }
                }
            }
            return _fn;
        }

        /// <summary>每帧开头调用一次（ModMain.OnUpdate 顶部）。只在此处真正轮询设备。</summary>
        public static void NewFrame()
        {
            _prev = _cur;
            _cur = 0;
            _polledThisFrame = true;

            var fn = Fn();
            if (fn == null) return;

            try
            {
                uint any = 0;
                int slot = -1;
                int lx = 0, ly = 0;
                for (uint i = 0; i < 4; i++)
                {
                    XINPUT_STATE st = default(XINPUT_STATE);
                    if (fn(i, ref st) != 0) continue;   // ERROR_SUCCESS = 0；1167 = 未连接
                    any |= st.Gamepad.wButtons;
                    if (slot < 0) { slot = (int)i; }
                    lx = st.Gamepad.sThumbLX;
                    ly = st.Gamepad.sThumbLY;
                }

                // 左摇杆 → 合成方向位（菜单里摇杆和十字键等价）
                if (ly > StickDeadZone) any |= StickUp;
                else if (ly < -StickDeadZone) any |= StickDown;
                if (lx > StickDeadZone) any |= StickRight;
                else if (lx < -StickDeadZone) any |= StickLeft;

                _cur = any;

                if (slot != _slot)
                {
                    _slot = slot;
                    QolLog.Info(slot < 0
                        ? "pad: 未检测到 XInput 手柄（dll=" + _dll + " 已加载，槽 0~3 均无设备）"
                        : "pad: XInput 手柄已连接 slot=" + slot + " dll=" + _dll);
                }

                // 诊断：每次有新按下的按钮就记一条，方便从 debug.log 确认手柄层是否活着
                uint pressed = _cur & ~_prev;
                if (pressed != 0) QolLog.Info("pad: press 0x" + pressed.ToString("X") + " (" + Describe(pressed) + ")");
            }
            catch (Exception e)
            {
                QolLog.DebugThrottled("pad.poll", "pad poll: " + e.Message);
            }
        }

        private static string Describe(uint mask)
        {
            string s = "";
            for (int i = 0; i < Bits.Length; i++)
                if ((mask & Bits[i]) != 0) s += (s.Length > 0 ? "+" : "") + Names[i];
            if ((mask & StickUp) != 0) s += (s.Length > 0 ? "+" : "") + "StickUp";
            if ((mask & StickDown) != 0) s += (s.Length > 0 ? "+" : "") + "StickDown";
            if ((mask & StickLeft) != 0) s += (s.Length > 0 ? "+" : "") + "StickLeft";
            if ((mask & StickRight) != 0) s += (s.Length > 0 ? "+" : "") + "StickRight";
            return s.Length > 0 ? s : "?";
        }

        private static void EnsurePolled()
        {
            if (!_polledThisFrame) NewFrame();
        }

        /// <summary>按钮是否按住。btn 用位标志（BtnLB / BtnRB / UpBit 等）。</summary>
        public static bool Held(uint btn)
        {
            EnsurePolled();
            return (_cur & btn) != 0;
        }

        /// <summary>本帧新按下（边沿检测）。组合掩码下：任一按下且之前都没按下 = true。</summary>
        public static bool Pressed(uint btn)
        {
            EnsurePolled();
            return (_cur & btn) != 0 && (_prev & btn) == 0;
        }

        /// <summary>虚键码（5000+bitIndex）本帧是否新按下。</summary>
        public static bool VirtualPressed(int vkey)
        {
            int i = vkey - VirtualBase;
            if (i < 0 || i >= Bits.Length) return false;
            return Pressed(Bits[i]);
        }

        /// <summary>虚键码（5000+bitIndex）是否按住。</summary>
        public static bool VirtualHeld(int vkey)
        {
            int i = vkey - VirtualBase;
            if (i < 0 || i >= Bits.Length) return false;
            return Held(Bits[i]);
        }

        /// <summary>按键学习：扫描全部 XInput 按钮，返回虚键码；本帧没有新按下的按钮返回 0。</summary>
        public static int Capture()
        {
            EnsurePolled();
            for (int i = 0; i < Bits.Length; i++)
                if (Pressed(Bits[i])) return VirtualBase + i;
            return 0;
        }

        public static string Name(int vkey)
        {
            int i = vkey - VirtualBase;
            if (i < 0 || i >= Names.Length) return null;
            return Names[i];
        }
    }
}
