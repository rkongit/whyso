# Installs the latest Whyso release for Windows (x64).
#
#   irm https://raw.githubusercontent.com/rkongit/whyso/main/install.ps1 | iex
#
# Optional environment variables:
#   WHYSO_VERSION      version to install, e.g. 0.2.0 (default: latest release)
#   WHYSO_INSTALL_DIR  where to put the binary (default: %LOCALAPPDATA%\Programs\whyso)
#   WHYSO_REPO         GitHub repo to download from (default: rkongit/whyso)
#   WHYSO_BASE_URL     download from this URL instead of the GitHub release (mirrors, testing)
#   WHYSO_NO_MODIFY_PATH  set to 1 to leave your PATH alone
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = if ($env:WHYSO_REPO) { $env:WHYSO_REPO } else { 'rkongit/whyso' }
$installDir = if ($env:WHYSO_INSTALL_DIR) { $env:WHYSO_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\whyso' }

if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -and $env:PROCESSOR_ARCHITEW6432 -ne 'AMD64') {
    throw "No prebuilt Whyso binary for $($env:PROCESSOR_ARCHITECTURE); build from source (see README)."
}

$version = $env:WHYSO_VERSION
if (-not $version) {
    $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'whyso-install' }
    $version = $latest.tag_name
    if (-not $version) { throw "No published release found for $repo." }
}
$version = $version -replace '^v', ''

$archive = "whyso-$version-win-x64.zip"
$base = if ($env:WHYSO_BASE_URL) { $env:WHYSO_BASE_URL.TrimEnd('/') } else { "https://github.com/$repo/releases/download/v$version" }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("whyso-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    Write-Host "Downloading $archive"
    $zip = Join-Path $tmp $archive
    $sums = Join-Path $tmp 'SHA256SUMS.txt'
    Invoke-WebRequest -Uri "$base/$archive" -OutFile $zip -UseBasicParsing
    Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -OutFile $sums -UseBasicParsing

    $expected = $null
    foreach ($line in Get-Content $sums) {
        $parts = $line -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $archive) { $expected = $parts[0] }
    }
    if (-not $expected) { throw "$archive is not listed in SHA256SUMS.txt." }
    $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Checksum mismatch for $archive (expected $expected, got $actual)." }

    $extract = Join-Path $tmp 'extract'
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $exe = Join-Path $extract 'whyso.exe'
    if (-not (Test-Path $exe)) { throw 'Archive does not contain whyso.exe.' }

    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -Path $exe -Destination (Join-Path $installDir 'whyso.exe') -Force
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$onPath = $false
if ($userPath) { $onPath = ($userPath -split ';') -contains $installDir }
if (-not $onPath -and $env:WHYSO_NO_MODIFY_PATH -ne '1') {
    $newPath = if ($userPath) { "$($userPath.TrimEnd(';'));$installDir" } else { $installDir }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    $env:Path = "$env:Path;$installDir"
    Write-Host "Added $installDir to your user PATH (new terminals pick it up)."
}

Write-Host "Installed whyso $version to $installDir\whyso.exe"
Write-Host "Next: run 'whyso init' inside a Git project."
