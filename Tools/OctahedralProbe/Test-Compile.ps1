param(
    [string]$EditorRoot = 'C:/Program Files/Tuanjie 2022.3.62t15/Editor',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputPath) { $OutputPath = Join-Path $repositoryRoot 'artifacts/OctahedralProbeCompile' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force $OutputPath | Out-Null
$engineRoot = Join-Path $EditorRoot 'Data/Managed/UnityEngine'
$sampleRoot = Join-Path $repositoryRoot 'Packages/com.unity.render-pipelines.high-definition/Samples~/SharedOctahedralProbe'
$references = @('UnityEngine.CoreModule','UnityEngine.ImageConversionModule','UnityEngine.PhysicsModule','UnityEngine.JSONSerializeModule','UnityEditor.CoreModule') | ForEach-Object {
    '<Reference Include="' + $_ + '"><HintPath>' + [Security.SecurityElement]::Escape((Join-Path $engineRoot ($_ + '.dll'))) + '</HintPath></Reference>'
}
$sources = Get-ChildItem -LiteralPath $sampleRoot -Filter '*.cs' | ForEach-Object {
    '<Compile Include="' + [Security.SecurityElement]::Escape($_.FullName) + '" />'
}
$sources += '<Compile Include="' + [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'Editor/SharedOctahedralProbeGpuTests.cs')) + '" />'
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><LangVersion>9</LangVersion></PropertyGroup><ItemGroup>' + ($references -join '') + ($sources -join '') + '</ItemGroup></Project>'
$projectPath = Join-Path $OutputPath 'Compile.csproj'
Set-Content -LiteralPath $projectPath -Value $project
& dotnet build $projectPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'C# compilation against the installed Editor assemblies failed.' }
