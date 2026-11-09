using System.Runtime.InteropServices;
using System.Text;

namespace DesktopPet;

/// <summary>Системні події Windows: простій користувача, батарея, активне вікно.</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>Скільки секунд користувач нічого не натискав і не рухав мишею.</summary>
    public static double UserIdleSeconds()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked(now - info.dwTime) / 1000.0;
        }
        catch { return 0; }
    }

    /// <summary>(відсотки, чи підключена зарядка) або null, якщо батареї немає.</summary>
    public static (int Percent, bool Plugged)? Battery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var st)) return null;
            if (st.BatteryLifePercent == 255 || st.BatteryFlag == 128) return null;
            return (st.BatteryLifePercent, st.ACLineStatus == 1);
        }
        catch { return null; }
    }

    /// <summary>Заголовок активного вікна малими літерами.</summary>
    public static string ForegroundTitle()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            int n = GetWindowTextLength(hwnd);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString().ToLowerInvariant();
        }
        catch { return ""; }
    }
}
