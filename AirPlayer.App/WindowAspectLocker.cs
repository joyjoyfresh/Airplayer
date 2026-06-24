using System;
using System.Runtime.InteropServices;
using AirPlayer.Protocol.Utils;

namespace AirPlayer.App
{
    /// <summary>
    /// 窗口宽高比锁定器：通过 Win32 子类化拦截 WM_SIZING，在用户拖动窗口边框时
    /// 实时修正窗口矩形，使客户区（视频显示区）始终保持视频比例——窗口恰好框住视频，无黑边无裁切。
    /// 仅当 aspectProvider 返回 &gt;0 时锁定；返回 0 时不干预（自由缩放，如全屏或非投屏态）。
    /// 所有 P/Invoke 与异常兜底均封装于此，对窗口/旋转逻辑零侵入。
    /// </summary>
    internal sealed class WindowAspectLocker : IDisposable
    {
        private const uint WM_SIZING = 0x0214;       // 用户拖动窗口边框时实时发送，lParam 指向可改写的窗口 RECT
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        // WM_SIZING 的 wParam：用户正在拖动的边或角
        private const int WMSZ_LEFT = 1;
        private const int WMSZ_TOP = 3;
        private const int WMSZ_TOPLEFT = 4;
        private const int WMSZ_TOPRIGHT = 5;
        private const int WMSZ_BOTTOM = 6;
        private const int WMSZ_BOTTOMLEFT = 7;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        // 根据窗口样式计算所需窗口矩形（含标题栏/边框等非客户区），用于反推非客户区厚度
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AdjustWindowRectEx(ref RECT lpRect, int dwStyle, bool bMenu, int dwExStyle);

        // 子类化回调委托：必须作为字段保持存活，否则 GC 回收后原生回调触发崩溃
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        private readonly IntPtr _hWnd;
        private readonly SUBCLASSPROC _proc;
        private readonly Func<double> _aspectProvider; // 返回应锁的客户区宽:高比例，<=0 表示当前不锁定
        private bool _installed;

        public WindowAspectLocker(IntPtr hWnd, Func<double> aspectProvider)
        {
            _hWnd = hWnd;
            _aspectProvider = aspectProvider;
            _proc = SubclassProc; // 缓存委托，保证其存活
        }

        /// <summary>安装子类化，开始拦截 WM_SIZING。</summary>
        public void Install()
        {
            if (_installed) return;
            SetWindowSubclass(_hWnd, _proc, (IntPtr)1, IntPtr.Zero);
            _installed = true;
            DiagLog.Write("[LOCK] 窗口比例锁定已安装");
        }

        public void Dispose()
        {
            if (!_installed) return;
            RemoveWindowSubclass(_hWnd, _proc, (IntPtr)1);
            _installed = false;
            DiagLog.Write("[LOCK] 窗口比例锁定已卸载");
        }

        private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_SIZING)
            {
                try
                {
                    if (HandleSizing(wParam, lParam))
                        return (IntPtr)1; // 已处理并改写 RECT
                }
                catch (Exception ex)
                {
                    // 任何异常都退化为不锁定（自由缩放），绝不让窗口操作崩溃
                    DiagLog.Write($"[LOCK] 比例修正异常（退化为自由缩放）: {ex.Message}");
                }
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        /// <summary>按视频比例改写窗口 RECT，使客户区保持视频比例。返回 true 表示已改写。</summary>
        private bool HandleSizing(IntPtr wParamPtr, IntPtr lParam)
        {
            double aspect = _aspectProvider();
            if (!(aspect > 0)) return false; // 当前不锁定（全屏/非投屏）

            int wParam = wParamPtr.ToInt32();
            var rc = Marshal.PtrToStructure<RECT>(lParam);

            // 计算非客户区厚度（标题栏+边框），把「客户区比例=视频比例」换算成「窗口矩形比例」
            int style = GetWindowLongW(_hWnd, GWL_STYLE);
            int exStyle = GetWindowLongW(_hWnd, GWL_EXSTYLE);
            var adj = new RECT();
            AdjustWindowRectEx(ref adj, style, false, exStyle);
            int ncvW = adj.right - adj.left; // 水平非客户厚度
            int ncvH = adj.bottom - adj.top; // 垂直非客户厚度（含标题栏）

            int clientW = Math.Max(1, (rc.right - rc.left) - ncvW);
            int clientH = Math.Max(1, (rc.bottom - rc.top) - ncvH);

            // 拖上下边（TOP/BOTTOM）：高度主导，保持高度算宽度；其余（左右边、四个角）：宽度主导，保持宽度算高度
            bool widthDominant = wParam != WMSZ_TOP && wParam != WMSZ_BOTTOM;
            int newClientW, newClientH;
            if (widthDominant)
            {
                newClientW = clientW;
                newClientH = (int)Math.Round(clientW / aspect);
            }
            else
            {
                newClientH = clientH;
                newClientW = (int)Math.Round(clientH * aspect);
            }
            int newWinW = newClientW + ncvW;
            int newWinH = newClientH + ncvH;

            // 按拖动方向固定对角，重设自由边：拖左边/左上左下→固定右边；拖上边/左上右上→固定下边
            int fixedX = (wParam == WMSZ_LEFT || wParam == WMSZ_TOPLEFT || wParam == WMSZ_BOTTOMLEFT)
                ? rc.right - newWinW : rc.left;
            int fixedY = (wParam == WMSZ_TOP || wParam == WMSZ_TOPLEFT || wParam == WMSZ_TOPRIGHT)
                ? rc.bottom - newWinH : rc.top;

            rc.left = fixedX;
            rc.top = fixedY;
            rc.right = fixedX + newWinW;
            rc.bottom = fixedY + newWinH;

            Marshal.StructureToPtr(rc, lParam, false);
            return true;
        }
    }
}
