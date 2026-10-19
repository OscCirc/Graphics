param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputPath) { $OutputPath = Join-Path $repositoryRoot 'artifacts/OctahedralProbeNative' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force $OutputPath | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudioRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudioRoot) { throw 'MSVC C++ build tools are required.' }
$toolset = Get-ChildItem -LiteralPath (Join-Path $visualStudioRoot 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um/d3d11.h') } | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
$previousPath = $env:PATH
try {
    $env:PATH = (Split-Path $compiler) + ';' + $previousPath
    $includes = @('/I' + (Join-Path $toolset.FullName 'include'))
    foreach ($part in @('shared','um','ucrt','winrt')) { $includes += '/I' + (Join-Path $sdkVersion.FullName $part) }
    $libraries = @('/LIBPATH:' + (Join-Path $toolset.FullName 'lib/x64'))
    foreach ($part in @('um','ucrt')) { $libraries += '/LIBPATH:' + (Join-Path $sdkRoot "Lib/$($sdkVersion.Name)/$part/x64") }
    $executable = Join-Path $OutputPath 'NativeGpuTests.exe'
    & $compiler /nologo /std:c++17 /EHsc /O2 @includes (Join-Path $PSScriptRoot 'NativeGpuTests.cpp') ('/Fo' + (Join-Path $OutputPath 'NativeGpuTests.obj')) ('/Fe' + $executable) /link @libraries
    if ($LASTEXITCODE -ne 0) { throw 'Native GPU harness compilation failed.' }
    $shader = Join-Path $repositoryRoot 'Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbe/SharedOctahedralProbe.compute'
    & $executable $shader $OutputPath
    if ($LASTEXITCODE -ne 0) { throw "Native GPU tests failed. See $OutputPath" }
}
finally { $env:PATH = $previousPath }
