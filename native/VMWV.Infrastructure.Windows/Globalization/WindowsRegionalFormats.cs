using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VMWV.Infrastructure.Windows.Globalization;

public static class WindowsRegionalFormats
{
    private const uint DateShortDate = 0x00000001;
    private const uint TimeNoSeconds = 0x00000002;
    private const uint LocaleShortTime = 0x00000079;
    private static CultureInfo _culture = ReadUserCulture();

    public static CultureInfo Culture => Volatile.Read(ref _culture);

    public static void Refresh()
    {
        CultureInfo.CurrentCulture.ClearCachedData();
        Volatile.Write(ref _culture, ReadUserCulture());
    }

    public static string FormatDateTime(DateTimeOffset value, bool includeSeconds = false)
    {
        var local = value.LocalDateTime;
        return $"{FormatDate(local)} {FormatTime(local, includeSeconds)}";
    }

    public static string FormatTime(DateTimeOffset value, bool includeSeconds = true) =>
        FormatTime(value.LocalDateTime, includeSeconds);

    private static string FormatDate(DateTime local)
    {
        if (OperatingSystem.IsWindows())
        {
            var native = new NativeSystemTime(local);
            // NULL selects the Windows user's current preferences, not the app's language.
            var length = GetDateFormatEx(null, DateShortDate, in native, null, null, 0, null);
            if (length > 0)
            {
                var buffer = new StringBuilder(length);
                if (GetDateFormatEx(null, DateShortDate, in native, null, buffer, buffer.Capacity, null) > 0)
                    return buffer.ToString();
            }
        }
        return local.ToString("d", Culture);
    }

    private static string FormatTime(DateTime local, bool includeSeconds)
    {
        if (OperatingSystem.IsWindows())
        {
            var native = new NativeSystemTime(local);
            // Short and long clocks can be customized independently in Windows.
            var pattern = includeSeconds ? null : ReadShortTimePattern();
            var flags = !includeSeconds && pattern is null ? TimeNoSeconds : 0;
            var length = GetTimeFormatEx(null, flags, in native, pattern, null, 0);
            if (length > 0)
            {
                var buffer = new StringBuilder(length);
                if (GetTimeFormatEx(null, flags, in native, pattern, buffer, buffer.Capacity) > 0)
                    return buffer.ToString();
            }
        }
        return local.ToString(includeSeconds ? "T" : "t", Culture);
    }

    private static string? ReadShortTimePattern()
    {
        var length = GetLocaleInfoEx(null, LocaleShortTime, null, 0);
        if (length <= 1) return null;
        var buffer = new StringBuilder(length);
        return GetLocaleInfoEx(null, LocaleShortTime, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    private static CultureInfo ReadUserCulture()
    {
        // The app's translation language must not replace Windows regional overrides.
        var localeName = new StringBuilder(85);
        if (OperatingSystem.IsWindows() && GetUserDefaultLocaleName(localeName, localeName.Capacity) > 0)
        {
            try
            {
                return CultureInfo.ReadOnly(new CultureInfo(localeName.ToString(), useUserOverride: true));
            }
            catch (CultureNotFoundException) { }
        }

        return CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetUserDefaultLocaleName(StringBuilder localeName, int count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetDateFormatEx(string? localeName, uint flags, in NativeSystemTime date,
        string? format, StringBuilder? buffer, int count, string? calendar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetTimeFormatEx(string? localeName, uint flags, in NativeSystemTime time,
        string? format, StringBuilder? buffer, int count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetLocaleInfoEx(string? localeName, uint type, StringBuilder? buffer, int count);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSystemTime(DateTime value)
    {
        public readonly ushort Year = (ushort)value.Year;
        public readonly ushort Month = (ushort)value.Month;
        public readonly ushort DayOfWeek = (ushort)value.DayOfWeek;
        public readonly ushort Day = (ushort)value.Day;
        public readonly ushort Hour = (ushort)value.Hour;
        public readonly ushort Minute = (ushort)value.Minute;
        public readonly ushort Second = (ushort)value.Second;
        public readonly ushort Milliseconds = (ushort)value.Millisecond;
    }
}
