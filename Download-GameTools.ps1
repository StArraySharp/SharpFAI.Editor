#Requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$toolsDirectory = Join-Path $PSScriptRoot 'tools'
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'SharpFAI-ToolBootstrap'
}

function Install-ReleaseTool {
    param([string]$Name, [string]$Executable)

    $repository = "StArraySharp/$Name"
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers $headers
    $tag = $release.tag_name
    if ($tag -notmatch '^v?[0-9](?:[A-Za-z0-9._+-]*[A-Za-z0-9])?$') {
        throw "Unexpected release tag for ${repository}: $tag"
    }

    $assets = @($release.assets | Where-Object { $_.name -match '-(?:windows|win)-x64\.zip$' })
    if ($assets.Count -ne 1) {
        throw "Expected one Windows x64 ZIP for $repository $tag, found $($assets.Count)."
    }
    $asset = $assets[0]
    if ($asset.digest -notmatch '^sha256:([a-f0-9]{64})$') {
        throw "No valid SHA-256 digest on $($asset.name)."
    }
    $expectedHash = $Matches[1]
    if (-not $asset.browser_download_url.StartsWith("https://github.com/$repository/releases/download/", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected download URL for $($asset.name)."
    }

    $toolDirectory = Join-Path $toolsDirectory $Name
    $destination = Join-Path $toolDirectory $tag
    $marker = '.archive-sha256'
    if (Test-Path -LiteralPath $destination) {
        $cachedHash = Join-Path $destination $marker
        if ((Test-Path -LiteralPath $cachedHash -PathType Leaf) -and
            (Get-Content -LiteralPath $cachedHash -Raw).Trim() -eq $expectedHash -and
            (Get-ChildItem -LiteralPath $destination -Recurse -File -Filter $Executable | Select-Object -First 1)) {
            Write-Host "Already extracted: $Name $tag -> $destination"
            return
        }
        throw "Existing directory does not match the release: $destination. Move it aside before retrying."
    }

    [IO.Directory]::CreateDirectory($toolDirectory) | Out-Null
    $staging = Join-Path $toolDirectory ('.download-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    try {
        $archive = Join-Path $staging 'release.zip'
        $extracted = Join-Path $staging 'extracted'
        Write-Host "Downloading $Name $tag ($($asset.name))..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
            throw "SHA-256 mismatch for $($asset.name)."
        }

        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
        if (-not (Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter $Executable | Select-Object -First 1)) {
            throw "Expected $Executable in $($asset.name)."
        }
        Set-Content -LiteralPath (Join-Path $extracted $marker) -Value $expectedHash
        [IO.Directory]::Move($extracted, $destination)
        Write-Host "Extracted: $Name $tag -> $destination"
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Install-ReleaseTool -Name 'HLSLcc' -Executable 'hlslcc.exe'
Install-ReleaseTool -Name 'AssetRipperPatcher' -Executable 'AssetRipperPatcher.exe'
