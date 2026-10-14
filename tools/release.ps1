# Builds everything a GitHub release of RimForge needs, into artifacts\<version>\:
#   RimForge-Setup-<version>.exe     the installer (Inno Setup 6 must be installed)
#   RimForge-<version>-portable.zip  RimForge.exe + portable.txt: unpack anywhere and run
#   RimForge.exe                     the bare exe RimForge downloads when it updates itself
#   latest.json                      the update feed RimForge reads (points at this release)
#   SHA256SUMS.txt                   checksums of all the above
# The version comes from Directory.Build.props. Usage: release.cmd  (or: powershell -File tools\release.ps1 -Notes "...")
param(
    [string]$Notes = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$repo = "SORLOK212/RimForge-X-Manager"
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root "artifacts\$version"
$build = Join-Path $root "artifacts\build"
Write-Host "RimForge $version -> $out" -ForegroundColor Cyan

# Inno Setup's compiler: per-user or machine-wide install.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 is not installed (https://jrsoftware.org/isdl.php)" }

if (-not $SkipTests) {
    Write-Host "Tests..." -ForegroundColor Cyan
    dotnet test (Join-Path $root "RimForge.slnx") --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Write-Host "Building RimForge.exe..." -ForegroundColor Cyan
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
dotnet publish (Join-Path $root "src\RimForge.App") -c Release -o $build --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$exe = Join-Path $build "RimForge.exe"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $exe (Join-Path $out "RimForge.exe")

Write-Host "Portable zip..." -ForegroundColor Cyan
$portable = Join-Path $root "artifacts\portable"
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item -ItemType Directory -Force $portable | Out-Null
Copy-Item $exe $portable
Copy-Item (Join-Path $root "LICENSE") (Join-Path $portable "LICENSE.txt")
@"
RimForge X Manager $version - portable build.
This file tells RimForge to keep everything (settings, modpacks, the library of game versions) in the "Data"
and "Library" folders next to RimForge.exe. Delete it to use the normal per-user folders instead.

Портативная версия RimForge X Manager $version.
Этот файл говорит RimForge хранить всё (настройки, сборки, библиотеку версий игры) в папках "Data" и "Library"
рядом с RimForge.exe. Удали его, чтобы RimForge хранил данные в обычных папках пользователя.
"@ | Set-Content (Join-Path $portable "portable.txt") -Encoding utf8
Compress-Archive -Path (Join-Path $portable "*") -DestinationPath (Join-Path $out "RimForge-$version-portable.zip") -CompressionLevel Optimal

Write-Host "Installer..." -ForegroundColor Cyan
& $iscc "/Q" "/DAppVersion=$version" "/DSourceExe=$exe" "/DOutputDir=$out" (Join-Path $root "installer\RimForge.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Write-Host "Update feed..." -ForegroundColor Cyan
$feed = [ordered]@{
    version = $version
    url     = "https://github.com/$repo/releases/download/v$version/RimForge.exe"
    sha256  = (Get-FileHash $exe -Algorithm SHA256).Hash
    size    = (Get-Item $exe).Length
    notes   = $Notes
}
[IO.File]::WriteAllText((Join-Path $out "latest.json"), ($feed | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))  # no BOM

Get-ChildItem $out -File | Where-Object Name -ne "SHA256SUMS.txt" | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $out "SHA256SUMS.txt") -Encoding ascii

Remove-Item $portable -Recurse -Force
Write-Host ""
Write-Host "Done. Upload everything in $out to the GitHub release v$version." -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
