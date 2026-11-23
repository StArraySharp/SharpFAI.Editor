param(
    [Parameter(Mandatory)][string]$ShaderRipperDirectory,
    [Parameter(Mandatory)][string]$ResourcesAsset,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
# Reuse the installed reader and Unity schemas; never alter the exporter or game.
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $ShaderRipperDirectory 'ShaderRipper.dll'))
$load = $assembly.GetType('ShaderRipper.Data.SerializedFile').GetMethods() | Where-Object {
    $_.Name -eq 'Load' -and $_.GetParameters()[0].ParameterType -eq [string]
}
$version = [Linq.Expressions.Expression]::Parameter([string], 'version')
$classId = [Linq.Expressions.Expression]::Parameter([int], 'classId')
$call = [Linq.Expressions.Expression]::Call($assembly.GetType('ShaderRipper.Data.TpkSchema').GetMethod('BuildClass'),
    [Linq.Expressions.Expression[]]@($classId, $version))
$provider = [Linq.Expressions.Expression]::Lambda($load.GetParameters()[1].ParameterType, $call,
    [Linq.Expressions.ParameterExpression[]]@($version, $classId)).Compile()
$asset = $load.Invoke($null, @($ResourcesAsset, $provider))
$shaders = @($asset.Shaders | Where-Object { $_.ParsedForm.Name -eq 'CameraFilterPackLegacy/Real_VHS' })
if ($shaders.Count -ne 1) { throw "Expected exactly one Legacy004 shader, got $($shaders.Count)." }
$shader = $shaders[0]
$slots = @(0..($shader.Platforms.Length - 1) | Where-Object { $shader.Platforms[$_] -eq 4 })
if ($slots.Count -ne 1) { throw 'Expected exactly one D3D11 slot.' }
$decode = $assembly.GetType('ShaderRipper.Data.BlobTool').GetMethod('DecodeBlob', [type[]]@($shader.GetType(), [int]))
[byte[]]$bytes = $decode.Invoke($null, @($shader.PSObject.BaseObject, [int]$slots[0]))
$candidates = @()
for ($offset = 0; $offset -lt $bytes.Length - 36; $offset++) {
    if ($bytes[$offset] -ne 68 -or $bytes[$offset + 1] -ne 88 -or $bytes[$offset + 2] -ne 66 -or $bytes[$offset + 3] -ne 67) { continue }
    $length = [BitConverter]::ToInt32($bytes, $offset + 24)
    $chunks = [BitConverter]::ToInt32($bytes, $offset + 28)
    if ($length -le 32 -or $offset + $length -gt $bytes.Length -or $chunks -le 0 -or $chunks -gt 100) { continue }
    for ($chunk = 0; $chunk -lt $chunks; $chunk++) {
        $start = $offset + [BitConverter]::ToInt32($bytes, $offset + 32 + $chunk * 4)
        $tag = [Text.Encoding]::ASCII.GetString($bytes, $start, 4)
        if ($tag -in 'SHEX', 'SHDR') {
            $token = [BitConverter]::ToUInt32($bytes, $start + 8)
            if (($token -shr 16) -eq 0) { $candidates += ,($bytes[$offset..($offset + $length - 1)]) }
        }
    }
}
if ($candidates.Count -ne 1) { throw "Expected one pixel shader, got $($candidates.Count)." }
[IO.File]::WriteAllBytes($OutputPath, [byte[]]$candidates[0])
Write-Output "Original Legacy004 ps bytecode: $OutputPath"
Get-FileHash -Algorithm SHA256 $OutputPath
