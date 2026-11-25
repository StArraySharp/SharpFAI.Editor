[CmdletBinding()]
param(
    [string[]]$Source,
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string[]]$Runtime = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')
)

$ErrorActionPreference = 'Stop'
$publishDirectory = Join-Path $PSScriptRoot 'publish'
$artifactsDirectory = Join-Path $PSScriptRoot 'SharpFAI.Editor/obj/single-file-publish'
$editorProject = Join-Path $PSScriptRoot 'SharpFAI.Editor/SharpFAI.Editor.csproj'

$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup |
    ForEach-Object { $_.VersionPrefix } |
    Where-Object { $_ } |
    Select-Object -First 1
if (-not $version) {
    throw 'Directory.Build.props does not define VersionPrefix.'
}

$publishArguments = @(
    '--configuration', 'Release',
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

function Get-EditorFileName([string]$runtime) {
    $fileName = "SharpFAI.Editor-$runtime-$version"
    if ($runtime -eq 'win-x64') {
        return "$fileName.exe"
    }
    return $fileName
}

function Publish-SingleFile([string]$project, [string]$name, [string]$runtime) {
    $bundleDirectory = Join-Path $artifactsDirectory "bundle/$runtime/$name"
    & dotnet publish $project --runtime $runtime --output $bundleDirectory @publishArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $name ($runtime, exit code $LASTEXITCODE)."
    }

    $executableName = if ($runtime -eq 'win-x64') { "$name.exe" } else { $name }
    $stagedExe = Join-Path $bundleDirectory $executableName
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "dotnet publish did not produce $stagedExe."
    }
    return $stagedExe
}

function Move-PublishedFile([string]$stagedExe, [string]$fileName, [string]$runtime) {
    $publishedExe = Join-Path $publishDirectory $fileName
    try {
        [IO.File]::Move($stagedExe, $publishedExe, $true)
    } catch {
        throw "Could not replace $publishedExe. Close the running application and try again. $($_.Exception.Message)"
    }

    Write-Host "Published $publishedExe"
    return [pscustomobject]@{
        Path = $publishedExe
        FileName = $fileName
        Runtime = $runtime
    }
}

function Add-FileToArchive($archive, [string]$filePath, [string]$fileName, [bool]$executable) {
    $entry = $archive.CreateEntry($fileName, [IO.Compression.CompressionLevel]::Optimal)
    if ($executable) {
        # 0100755 (rwxr-xr-x) in the high 16 bits of the external attributes.
        $entry.ExternalAttributes = -2115174400
    }

    $entryStream = $entry.Open()
    try {
        $sourceStream = [IO.File]::OpenRead($filePath)
        try {
            $sourceStream.CopyTo($entryStream)
        } finally {
            $sourceStream.Dispose()
        }
    } finally {
        $entryStream.Dispose()
    }
}

Push-Location -LiteralPath $PSScriptRoot
try {
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    $publishedFiles = [System.Collections.Generic.List[object]]::new()
    foreach ($targetRuntime in $Runtime) {
        $stagedExe = Publish-SingleFile $editorProject 'SharpFAI.Editor' $targetRuntime
        $publishedFiles.Add((Move-PublishedFile $stagedExe (Get-EditorFileName $targetRuntime) $targetRuntime))
    }

    $zipPath = Join-Path $publishDirectory "SharpFAI.Editor-MultiPlatforms-$version.zip"
    $fontLicense = Join-Path $PSScriptRoot 'SharpFAI.Player/Resources/Fonts/NotoSansCJK-LICENSE.txt'
    Add-Type -AssemblyName System.IO.Compression
    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    try {
        $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $publishedFiles) {
                Add-FileToArchive $archive $file.Path $file.FileName ($file.Runtime -ne 'win-x64')
            }
            Add-FileToArchive $archive $fontLicense 'NotoSansCJK-LICENSE.txt' $false
        } finally {
            $archive.Dispose()
        }
    } finally {
        $zipStream.Dispose()
    }
    Write-Host "Published $zipPath"
} finally {
    Pop-Location
}
