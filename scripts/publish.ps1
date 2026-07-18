<#
  Produce the shippable build: ONE self-contained .exe a pilot can download and run
  with no .NET install. Kept as a script (not csproj properties) so RuntimeIdentifier
  doesn't reshuffle the normal dev build output paths.

  Why self-contained: the audience is people who can't get a USB driver working. Telling
  them to first install a .NET Desktop Runtime loses most of them. Size is the price.

  Notes:
   - PublishTrimmed is NOT used: trimming is unsupported for WPF and silently breaks XAML.
   - IncludeNativeLibrariesForSelfExtract bundles libwdi.dll inside the single file.
   - InvariantGlobalization drops ICU (~30MB); this app does no culture-sensitive work.
#>
param(
    [string] $Configuration = "Release",
    [switch] $ReadyToRun    # bigger file, faster cold start
)

# Paths are resolved relative to this script so the same command works on a dev machine
# and on a CI runner, where the repo lives somewhere else entirely.
$repo  = Split-Path -Parent $PSScriptRoot
$proj  = Join-Path $repo "app\FcDriverFixer.Wpf\FcDriverFixer.Wpf.csproj"
$out   = Join-Path $repo "dist"
$loose = Join-Path $repo "dist-loose"

$common = @(
    "publish", $proj,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:InvariantGlobalization=true",
    "-p:DebugType=none"
)

# ---- 1. Convenience build: one self-contained file, nothing to install ----
$single = $common + @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o", $out
)
if ($ReadyToRun) { $single += "-p:PublishReadyToRun=true" }

Write-Host "[1/2] Publishing self-contained single-file..."
& dotnet @single 2>&1 | Select-Object -Last 3

# ---- 2. LGPL build: libwdi.dll left loose so it can be replaced ----
# libwdi is LGPLv3. Bundling it inside a single-file exe would stop users swapping in
# their own build, so we also ship a folder build where libwdi.dll is a normal,
# replaceable DLL. This is what keeps us clean on the licence, and the README promises it.
Write-Host "[2/2] Publishing replaceable-DLL build (LGPL compliance)..."
$multi = $common + @("-p:PublishSingleFile=false", "-o", $loose)
& dotnet @multi 2>&1 | Select-Object -Last 3

$zip = Join-Path $out "PlugAndPray-replaceable-libwdi.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$loose\*" -DestinationPath $zip

Write-Host "`nArtifacts:"
Get-ChildItem $out -Include *.exe, *.zip -Recurse | ForEach-Object {
    "  {0,-42} {1,7:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
if (Test-Path "$loose\libwdi.dll") {
    "  (replaceable libwdi.dll present in dist-loose)"
} else {
    Write-Warning "libwdi.dll missing from the loose build - LGPL claim in README would be false!"
}
