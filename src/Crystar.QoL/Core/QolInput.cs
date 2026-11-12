using UnityEngine;

namespace CrystarQoL
{
    /// <summary>
    /// mod 自己的输入层：键盘直读 UnityEngine.Input，手柄直读 XInput（PadInput）。
    ///
    /// 为什么不用游戏的输入层：Common.InputManager 和 StellaInputManager 会在菜单打开时被
    /// GameInputGate 掐断/冻结（否则改 mod 值的按键会穿透到游戏里），mod 必须走一条
    /// 不会被自己掐断的路。
    ///
    /// 【v0.1.7 手柄改造】本作手柄走 StellaInputManager（原生层），UnityEngine.Input 的
    /// JoystickButton 收不到任何手柄事件（v0.1 实测）——旧的"Unity KeyCode 学习/组合键"
    /// 对手柄永远无效（v0.1.6 用户实测确认）。现在手柄走 PadInput（XInput P/Invoke）：
    ///   - 菜单导航自动映射：方向键↔十字键、回车↔A、Esc/Backspace↔B、Q/E↔LB/RB
    ///   - 菜单开关：默认组合 LB+RB（Key.Pad1/2 未绑定时）；学习绑定的手柄键以
    ///     虚键码 5000+ 存进配置，与键盘绑定同管道。
    /// </summary>
    public static class QolInput
    {
        private const int ScanMin = 8;
        private const int ScanMax = 350;

        /// <summary>每帧开头调用一次：轮询一次 XInput（ModMain.OnUpdate 顶部）。</summary>
        public static void NewFrame()
        {
            PadInput.NewFrame();
        }

        public static bool Down(int kc)
        {
            if (kc <= 0) return false;
            if (kc >= PadInput.VirtualBase) return PadInput.VirtualPressed(kc);
            return Input.GetKeyDown((KeyCode)kc) || PadMappedDown(kc);
        }

        public static bool Held(int kc)
        {
            if (kc <= 0) return false;
            if (kc >= PadInput.VirtualBase) return PadInput.VirtualHeld(kc);
            return Input.GetKey((KeyCode)kc) || PadMappedHeld(kc);
        }

        /// <summary>菜单开关：键盘热键 或 手柄组合（默认 LB+RB，未绑定时）。</summary>
        public static bool MenuPressed()
        {
            return Down(QolConfig.KeyMenu.Value) || PadComboDown();
        }

        public static bool KillPressed()
        {
            return Down(QolConfig.KeyKillSwitch.Value);
        }

        private static bool PadComboDown()
        {
            int a = QolConfig.KeyPad1 == null ? 0 : QolConfig.KeyPad1.Value;
            int b = QolConfig.KeyPad2 == null ? 0 : QolConfig.KeyPad2.Value;

            // 未绑定 → 默认组合 LB+RB（必须走 XInput：Unity Input 在本作收不到手柄）
            if (a <= 0 && b <= 0)
            {
                return PadInput.Held(PadInput.BtnLB) && PadInput.Held(PadInput.BtnRB)
                    && (PadInput.Pressed(PadInput.BtnLB) || PadInput.Pressed(PadInput.BtnRB));
            }
            if (a <= 0) return false;

            bool downA = Down(a);
            if (b <= 0) return downA;

            // 组合键：两个都处于按下状态，且至少一个是本帧刚按下的
            return Held(a) && Held(b) && (downA || Down(b));
        }

        // ---- 菜单按键 → 手柄映射（只映射无冲突的键：字母键不映射，
        //      否则手柄 A（确认）会撞上 KeyCode.A（菜单左调）） ----

        private static bool PadMappedDown(int kc)
        {
            uint b = MapToPad(kc);
            return b != 0 && PadInput.Pressed(b);
        }

        private static bool PadMappedHeld(int kc)
        {
            uint b = MapToPad(kc);
            return b != 0 && PadInput.Held(b);
        }

        private static uint MapToPad(int kc)
        {
            switch ((KeyCode)kc)
            {
                case KeyCode.UpArrow: return PadInput.UpBit;         // 十字键上 或 左摇杆上
                case KeyCode.DownArrow: return PadInput.DownBit;
                case KeyCode.LeftArrow: return PadInput.LeftBit;
                case KeyCode.RightArrow: return PadInput.RightBit;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: return PadInput.BtnABit;
                case KeyCode.Escape:
                case KeyCode.Backspace: return PadInput.BtnBBit;
                case KeyCode.Q: return PadInput.BtnLB;
                case KeyCode.E: return PadInput.BtnRB;
                default: return 0;
            }
        }

        /// <summary>
        /// 按键学习：抓当前按下的任意键（键盘 + 手柄），返回键码，没抓到返回 0。
        /// 不用 Input.anyKeyDown —— 它不在 DummyDll 里（本作 IL2CPP 裁掉了）。
        /// 手柄返回虚键码 5000+（见 PadInput）。
        /// 只在"学习模式"下才会逐键扫描，常态不走这条路径。
        /// </summary>
        public static int Capture()
        {
            int pad = PadInput.Capture();
            if (pad > 0) return pad;
            for (int k = ScanMin; k <= ScanMax; k++)
            {
                try
                {
                    if (Input.GetKeyDown((KeyCode)k)) return k;
                }
                catch
                {
                    // 某些 KeyCode 在运行时不存在，跳过
                }
            }
            return 0;
        }

        public static string Name(int kc)
        {
            if (kc <= 0) return "-";
            if (kc >= PadInput.VirtualBase)
            {
                string n = PadInput.Name(kc);
                return n ?? ("Pad#" + (kc - PadInput.VirtualBase));
            }
            return ((KeyCode)kc).ToString();
        }
    }
}
