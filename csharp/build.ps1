

param(
    [switch]$Publish,
    [switch]$Installer
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csharp = Join-Path $root "csharp"

function Find-Dotnet {

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and (Test-Path (Join-Path (Split-Path -Parent $cmd.Source) "sdk"))) { return $cmd.Source }
    $local = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $local) { return $local }
    throw "dotnet SDK not found. Install .NET 8 SDK first."
}
$dotnet = Find-Dotnet
Write-Output "dotnet: $dotnet"

& $dotnet build (Join-Path $csharp "OppoUnlockToolbox\OppoUnlockToolbox.csproj") -c Release --nologo -v m
if ($LASTEXITCODE -ne 0) { exit 1 }

$exe = Join-Path $csharp "OppoUnlockToolbox\bin\Release\net48\OppoUnlockToolbox.exe"
if (-not (Test-Path $exe)) { throw "missing artifact: $exe" }
Remove-Item (Join-Path $exe "..\selftest.txt") -ErrorAction SilentlyContinue
$selftestOut = Join-Path $exe "..\selftest-stdout.txt"
Start-Process -FilePath $exe -ArgumentList "--selftest" -Wait -RedirectStandardOutput $selftestOut -WindowStyle Hidden
Remove-Item $selftestOut -ErrorAction SilentlyContinue
$selftest = Join-Path $exe "..\selftest.txt"
if (Test-Path $selftest) {
    Get-Content $selftest
    $first = (Get-Content $selftest -First 1)
    if (-not $first.StartsWith("OK")) { Write-Output "selftest FAILED"; exit 1 }
    Write-Output "selftest passed"
} else {
    Write-Output "selftest report missing"
    exit 1
}

if ($Publish) {
    # net48 是「依赖框架」发布：.NET Framework 4.8 由系统自带，产物就是一个小文件夹
    # （exe + Core.dll + System.Text.Json 那几个依赖），总量约 2 MB，没有单文件可打。
    $cnName = "OPPO" + [char]0x89E3 + [char]0x9501 + [char]0x5DE5 + [char]0x5177 + [char]0x7BB1
    $out = Join-Path $root (Join-Path "dist-cs" $cnName)
    $bin = Join-Path $csharp "OppoUnlockToolbox\bin\Release\net48"
    if (-not (Test-Path (Join-Path $bin "OppoUnlockToolbox.exe"))) { throw "missing artifact: $bin" }

    # 不动目录里已有的 tools\ 和 resources\（安装包要用），只覆盖程序本体
    if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out -Force | Out-Null }
    Copy-Item (Join-Path $bin "*.dll") $out -Force
    Copy-Item (Join-Path $bin "OppoUnlockToolbox.exe") $out -Force
    Copy-Item (Join-Path $bin "OppoUnlockToolbox.exe.config") $out -Force

    $exeName = "OPPO" + [char]0x89E3 + [char]0x9501 + [char]0x5DE5 + [char]0x5177 + [char]0x7BB1 + ".exe"
    if (Test-Path (Join-Path $out $exeName)) { Remove-Item (Join-Path $out $exeName) -Force }
    Rename-Item (Join-Path $out "OppoUnlockToolbox.exe") $exeName -Force
    # 配置文件必须跟着 exe 一起改名，否则绑定重定向不会生效
    if (Test-Path (Join-Path $out ($exeName + ".config"))) { Remove-Item (Join-Path $out ($exeName + ".config")) -Force }
    Rename-Item (Join-Path $out "OppoUnlockToolbox.exe.config") ($exeName + ".config") -Force

    Write-Output "publish done: $out"
}

if ($Installer) {
    function Find-Issc {
        $cmd = Get-Command iscc -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
        $scoop = Join-Path $env:USERPROFILE "scoop\apps\inno-setup\current\ISCC.exe"
        if (Test-Path $scoop) { return $scoop }
        $pf = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
        if (Test-Path $pf) { return $pf }
        throw "ISCC.exe not found. Install Inno Setup 6 (scoop install inno-setup)."
    }
    $iscc = Find-Issc
    Write-Output "iscc: $iscc"
    & $iscc (Join-Path $csharp "installer\installer.iss")
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Write-Output "installer done"
}
