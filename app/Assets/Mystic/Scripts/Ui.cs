using System.Collections.Generic;
using UnityEngine;

namespace Mystic
{
    /// <summary>Small immediate-mode widget kit: rounded dark panel, tabs, pills, switches and sliders.</summary>
    public static class Ui
    {
        public static readonly Color PanelBg = new Color(0.075f, 0.082f, 0.102f, 0.95f);
        public static readonly Color Card = new Color(0.135f, 0.145f, 0.18f, 1f);
        public static readonly Color CardHover = new Color(0.18f, 0.195f, 0.24f, 1f);
        public static readonly Color CardPressed = new Color(0.11f, 0.12f, 0.15f, 1f);
        public static readonly Color Accent = new Color(0.42f, 0.55f, 1f, 1f);
        public static readonly Color AccentHover = new Color(0.52f, 0.63f, 1f, 1f);
        public static readonly Color Text = new Color(0.93f, 0.94f, 0.97f, 1f);
        public static readonly Color Muted = new Color(0.60f, 0.63f, 0.70f, 1f);
        public static readonly Color Good = new Color(0.35f, 0.85f, 0.55f, 1f);
        public static readonly Color Warn = new Color(1f, 0.78f, 0.30f, 1f);
        public static readonly Color Bad = new Color(1f, 0.42f, 0.42f, 1f);

        static readonly Dictionary<(Color, int), Texture2D> TexCache = new Dictionary<(Color, int), Texture2D>();
        static Font _font;
        static bool _built;

        static GUIStyle _panel, _label, _muted, _title, _value, _section, _button, _buttonAccent, _tab, _tabOn, _chip,
            _chipOn, _row, _rowOn, _track, _fill, _thumb, _pillOn, _pillOff, _knob, _swatch, _dot, _field;

        /// <summary>A white rounded rectangle texture (anti-aliased corners) tinted with the given colour.</summary>
        static Texture2D Rounded(Color c, int radius)
        {
            if (TexCache.TryGetValue((c, radius), out var cached) && cached != null) return cached;
            int s = radius * 2 + 2;
            var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear };
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float cx = Mathf.Clamp(fx, radius, s - radius), cy = Mathf.Clamp(fy, radius, s - radius);
                    float d = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    var col = c;
                    col.a *= Mathf.Clamp01(radius - d + 0.5f);
                    px[y * s + x] = col;
                }
            }
            t.SetPixels32(px);
            t.Apply();
            TexCache[(c, radius)] = t;
            return t;
        }

        static GUIStyle Box(Color bg, int radius, Color? hover = null, Color? pressed = null)
        {
            var st = new GUIStyle
            {
                border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1),
                padding = new RectOffset(10, 10, 6, 6),
                alignment = TextAnchor.MiddleCenter,
                font = _font,
                fontSize = 13,
                clipping = TextClipping.Clip,
            };
            st.normal.background = Rounded(bg, radius);
            st.normal.textColor = Text;
            st.hover.background = Rounded(hover ?? bg, radius);
            st.hover.textColor = Text;
            st.active.background = Rounded(pressed ?? hover ?? bg, radius);
            st.active.textColor = Text;
            st.focused.background = st.normal.background;
            st.focused.textColor = Text;
            return st;
        }

        static void Build()
        {
            if (_built && _label != null) return;
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Helvetica Neue", "Arial" }, 14);

            _panel = Box(PanelBg, 14);
            _panel.padding = new RectOffset(16, 16, 14, 14);

            _label = new GUIStyle { font = _font, fontSize = 13, wordWrap = true, alignment = TextAnchor.MiddleLeft };
            _label.normal.textColor = Text;
            _muted = new GUIStyle(_label) { fontSize = 11 };
            _muted.normal.textColor = Muted;
            _title = new GUIStyle(_label) { fontSize = 18, fontStyle = FontStyle.Bold };
            _value = new GUIStyle(_label) { alignment = TextAnchor.MiddleRight, fontSize = 12 };
            _value.normal.textColor = Muted;
            _section = new GUIStyle(_label) { fontSize = 11, fontStyle = FontStyle.Bold };
            _section.normal.textColor = Muted;

            _button = Box(Card, 8, CardHover, CardPressed);
            _button.fixedHeight = 32;
            _buttonAccent = Box(Accent, 8, AccentHover, Accent);
            _buttonAccent.fixedHeight = 32;
            _buttonAccent.fontStyle = FontStyle.Bold;

            var clear = new Color(0, 0, 0, 0);
            _tab = Box(clear, 8, CardHover, CardPressed);
            _tab.fixedHeight = 30;
            _tab.normal.textColor = Muted;
            _tabOn = Box(Card, 8);
            _tabOn.fixedHeight = 30;
            _tabOn.fontStyle = FontStyle.Bold;

            _chip = Box(Card, 14, CardHover, CardPressed);
            _chip.fixedHeight = 28;
            _chipOn = Box(Accent, 14, AccentHover, Accent);
            _chipOn.fixedHeight = 28;
            _chipOn.fontStyle = FontStyle.Bold;

            _row = Box(Card, 8, CardHover, CardPressed);
            _row.alignment = TextAnchor.MiddleLeft;
            _row.fixedHeight = 34;
            _row.padding = new RectOffset(12, 10, 4, 4);
            _rowOn = Box(Accent, 8, AccentHover, Accent);
            _rowOn.alignment = TextAnchor.MiddleLeft;
            _rowOn.fixedHeight = 34;
            _rowOn.padding = new RectOffset(12, 10, 4, 4);
            _rowOn.fontStyle = FontStyle.Bold;

            // Radii are chosen so (radius + 1) * 2 <= the size each shape is drawn at (9-slice borders must fit).
            _track = Box(new Color(0.22f, 0.24f, 0.30f), 1);   // drawn 4 px tall
            _fill = Box(Accent, 1);
            _thumb = Box(Color.white, 6);                      // 14 px
            _pillOn = Box(Accent, 8);                          // 36 x 18
            _pillOff = Box(new Color(0.26f, 0.28f, 0.34f), 8);
            _knob = Box(Color.white, 6);                       // 14 px
            _swatch = Box(Color.white, 8);
            _dot = Box(Color.white, 4);                        // 10 px
            _field = Box(Card, 6, CardHover, CardPressed);
            _field.alignment = TextAnchor.MiddleLeft;
            _field.fixedHeight = 26;
            _field.padding = new RectOffset(8, 8, 4, 4);

            _built = true;
        }

        // ------------------------------------------------------------------ layout helpers

        // Screen rectangles of the panels drawn in the last couple of frames, so 3D camera controls can ignore the
        // mouse while it is over the UI (e.g. scrolling the panel must not zoom the avatar).
        static readonly List<(Rect rect, int frame)> Blockers = new List<(Rect, int)>();

        /// <summary>Stream mode: every overlay hides so the window can be captured clean.</summary>
        public static bool StreamMode;

        /// <summary>True if the point (screen pixels, origin top-left) is over a panel drawn recently.</summary>
        public static bool IsPointerOverUi(Vector2 screenTopLeft)
        {
            int frame = Time.frameCount;
            foreach (var (rect, f) in Blockers)
                if (f >= frame - 2 && rect.Contains(screenTopLeft)) return true;
            return false;
        }

        /// <summary>Marks a rectangle (in the current GUI matrix) as UI, so camera controls ignore the mouse there.</summary>
        public static void Block(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            int frame = Time.frameCount;
            Blockers.RemoveAll(b => b.frame < frame - 2);
            var m = GUI.matrix;
            var a = m.MultiplyPoint3x4(new Vector3(rect.xMin, rect.yMin, 0f));
            var b2 = m.MultiplyPoint3x4(new Vector3(rect.xMax, rect.yMax, 0f));
            Blockers.Add((Rect.MinMaxRect(a.x, a.y, b2.x, b2.y), frame));
        }

        public enum Icon { Pin, StreamMode }

        /// <summary>A small square icon button (top-right corner controls). Shows its tooltip to the left on hover.</summary>
        public static bool IconButton(Rect r, Icon icon, bool active, string tooltip)
        {
            Build();
            Block(r);
            var e = Event.current;
            bool hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                (active ? _buttonAccent : _button).Draw(r, hover, false, false, false);
                float cx = r.center.x, cy = r.center.y;
                if (icon == Icon.Pin)
                {
                    // Push pin: round head with a short needle under it.
                    _dot.Draw(new Rect(cx - 5f, cy - 8f, 10f, 10f), false, false, false, false);
                    GUI.DrawTexture(new Rect(cx - 4f, cy + 1f, 8f, 2f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx - 1f, cy + 2f, 2f, 7f), Texture2D.whiteTexture);
                }
                else
                {
                    // Stream mode: a panel outline with a slash through it.
                    var box = new Rect(cx - 8f, cy - 6f, 16f, 12f);
                    GUI.DrawTexture(new Rect(box.x, box.y, box.width, 2f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(box.x, box.yMax - 2f, box.width, 2f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(box.x, box.y, 2f, box.height), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(box.xMax - 2f, box.y, 2f, box.height), Texture2D.whiteTexture);
                    var old = GUI.matrix;
                    GUIUtility.RotateAroundPivot(-35f, r.center);
                    GUI.DrawTexture(new Rect(cx - 11f, cy - 1f, 22f, 2f), Texture2D.whiteTexture);
                    GUI.matrix = old;
                }
                if (hover && !string.IsNullOrEmpty(tooltip))
                {
                    var size = _muted.CalcSize(new GUIContent(tooltip));
                    var tip = new Rect(r.x - size.x - 18f, r.y + (r.height - 22f) * 0.5f, size.x + 12f, 22f);
                    _button.Draw(tip, false, false, false, false);
                    GUI.Label(new Rect(tip.x + 6f, tip.y, size.x, 22f), tooltip, _muted);
                }
            }
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        public static void Panel(Rect rect, System.Action content)
        {
            Build();
            Block(rect);
            GUILayout.BeginArea(rect, _panel);
            content();
            GUILayout.EndArea();
        }

        public static void Title(string text, Color dot, string status)
        {
            Build();
            GUILayout.BeginHorizontal();
            GUILayout.Label(text, _title);
            GUILayout.FlexibleSpace();
            var r = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(24));
            if (Event.current.type == EventType.Repaint)
            {
                var old = GUI.color;
                GUI.color = dot;
                _dot.Draw(new Rect(r.x, r.center.y - 5, 10, 10), false, false, false, false);
                GUI.color = old;
            }
            GUILayout.Space(4);
            GUILayout.Label(status, _muted, GUILayout.Width(110));
            GUILayout.EndHorizontal();
        }

        public static void Section(string text)
        {
            Build();
            GUILayout.Space(10);
            GUILayout.Label(text.ToUpperInvariant(), _section);
            GUILayout.Space(2);
        }

        public static void Hint(string text)
        {
            Build();
            GUILayout.Label(text, _muted);
        }

        public static void Label(string text)
        {
            Build();
            GUILayout.Label(text, _label);
        }

        public static void Gap(float h = 6f) => GUILayout.Space(h);

        // ------------------------------------------------------------------ controls

        public static bool Button(string text, bool accent = false)
        {
            Build();
            return GUILayout.Button(text, accent ? _buttonAccent : _button);
        }

        public static bool ListRow(string text, bool selected)
        {
            Build();
            return GUILayout.Button(text, selected ? _rowOn : _row);
        }

        /// <summary>Tab strip; returns the new selected index.</summary>
        public static int Tabs(string[] names, int selected)
        {
            Build();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < names.Length; i++)
                if (GUILayout.Button(names[i], i == selected ? _tabOn : _tab)) selected = i;
            GUILayout.EndHorizontal();
            return selected;
        }

        public static bool Chip(string text, bool on)
        {
            Build();
            return GUILayout.Button(text, on ? _chipOn : _chip);
        }

        public static bool Toggle(string label, bool value)
        {
            Build();
            var r = GUILayoutUtility.GetRect(10, 26, GUILayout.ExpandWidth(true));
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (GUI.enabled && e.button == 0 && r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        if (r.Contains(e.mousePosition)) { value = !value; GUI.changed = true; }
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    GUI.Label(new Rect(r.x, r.y, r.width - 46, r.height), label, _label);
                    var pill = new Rect(r.xMax - 38, r.center.y - 9, 36, 18);
                    (value ? _pillOn : _pillOff).Draw(pill, false, false, false, false);
                    _knob.Draw(new Rect(value ? pill.xMax - 16 : pill.x + 2, pill.y + 2, 14, 14), false, false, false, false);
                    break;
            }
            return value;
        }

        public static float Slider(string label, float value, float min, float max, string format = "0.00")
        {
            Build();
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label);
            GUILayout.FlexibleSpace();
            GUILayout.Label(value.ToString(format), _value, GUILayout.Width(48));
            GUILayout.EndHorizontal();

            var r = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
            const float pad = 8f;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;
            float FromMouse(float mx) => Mathf.Lerp(min, max, Mathf.Clamp01((mx - r.x - pad) / (r.width - 2 * pad)));

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (GUI.enabled && e.button == 0 && r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        value = FromMouse(e.mousePosition.x);
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        value = FromMouse(e.mousePosition.x);
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    float t = Mathf.InverseLerp(min, max, value);
                    float w = r.width - 2 * pad;
                    var track = new Rect(r.x + pad, r.center.y - 2, w, 4);
                    _track.Draw(track, false, false, false, false);
                    if (t > 0.01f) _fill.Draw(new Rect(track.x, track.y, w * t, 4), false, false, false, false);
                    _thumb.Draw(new Rect(track.x + w * t - 7, r.center.y - 7, 14, 14), false, false, false, false);
                    break;
            }
            GUILayout.Space(4);
            return value;
        }

        /// <summary>Label on the left, editable text on the right.</summary>
        public static string TextField(string label, string value)
        {
            Build();
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label, GUILayout.Width(110));
            value = GUILayout.TextField(value ?? "", _field);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            return value;
        }

        /// <summary>A port or other whole number; keeps the old value while the text isn't a valid number.</summary>
        public static int IntField(string label, int value, int min, int max)
        {
            var text = TextField(label, value.ToString());
            return int.TryParse(text, out var n) && n >= min && n <= max ? n : value;
        }

        /// <summary>A read-only bar, e.g. a microphone level.</summary>
        public static void Meter(string label, float value)
        {
            Build();
            GUILayout.Label(label, _label);
            var r = GUILayoutUtility.GetRect(10, 12, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                var track = new Rect(r.x + 8f, r.center.y - 2f, r.width - 16f, 4f);
                _track.Draw(track, false, false, false, false);
                float t = Mathf.Clamp01(value);
                if (t > 0.01f) _fill.Draw(new Rect(track.x, track.y, track.width * t, 4f), false, false, false, false);
            }
            GUILayout.Space(4);
        }

        /// <summary>A row of colour swatches; returns the index clicked, or -1.</summary>
        public static int Swatches(Color[] colors, Color current)
        {
            Build();
            int clicked = -1;
            GUILayout.BeginHorizontal();
            for (int i = 0; i < colors.Length; i++)
            {
                var r = GUILayoutUtility.GetRect(30, 30, GUILayout.Width(30), GUILayout.Height(30));
                bool selected = Approximately(colors[i], current);
                if (Event.current.type == EventType.Repaint)
                {
                    if (selected)
                    {
                        GUI.color = Accent;
                        _swatch.Draw(r, false, false, false, false);
                    }
                    GUI.color = colors[i];
                    _swatch.Draw(new Rect(r.x + 3, r.y + 3, r.width - 6, r.height - 6), false, false, false, false);
                    GUI.color = Color.white;
                }
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) clicked = i;
                GUILayout.Space(4);
            }
            GUILayout.EndHorizontal();
            return clicked;
        }

        static bool Approximately(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.01f;
    }
}
