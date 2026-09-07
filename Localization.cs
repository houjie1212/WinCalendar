using System;
using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;

namespace WinCalendar;

// 界面语言与区域格式独立，避免中文界面覆盖用户的日期格式。
public static class L
{
    public static readonly ResourceManager Resources = new("WinCalendar.Resources.Strings", typeof(L).Assembly);
    public static CultureInfo Ui { get; private set; } = CultureInfo.GetCultureInfo("en");
    public static CultureInfo Format { get; private set; } = CultureInfo.CurrentCulture;
    public static event Action? Changed;
    internal static void PreviewLanguage(string name) { Ui = CultureInfo.GetCultureInfo(Match(name)); Changed?.Invoke(); }
    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();
    public static string Match(string name)
    {
        var c = CultureInfo.GetCultureInfo(name);
        if (c.TwoLetterISOLanguageName == "zh")
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.EndsWith("TW", StringComparison.OrdinalIgnoreCase) || name.EndsWith("HK", StringComparison.OrdinalIgnoreCase) || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase) ? "zh-Hant" : "zh-Hans";
        return c.TwoLetterISOLanguageName == "ja" ? "ja" : "en";
    }
    public static void Reload()
    {
        CultureInfo.CurrentCulture.ClearCachedData();
        Ui = CultureInfo.GetCultureInfo(Match(CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).Name));
        // user override 来自当前用户区域设置，而不是安装时语言。
        Format = CultureInfo.CurrentCulture = new CultureInfo(GetUserCulture(), true);
        TimeZoneInfo.ClearCachedData();
        Changed?.Invoke();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetUserDefaultLocaleName(System.Text.StringBuilder name, int size);
    private static string GetUserCulture()
    {
        var b = new System.Text.StringBuilder(85);
        return GetUserDefaultLocaleName(b, b.Capacity) > 0 ? b.ToString() : "en-US";
    }
    public static string T(string key) => Resources.GetString(key, Ui) ?? key;
    public static string Festival(string original) => original switch
    {
        "元旦" => T("NewYear"), "春节" => T("SpringFestival"), "清明节" => T("Qingming"),
        "劳动节" => T("LabourDay"), "端午节" => T("DragonBoat"), "中秋节" => T("MidAutumn"), "国庆节" => T("NationalDay"),
        _ => original
    };
}
