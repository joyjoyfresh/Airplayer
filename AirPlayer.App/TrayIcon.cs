using System;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Utils;

namespace AirPlayer.App
{
    /// <summary>
    /// 系统托盘图标（基于 Win32 Shell_NotifyIcon），自包含、无第三方依赖。
    /// 通过窗口子类化接收托盘鼠标回调：左键还原窗口，右键弹出菜单（显示窗口 / 退出）。
    /// 事件均在窗口消息线程（UI 线程）触发，回调里可直接操作 UI。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        // ── 常量 ──────────────────────────────────────────────────────────
        private const int WM_APP            = 0x8000;
        private const int WM_TRAYICON       = WM_APP + 1;   // 自定义托盘回调消息
        private const int WM_NULL           = 0x0000;
        private const int WM_LBUTTONUP      = 0x0202;
        private const int WM_LBUTTONDBLCLK  = 0x0203;
        private const int WM_RBUTTONUP      = 0x0205;

        private const int NIM_ADD    = 0x0;
        private const int NIM_MODIFY = 0x1;
        private const int NIM_DELETE = 0x2;
        private const int NIF_MESSAGE = 0x1;
        private const int NIF_ICON    = 0x2;
        private const int NIF_TIP     = 0x4;

        private const uint MF_STRING        = 0x0;
        private const uint TPM_RIGHTBUTTON  = 0x2;
        private const uint TPM_RETURNCMD    = 0x100;

        private const uint IMAGE_ICON       = 1;
        private const uint LR_LOADFROMFILE  = 0x10;
        private const uint LR_DEFAULTSIZE   = 0x40;

        private const uint ID_SHOW = 1;
        private const uint ID_EXIT = 2;

        // ── 回调事件 ──────────────────────────────────────────────────────
        /// <summary>左键单击/双击托盘图标</summary>
        public event Action? LeftClick;
        /// <summary>右键菜单「显示窗口」</summary>
        public event Action? ShowRequested;
        /// <summary>右键菜单「退出」</summary>
        public event Action? ExitRequested;

        private readonly IntPtr _hwnd;
        private readonly SUBCLASSPROC _subclassProc; // 必须保存引用防止被 GC
        private readonly UIntPtr _subclassId = (UIntPtr)1001;
        private IntPtr _hIcon;
        private bool _added;
        private bool _disposed;

        /// <param name="hwnd">宿主窗口句柄</param>
        /// <param name="iconPath">.ico 文件路径</param>
        /// <param name="tooltip">悬停提示文本</param>
        public TrayIcon(IntPtr hwnd, string iconPath, string tooltip)
        {
            _hwnd = hwnd;
            _subclassProc = SubclassProc; // 持有委托引用

            // 子类化窗口以接收托盘回调消息
            SetWindowSubclass(_hwnd, _subclassProc, _subclassId, UIntPtr.Zero);

            // 加载图标（失败则用 IntPtr.Zero，托盘仍可工作只是无图标）
            try
            {
                _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }
            catch { _hIcon = IntPtr.Zero; }

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = tooltip ?? "AirPlayer"
            };
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_added)
                DiagLog.Write("[TRAY] 添加托盘图标失败");
        }

        // ── 子类化窗口过程：拦截托盘回调消息 ──────────────────────────────
        private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_TRAYICON)
            {
                int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF); // 低字为鼠标消息
                if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_LBUTTONDBLCLK)
                {
                    try { LeftClick?.Invoke(); } catch (Exception ex) { DiagLog.Write($"[TRAY] 左键回调异常: {ex.Message}"); }
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowContextMenu();
                }
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        /// <summary>在光标处弹出原生右键菜单（同步返回所选项）。</summary>
        private void ShowContextMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                AppendMenu(menu, MF_STRING, ID_SHOW, "显示窗口");
                AppendMenu(menu, MF_STRING, ID_EXIT, "退出");

                // Win32 规范：弹菜单前需把窗口设为前台，否则菜单不会自动消失
                SetForegroundWindow(_hwnd);
                GetCursorPos(out POINT pt);
                uint cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero); // Win32 惯例，确保菜单正常收起

                if (cmd == ID_SHOW)
                    try { ShowRequested?.Invoke(); } catch (Exception ex) { DiagLog.Write($"[TRAY] 显示回调异常: {ex.Message}"); }
                else if (cmd == ID_EXIT)
                    try { ExitRequested?.Invoke(); } catch (Exception ex) { DiagLog.Write($"[TRAY] 退出回调异常: {ex.Message}"); }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_added)
                {
                    var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 };
                    Shell_NotifyIcon(NIM_DELETE, ref data);
                    _added = false;
                }
                RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
                if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
            }
            catch (Exception ex) { DiagLog.Write($"[TRAY] 释放异常: {ex.Message}"); }
        }

        // ── Win32 互操作 ──────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);
        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);
        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }
}
