

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

$exe = Join-Path $csharp "OppoUnlockToolbox\bin\Release\net8.0-windows\OppoUnlockToolbox.exe"
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
    $cnName = "OPPO" + [char]0x89E3 + [char]0x9501 + [char]0x5DE5 + [char]0x5177 + [char]0x7BB1
    $out = Join-Path $root (Join-Path "dist-cs" $cnName)
    & $dotnet publish (Join-Path $csharp "OppoUnlockToolbox\OppoUnlockToolbox.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishDir=$out --nologo -v m
    if ($LASTEXITCODE -ne 0) { exit 1 }

    $published = Get-ChildItem $out -Filter "OppoUnlockToolbox.exe" | Select-Object -First 1
    $target = [System.IO.Path]::Combine($out, "OPPO" + [char]0x89E3 + [char]0x9501 + [char]0x5DE5 + [char]0x5177 + [char]0x7BB1 + ".exe")
    if ($published) {
        if (Test-Path $target) { Remove-Item $target -Force }
        Rename-Item $published.FullName $target -Force
    }
    Write-Output "publish done: $out"
}

if ($Installer) {
    function Find-Issc {
        $cmd = Get-Command iscc -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
        $scoop = Join-Path $env:USERPROFILE "scooppps\inno-setup\current\ISCC.exe"
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
