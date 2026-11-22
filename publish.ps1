[CmdletBinding()]
param(
    [string[]]$Source
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SharpFAI.Player/SharpFAI.Player.csproj'
$publishDirectory = Join-Path $PSScriptRoot 'publish'
$artifactsDirectory = Join-Path $PSScriptRoot 'SharpFAI.Player/obj/single-file-publish'
$bundleDirectory = Join-Path $artifactsDirectory 'bundle'
$stagedExe = Join-Path $bundleDirectory 'SharpFAI.Player.exe'
$publishedExe = Join-Path $publishDirectory 'SharpFAI.Player.exe'

$publishArguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $bundleDirectory,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:DebugType=embedded',
    '-p:GeneratePackageOnBuild=false',
    '-p:EnableSingleFileAnalyzer=false',
    '-p:UseArtifactsOutput=true',
    "-p:ArtifactsPath=$artifactsDirectory"
)

foreach ($packageSource in $Source) {
    $publishArguments += @('--source', $packageSource)
}

Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit code $LASTEXITCODE)."
    }

    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "dotnet publish did not produce $stagedExe."
    }

    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $publishedExe -PathType Leaf) {
        [IO.File]::Replace($stagedExe, $publishedExe, [NullString]::Value)
    } else {
        [IO.File]::Move($stagedExe, $publishedExe)
    }
    Write-Host "Published $publishedExe"
} finally {
    Pop-Location
}
