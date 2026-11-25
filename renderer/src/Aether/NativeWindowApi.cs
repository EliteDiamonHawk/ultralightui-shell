using System.Drawing;
using System.Runtime.InteropServices;

namespace Aether;

internal static class NativeWindowApi
{
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_NOSENDCHANGING = 0x0400;
    internal const uint SWP_NOOWNERZORDER = 0x0200;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    private const uint ABM_NEW = 0x00000000;
    private const uint ABM_REMOVE = 0x00000001;
    private const uint ABM_QUERYPOS = 0x00000002;
    private const uint ABM_SETPOS = 0x00000003;
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;
    private static readonly object AppBarSync = new();
    private static readonly HashSet<IntPtr> AppBars = new();

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    internal static void SetAppBarReservation(IntPtr hwnd, WorkAreaReservation reservation)
    {
        if (!reservation.Enabled || reservation.Thickness <= 0)
            return;
        lock (AppBarSync)
        {
            var data = new APPBARDATA { CbSize = (uint)Marshal.SizeOf<APPBARDATA>(), HWnd = hwnd };
            if (AppBars.Add(hwnd))
                SHAppBarMessage(ABM_NEW, ref data);
            data.Edge = reservation.Edge switch
            {
                "left" => ABE_LEFT, "top" => ABE_TOP, "right" => ABE_RIGHT, _ => ABE_BOTTOM
            };
            if (!NativeMethods.TryGetWindowRect(hwnd, out Rectangle bounds))
                return;
            data.Rect = new NativeMethods.RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
            if (data.Edge == ABE_BOTTOM) data.Rect.Top = data.Rect.Bottom - reservation.Thickness;
            if (data.Edge == ABE_TOP) data.Rect.Bottom = data.Rect.Top + reservation.Thickness;
            if (data.Edge == ABE_LEFT) data.Rect.Right = data.Rect.Left + reservation.Thickness;
            if (data.Edge == ABE_RIGHT) data.Rect.Left = data.Rect.Right - reservation.Thickness;
            SHAppBarMessage(ABM_QUERYPOS, ref data);
            SHAppBarMessage(ABM_SETPOS, ref data);
        }
    }

    internal static void RemoveAppBarReservation(IntPtr hwnd)
    {
        lock (AppBarSync)
        {
            if (!AppBars.Remove(hwnd))
                return;
            var data = new APPBARDATA { CbSize = (uint)Marshal.SizeOf<APPBARDATA>(), HWnd = hwnd };
            SHAppBarMessage(ABM_REMOVE, ref data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint CallbackMessage;
        public uint Edge;
        public NativeMethods.RECT Rect;
        public IntPtr Param;
    }

    internal static bool IsDescendantOrSelf(IntPtr hwnd, IntPtr ancestor)
    {
        if (hwnd == IntPtr.Zero || ancestor == IntPtr.Zero)
            return false;
        for (IntPtr current = hwnd; current != IntPtr.Zero; current = GetParent(current))
            if (current == ancestor)
                return true;
        return false;
    }
}
