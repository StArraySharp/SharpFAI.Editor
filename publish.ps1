[CmdletBinding()]
param(
    [string[]]$Source
)

$ErrorActionPreference = 'Stop'
$publishDirectory = Join-Path $PSScriptRoot 'publish'
$artifactsDirectory = Join-Path $PSScriptRoot 'SharpFAI.Editor/obj/single-file-publish'
$projects = [ordered]@{
    'SharpFAI.Editor' = 'SharpFAI.Editor/SharpFAI.Editor.csproj'
    'PrepareGameAssets' = 'build/PrepareGameAssets/PrepareGameAssets.csproj'
}

$publishArguments = @(
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
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
    foreach ($name in $projects.Keys) {
        $project = Join-Path $PSScriptRoot $projects[$name]
        $bundleDirectory = Join-Path $artifactsDirectory "bundle/$name"
        & dotnet publish $project --output $bundleDirectory @publishArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $name (exit code $LASTEXITCODE)."
        }

        $stagedExe = Join-Path $bundleDirectory "$name.exe"
        if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
            throw "dotnet publish did not produce $stagedExe."
        }
    }

    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    foreach ($name in $projects.Keys) {
        $stagedExe = Join-Path $artifactsDirectory "bundle/$name/$name.exe"
        $publishedExe = Join-Path $publishDirectory "$name.exe"
        try {
            [IO.File]::Move($stagedExe, $publishedExe, $true)
        } catch {
            throw "Could not replace $publishedExe. Close the running application and try again. $($_.Exception.Message)"
        }
        Write-Host "Published $publishedExe"
    }
} finally {
    Pop-Location
}
