# Build validation (completion spec v1.1 §10.3): the published files of one environment must not point at the other.
# A prod build may not contain dev endpoint URLs; a dev build may not contain prod endpoint URLs. Strings are searched as
# UTF-8 and UTF-16 (how .NET stores literals), in every .exe and .dll (single-file bundles included).
#   ./scripts/check-environment.ps1 -Environment prod -Path artifacts\win-x64
param(
    [Parameter(Mandatory)][ValidateSet("prod", "dev")][string]$Environment,
    [Parameter(Mandatory)][string]$Path
)
$ErrorActionPreference = "Stop"
$forbidden = if ($Environment -eq "prod") {
    @("https://api.alondra1.dev", "https://config.alondra1.dev", "https://config2.alondra1.dev", "https://app.alondra1.dev")
} else {
    @("https://api.alondra1.com", "https://config.alondra1.com", "https://config2.alondra1.com", "https://app.alondra1.com")
}
$required = if ($Environment -eq "prod") { "https://api.alondra1.com" } else { "https://api.alondra1.dev" }
$files = Get-ChildItem -Path $Path -Recurse -Include *.exe, *.dll -File
if (-not $files) { throw "No binaries found in $Path" }
$found = $false
$problems = @()
foreach ($file in $files) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    # UTF-16 literals can start at an odd byte offset (the metadata string heap isn't aligned): read from both.
    $texts = @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1))
    foreach ($text in $texts) {
        foreach ($bad in $forbidden) {
            if ($text.Contains($bad)) { $problems += "$($file.Name) contains $bad" }
        }
        if ($text.Contains($required)) { $found = $true }
    }
}
if (-not $found) { $problems += "no binary contains the $Environment API origin $required (wrong build?)" }
if ($problems) {
    $problems | Sort-Object -Unique | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    throw "The $Environment build contains endpoints of the other environment."
}
Write-Host "ok: $($files.Count) binaries belong to $Environment"
