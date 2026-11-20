using System.ComponentModel;
using System.Globalization;

namespace SRM.Core.Localization;

/// <summary>
/// 界面文案的唯一出处：所有要显示给用户的话都按「键」去两张表里查（中文 / 英文）。
/// 界面用 <see cref="Instance"/> 绑定（切语言会自动重画），命令行和后台线程用 <see cref="T"/>/<see cref="F"/>。
/// </summary>
public static class Loc
{
    /// <summary>简体中文。这个值会写进设置文件，改名会让老用户的设置失效。</summary>
    public const string ChineseSimplified = "zh-Hans";

    /// <summary>英文。</summary>
    public const string English = "en";

    private static Dictionary<string, string> _map = StringsZhHans.Map;
    private static string _language = ChineseSimplified;

    /// <summary>语言切换之后触发：界面自己去重新铺一遍用代码写的文案。</summary>
    public static event Action? LanguageChanged;

    /// <summary>XAML 绑定的数据源：写 {Binding [键], Source={x:Static ...Loc.Instance}} 即可。</summary>
    public static Localizer Instance { get; } = new();

    /// <summary>当前语言表里的全部键（装进应用级资源字典，供 XAML 里的 {DynamicResource 键} 用）。</summary>
    public static IEnumerable<string> Keys => _map.Keys;

    public static string Language => _language;

    /// <summary>当前语言对应的 .NET 区域信息，供日期、数字等格式化使用。</summary>
    public static CultureInfo Culture => _language == English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN");

    public static bool IsEnglish => _language == English;

    /// <summary>按「设置文件里存的」→「系统界面语言」的顺序决定用哪种语言。</summary>
    public static string Resolve()
    {
        var saved = UserSettings.LoadLanguage();
        if (IsKnown(saved)) return saved;
        var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.Equals(ui, "zh", StringComparison.OrdinalIgnoreCase) ? ChineseSimplified : English;
    }

    public static bool IsKnown(string? language) => language is ChineseSimplified or English;

    /// <summary>启动时调用：按设置或系统语言初始化。不会覆盖用户已保存的选择。</summary>
    public static void Initialize() => SetLanguage(Resolve(), persist: false);

    /// <summary>切换语言；persist=true 时把选择记进设置文件。</summary>
    public static void SetLanguage(string language, bool persist = true)
    {
        if (!IsKnown(language)) language = ChineseSimplified;
        var changed = _language != language;
        _language = language;
        _map = language == English ? StringsEnglish.Map : StringsZhHans.Map;

        var culture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (persist) UserSettings.SaveLanguage(language);
        if (!changed) return;
        Instance.Notify();
        LanguageChanged?.Invoke();
    }

    /// <summary>按键取文案；表里没有就原样返回键名（界面上一眼能看出漏了哪条）。</summary>
    public static string T(string key) =>
        _map.TryGetValue(key, out var text) ? text
        : StringsZhHans.Map.TryGetValue(key, out var fallback) ? fallback
        : key;

    /// <summary>带占位符的文案，例如 F("Core.Route.WriteFailed", destination)。</summary>
    public static string F(string key, params object?[] args)
    {
        var format = T(key);
        try
        {
            return string.Format(Culture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    /// <summary>XAML 绑定的载体：索引器就是 T()，切语言时整体通知刷新。</summary>
    public sealed class Localizer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string key] => T(key);

        internal void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
