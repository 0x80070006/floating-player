using System;
using System.Runtime.InteropServices;

namespace FloatingPlayer
{
    static class Native
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_ROUND = 2;
        const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        const int WCA_ACCENT_POLICY = 19;
        const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
        const int ACCENT_ENABLE_BLURBEHIND = 3;
        const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

        public static void PressPlayPauseKey()
        {
            const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 1 /* extended */, IntPtr.Zero);
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 1 | 2 /* key up */, IntPtr.Zero);
        }

        static int WindowsBuild { get { return Environment.OSVersion.Version.Build; } }
        public static bool IsWindows11 { get { return WindowsBuild >= 22000; } }

        // The bar must never take the focus away from what the user is doing.
        public static void MakeNonActivating(IntPtr hwnd)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        // Rounded corners without the default border (Windows 11 only; Windows 10 stays square).
        public static void ApplyFrame(IntPtr hwnd, bool dark)
        {
            if (!IsWindows11) return;
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            int border = unchecked((int)DWMWA_COLOR_NONE);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, 4);
            int darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, 4);
        }

        // What shows through the translucent surface: blurred desktop, or the desktop as it is.
        public static void ApplyBackdrop(IntPtr hwnd, bool blur)
        {
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var accent = new AccentPolicy
            {
                AccentState = !blur ? ACCENT_ENABLE_TRANSPARENTGRADIENT
                            : WindowsBuild >= 17134 ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND,
                AccentFlags = 2,
                GradientColor = blur ? 0x18000000u : 0u
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }
}
