using System.Windows;

namespace OppoUnlockToolbox.Services;

/// <summary>
/// 明暗主题切换：把 App.xaml 里第 2 个合并字典（索引 1）换成 Light.xaml 或 Dark.xaml。
/// 控件样式一律用 DynamicResource 取色，所以换字典就能整体变色，界面不用重建。
/// </summary>
public sealed class ThemeService
{
    /// <summary>App.xaml 中 MergedDictionaries 的顺序：0=Tokens，1=主题配色，2=Controls。</summary>
    private const int ThemeDictionaryIndex = 1;

    private readonly UserSettings _settings;

    public ThemeService(UserSettings settings)
    {
        _settings = settings;
        IsDark = settings.DarkTheme;
    }

    public bool IsDark { get; private set; }

    /// <summary>主题变了（供日志等需要重算颜色地方刷新）。</summary>
    public event Action? Changed;

    public void Apply() => SwapDictionary(IsDark);

    public void Toggle()
    {
        IsDark = !IsDark;
        SwapDictionary(IsDark);
        _settings.DarkTheme = IsDark;
        _settings.Save();
        Changed?.Invoke();
    }

    private static void SwapDictionary(bool dark)
    {
        var app = Application.Current;
        if (app is null)
            return;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > ThemeDictionaryIndex)
            merged[ThemeDictionaryIndex] = dictionary;
        else
            merged.Add(dictionary);
    }
}
