using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Mystic
{
    /// <summary>
    /// Windows-only bits Unity doesn't provide at runtime: the standard Open File dialog, always-on-top and window
    /// size. Window changes only apply in the built app; in the editor they would resize or pin the editor itself.
    /// </summary>
    public class NativeWindow : MonoBehaviour
    {
        public bool alwaysOnTop;
        [Tooltip("Window size in the built app; 0 keeps whatever size it starts at.")]
        public int windowWidth, windowHeight;

        bool _appliedTop;

        /// <summary>True when window changes can apply (the built Windows app, not the editor).</summary>
        public static bool Supported => !Application.isEditor && Application.platform == RuntimePlatform.WindowsPlayer;

        void Start()
        {
            if (!Supported) return;
            if (windowWidth > 0 && windowHeight > 0) Screen.SetResolution(windowWidth, windowHeight, FullScreenMode.Windowed);
            ApplyTopmost();
        }

        void Update()
        {
            if (Supported && alwaysOnTop != _appliedTop) ApplyTopmost();
        }

        public void SetSize(int width, int height)
        {
            windowWidth = width;
            windowHeight = height;
            if (Supported) Screen.SetResolution(width, height, FullScreenMode.Windowed);
        }

        void ApplyTopmost()
        {
            _appliedTop = alwaysOnTop;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, alwaysOnTop ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
#endif
        }

        // ------------------------------------------------------------------ open file dialog

        /// <summary>Shows the Windows Open dialog; returns the chosen path or null. Blocks until closed, so call it
        /// from Update, not from OnGUI.</summary>
        public static string OpenFile(string title, string filter)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var ofn = new OpenFileName();
            ofn.structSize = Marshal.SizeOf(ofn);
            ofn.dlgOwner = GetActiveWindow();
            ofn.filter = filter;
            ofn.file = new string('\0', 1024);
            ofn.maxFile = ofn.file.Length;
            ofn.fileTitle = new string('\0', 256);
            ofn.maxFileTitle = ofn.fileTitle.Length;
            ofn.title = title;
            ofn.flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir;
            if (!GetOpenFileName(ofn)) return null;
            var path = ofn.file.TrimEnd('\0');
            int nul = path.IndexOf('\0');
            return nul >= 0 ? path.Substring(0, nul) : path;
#else
            return null;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        const int OfnExplorer = 0x00080000, OfnFileMustExist = 0x00001000, OfnPathMustExist = 0x00000800,
            OfnNoChangeDir = 0x00000008;
        static readonly IntPtr HwndTopmost = new IntPtr(-1), HwndNotTopmost = new IntPtr(-2);
        const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class OpenFileName
        {
            public int structSize;
            public IntPtr dlgOwner;
            public IntPtr instance;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
#endif
    }
}
