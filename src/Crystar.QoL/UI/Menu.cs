using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CrystarQoL
{
    /// <summary>
    /// 注入式菜单。只认识 MenuItem，不认识任何具体功能。
    /// 键盘为主；手柄通过"按键学习"绑定到玩家自己的组合（Steam Input 场景下猜 KeyCode 是猜不中的）。
    /// </summary>
    public sealed class Menu
    {
        private const int PageSize = 16;
        private const long RepeatDelayMs = 380;
        private const long RepeatRateMs = 70;

        private readonly List<MenuItem> _items = new List<MenuItem>();
        private int _sel;
        private int _page;
        private bool _open;

        private bool _confirming;
        private Action<int> _learnCb;
        private string _status;
        private long _statusUntil;

        private long _repeatAt;
        private int _repeatDir;

        public int Count { get { return _items.Count; } }
        public bool Open { get { return _open; } }
        public bool Learning { get { return _learnCb != null; } }

        public void Build(IEnumerable<IFeature> features)
        {
            _items.Clear();
            if (features == null) return;
            foreach (var f in features)
            {
                if (f == null) continue;
                try
                {
                    foreach (var mi in f.BuildMenu())
                    {
                        if (mi != null) _items.Add(mi);
                    }
                }
                catch (Exception e)
                {
                    QolLog.Error("menu build '" + f.Id + "': " + e.Message);
                }
            }
            if (_sel >= _items.Count) _sel = _items.Count > 0 ? _items.Count - 1 : 0;
        }

        public void Toggle()
        {
            _open = !_open;
            _confirming = false;
            QolLog.Debug("menu " + (_open ? "open" : "closed"));
        }

        public void Close()
        {
            _open = false;
            _confirming = false;
        }

        public void ShowStatus(string s)
        {
            _status = s;
            _statusUntil = Environment.TickCount + 4000;
        }

        /// <summary>进入按键学习：下一次按下的键会通过回调返回。</summary>
        public void RequestLearn(Action<int> cb)
        {
            _learnCb = cb;
            _confirming = false;
        }

        public void Update()
        {
            if (_learnCb != null)
            {
                int k = QolInput.Capture();
                if (k > 0)
                {
                    var cb = _learnCb;
                    _learnCb = null;
                    if (k != (int)KeyCode.Escape) { try { cb(k); } catch (Exception e) { QolLog.Error("learn: " + e.Message); } }
                    else ShowStatus(Loc.T("capture.cancel"));
                }
                return;
            }

            if (!_open || _items.Count == 0) return;

            if (QolInput.Down((int)KeyCode.Escape) || QolInput.Down((int)KeyCode.Backspace))
            {
                if (_confirming) { _confirming = false; return; }
                Close();
                return;
            }

            int dir = 0;
            if (QolInput.Down((int)KeyCode.UpArrow) || QolInput.Down((int)KeyCode.W)) { dir = -1; _repeatDir = -1; _repeatAt = Environment.TickCount + RepeatDelayMs; }
            else if (QolInput.Down((int)KeyCode.DownArrow) || QolInput.Down((int)KeyCode.S)) { dir = 1; _repeatDir = 1; _repeatAt = Environment.TickCount + RepeatDelayMs; }
            else if (_repeatDir != 0 && HeldNav(_repeatDir) && Environment.TickCount >= _repeatAt)
            {
                dir = _repeatDir;
                _repeatAt = Environment.TickCount + RepeatRateMs;
            }
            else if (_repeatDir != 0 && !HeldNav(_repeatDir)) _repeatDir = 0;

            if (dir != 0) { _confirming = false; Move(dir); }

            if (QolInput.Down((int)KeyCode.Q)) Page(-1);
            if (QolInput.Down((int)KeyCode.E)) Page(+1);

            var it = _items[_sel];

            bool inc = QolInput.Down((int)KeyCode.RightArrow) || QolInput.Down((int)KeyCode.D);
            bool dec = QolInput.Down((int)KeyCode.LeftArrow) || QolInput.Down((int)KeyCode.A);
            bool confirm = QolInput.Down((int)KeyCode.Return) || QolInput.Down((int)KeyCode.KeypadEnter);
            bool reset = QolInput.Down((int)KeyCode.R);

            if (inc && it.Adjust != null) { _confirming = false; Safe(() => it.Adjust(+1), it.Label); }
            else if (dec && it.Adjust != null) { _confirming = false; Safe(() => it.Adjust(-1), it.Label); }
            else if (confirm && it.Confirm != null)
            {
                if (it.NeedConfirm && !_confirming) { _confirming = true; }
                else { _confirming = false; Safe(it.Confirm, it.Label); }
            }

            if (reset && it.Reset != null) { _confirming = false; Safe(it.Reset, it.Label); ShowStatus(Loc.T("menu.reopenitem")); }
        }

        private bool HeldNav(int dir)
        {
            return dir < 0
                ? (QolInput.Held((int)KeyCode.UpArrow) || QolInput.Held((int)KeyCode.W))
                : (QolInput.Held((int)KeyCode.DownArrow) || QolInput.Held((int)KeyCode.S));
        }

        private static void Safe(Action a, string what)
        {
            try { a(); }
            catch (Exception e) { QolLog.Error("menu action '" + what + "': " + e.Message); }
        }

        private void Move(int delta)
        {
            _sel += delta;
            if (_sel < 0) _sel = _items.Count - 1;
            if (_sel >= _items.Count) _sel = 0;
            _page = _sel / PageSize;
        }

        private void Page(int delta)
        {
            int pages = Math.Max(1, (_items.Count + PageSize - 1) / PageSize);
            _page = (_page + delta + pages) % pages;
            _sel = Math.Min(_page * PageSize, _items.Count - 1);
        }

        public void Render(StringBuilder sb)
        {
            if (_status != null && Environment.TickCount > _statusUntil) _status = null;

            if (_learnCb != null)
            {
                sb.Append(Loc.T("menu.title")).Append('\n');
                sb.Append("> ").Append(Loc.T("capture.wait")).Append('\n');
                return;
            }

            if (!_open || _items.Count == 0) return;

            int pages = Math.Max(1, (_items.Count + PageSize - 1) / PageSize);
            sb.Append(Loc.T("menu.title"))
              .Append("   ").Append(Loc.T("menu.item")).Append(' ').Append(_sel + 1).Append('/').Append(_items.Count)
              .Append("   ").Append(Loc.T("menu.page")).Append(' ').Append(_page + 1).Append('/').Append(pages)
              .Append('\n');
            sb.Append(Loc.T("menu.nav")).Append('\n');

            int start = _page * PageSize;
            int end = Math.Min(start + PageSize, _items.Count);
            for (int i = start; i < end; i++)
            {
                var it = _items[i];
                sb.Append(i == _sel ? "> " : "  ").Append(it.Label);

                string val = null;
                try { val = it.ValueText != null ? it.ValueText() : null; }
                catch (Exception e) { val = "?"; QolLog.Debug("valuetext: " + e.Message); }
                if (!string.IsNullOrEmpty(val)) sb.Append("  [").Append(val).Append(']');

                sb.Append(' ').Append(Loc.EffectTag(it.Effect));
                if (it.Risk == RiskTier.Irreversible) sb.Append(' ').Append(Loc.T("menu.irrev"));
                else if (it.Risk == RiskTier.Risky) sb.Append(' ').Append(Loc.T("menu.risk"));

                if (i == _sel && _confirming) sb.Append("  ").Append(Loc.T("menu.confirming"));
                sb.Append('\n');
            }

            if (!string.IsNullOrEmpty(_status)) sb.Append('\n').Append(_status).Append('\n');
        }
    }
}
