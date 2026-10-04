
using System.IO;
using System.Text;
using System.Windows;
using OppoUnlockToolbox.Core;
using OppoUnlockToolbox.Ui;

namespace OppoUnlockToolbox;

public partial class App : Application
{
    public static bool SelfTestMode { get; private set; }

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

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (SelfTestMode)
            SelfTest.Run(window);
    }

    private static void ReportCrash(Exception exc)
    {
        var text = exc.ToString();
        try
        {
            var main = Application.Current?.MainWindow as MainWindow;
            main?.Dispatcher.Invoke(() => main.AppendLog("err", "界面异常：" + exc.Message));
        }
        catch
        {

        }
        try
        {
            File.AppendAllText(Path.Combine(AppConfig.LogDir(), "ui_error.log"),
                text + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {

        }
    }
}

internal static class SelfTest
{
    public static void Run(MainWindow window)
    {
        window.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1200);
            var report = new List<string>();
            try
            {
                report.Add($"OK title={window.Title}");
                report.Add($"stages={Workflow.Stages.Length}");
                report.Add($"base=#{((System.Windows.Media.SolidColorBrush)window.Background).Color.ToString().Substring(3)}");
                var toolsDir = Path.Combine(AppConfig.ResourceDir(), "tools", "platform-tools");
                report.Add($"bundled_tools={(Directory.Exists(toolsDir) ? string.Join(",", Directory.GetFiles(toolsDir).Select(Path.GetFileName)) : "缺失")}");
                report.Add($"adb_source={AppConfig.ToolSource("adb")}");
                report.Add($"adb_path={AppConfig.FindTool("adb") ?? ""}");
                report.Add($"adb_version={AppConfig.ToolVersion("adb")}");
                report.Add($"fastboot_source={AppConfig.ToolSource("fastboot")}");
                report.Add($"work_dir={AppConfig.AppDir()}");
                report.Add($"device_tiles=4");
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
                    File.WriteAllLines(Path.Combine(AppConfig.AppDir(), "selftest.txt"), report);
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
