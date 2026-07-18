<#
  Launch the (non-elevated, iteration build of the) WPF app, capture its window to PNG,
  then close it. Used for fast design iteration without a UAC prompt each time.
#>
param(
    [string] $Out = "$env:TEMP\plugandpray-capture.png",
    [int]    $SettleMs = 2200,
    [string] $Preview                # e.g. NormalMode / DfuNeedsFix / NothingDetected
)

Add-Type -AssemblyName System.Drawing
if (-not ("WCap" -as [type])) {
Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;
public class WCap {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  // PW_RENDERFULLCONTENT (2) renders the window's own content even when occluded,
  // unlike CopyFromScreen which just grabs whatever pixels are on top.
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}
[WCap]::SetProcessDPIAware() | Out-Null

$repo = Split-Path -Parent $PSScriptRoot
$app  = Join-Path $repo "app\FcDriverFixer.Wpf\bin\Debug\net8.0-windows\PlugAndPray.exe"
if (-not (Test-Path $app)) { throw "Build the WPF app first: dotnet build app/FcDriverFixer.Wpf" }

Get-Process -Name PlugAndPray -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch {}
}
Start-Sleep -Milliseconds 400

if ($Preview) { Start-Process -FilePath $app -ArgumentList "--preview:$Preview" }
else          { Start-Process -FilePath $app }
$proc = $null
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    $proc = Get-Process -Name PlugAndPray -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($proc) { break }
    Start-Sleep -Milliseconds 300
}
if (-not $proc) { Write-Error "window never appeared"; exit 1 }

Start-Sleep -Milliseconds $SettleMs
[WCap]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 450

$r = New-Object WCap+RECT
[WCap]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WCap]::PrintWindow($proc.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
if (-not $ok) { Write-Warning "PrintWindow returned false; image may be blank." }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {}
"Captured ${w}x${h} -> $Out"
