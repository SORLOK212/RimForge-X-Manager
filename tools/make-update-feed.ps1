# Writes publish\latest.json — what RimForge reads to offer itself an update.
# Run after publish.cmd, then upload RimForge.exe and latest.json to the release.
#   powershell -File tools\make-update-feed.ps1 -Url "https://…/RimForge.exe" -Notes "What changed"
param(
    [Parameter(Mandatory = $true)][string]$Url,
    [string]$Notes = ""
)
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "publish\RimForge.exe"
if (-not (Test-Path $exe)) { throw "Build first: publish\RimForge.exe is missing" }
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$feed = [ordered]@{
    version = $version
    url     = $Url
    sha256  = (Get-FileHash $exe -Algorithm SHA256).Hash
    size    = (Get-Item $exe).Length
    notes   = $Notes
}
$out = Join-Path $root "publish\latest.json"
$feed | ConvertTo-Json | Set-Content $out -Encoding utf8
Write-Host "Written $out (version $version)"
