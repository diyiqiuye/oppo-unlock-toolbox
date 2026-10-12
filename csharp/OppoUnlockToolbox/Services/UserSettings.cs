using System.IO;
using System.Text.Json;

namespace OppoUnlockToolbox.Services;

/// <summary>用户偏好：目前只有主题，落在工作目录的 settings.json 里。</summary>
public sealed class UserSettings
{
    /// <summary>首次运行默认深色；用户切换后会写回这里。</summary>
    public bool DarkTheme { get; set; } = true;

    private static string Path => System.IO.Path.Combine(AppPaths.AppDir(), "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path)) ?? new UserSettings();
        }
        catch
        {

        }
        return new UserSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {

        }
    }
}
