using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace CrystarQoL.Features
{
    /// <summary>mod 自身的设置项。跟玩法功能走同一套契约，不在菜单里搞特殊化。</summary>
    public sealed class FeatureCore : IFeature
    {
        public string Id { get { return "core"; } }
        public RiskTier Risk { get { return RiskTier.Safe; } }

        private Menu _menu;
        private int _padStage;

        public void Init(FeatureContext ctx)
        {
            _menu = ctx.Menu;
        }

        public void OnUpdate() { }
        public void OnSceneLoaded(int buildIndex, string sceneName) { }
        public void OnReset() { }

        public IEnumerable<MenuItem> BuildMenu()
        {
            yield return Bool(Loc.T("core.master"), QolConfig.Master, true);
            yield return Cycle(Loc.T("core.loglevel"), QolConfig.LogLevel, 0, 2,
                () => Loc.Level(QolConfig.LogLevel.Value));
            yield return Cycle(Loc.T("core.language"), QolConfig.Language, 0, 1,
                () => QolConfig.Language.Value == 0 ? Loc.T("lang.zh") : Loc.T("lang.en"));
            // v0.3.1-r13：「屏幕状态条」与其热键（F3）已撤除，见 QolConfig 注释。
            yield return Learn(Loc.T("core.keymenu"), () => QolConfig.KeyMenu);
            yield return Learn(Loc.T("core.keykill"), () => QolConfig.KeyKillSwitch);

            yield return new MenuItem
            {
                Label = Loc.T("core.bindpad"),
                ValueText = () => QolInput.Name(QolConfig.KeyPad1.Value) + " + " + QolInput.Name(QolConfig.KeyPad2.Value),
                Confirm = StartPadLearn
            };

            yield return new MenuItem
            {
                Label = Loc.T("core.diag"),
                ValueText = () => "",
                Confirm = () =>
                {
                    string p = Diagnostics.Export();
                    if (_menu != null) _menu.ShowStatus(p != null ? Loc.T("diag.done") + ": " + Path.GetFileName(p) : Loc.T("diag.fail"));
                }
            };

            yield return new MenuItem
            {
                Label = Loc.T("core.backup"),
                ValueText = () => "",
                Confirm = () =>
                {
                    string p = SaveBackup.Backup("manual");
                    if (_menu != null) _menu.ShowStatus(p != null ? Loc.T("backup.done") : Loc.T("backup.fail"));
                }
            };

            yield return new MenuItem
            {
                Label = Loc.T("core.resetall"),
                ValueText = () => "",
                Risk = RiskTier.Risky,
                NeedConfirm = true,
                Confirm = () =>
                {
                    QolConfig.ResetAll();
                    if (_menu != null) _menu.ShowStatus(Loc.T("reset.done"));
                }
            };

            yield return new MenuItem
            {
                Label = Loc.T("core.killswitch"),
                ValueText = () => KillSwitch.Tripped ? Loc.T("kill.tripped") : Loc.OnOff(true),
                Risk = RiskTier.Risky,
                Confirm = () =>
                {
                    KillSwitch.Toggle();
                    if (_menu != null) _menu.ShowStatus(KillSwitch.Tripped ? Loc.T("kill.tripped") : Loc.T("kill.restored"));
                }
            };
        }

        // ---- 菜单项构造 ----

        private static MenuItem Bool(string label, MelonPreferences_Entry<bool> e, bool def)
        {
            return new MenuItem
            {
                Label = label,
                ValueText = () => Loc.OnOff(e.Value),
                Adjust = d => { e.Value = !e.Value; },
                Confirm = () => e.Value = !e.Value,
                Reset = () => e.Value = def
            };
        }

        private static MenuItem Cycle(string label, MelonPreferences_Entry<int> e, int min, int max, Func<string> text)
        {
            return new MenuItem
            {
                Label = label,
                ValueText = text,
                Adjust = d =>
                {
                    int v = e.Value + (d > 0 ? 1 : -1);
                    if (v > max) v = min;
                    if (v < min) v = max;
                    e.Value = v;
                    QolConfig.Save();
                },
                Reset = () => { e.ResetToDefault(); QolConfig.Save(); }
            };
        }

        private MenuItem Learn(string label, Func<MelonPreferences_Entry<int>> get)
        {
            return new MenuItem
            {
                Label = label,
                ValueText = () => QolInput.Name(get().Value),
                Confirm = () =>
                {
                    if (_menu == null) return;
                    _menu.RequestLearn(k =>
                    {
                        get().Value = k;
                        QolConfig.Save();
                        _menu.ShowStatus(Loc.T("capture.done") + ": " + QolInput.Name(k));
                    });
                }
            };
        }

        private void StartPadLearn()
        {
            if (_menu == null) return;
            _padStage = 1;
            _menu.RequestLearn(OnPadCaptured);
        }

        private void OnPadCaptured(int key)
        {
            if (_padStage == 1)
            {
                QolConfig.KeyPad1.Value = key;
                _padStage = 2;
                QolConfig.Save();
                _menu.ShowStatus(Loc.T("capture.wait2"));
                _menu.RequestLearn(OnPadCaptured);
                return;
            }

            QolConfig.KeyPad2.Value = key;
            _padStage = 0;
            QolConfig.Save();
            _menu.ShowStatus(Loc.T("capture.done") + ": " + QolInput.Name(QolConfig.KeyPad1.Value) + " + " + QolInput.Name(key));
        }
    }
}
