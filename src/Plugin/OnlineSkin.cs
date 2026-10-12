using UnityEngine;
using UnityEngine.UI;

namespace DigimonNetplay
{
    /// <summary>Look of the online screen: the game's menu font on dark blue panels. Only valid inside OnGUI.</summary>
    internal static class OnlineSkin
    {
        public const float CanvasHeight = 720f;

        public static GUIStyle Panel, Title, Hint, Hud, Notice, Heading, Small, Warn, Link, Row, Selected, Tiny;
        public static Texture2D Dim;

        private static GUISkin _skin;
        private static Font _font;

        private static float _nextFontTry;

        public static GUISkin Get()
        {
            // The menu font only exists once a menu scene is up; look for it now and then until found.
            if (_font == null && Time.unscaledTime >= _nextFontTry)
            {
                _nextFontTry = Time.unscaledTime + 1f;
                if (TryFindGameFont()) _skin = null;
            }
            if (_skin == null) Build();
            return _skin;
        }

        private static bool TryFindGameFont()
        {
            foreach (var t in Object.FindObjectsOfType<Text>())
                if (t.font != null) { _font = t.font; return true; }
            return false;
        }

        private static void Build()
        {
            var white = Color.white;
            var accent = new Color(1f, 0.78f, 0.25f);

            _skin = Object.Instantiate(GUI.skin);
            _skin.hideFlags = HideFlags.HideAndDontSave;
            if (_font != null) _skin.font = _font;

            Style(_skin.label, 20, white, null);
            _skin.label.wordWrap = true;
            Style(_skin.button, 21, white, Tex(new Color(0.13f, 0.27f, 0.6f)));
            _skin.button.hover.background = Tex(new Color(0.22f, 0.42f, 0.85f));
            _skin.button.hover.textColor = white;
            _skin.button.active.background = Tex(new Color(0.95f, 0.6f, 0.1f));
            _skin.button.active.textColor = white;
            _skin.button.padding = new RectOffset(12, 12, 6, 6);
            _skin.button.margin = new RectOffset(4, 4, 4, 4);
            Style(_skin.textField, 20, white, Tex(new Color(0.02f, 0.03f, 0.08f)));
            _skin.textField.focused.background = Tex(new Color(0.05f, 0.08f, 0.2f));
            _skin.textField.focused.textColor = accent;
            _skin.textField.padding = new RectOffset(8, 8, 5, 5);
            _skin.box.normal.background = Tex(new Color(0f, 0f, 0f, 0.75f));

            Panel = new GUIStyle { normal = { background = Tex(new Color(0.03f, 0.06f, 0.16f, 0.94f)) }, border = new RectOffset(2, 2, 2, 2) };
            Title = new GUIStyle(_skin.label) { fontSize = 34, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            Title.normal.textColor = accent;
            Title.margin = new RectOffset(0, 0, 4, 14);
            Hint = new GUIStyle(_skin.label) { fontSize = 16, alignment = TextAnchor.MiddleLeft };
            Hint.normal.textColor = new Color(0.7f, 0.75f, 0.9f);
            Hud = new GUIStyle(_skin.label) { fontSize = 18, wordWrap = false };
            Notice = new GUIStyle(_skin.box) { fontSize = 19, alignment = TextAnchor.MiddleCenter, font = _font };
            Notice.normal.textColor = white;
            Heading = new GUIStyle(_skin.label) { fontSize = 24, wordWrap = false };
            Heading.normal.textColor = accent;
            Heading.margin = new RectOffset(4, 4, 8, 4);
            Small = new GUIStyle(_skin.label) { fontSize = 17 };
            Small.normal.textColor = new Color(0.8f, 0.85f, 0.95f);
            Warn = new GUIStyle(_skin.label);
            Warn.normal.textColor = new Color(1f, 0.55f, 0.45f);
            Link = new GUIStyle(_skin.label) { fontSize = 18 };
            Link.normal.textColor = accent;
            Link.hover.textColor = Color.white;
            Row = new GUIStyle { normal = { background = Tex(new Color(1f, 1f, 1f, 0.06f)) }, margin = new RectOffset(0, 0, 2, 2), padding = new RectOffset(6, 6, 2, 2) };
            Selected = new GUIStyle(Row) { normal = { background = Tex(accent) } };
            Tiny = new GUIStyle(_skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            Tiny.normal.textColor = new Color(0.8f, 0.85f, 0.95f);
            Dim = Tex(new Color(0f, 0f, 0f, 0.6f));
            // Player names and chat are shown as typed, never as markup.
            foreach (var st in new[] { _skin.label, _skin.button, _skin.textField, _skin.box, Title, Hint, Hud, Notice, Heading, Small, Warn, Link, Tiny })
                st.richText = false;
        }

        private static void Style(GUIStyle s, int size, Color text, Texture2D bg)
        {
            s.fontSize = size;
            s.normal.textColor = text;
            if (bg != null) s.normal.background = bg;
        }

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
