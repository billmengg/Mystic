using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mystic
{
    /// <summary>
    /// All Mystic hotkeys go through here. While Mystic has focus, plain keys work (1 = Happy). While another app has
    /// focus (a game, OBS), global hotkeys need the modifier as well (Ctrl+Shift+1 by default), so typing in chat
    /// doesn't trigger them. Global keys are read with Windows' GetAsyncKeyState: no hooks, no admin rights.
    /// </summary>
    public static class Hotkeys
    {
        public enum Modifier { CtrlShift, CtrlAlt, Alt, None }

        public static bool GlobalEnabled = true;
        public static Modifier GlobalModifier = Modifier.CtrlShift;

        public static string ModifierLabel(Modifier m) => m switch
        {
            Modifier.CtrlShift => "Ctrl+Shift",
            Modifier.CtrlAlt => "Ctrl+Alt",
            Modifier.Alt => "Alt",
            _ => "none",
        };

        const int VkShift = 0x10, VkControl = 0x11, VkAlt = 0x12;

        // Per-frame edge detection for keys read globally.
        static readonly Dictionary<int, bool> WasDown = new Dictionary<int, bool>();
        static readonly Dictionary<int, bool> PressedCache = new Dictionary<int, bool>();
        static int _cacheFrame = -1;

        /// <summary>True on the frame the key was pressed (with the global modifier when Mystic is not focused).</summary>
        public static bool Pressed(Key key)
        {
            if (Application.isFocused)
            {
                var kb = Keyboard.current;
                return kb != null && kb[key].wasPressedThisFrame;
            }
            if (!GlobalEnabled || !TryVirtualKey(key, out int vk)) return false;

            if (_cacheFrame != Time.frameCount)
            {
                _cacheFrame = Time.frameCount;
                PressedCache.Clear();
            }
            if (PressedCache.TryGetValue(vk, out bool cached)) return cached;

            bool down = IsDown(vk);
            bool was = WasDown.TryGetValue(vk, out bool w) && w;
            WasDown[vk] = down;
            bool pressed = down && !was && ModifierHeld();
            PressedCache[vk] = pressed;
            return pressed;
        }

        static bool ModifierHeld()
        {
            switch (GlobalModifier)
            {
                case Modifier.CtrlShift: return IsDown(VkControl) && IsDown(VkShift);
                case Modifier.CtrlAlt: return IsDown(VkControl) && IsDown(VkAlt);
                case Modifier.Alt: return IsDown(VkAlt);
                default: return true;
            }
        }

        // Unity Input System key -> Windows virtual-key code, for the keys hotkeys are likely to use.
        static bool TryVirtualKey(Key key, out int vk)
        {
            vk = 0;
            if (key >= Key.A && key <= Key.Z) vk = 0x41 + (key - Key.A);
            else if (key >= Key.Digit1 && key <= Key.Digit9) vk = 0x31 + (key - Key.Digit1);
            else if (key == Key.Digit0) vk = 0x30;
            else if (key >= Key.F1 && key <= Key.F12) vk = 0x70 + (key - Key.F1);
            else if (key >= Key.Numpad0 && key <= Key.Numpad9) vk = 0x60 + (key - Key.Numpad0);
            else if (key == Key.Space) vk = 0x20;
            return vk != 0;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
#else
        static bool IsDown(int vk) => false;
#endif
    }
}
