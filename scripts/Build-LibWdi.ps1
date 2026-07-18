<#
.SYNOPSIS
  Build libwdi.dll from source, from scratch, on any machine (local or CI runner).

.DESCRIPTION
  libwdi ships no prebuilt binaries by design, and it will not compile out of the box on
  a modern toolchain. This script encodes every workaround required, so the build is
  reproducible instead of tribal knowledge. Each fix is explained inline; the full
  archaeology is in NOTES.md.

  The five problems it solves:
    1. WinUSB support is compiled out unless WDK_DIR is defined, and the WDK 8.0-era
       redistributables it wants are not in any current WDK. We fetch Microsoft's
       still-hosted coinstaller MSI and point WDK_DIR at it.
    2. config.h enables ARM64, which needs a cross-compiler most machines lack.
    3. config.h points LIBUSB0_DIR / LIBUSBK_DIR at placeholder D:\ paths that do not
       exist, which makes the resource embedder fail.
    4. The DLL project is present in the solution but not flagged to build (it has
       ActiveCfg entries but no Build.0 entries), so it silently produces nothing.
    5. The pre-build step runs a bare `embedder`, relying on the current directory being
       searched for executables. That fails across drives and fails outright when
       NoDefaultCurrentDirectoryInExePath is set.

.NOTES
  Idempotent: safe to re-run. Verifies its own output.
#>
[CmdletBinding()]
param(
    # Pin the exact upstream commit we have validated against.
    [string] $LibWdiCommit = "30df0c0e051b0132c4b9ebed8c054bc8eb3aaaec",
    [string] $WorkDir      = (Join-Path $PSScriptRoot "..\vendor"),
    [string] $Configuration = "Release",
    [switch] $Force          # re-clone / rebuild even if outputs already exist
)

$ErrorActionPreference = "Stop"
function Step($n, $msg) { Write-Host "`n[$n] $msg" -ForegroundColor Cyan }

$WorkDir   = (Resolve-Path -LiteralPath $WorkDir -ErrorAction SilentlyContinue)?.Path `
             ?? (New-Item -ItemType Directory -Force -Path $WorkDir).FullName
$libwdiDir = Join-Path $WorkDir "libwdi"
$redistDir = Join-Path $WorkDir "wdk-redist"
$msiPath   = Join-Path $WorkDir "wdfcoinstaller.msi"

# ---------------------------------------------------------------- 1. source
Step 1 "Fetching libwdi source (pinned $($LibWdiCommit.Substring(0,10)))"
if ($Force -and (Test-Path $libwdiDir)) { Remove-Item -Recurse -Force $libwdiDir }
if (-not (Test-Path (Join-Path $libwdiDir "libwdi\libwdi.h"))) {
    if (Test-Path $libwdiDir) { Remove-Item -Recurse -Force $libwdiDir }
    git clone --quiet https://github.com/pbatard/libwdi.git $libwdiDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
}
Push-Location $libwdiDir
try {
    git fetch --quiet origin $LibWdiCommit 2>$null
    git checkout --quiet $LibWdiCommit
    if ($LASTEXITCODE -ne 0) { throw "could not check out pinned commit $LibWdiCommit" }
    # Discard any previous run's edits so patching is deterministic.
    git checkout --quiet -- .
} finally { Pop-Location }
Write-Host "    source at $LibWdiCommit"

# ------------------------------------------------- 2. WDK redistributables
# Problem 1. libwdi.c compiles WinUSB support out entirely unless WDK_DIR is defined
# (wdi_is_driver_supported returns FALSE for WDI_WINUSB), and embedder_files.h wants
# WdfCoInstaller01011.dll + winusbcoinstaller2.dll, which modern WDKs no longer ship.
# Microsoft still hosts the WDK 8.0 redistributable MSI at this fwlink.
Step 2 "Obtaining WDK WinUSB coinstallers"
$wdkRoot = Join-Path $redistDir "Windows Kits\8.0"
if ($Force -or -not (Test-Path (Join-Path $wdkRoot "redist\wdf\x64\winusbcoinstaller2.dll"))) {
    if (-not (Test-Path $msiPath)) {
        $url = "https://download.microsoft.com/download/0/5/F/05FD6919-6250-425B-86ED-9B095E54065A/wdfcoinstaller.msi"
        Write-Host "    downloading $([IO.Path]::GetFileName($url))"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $msiPath
    }
    if (Test-Path $redistDir) { Remove-Item -Recurse -Force $redistDir }
    # An administrative install (/a) unpacks the real directory layout without
    # installing anything, and does not require elevation.
    $p = Start-Process msiexec.exe -ArgumentList "/a `"$msiPath`" /qn TARGETDIR=`"$redistDir`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "msiexec extraction failed ($($p.ExitCode))" }
}
if (-not (Test-Path (Join-Path $wdkRoot "redist\wdf\x64\winusbcoinstaller2.dll"))) {
    throw "coinstallers missing under $wdkRoot"
}
Write-Host "    WDK_DIR -> $wdkRoot"

# ---------------------------------------------------------------- 3. patch
Step 3 "Applying build fixes"

# NOTE: all of these edits are done line by line rather than with multiline regex.
# These files use CRLF endings, so a pattern anchored with `"$` never matches: the \r
# sits between the closing quote and the line end. That failure is silent, which is
# exactly how you end up with a "successful" build that quietly lacks WinUSB support.

# --- config.h -------------------------------------------------------------
$configPath = Join-Path $libwdiDir "msvc\config.h"
$wdkFwd = $wdkRoot -replace '\\', '/'      # config.h uses forward slashes
$applied = @{ wdk = $false; arm = $false; usb0 = $false; usbk = $false }

$lines = [IO.File]::ReadAllLines($configPath)
for ($i = 0; $i -lt $lines.Count; $i++) {
    switch -Regex ($lines[$i]) {
        # Problem 1: point WDK_DIR at the redistributables we just extracted.
        '^\s*#define\s+WDK_DIR\s+"' {
            $lines[$i] = "#define WDK_DIR `"$wdkFwd`""; $applied.wdk = $true; break
        }
        # Problem 2: ARM64 needs a cross-compiler we cannot assume exists.
        '^\s*#define\s+OPT_ARM\s*$' {
            $lines[$i] = '/* #define OPT_ARM */   /* disabled: no ARM64 cross-compiler */'
            $applied.arm = $true; break
        }
        # Problem 3: these point at nonexistent D:\ paths and break the embedder.
        # We only ship WinUSB, so neither driver is wanted.
        '^\s*#define\s+LIBUSB0_DIR\s+"' {
            $lines[$i] = '/* LIBUSB0_DIR disabled: we only ship WinUSB */'; $applied.usb0 = $true; break
        }
        '^\s*#define\s+LIBUSBK_DIR\s+"' {
            $lines[$i] = '/* LIBUSBK_DIR disabled: we only ship WinUSB */'; $applied.usbk = $true; break
        }
    }
}
[IO.File]::WriteAllLines($configPath, $lines)

foreach ($k in $applied.Keys) {
    if (-not $applied[$k]) { throw "config.h patch failed: '$k' rule matched nothing (upstream format changed?)" }
}
Write-Host "    config.h patched (WDK_DIR, OPT_ARM off, libusb0/libusbK off)"

# --- solution -------------------------------------------------------------
# Problem 4: 'libwdi (dll)' has ActiveCfg entries but no Build.0 entries, so the
# solution never actually builds it and silently produces nothing. Meanwhile
# installer_arm64 IS flagged to build, and always fails.
$slnPath = Join-Path $libwdiDir "libwdi.sln"
$dllGuid = '79275348-41A4-4D07-8990-4068C9594A2C'
$armGuid = '6AC16F78-F266-4AE0-BD63-550A55F54C15'

$out = [System.Collections.Generic.List[string]]::new()
$addedBuild = 0; $droppedArm = 0
foreach ($line in [IO.File]::ReadAllLines($slnPath)) {
    # drop arm64 from the build set (leave its ActiveCfg lines alone)
    if ($line -match "^\s*\{$armGuid\}\..+\.Build\.0\s*=") { $droppedArm++; continue }

    $out.Add($line)

    # flag the DLL project to actually build, mirroring each ActiveCfg
    if ($line -match "^\s*\{$dllGuid\}\.(.+?)\.ActiveCfg\s*=\s*(.+?)\s*$") {
        $out.Add("`t`t{$dllGuid}.$($Matches[1]).Build.0 = $($Matches[2])")
        $addedBuild++
    }
}
[IO.File]::WriteAllLines($slnPath, $out)
if ($addedBuild -eq 0) { throw "sln patch failed: DLL project ActiveCfg lines not found" }
if ($droppedArm -eq 0) { throw "sln patch failed: arm64 Build.0 lines not found" }
Write-Host "    libwdi.sln patched (+$addedBuild dll build entries, -$droppedArm arm64)"

# --- project files --------------------------------------------------------
foreach ($proj in @("libwdi\.msvc\libwdi_dll.vcxproj", "libwdi\.msvc\libwdi_static.vcxproj")) {
    $path = Join-Path $libwdiDir $proj
    $xml = Get-Content $path -Raw

    # Problem 2 (cont.): a hard ProjectReference to installer_arm64 drags the ARM64
    # build in even when the solution no longer builds it.
    $xml = [regex]::Replace($xml,
        '(?is)\s*<ProjectReference Include="installer_arm64\.vcxproj">.*?</ProjectReference>', '')

    # Problem 5: `cd` without /d will not change drive, and a bare `embedder` is not
    # found when the current directory is excluded from the executable search path.
    $xml = $xml -replace '<Command>cd \$\(ProjectDir\)\\\.\.', '<Command>cd /d $(ProjectDir)\..'
    $xml = $xml -replace '(?m)^embedder embedded\.h</Command>', '.\embedder embedded.h</Command>'

    Set-Content -LiteralPath $path -Value $xml -NoNewline
    if ($xml -match 'installer_arm64') { throw "$proj patch failed: arm64 reference remains" }
    if ($xml -match '(?m)^embedder embedded\.h') { throw "$proj patch failed: bare embedder call remains" }
}
Write-Host "    vcxproj files patched"

# ---------------------------------------------------------------- 4. build
Step 4 "Building libwdi ($Configuration|x64)"
# Finding MSBuild is fiddlier than it looks. `-products *` is essential: without it
# vswhere only considers the full IDE SKUs and silently ignores a Build Tools install,
# which is what most build machines actually have. On CI, MSBuild is usually already
# on PATH (e.g. via microsoft/setup-msbuild), so we check that first.
$msbuild = (Get-Command MSBuild.exe -ErrorAction SilentlyContinue)?.Source
if (-not $msbuild) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * `
                                  -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                                  -property installationPath | Select-Object -First 1
        if ($installPath) {
            $msbuild = Get-ChildItem -Path (Join-Path $installPath "MSBuild") -Recurse `
                                     -Filter MSBuild.exe -ErrorAction SilentlyContinue |
                       Where-Object { $_.FullName -notmatch '\\amd64\\' } |
                       Select-Object -First 1 -ExpandProperty FullName
        }
    }
}
if (-not $msbuild) {
    throw "MSBuild not found. Install VS Build Tools with the 'Desktop development with C++' workload."
}
Write-Host "    msbuild: $msbuild"

# Use an absolute solution path and set the *process* working directory explicitly.
# PowerShell's Push-Location moves the shell's location but does not reliably update
# [Environment]::CurrentDirectory, which is what a native child process inherits, so a
# relative "libwdi.sln" can resolve against the wrong directory.
$slnFull = Join-Path $libwdiDir "libwdi.sln"
$prevCwd = [Environment]::CurrentDirectory
Push-Location $libwdiDir
[Environment]::CurrentDirectory = $libwdiDir
try {
    & $msbuild $slnFull /p:Configuration=$Configuration /p:Platform=x64 /m /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "libwdi build failed ($LASTEXITCODE)" }
} finally {
    [Environment]::CurrentDirectory = $prevCwd
    Pop-Location
}

# ---------------------------------------------------------------- 5. verify
Step 5 "Verifying output"
$dll = Join-Path $libwdiDir "x64\$Configuration\dll\libwdi.dll"
if (-not (Test-Path $dll)) { throw "libwdi.dll was not produced at $dll" }

# Confirm it is a 64-bit PE, since a silently-32-bit DLL would fail at P/Invoke time.
$bytes = [IO.File]::ReadAllBytes($dll)
$machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 4)
if ($machine -ne 0x8664) { throw ("libwdi.dll is not x64 (machine 0x{0:X})" -f $machine) }

"{0}  {1:N1} MB  x64" -f (Split-Path $dll -Leaf), ((Get-Item $dll).Length / 1MB)
Write-Host "`nlibwdi.dll ready: $dll" -ForegroundColor Green
