<#
  Renders the hand-authored SVG artwork to PNG.

  The SVGs are the source of truth (editable, diffable, resolution independent). The
  PNGs exist because GitHub's markdown sanitiser is unreliable with SVG filters, and
  the social preview image must be a raster file.

  Uses headless Chrome or Edge, whichever is installed.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"

$browser = @(
    "C:\Program Files\Google\Chrome\Application\chrome.exe",
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "Need Chrome or Edge installed to rasterise the SVGs." }

$assets = $PSScriptRoot
$tmp = Join-Path $env:TEMP "plugandpray-render"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# name, width, height
$targets = @(
    @{ name = "banner"; w = 1280; h = 640 },   # GitHub social preview / README header
    @{ name = "logo";   w = 512;  h = 512 }    # square mark, avatar, future app icon
)

foreach ($t in $targets) {
    $svg  = Join-Path $assets "$($t.name).svg"
    $html = Join-Path $tmp "$($t.name).html"
    $png  = Join-Path $assets "$($t.name).png"
    if (-not (Test-Path $svg)) { throw "missing $svg" }

    # Wrap in HTML at exact pixel size: pointing Chrome straight at an SVG adds
    # default margins and centring, which shifts the output.
    $uri = "file:///" + ($svg -replace '\\', '/')
    @"
<style>html,body{margin:0;padding:0;background:#0B111B}img{display:block;width:$($t.w)px;height:$($t.h)px}</style>
<img src="$uri">
"@ | Set-Content -LiteralPath $html -Encoding utf8

    Remove-Item $png -ErrorAction SilentlyContinue
    & $browser --headless --disable-gpu --hide-scrollbars `
               --screenshot="$png" --window-size="$($t.w),$($t.h)" `
               ("file:///" + ($html -replace '\\', '/')) 2>&1 | Out-Null
    Start-Sleep -Milliseconds 700

    if (-not (Test-Path $png)) { throw "failed to render $($t.name).png" }
    "{0,-12} {1}x{2}  {3:N0} KB" -f "$($t.name).png", $t.w, $t.h, ((Get-Item $png).Length / 1KB)
}
