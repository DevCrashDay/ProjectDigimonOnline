using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace DigimonNetplay
{
    /// <summary>
    /// Gamepad and keyboard control for the IMGUI online screen. Every interactive control is drawn through this class,
    /// which numbers them in drawing order; up/down moves a highlight between them, left/right changes a focused choice,
    /// A / Enter presses it and B / Esc goes back. Clicks and presses never change state while IMGUI is mid-frame:
    /// they are queued and run in the next Update, so layout and repaint always see the same controls.
    /// </summary>
    internal sealed class OnlineNav
    {
        /// <summary>The highlight is shown once the player uses a gamepad or the keyboard; the mouse hides it.</summary>
        public bool Visible { get; private set; }
        public Vector2 Scroll;

        private int _focus;
        private readonly List<bool> _enabled = new List<bool>();
        private readonly List<bool> _enabledThisPass = new List<bool>();
        private int _countThisPass;
        private string _page, _focusRequest;
        private bool _unfocusRequest;

        // input gathered in Update, used by the next repaint
        private bool _confirm;
        private int _moveV, _moveH;
        private int _heldV, _heldH;
        private float _repeatV, _repeatH;
        private int _blockUntilFrame;

        private readonly List<Action> _deferred = new List<Action>();
        private Action _back, _backThisPass;
        private readonly Dictionary<string, Action> _submit = new Dictionary<string, Action>();

        private bool _inScroll, _scrollToFocus, _focusInScroll;
        private Rect _focusRect, _viewRect;
        private Texture2D _frame;

        public bool Typing => GUIUtility.keyboardControl != 0;

        /// <summary>Ignore buttons for a moment (the press that opened the screen must not also press something on it).</summary>
        public void Block() => _blockUntilFrame = Time.frameCount + 2;

        /// <summary>Queue work for the next Update.</summary>
        public void Defer(Action a) { if (a != null) _deferred.Add(a); }

        /// <summary>Runs queued clicks. Call from Update.</summary>
        public void RunDeferred()
        {
            if (_deferred.Count == 0) return;
            var list = _deferred.ToArray();
            _deferred.Clear();
            foreach (var a in list)
            {
                try { a(); }
                catch (Exception e) { Plugin.Log.LogError("Online screen: " + e); }
            }
        }

        /// <summary>Reads the gamepad and keyboard. Returns true if Back (B / Esc) was pressed and not used for typing.</summary>
        public bool Poll()
        {
            var kb = Keyboard.current;
            var gp = Gamepad.current;
            var mouse = Mouse.current;
            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 4f || mouse.leftButton.wasPressedThisFrame || mouse.scroll.ReadValue().y != 0)) Visible = false;
            if (Time.frameCount < _blockUntilFrame) return false;

            bool Pressed(ButtonControl b) => b != null && b.wasPressedThisFrame;
            bool back = (kb != null && Pressed(kb.escapeKey)) || (gp != null && Pressed(gp.buttonEast));
            if (Typing)
            {
                // Esc / B / A leave the text box; Enter is handled by the box itself (send chat, or leave the box).
                if (back || (gp != null && Pressed(gp.buttonSouth))) _unfocusRequest = true;
                return false;
            }

            int v = 0, h = 0;
            if (kb != null)
            {
                if (kb.upArrowKey.isPressed || kb.wKey.isPressed) v = -1;
                else if (kb.downArrowKey.isPressed || kb.sKey.isPressed) v = 1;
                if (kb.leftArrowKey.isPressed || kb.aKey.isPressed) h = -1;
                else if (kb.rightArrowKey.isPressed || kb.dKey.isPressed) h = 1;
                if (Pressed(kb.tabKey)) Move(kb.shiftKey.isPressed ? -1 : 1, 0);
            }
            if (gp != null)
            {
                var stick = gp.leftStick.ReadValue();
                if (gp.dpad.up.isPressed || stick.y > 0.6f) v = -1;
                else if (gp.dpad.down.isPressed || stick.y < -0.6f) v = 1;
                if (gp.dpad.left.isPressed || stick.x < -0.6f) h = -1;
                else if (gp.dpad.right.isPressed || stick.x > 0.6f) h = 1;
            }
            float now = Time.unscaledTime;
            if (v != _heldV) { _heldV = v; _repeatV = now + 0.38f; if (v != 0) Move(v, 0); }
            else if (v != 0 && now >= _repeatV) { _repeatV = now + 0.075f; Move(v, 0); }
            if (h != _heldH) { _heldH = h; _repeatH = now + 0.38f; if (h != 0) Move(0, h); }
            else if (h != 0 && now >= _repeatH) { _repeatH = now + 0.1f; Move(0, h); }

            bool confirm = (kb != null && (Pressed(kb.enterKey) || Pressed(kb.numpadEnterKey) || Pressed(kb.spaceKey))) || (gp != null && Pressed(gp.buttonSouth));
            if (confirm)
            {
                if (Visible) _confirm = true;
                Visible = true;
            }
            return back;
        }

        private void Move(int v, int h)
        {
            if (!Visible) { Visible = true; return; } // first press only shows where the highlight is
            if (v != 0) _moveV = v;
            if (h != 0) _moveH = h;
        }

        /// <summary>The page's Back action (B / Esc), taken from the last drawn frame. Null = close the screen.</summary>
        public Action BackAction => _back;

        // ------------------------------------------------------------------ drawing (inside OnGUI)

        /// <summary>Start of the online screen's OnGUI. <paramref name="page"/> names what is shown; a new page starts at the top.</summary>
        public void Begin(string page)
        {
            var e = Event.current;
            if (e.type == EventType.Layout && page != _page)
            {
                _page = page;
                _focus = 0;
                Scroll = Vector2.zero;
                GUIUtility.keyboardControl = 0; // a text box from the old page must not keep the keyboard
            }
            if (e.type == EventType.Layout)
            {
                if (_unfocusRequest) { GUIUtility.keyboardControl = 0; _unfocusRequest = false; }
                if (_focusRequest != null) { GUI.FocusControl(_focusRequest); _focusRequest = null; }
            }
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && Typing)
            {
                string name = GUI.GetNameOfFocusedControl();
                if (name != null && _submit.TryGetValue(name, out var submit)) Defer(submit);
                else _unfocusRequest = true;
                e.Use();
            }
            _countThisPass = 0;
            _enabledThisPass.Clear();
            _backThisPass = null;
            _inScroll = false;
        }

        /// <summary>End of the online screen's OnGUI.</summary>
        public void End()
        {
            if (Event.current.type != EventType.Repaint) return;
            _enabled.Clear();
            _enabled.AddRange(_enabledThisPass);
            _back = _backThisPass;
            if (_focus >= _enabled.Count) _focus = Math.Max(0, _enabled.Count - 1);
            if (_moveV != 0) StepFocus(_moveV);
            _moveV = 0;
            _moveH = 0;
            _confirm = false;
            if (_scrollToFocus && _focusInScroll && _viewRect.height > 10)
            {
                if (_focusRect.yMin < Scroll.y + 4) Scroll.y = Mathf.Max(0, _focusRect.yMin - 30);
                else if (_focusRect.yMax > Scroll.y + _viewRect.height - 4) Scroll.y = _focusRect.yMax - _viewRect.height + 30;
            }
            _scrollToFocus = false;
        }

        private void StepFocus(int dir)
        {
            int n = _enabled.Count;
            if (n == 0) return;
            int f = _focus;
            for (int i = 0; i < n; i++)
            {
                f = (f + dir + n) % n;
                if (_enabled[f]) break;
            }
            _focus = f;
            _scrollToFocus = true;
        }

        public void Back(Action a) => _backThisPass = a;

        public void BeginScroll(params GUILayoutOption[] options)
        {
            Scroll = GUILayout.BeginScrollView(Scroll, options);
            _inScroll = true;
        }

        public void EndScroll()
        {
            GUILayout.EndScrollView();
            _inScroll = false;
            if (Event.current.type == EventType.Repaint) _viewRect = GUILayoutUtility.GetLastRect();
        }

        /// <summary>Registers the control just drawn (its rect is GUILayoutUtility.GetLastRect()).</summary>
        private void Track(bool enabled, Action confirm, Action<int> horizontal = null, Func<int, Action> vertical = null)
        {
            int i = _countThisPass++;
            _enabledThisPass.Add(enabled);
            if (Event.current.type != EventType.Repaint || i != _focus) return;
            var r = GUILayoutUtility.GetLastRect();
            _focusRect = r;
            _focusInScroll = _inScroll;
            if (Visible) DrawFrame(r);
            if (!enabled) return;
            if (_confirm && confirm != null) { Defer(confirm); _confirm = false; }
            if (_moveH != 0 && horizontal != null) { int d = _moveH; Defer(() => horizontal(d)); _moveH = 0; }
            if (_moveV != 0 && vertical != null)
            {
                var a = vertical(_moveV);
                if (a != null) { Defer(a); _moveV = 0; _scrollToFocus = true; }
            }
        }

        private void DrawFrame(Rect r)
        {
            if (_frame == null)
            {
                _frame = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _frame.SetPixel(0, 0, new Color(1f, 0.78f, 0.25f));
                _frame.Apply();
            }
            const float t = 3f;
            r = new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), _frame);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), _frame);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), _frame);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), _frame);
        }

        // ------------------------------------------------------------------ controls

        public void Button(string text, Action onClick, bool enabled = true, params GUILayoutOption[] options) =>
            Button(text, onClick, enabled, GUI.skin.button, options);

        public void Button(string text, Action onClick, bool enabled, GUIStyle style, params GUILayoutOption[] options)
        {
            bool was = GUI.enabled;
            GUI.enabled = was && enabled;
            if (GUILayout.Button(text, style, options)) Defer(onClick);
            GUI.enabled = was;
            Track(was && enabled, onClick);
        }

        /// <summary>A row of options; left/right or a click picks one.</summary>
        public void Choice(int current, string[] options, Action<int> set, bool enabled = true, params GUILayoutOption[] layout)
        {
            bool was = GUI.enabled;
            GUI.enabled = was && enabled;
            int v = GUILayout.Toolbar(current, options, layout);
            if (v != current) Defer(() => set(v));
            GUI.enabled = was;
            int n = options.Length;
            Track(was && enabled, () => set((current + 1) % n), d => set(Mathf.Clamp(current + d, 0, n - 1)));
        }

        /// <summary>"&lt; label &gt;" with left/right on the pad.</summary>
        public void Stepper(string label, Action<int> step, bool enabled = true, float labelWidth = 420)
        {
            bool was = GUI.enabled;
            GUI.enabled = was && enabled;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(50))) Defer(() => step(-1));
            GUILayout.Label(label, GUILayout.Width(labelWidth));
            if (GUILayout.Button(">", GUILayout.Width(50))) Defer(() => step(1));
            GUILayout.EndHorizontal();
            GUI.enabled = was;
            Track(was && enabled, () => step(1), step);
        }

        public void Toggle(bool value, string label, Action<bool> set, bool enabled = true, GUIStyle style = null)
        {
            bool was = GUI.enabled;
            GUI.enabled = was && enabled;
            bool v = style == null ? GUILayout.Toggle(value, label) : GUILayout.Toggle(value, label, style);
            if (v != value) Defer(() => set(v));
            GUI.enabled = was;
            Track(was && enabled, () => set(!value));
        }

        /// <summary>A text box. A / Enter starts typing; Enter calls <paramref name="onSubmit"/> (or stops typing).</summary>
        public string Text(string name, string value, int maxLength, bool password = false, Action onSubmit = null, params GUILayoutOption[] options)
        {
            GUI.SetNextControlName(name);
            value = password ? GUILayout.PasswordField(value ?? "", '*', maxLength, options) : GUILayout.TextField(value ?? "", maxLength, options);
            if (onSubmit != null) _submit[name] = onSubmit; else _submit.Remove(name);
            Track(GUI.enabled, () => _focusRequest = name);
            return value;
        }

        /// <summary>
        /// A custom control occupying <paramref name="rect"/> (already reserved with GUILayoutUtility.GetRect).
        /// <paramref name="vertical"/> may claim up/down presses while focused by returning an action.
        /// </summary>
        public bool Custom(Action confirm, Action<int> horizontal, Func<int, Action> vertical)
        {
            int index = _countThisPass;
            Track(true, confirm, horizontal, vertical);
            return index == _focus && Visible;
        }
    }
}
