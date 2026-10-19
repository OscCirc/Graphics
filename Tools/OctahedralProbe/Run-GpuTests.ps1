param(
    [string]$EditorPath = 'C:/Program Files/Tuanjie 2022.3.62t15/Editor/Tuanjie.exe',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputPath) { $OutputPath = Join-Path $repositoryRoot 'artifacts/OctahedralProbe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$testProjectRoot = Join-Path $OutputPath 'Project'
$marker = Join-Path $testProjectRoot '.oct-probe-test-project'
if ((Test-Path -LiteralPath $testProjectRoot) -and -not (Test-Path -LiteralPath $marker)) {
    throw "Refusing to write into an existing project without the test marker: $testProjectRoot"
}
if (-not (Test-Path -LiteralPath $EditorPath)) { throw "Editor executable not found: $EditorPath" }
$sampleRoot = Join-Path $repositoryRoot 'Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbe'
$assetRoot = Join-Path $testProjectRoot 'Assets/SharedOctahedralProbe'
$editorRoot = Join-Path $testProjectRoot 'Assets/Editor'
$packageRoot = Join-Path $testProjectRoot 'Packages'
New-Item -ItemType Directory -Force $assetRoot, $editorRoot, $packageRoot | Out-Null
Set-Content -LiteralPath $marker -Value 'Generated test project; contains copied prototype sources only.'
foreach ($name in @('SharedOctahedralProbeAtlas.cs', 'SharedOctahedralProbeExample.cs', 'SharedOctahedralProbe.compute', 'SharedOctahedralProbeSampling.hlsl')) {
    Copy-Item -LiteralPath (Join-Path $sampleRoot $name) -Destination $assetRoot -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Editor/SharedOctahedralProbeGpuTests.cs') -Destination $editorRoot -Force
Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Value '{"dependencies":{"com.unity.modules.imageconversion":"1.0.0","com.unity.modules.physics":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
$resultsRoot = Join-Path $OutputPath 'Results'
New-Item -ItemType Directory -Force $resultsRoot | Out-Null
$logPath = Join-Path $OutputPath 'Editor.log'
$arguments = @('-batchmode', '-force-d3d11', '-projectPath', ('"' + $testProjectRoot + '"'),
    '-executeMethod', 'SharedOctahedralProbeGpuTests.Run', '-logFile', ('"' + $logPath + '"'),
    '-octTestOutput', ('"' + $resultsRoot + '"'))
$process = Start-Process -FilePath $EditorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "GPU test Editor PID: $($process.Id)"
if (-not $process.WaitForExit(600000)) {
    Stop-Process -Id $process.Id
    throw "GPU tests exceeded ten minutes. See $logPath"
}
$process.Refresh()
if ($process.ExitCode -ne 0) {
    # Keep full licensing/machine diagnostics local rather than dumping the Editor log into a public CI console.
    throw "GPU tests failed with exit code $($process.ExitCode). See $logPath"
}
Get-Content -LiteralPath (Join-Path $resultsRoot 'results.json')
