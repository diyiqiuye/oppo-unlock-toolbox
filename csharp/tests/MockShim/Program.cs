

using System.Diagnostics;

var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "adb")
    .ToLowerInvariant();
var fakeName = exeName.StartsWith("fastboot") ? "fastboot.py" : "adb.py";

var mockScript = Environment.GetEnvironmentVariable("MOCK_DEVICE_PY");
if (string.IsNullOrEmpty(mockScript) || !File.Exists(mockScript))
{
    Console.Error.WriteLine("mockshim: MOCK_DEVICE_PY 未设置或文件不存在");
    return 2;
}

var python = FindPython();
if (python is null)
{
    Console.Error.WriteLine("mockshim: 找不到 python，无法运行模拟设备");
    return 2;
}

var bootstrap = "import sys; sys.argv=[{0}]+sys.argv[1:]; _f={1}; " +
                "exec(compile(open(_f, encoding='utf-8').read(), _f, 'exec'), " +
                "{{'__name__': '__main__', '__file__': _f}})";
bootstrap = string.Format(bootstrap, Quote(fakeName), Quote(mockScript));
var psi = new ProcessStartInfo
{
    FileName = python,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
psi.ArgumentList.Add("-c");
psi.ArgumentList.Add(bootstrap);
foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
    psi.ArgumentList.Add(arg);

using var proc = Process.Start(psi)!;
var stdout = proc.StandardOutput.ReadToEndAsync();
var stderr = proc.StandardError.ReadToEndAsync();
proc.WaitForExit();
Console.Out.Write(stdout.Result);
Console.Error.Write(stderr.Result);
return proc.ExitCode;

static string Quote(string s) => "\"" + s.Replace("\\", "\\\\") + "\"";

static string? FindPython()
{
    foreach (var name in new[] { "python", "python3", "py" })
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), name + ".exe");
            if (File.Exists(candidate))
                return candidate;
        }
    }
    return null;
}
