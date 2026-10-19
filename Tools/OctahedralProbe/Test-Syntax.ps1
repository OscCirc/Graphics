param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputPath) { $OutputPath = Join-Path $repositoryRoot 'artifacts/OctahedralProbeSyntax' }
New-Item -ItemType Directory -Force $OutputPath | Out-Null
$roslyn = $PSHOME # Use the same Roslyn version already loaded by PowerShell's Add-Type.
Add-Type -Path (Join-Path $roslyn 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $roslyn 'Microsoft.CodeAnalysis.CSharp.dll')
$packageRoot = Join-Path $repositoryRoot 'Packages/com.unity.render-pipelines.high-definition'
$paths = @(
    'Runtime/Lighting/LightLoop/LightLoop.cs',
    'Runtime/Lighting/Reflection/HDProbe.cs',
    'Runtime/Lighting/Reflection/HDProbe.ExternalOctahedral.cs',
    'Runtime/Lighting/Reflection/OctahedralProbeSourceLayout.cs',
    'Runtime/Lighting/Reflection/ReflectionProbeTextureCache.cs',
    'Runtime/RenderPipeline/HDRenderPipeline.ExternalOctahedralProbe.cs',
    'Runtime/Settings/HDRenderPipelineRuntimeShaders.cs',
    'Samples~/SharedOctahedralProbe/SharedOctahedralProbeAtlas.cs',
    'Samples~/SharedOctahedralProbe/SharedOctahedralProbeExample.cs',
    'Samples~/SharedOctahedralProbeHDRPIntegration/SharedOctahedralProbeHDRPBridge.cs',
    'Tests/Editor/ExternalOctahedralProbeTests.cs'
)
$checks = @()
foreach ($configuration in @('Player','Editor')) {
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
    if ($configuration -eq 'Editor') { $options = $options.WithPreprocessorSymbols([string[]]@('UNITY_EDITOR')) }
    foreach ($relative in $paths) {
        $path = Join-Path $packageRoot $relative
        $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path), $options, $path, [Text.Encoding]::UTF8, [Threading.CancellationToken]::None)
        $errors = @($tree.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error } | ForEach-Object ToString)
        $checks += @{ path = $relative; configuration = $configuration; errors = $errors }
    }
}
$failed = @($checks | Where-Object { $_.errors.Count -gt 0 }).Count
@{ backend = 'Roslyn syntax only, not full HDRP type compilation'; passed = $checks.Count - $failed; failed = $failed; checks = $checks } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputPath 'syntax-results.json')
if ($failed) { $checks | Where-Object { $_.errors.Count -gt 0 } | ConvertTo-Json -Depth 6; throw 'C# syntax check failed.' }
Write-Output "$($checks.Count) Player/Editor C# syntax checks passed. This is not an HDRP assembly build."
