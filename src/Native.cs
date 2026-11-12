using System;
using System.Runtime.InteropServices;
using System.Text;

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

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public int Flags; }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
        [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

        public static void PressKey(byte key)
        {
            keybd_event(key, 0, 1, IntPtr.Zero);
            keybd_event(key, 0, 1 | 2, IntPtr.Zero);
        }

        // Executable name without ".exe", or "" when the process is gone or protected.
        public static string ProcessName(uint pid)
        {
            IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (handle == IntPtr.Zero) return "";
            try
            {
                var text = new StringBuilder(1024);
                int size = text.Capacity;
                if (!QueryFullProcessImageName(handle, 0, text, ref size)) return "";
                return System.IO.Path.GetFileNameWithoutExtension(text.ToString(0, size));
            }
            finally { CloseHandle(handle); }
        }

        // Lock and unlock notifications (WM_WTSSESSION_CHANGE) for this window.
        public static void WatchSession(IntPtr hwnd, bool watch)
        {
            try
            {
                if (watch) WTSRegisterSessionNotification(hwnd, 0);
                else WTSUnRegisterSessionNotification(hwnd);
            }
            catch { }
        }

        static RECT MonitorArea(IntPtr monitor, bool work)
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(monitor, ref info);
            return work ? info.Work : info.Monitor;
        }

        // Work area (in pixels) of the monitor that holds most of the rectangle.
        public static RECT WorkAreaOf(RECT rect)
        {
            return MonitorArea(MonitorFromRect(ref rect, 2 /* nearest */), true);
        }

        // True when the foreground window covers the whole monitor the bar is on (video, game, slides).
        public static bool IsFullscreenAppOver(IntPtr bar)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == bar) return false;
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string name = cls.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return false;
            // A maximized window with its title bar is not full screen, even on a screen without taskbar.
            const int WS_CAPTION = 0x00C00000;
            if (IsZoomed(fg) && (GetWindowLong(fg, -16 /* GWL_STYLE */) & WS_CAPTION) == WS_CAPTION) return false;
            IntPtr monitor = MonitorFromWindow(fg, 0 /* null */);
            if (monitor == IntPtr.Zero || monitor != MonitorFromWindow(bar, 2)) return false;
            RECT window, screen = MonitorArea(monitor, false);
            if (!GetWindowRect(fg, out window)) return false;
            return window.Left <= screen.Left && window.Top <= screen.Top && window.Right >= screen.Right && window.Bottom >= screen.Bottom;
        }

        // Brings the app behind a media session to the front. Browsers get the window whose title
        // carries the track (the tab that plays) when one does.
        public static bool ActivateApp(string[] processNames, string titleHint)
        {
            var names = new System.Collections.Generic.HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
            IntPtr best = IntPtr.Zero, fallback = IntPtr.Zero;
            var pidNames = new System.Collections.Generic.Dictionary<uint, string>();
            EnumWindows((hwnd, param) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                int length = GetWindowTextLength(hwnd);
                if (length == 0) return true;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                string name;
                if (!pidNames.TryGetValue(pid, out name)) pidNames[pid] = name = ProcessName(pid);
                if (!names.Contains(name)) return true;
                var text = new StringBuilder(length + 1);
                GetWindowText(hwnd, text, text.Capacity);
                if (!string.IsNullOrEmpty(titleHint) && text.ToString().IndexOf(titleHint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = hwnd;
                    return false;
                }
                if (fallback == IntPtr.Zero) fallback = hwnd;
                return true;
            }, IntPtr.Zero);

            IntPtr target = best != IntPtr.Zero ? best : fallback;
            if (target == IntPtr.Zero) return false;
            if (IsIconic(target)) ShowWindow(target, 9 /* SW_RESTORE */);
            if (!SetForegroundWindow(target))
            {
                // Windows only lets the app that got the last input change the foreground;
                // a harmless Alt tap gives that right back.
                keybd_event(0x12, 0, 0, IntPtr.Zero);
                keybd_event(0x12, 0, 2, IntPtr.Zero);
                SetForegroundWindow(target);
            }
            return true;
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
