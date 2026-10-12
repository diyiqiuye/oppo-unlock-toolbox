using System.IO;
using System.Text;
using System.Windows;
using OppoUnlockToolbox.ViewModels;
using OppoUnlockToolbox.Views;

namespace OppoUnlockToolbox;

/// <summary>组装根：这里把服务、ViewModel、主窗口接起来。</summary>
public partial class App : Application
{
    public static bool SelfTestMode { get; private set; }

    public MainViewModel? ViewModel { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        SelfTestMode = e.Args.Contains("--selftest");

        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exc)
                ReportCrash(exc);
        };

        base.OnStartup(e);

        var settings = UserSettings.Load();
        var theme = new ThemeService(settings);
        theme.Apply();

        var dialogs = new DialogService();
        var viewModel = new MainViewModel(dialogs, theme, SelfTestMode);
        ViewModel = viewModel;

        var window = new MainWindow { DataContext = viewModel };
        dialogs.Owner = window;
        MainWindow = window;
        window.Show();

        if (SelfTestMode)
            SelfTest.Run(window, viewModel);
    }

    private static void ReportCrash(Exception exc)
    {
        var text = exc.ToString();
        try
        {
            (Current as App)?.ViewModel?.AppendLog("err", "界面异常：" + exc.Message);
        }
        catch
        {

        }
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.LogDir(), "ui_error.log"),
                text + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {

        }
    }
}

/// <summary>`--selftest` 时跑的冒烟检查：把关键装配点写进 selftest.txt 供构建脚本判断。</summary>
internal static class SelfTest
{
    public static void Run(MainWindow window, MainViewModel viewModel)
    {
        window.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1200);
            var report = new List<string>();
            try
            {
                report.Add($"OK title={window.Title}");
                report.Add($"stages={StageRegistry.Stages.Length}");
                report.Add($"base=#{((System.Windows.Media.SolidColorBrush)window.Background).Color.ToString().Substring(3)}");
                var toolsDir = Path.Combine(AppPaths.ResourceDir(), "tools", "platform-tools");
                report.Add($"bundled_tools={(Directory.Exists(toolsDir) ? string.Join(",", Directory.GetFiles(toolsDir).Select(Path.GetFileName)) : "缺失")}");
                report.Add($"adb_source={ToolLocator.ToolSource("adb")}");
                report.Add($"adb_path={ToolLocator.FindTool("adb") ?? ""}");
                report.Add($"adb_version={ToolLocator.ToolVersion("adb")}");
                report.Add($"fastboot_source={ToolLocator.ToolSource("fastboot")}");
                report.Add($"work_dir={AppPaths.AppDir()}");
                report.Add($"device_tiles={viewModel.Device.Tiles.Count}");
                report.Add($"rail_items={viewModel.Rail.Count}");
                report.Add($"progress=determinate");
            }
            catch (Exception exc)
            {
                report.Add("FAIL 自检异常");
                report.Add(exc.ToString());
            }
            finally
            {
                try
                {
                    File.WriteAllLines(Path.Combine(AppPaths.AppDir(), "selftest.txt"), report);
                }
                catch
                {

                }
                window.Close();
                Environment.Exit(report.Count > 0 && report[0].StartsWith("OK") ? 0 : 1);
            }
        });
    }
}
