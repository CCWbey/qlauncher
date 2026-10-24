using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QLaunch;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int VK_SPACE = 0x20;

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_C = 0x43;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const uint WM_COPY = 0x0301;
    private const uint GCS_COMPSTR = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public System.Drawing.Rectangle rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // INPUT 的联合体按最大的 MOUSEINPUT 分配，即使只发送键盘事件也不能省略。
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 无边框窗口模拟按下标题栏拖动。松手后窗口位置就停在原地，不写盘，下次唤出仍按居中位置摆。
    /// </summary>
    /// <param name="hWnd">要拖动的窗口句柄。</param>
    /// <returns>无返回值。</returns>
    public static void DragWindow(IntPtr hWnd)
    {
        ReleaseCapture();
        SendMessage(hWnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    /// <summary>
    /// 抢占前台焦点。直接 SetForegroundWindow 在别的程序持有前台锁时会静默失败，
    /// 这里用 AttachThreadInput 把自己的输入队列并入前台线程再抢。
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <returns>是否成功成为前台窗口。</returns>
    public static bool ForceForeground(IntPtr hWnd)
    {
        ShowWindow(hWnd, 5);
        IntPtr fg = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = foreThread != 0 && foreThread != thisThread && AttachThreadInput(foreThread, thisThread, true);
        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foreThread, thisThread, false);
            }
        }
        return GetForegroundWindow() == hWnd;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// 剪贴板变更序号。任何程序写入剪贴板都会让它 +1，比比对文本内容可靠：
    /// 重复复制同一段文字时内容没变但序号会变。
    /// </summary>
    /// <returns>当前序号；取不到时返回 0。</returns>
    public static uint ClipboardSequence()
    {
        try
        {
            return GetClipboardSequenceNumber();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[native] 读取剪贴板序号失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 取指定输入队列里真正拿到键盘焦点的控件句柄。
    /// 顶层窗口往往不是接收 WM_COPY 的那个控件，直接发给它会没反应。
    /// </summary>
    /// <param name="threadId">输入队列所属线程。</param>
    /// <returns>焦点控件句柄；拿不到时返回 IntPtr.Zero。</returns>
    public static IntPtr GetFocusWindow(uint threadId)
    {
        GUITHREADINFO info = new() { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    public static bool PostCopy(IntPtr hWnd) => hWnd != IntPtr.Zero && PostMessage(hWnd, WM_COPY, IntPtr.Zero, IntPtr.Zero);

    public static bool SendCtrlC(IntPtr foreground)
    {
        if (GetForegroundWindow() != foreground || ModifiersHeld())
        {
            return false;
        }
        INPUT[] inputs =
        [
            KeyInput(VK_CONTROL, false),
            KeyInput(VK_C, false),
            KeyInput(VK_C, true),
            KeyInput(VK_CONTROL, true)
        ];
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            return true;
        }
        int error = Marshal.GetLastWin32Error();
        if (sent > 0)
        {
            INPUT[] release = [KeyInput(VK_C, true), KeyInput(VK_CONTROL, true)];
            SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
        }
        Debug.WriteLine($"[selection] SendInput failed: sent={sent}, error={error}");
        return false;
    }

    private static readonly ushort[] Modifiers = [VK_MENU, VK_CONTROL, VK_SHIFT, VK_LWIN, VK_RWIN, VK_SPACE];

    public static bool ModifiersHeld() => Modifiers.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        data = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = 0x0008u | (up ? KEYEVENTF_KEYUP : 0u)
            }
        }
    };

    /// <summary>
    /// 输入法是否正在组合（拼音还没上屏）。组合期间的回车与方向键属于输入法，
    /// 不能被当成"打开结果"或"移动选中项"来处理。
    /// </summary>
    /// <param name="hWnd">输入框句柄。</param>
    /// <returns>true 表示当前有未上屏的组合串。</returns>
    public static bool IsComposing(IntPtr hWnd)
    {
        IntPtr context = ImmGetContext(hWnd);
        if (context == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            return ImmGetCompositionString(context, GCS_COMPSTR, IntPtr.Zero, 0) > 0;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"[ime] 读取组合状态失败: {e.Message}");
            return false;
        }
        finally
        {
            ImmReleaseContext(hWnd, context);
        }
    }

    /// <summary>
    /// 交给系统 DWM 画圆角。无边框窗口自己抠 Region 只能是硬边，Win11 的圆角是唯一平滑的做法。
    /// </summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <returns>无返回值。</returns>
    public static void RoundCorners(IntPtr hWnd)
    {
        int preference = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hWnd, 33, ref preference, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionString(IntPtr hIMC, uint dwIndex, IntPtr lpBuf, uint dwBufLen);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
}
