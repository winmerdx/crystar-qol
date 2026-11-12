using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace CrystarQoL
{
    /// <summary>
    /// 屏幕文字覆盖层（uGUI）。
    ///
    /// v0.4（02:3x）：回退到 v0.1 验证过的渲染方案，叠加这几轮实测出的 IL2CPP 约束：
    ///
    /// 【实测铁律 —— IL2CPP 泛型与 strip 行为】
    ///   1) 泛型 AddComponent&lt;T&gt;() 可用（游戏自己用过该泛型方法 → IL2CPP 泛型共享，
    ///      T=Canvas/Text 任意引用类型都能跑）。v0.1 / 02:18 会话均证实行到字体阶段。
    ///   2) 【非泛型】AddComponent(Type) 运行时 MissingMethod —— 02:32 会话实测被裁。
    ///      所以组件一律走泛型 AddComponent&lt;T&gt;()，绝不用 AddComponent(typeof(...))。
    ///   3) Object.FindObjectOfType / FindObjectsOfType 家族（泛型+非泛型）运行时全裁
    ///      （02:18/02:33 会话 MissingMethod），【不能用于借字体】。
    ///   4) v0.1 验证可用的字体来源：Resources.GetBuiltinResource&lt;Font&gt;("Arial.ttf")。
    ///
    /// 字体获取顺序（都失败则保持 null，Tick 节流 3s 重试）：
    ///   a) Resources.GetBuiltinResource&lt;Font&gt;("Arial.ttf")        —— v0.1 主方案
    ///   b) Resources.GetBuiltinResource&lt;Font&gt;("LegacyRuntime.ttf")  —— Unity2019 别名
    ///   c) 反射 Text.AssignDefaultFont()（private）
    ///   绝不再尝试场景借字体（Find* 全裁）。
    /// </summary>
    public sealed class Overlay
    {
        private const long CreateRetryMs = 2500;
        private const long FontRetryMs = 3000;

        /// <summary>v0.1.3：玩家反馈菜单字太小，字号翻倍（15 → 30）。</summary>
        private const int FontSize = 30;
        /// <summary>背景板不透明度。</summary>
        private const float BgAlpha = 0.75f;
        /// <summary>背景板比文字区域四周各大出多少像素。</summary>
        private const float BgPadding = 18f;

        private GameObject _root;
        private Text _text;
        private bool _failed;
        private long _nextCreateAt;
        private long _nextFontAt;
        private bool _warnedNoFont;
        private string _pending;
        private bool _visible;
        private string _lastText;

        public bool Ready { get { return _text != null && _text.font != null && !_failed; } }

        public void Tick()
        {
            if (_failed) return;
            try
            {
                long now = Environment.TickCount;

                if (_root == null)
                {
                    if (now < _nextCreateAt) return;
                    CreateSafe();
                    if (_root == null) { _nextCreateAt = now + CreateRetryMs; return; }
                }

                if (_text != null && _text.font == null && now >= _nextFontAt)
                {
                    _nextFontAt = now + FontRetryMs;
                    EnsureFont();
                    if (_text.font != null && _warnedNoFont)
                    {
                        _warnedNoFont = false;
                        QolLog.Info("overlay: font acquired -> " + _text.font.name);
                    }
                    else if (_text.font == null && !_warnedNoFont)
                    {
                        _warnedNoFont = true;
                        QolLog.Warn("overlay: no font yet (Arial/LegacyRuntime/AssignDefaultFont all failed), will retry");
                    }
                }

                if (_text != null && _pending != null && _pending != _lastText)
                {
                    _text.text = _pending;
                    _lastText = _pending;
                }
                _pending = null;

                bool show = _visible && _text != null && _text.font != null;
                if (_root != null && _root.activeSelf != show) _root.SetActive(show);
            }
            catch (Exception e)
            {
                _failed = true;
                QolLog.Error("overlay disabled: " + e.GetType().Name + ": " + e.Message);
                QolLog.Error("  at " + FirstFrame(e));
            }
        }

        public void SetText(string s) { _pending = s; }
        public void SetVisible(bool v) { _visible = v; }

        /// <summary>场景切换后调用：若字体失效则强制重取。</summary>
        public void OnSceneChanged()
        {
            _nextFontAt = 0;
            if (_text != null) _text.font = null;
        }

        // ---- 内部 ----

        private void CreateSafe()
        {
            try
            {
                _root = new GameObject("CrystarQoL.Overlay");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                // 泛型 AddComponent（v0.1 同款，实测可用；非泛型 AddComponent(Type) 会被裁）
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32767;

                // 背景板先建：后建的文字会盖在它上面
                CreateBackground();

                var go = new GameObject("CrystarQoL.OverlayText");
                go.transform.parent = _root.transform; // v0.1 同款挂法

                _text = go.AddComponent<Text>();
                _text.fontSize = FontSize;
                _text.color = new Color(1f, 1f, 1f, 1f);
                _text.supportRichText = false;
                _text.horizontalOverflow = HorizontalWrapMode.Overflow;
                _text.verticalOverflow = VerticalWrapMode.Overflow;

                // v0.1 同款布局（能显示即可，尽量贴近已验证形态）；字号翻倍后放大画布
                try
                {
                    var rt = _text.rectTransform;
                    rt.anchoredPosition = new Vector2(10f, -10f);
                    rt.sizeDelta = new Vector2(760f, 720f);
                }
                catch (Exception e) { QolLog.Warn("overlay rect: " + e.GetType().Name + ": " + e.Message); }

                EnsureFont();
                QolLog.Info("overlay created, font=" + (_text.font == null ? "none (will retry)" : _text.font.name));
            }
            catch (Exception e)
            {
                QolLog.Warn("overlay create failed: " + e.GetType().Name + ": " + e.Message);
                QolLog.Warn("  at " + FirstFrame(e));
                Cleanup();
            }
        }

        /// <summary>
        /// 菜单背景板：75% 不透明的深色圆角矩形，纯为可读性。
        /// 全程 try/catch —— 这一步失败绝不能让文字一起消失（v0.1 的教训：
        /// overlay 一旦初始化失败，菜单就完全隐形）。最坏情况退化为纯色矩形。
        /// </summary>
        private void CreateBackground()
        {
            try
            {
                var bgGo = new GameObject("CrystarQoL.OverlayBg");
                bgGo.transform.parent = _root.transform;

                var img = bgGo.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, BgAlpha);
                img.raycastTarget = false;

                // 圆角贴图；拿不到就退回纯色矩形（sprite=null 时 Image 画实心矩形）
                var sp = MakeRoundSprite(64, 14);
                if (sp != null)
                {
                    img.sprite = sp;
                    try { img.type = Image.Type.Sliced; } catch { }
                }

                var rt = bgGo.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // 与文字同锚点同中心，只是四周大一圈
                    rt.anchoredPosition = new Vector2(10f, -10f);
                    rt.sizeDelta = new Vector2(760f + BgPadding * 2f, 720f + BgPadding * 2f);
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("overlay background skipped: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 程序化生成圆角矩形贴图（不依赖任何美术资源，也不调用运行时可能被裁的加载 API）。
        /// 失败返回 null，由调用方退化为纯色矩形。
        /// </summary>
        private static Sprite MakeRoundSprite(int size, int radius)
        {
            try
            {
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                int r = radius;
                if (r > size / 2) r = size / 2;

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        bool inside = true;
                        // 四个角：落在圆角外则透明
                        int cx = x < r ? r : (x > size - 1 - r ? size - 1 - r : x);
                        int cy = y < r ? r : (y > size - 1 - r ? size - 1 - r : y);
                        if (cx != x || cy != y)
                        {
                            int dx = x - cx, dy = y - cy;
                            inside = (dx * dx + dy * dy) <= r * r;
                        }
                        tex.SetPixel(x, y, inside ? new Color(1f, 1f, 1f, 1f) : new Color(0f, 0f, 0f, 0f));
                    }
                }
                tex.Apply(false, true);

                // 优先九宫格（角不拉伸）；该重载若被裁则退回普通 sprite
                try
                {
                    return Sprite.Create(tex, new Rect(0f, 0f, size, size),
                        new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.Tight,
                        new Vector4(r, r, r, r));
                }
                catch
                {
                    return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
                }
            }
            catch (Exception e)
            {
                QolLog.Warn("overlay round sprite failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>字体获取：内置 Arial → LegacyRuntime → 反射 AssignDefaultFont。</summary>
        private void EnsureFont()
        {
            if (_text == null || _text.font != null) return;

            // a) v0.1 主方案
            try
            {
                var f = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (f != null) { _text.font = f; QolLog.Info("overlay: font Arial.ttf"); return; }
                QolLog.Warn("overlay: Arial.ttf returned null");
            }
            catch (Exception e) { QolLog.Warn("overlay: Arial.ttf failed: " + e.GetType().Name + ": " + e.Message); }

            // b) Unity2019 内置字体别名
            try
            {
                var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (f != null) { _text.font = f; QolLog.Info("overlay: font LegacyRuntime.ttf"); return; }
                QolLog.Warn("overlay: LegacyRuntime.ttf returned null");
            }
            catch (Exception e) { QolLog.Warn("overlay: LegacyRuntime.ttf failed: " + e.GetType().Name + ": " + e.Message); }

            // c) 反射 AssignDefaultFont
            try
            {
                var m = typeof(Text).GetMethod("AssignDefaultFont",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    m.Invoke(_text, null);
                    if (_text.font != null) { QolLog.Info("overlay: font via AssignDefaultFont"); return; }
                    QolLog.Warn("overlay: AssignDefaultFont invoked but font still null");
                }
                else
                {
                    QolLog.Warn("overlay: AssignDefaultFont method not found");
                }
            }
            catch (Exception e) { QolLog.Warn("overlay: AssignDefaultFont failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private void Cleanup()
        {
            if (_root != null)
            {
                try { UnityEngine.Object.Destroy(_root); } catch { }
                _root = null;
            }
            _text = null;
            _nextCreateAt = Environment.TickCount + CreateRetryMs;
        }

        private static string FirstFrame(Exception e)
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(e, true);
                if (st != null && st.FrameCount > 0)
                {
                    var f = st.GetFrame(0);
                    return f.GetFileName() + ":" + f.GetFileLineNumber() + " " + f.GetMethod();
                }
            }
            catch { }
            return "(no stack)";
        }
    }
}
