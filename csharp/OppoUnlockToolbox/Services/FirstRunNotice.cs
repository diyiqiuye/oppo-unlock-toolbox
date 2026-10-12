using System.IO;

namespace OppoUnlockToolbox.Services;

/// <summary>「使用须知」只弹一次：弹过就在工作目录里留个标记文件，标记里记着当时的版本。</summary>
public static class FirstRunNotice
{
    private const string FlagName = "welcome.ok";

    private static string FlagPath() => Path.Combine(AppPaths.AppDir(), FlagName);

    public static bool AlreadyShown()
    {
        try
        {
            return File.Exists(FlagPath());
        }
        catch
        {
            return true;
        }
    }

    public static void MarkShown()
    {
        try
        {
            File.WriteAllText(FlagPath(), AppMeta.AppVersion);
        }
        catch
        {

        }
    }
}
