

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OppoUnlockToolbox.Core;

public static class Cmd
{
    public const int CreateNoWindow = 0x08000000;
    public const int CreateNewConsole = 0x00000010;

    public static string QuoteArgv(IEnumerable<string?> argv)
    {
        var result = new StringBuilder();
        foreach (var arg0 in argv)
        {
            var arg = arg0 ?? "";
            var bsBuf = 0;
            if (result.Length > 0)
                result.Append(' ');
            var needQuote = arg.Contains(' ') || arg.Contains('\t') || arg.Length == 0;
            if (needQuote)
                result.Append('"');
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    bsBuf++;
                }
                else if (c == '"')
                {
                    result.Append('\\', bsBuf * 2);
                    bsBuf = 0;
                    result.Append("\\\"");
                }
                else
                {
                    if (bsBuf > 0)
                    {
                        result.Append('\\', bsBuf);
                        bsBuf = 0;
                    }
                    result.Append(c);
                }
            }
            if (bsBuf > 0)
                result.Append('\\', bsBuf);
            if (needQuote)
            {

                result.Append('\\', bsBuf);
                result.Append('"');
            }
        }
        return result.ToString();
    }
}

public sealed class RunResult
{
    public string[] Argv { get; }
    public int Code { get; set; }
    public string Out { get; set; } = "";
    public bool TimedOut { get; set; }
    public string Error { get; set; } = "";

    public RunResult(string[] argv, int code = 0, string outText = "", bool timedOut = false, string error = "")
    {
        Argv = argv;
        Code = code;
        Out = outText ?? "";
        TimedOut = timedOut;
        Error = error ?? "";
    }

    public bool Ok => Code == 0 && !TimedOut && Error.Length == 0;

    public string Command => Cmd.QuoteArgv(Argv);

    public List<string> Lines()
    {
        return Out.Split('\n')
            .Select(ln => ln.TrimEnd('\r'))
            .Where(ln => ln.Trim().Length > 0)
            .ToList();
    }

    public string Tail(int count = 6)
    {
        var lines = Lines();
        return lines.Count > 0 ? string.Join(" / ", lines.TakeLast(count)) : "(无输出)";
    }
}

public sealed class RunOptions
{
    public int? TimeoutMs { get; init; }
    public string? WorkingDir { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraEnv { get; init; }
    public Action<string>? OnLine { get; init; }
    public CancellationToken Cancel { get; init; }
    public string? InputText { get; init; }
}

public static class ProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static RunResult Run(IEnumerable<object?> argvRaw, RunOptions? options = null)
    {
        var argv = argvRaw.Select(a => a?.ToString() ?? "").ToArray();
        var opts = options ?? new RunOptions();
        if (argv.Length == 0)
            return new RunResult(argv, -1, error: "空命令");

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = opts.WorkingDir ?? "",
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = opts.InputText != null,
        };
        startInfo.FileName = argv[0];
        for (var i = 1; i < argv.Length; i++)
            startInfo.ArgumentList.Add(argv[i]);
        if (opts.ExtraEnv != null)
            foreach (var (key, value) in opts.ExtraEnv)
                startInfo.Environment[key] = value;

        Process proc;
        try
        {
            proc = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("进程启动返回空");
        }
        catch (FileNotFoundException)
        {
            return new RunResult(argv, -1, error: $"找不到可执行文件：{argv[0]}");
        }
        catch (SystemException exc)
        {
            return new RunResult(argv, -1, error: $"无法启动进程：{exc.Message}");
        }

        var sync = new object();
        var chunks = new List<string>();
        var collected = new List<string>();
        var timedOut = false;
        var cancelled = false;

        void HandleLine(string? line)
        {
            if (line is null)
                return;
            lock (sync)
            {
                chunks.Add(line);
                collected.Add(line);
            }
            try { opts.OnLine?.Invoke(line); } catch {   }
        }

        proc.OutputDataReceived += (_, e) => HandleLine(e.Data);
        proc.ErrorDataReceived += (_, e) => HandleLine(e.Data);

        using var watchdog = new CancellationTokenSource();
        var guard = Task.Run(async () =>
        {
            var deadline = opts.TimeoutMs is { } ms ? DateTime.UtcNow.AddMilliseconds(ms) : (DateTime?)null;
            while (!watchdog.Token.IsCancellationRequested)
            {
                try { await Task.Delay(150, watchdog.Token); } catch (TaskCanceledException) { return; }
                if (proc.HasExited)
                    return;
                if (opts.Cancel.IsCancellationRequested)
                {
                    cancelled = true;
                    TryKill(proc);
                    return;
                }
                if (deadline.HasValue && DateTime.UtcNow > deadline.Value)
                {
                    timedOut = true;
                    TryKill(proc);
                    return;
                }
            }
        });

        try
        {
            if (opts.InputText != null)
            {
                try
                {
                    proc.StandardInput.Write(opts.InputText);
                    proc.StandardInput.Close();
                }
                catch {   }
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();
        }
        finally
        {
            watchdog.Cancel();
            try { guard.Wait(200); } catch {   }
            try { proc.WaitForExit(10000); } catch { TryKill(proc); }
        }

        string outText;
        lock (sync)
        {
            outText = string.Join("\n", chunks);
        }

        var code = proc.HasExited ? proc.ExitCode : -1;
        try { proc.Dispose(); } catch { }
        var result = new RunResult(argv, code, outText, timedOut: timedOut);
        if (cancelled)
            result.Error = "已取消";
        else if (timedOut)
            result.Error = $"超时（{opts.TimeoutMs!.Value / 1000.0:0.#}s）";
        return result;
    }

    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch {   }
    }

    public static (bool Ok, string Error) SpawnConsole(IEnumerable<object?> argvRaw, string? cwd = null,
        bool keepOpen = true, IEnumerable<string>? banner = null)
    {
        var argv = argvRaw.Select(a => a?.ToString() ?? "").ToArray();
        if (OperatingSystem.IsWindows() && keepOpen)
        {
            var bat = WriteConsoleBat(argv, banner);
            if (!string.IsNullOrEmpty(bat))
            {
                try
                {

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + QuoteForCmd(bat),
                        WorkingDirectory = cwd ?? "",
                        UseShellExecute = true,
                    });
                    return (true, "");
                }
                catch (SystemException exc)
                {
                    return (false, exc.Message);
                }
            }
        }
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = argv[0],
                UseShellExecute = true,
                WorkingDirectory = cwd ?? "",
                Arguments = Cmd.QuoteArgv(argv.Skip(1)),
            };
            Process.Start(startInfo);
            return (true, "");
        }
        catch (SystemException exc)
        {
            return (false, exc.Message);
        }
    }

    private static string QuoteForCmd(string path) =>
        path.Contains(' ') ? "\"" + path + "\"" : path;

    private static readonly string[] ConsoleLines =
    {
        "  [OPPO 解锁工具箱] 交互式 root shell",
        "  ------------------------------------------------------------",
        "  * 这个窗口不会自动关闭：shell 退出或报错后，按任意键关闭。",
        "  * 若下方报错，先看报错内容，再回工具箱看「实时输出」。",
        "  ------------------------------------------------------------",
    };

    private static string ConsoleBatPath()
    {
        string folder;
        try
        {
            folder = AppConfig.LogDir();
        }
        catch
        {
            folder = Path.GetTempPath();
        }
        return Path.Combine(folder, "root_shell.bat");
    }

    private static string WriteConsoleBat(string[] argv, IEnumerable<string>? banner)
    {
        var path = ConsoleBatPath();
        var lines = new List<string> { "@echo off", "echo." };
        lines.AddRange(ConsoleLines.Select(item => "echo " + item));
        if (banner != null)
            lines.AddRange(banner.Select(item => "echo " + item));
        lines.Add("echo.");
        lines.Add(Cmd.QuoteArgv(argv));
        lines.Add("echo.");
        lines.Add("echo   [进程已退出] 按任意键关闭窗口...");
        lines.Add("pause >nul");
        lines.Add("");
        var text = string.Join("\n", lines);

        Encoding[] encodings;
        try
        {
            encodings = new[] { Encoding.GetEncoding(AcpCodePage()), Encoding.UTF8, Encoding.ASCII };
        }
        catch
        {
            encodings = new[] { Encoding.UTF8, Encoding.ASCII };
        }
        foreach (var encoding in encodings)
        {
            try
            {
                File.WriteAllText(path, text, encoding);
                return path;
            }
            catch
            {

            }
        }
        return "";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetACP();

    private static int AcpCodePage()
    {
        try
        {
            return GetACP();
        }
        catch
        {
            return 65001;
        }
    }
}

public static class FileTools
{

    public static string Md5File(string path, CancellationToken cancel = default, long limit = 0)
    {
        try
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancel.IsCancellationRequested)
                    return "";
                if (limit > 0)
                {
                    var remain = limit - total;
                    if (remain <= 0)
                        break;
                    if (read > remain)
                        read = (int)remain;
                }
                md5.TransformBlock(buffer, 0, read, null, 0);
                total += read;
            }
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(md5.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    public static long? FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }

    public static string FmtSize(object? num)
    {
        double value;
        if (num is null)
            return "—";
        try
        {
            value = Convert.ToDouble(num);
        }
        catch
        {
            return "—";
        }
        string[] units = { "B", "KB", "MB", "GB" };
        for (var i = 0; i < units.Length; i++)
        {
            if (value < 1024 || i == units.Length - 1)
            {
                return i == 0
                    ? string.Format("{0:0} {1}", value, units[i])
                    : string.Format("{0:0.0} {1}", value, units[i]);
            }
            value /= 1024.0;
        }
        return "—";
    }

    public static string FmtMd5(string? md5, int width = 8)
    {
        return string.IsNullOrEmpty(md5) ? "—" : md5[..Math.Min(width, md5.Length)];
    }
}
