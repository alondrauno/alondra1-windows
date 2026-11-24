# Builds the self-contained app and the setup for one environment:
#   ./installer/publish.ps1                  -> artifacts\Alondra1-setup.exe      (prod)
#   ./installer/publish.ps1 -Environment dev -> artifacts\Alondra1-dev-setup.exe  (dev: own AppId, data folder, endpoints)
param([ValidateSet("prod", "dev")][string]$Environment = "prod")
$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $dotnet = Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe"
} else {
    $dotnet = $dotnet.Source
}
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Alondra1.App\Alondra1.App.csproj"
$out = Join-Path $root $(if ($Environment -eq "dev") { "artifacts\win-x64-dev" } else { "artifacts\win-x64" })
# Start clean so files from an older build are not packaged.
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
& $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:Alondra1Environment=$Environment" -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 is required to compile Alondra1-setup.exe." }
# The published files must belong to this environment before they are packaged.
& (Join-Path $root "scripts\check-environment.ps1") -Environment $Environment -Path $out
if ($Environment -eq "dev") { & $iscc /DDev (Join-Path $PSScriptRoot "alondra1.iss") } else { & $iscc (Join-Path $PSScriptRoot "alondra1.iss") }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$setup = if ($Environment -eq "dev") { "Alondra1-dev-setup.exe" } else { "Alondra1-setup.exe" }
Write-Host "Created $(Join-Path $root "artifacts\$setup")"
