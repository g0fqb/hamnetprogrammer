# Builds the HamNetProgrammer installer end to end: a fresh self-contained publish, then wraps it
# with Inno Setup into a single-file installer under dist\. This exists so the exact working
# publish flags (see HamNetProgrammer.Desktop.csproj's comments on why - Release crashes on launch,
# and a plain publish silently drops compiled XAML) live in one script instead of as tribal
# knowledge someone has to remember correctly under time pressure.
#
# Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php, or `winget install JRSoftware.InnoSetup`).
#
# Usage: powershell -File installer\build.ps1

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$desktopProj = Join-Path $repoRoot "src\HamNetProgrammer.Desktop\HamNetProgrammer.Desktop.csproj"
$publishDir = Join-Path $repoRoot "src\HamNetProgrammer.Desktop\bin\Publish"
$issFile = Join-Path $PSScriptRoot "HamNetProgrammer.iss"

# Pull the version from the csproj rather than duplicating it here, so the installer filename and
# AppVersion can never drift out of sync with what the app itself reports.
$csprojContent = Get-Content $desktopProj -Raw
if ($csprojContent -notmatch '<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>') {
    throw "Could not find <ApplicationDisplayVersion> in $desktopProj"
}
$version = $matches[1]
Write-Output "Building HamNetProgrammer $version..."

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

Write-Output "Publishing self-contained build (win-x64, Debug config - see csproj comments for why)..."
dotnet publish $desktopProj -c Debug -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if (-not (Test-Path (Join-Path $publishDir "HamNetProgrammer.Desktop.exe"))) {
    throw "Publish output is missing HamNetProgrammer.Desktop.exe - publish did not complete as expected."
}

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) {
        throw "ISCC.exe (Inno Setup compiler) not found. Install it: winget install JRSoftware.InnoSetup"
    }
    $iscc = $found
} else {
    $iscc = $iscc.Source
}

Write-Output "Compiling installer with Inno Setup..."
& $iscc "/DMyAppVersion=$version" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$outputExe = Join-Path $repoRoot "dist\HamNetProgrammer-Setup-$version.exe"
if (-not (Test-Path $outputExe)) {
    throw "Expected installer not found at $outputExe - check the ISCC output above."
}
Write-Output "Done: $outputExe"
