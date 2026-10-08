param(
    [string]$Dotnet = '',
    [string]$DalamudLibPath = (Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev')
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not $Dotnet) {
    $taskSdk = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\..\work\dotnet\dotnet.exe'))
    if (Test-Path -LiteralPath $taskSdk) { $Dotnet = $taskSdk }
    else { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.build-home\packages'
$nugetConfig = Join-Path $projectRoot 'NuGet.Config'
if (-not (Test-Path -LiteralPath (Join-Path $DalamudLibPath 'Dalamud.dll'))) { throw '找不到 Dalamud.dll，请通过 -DalamudLibPath 指定国服 Hooks\dev 路径。' }
& $Dotnet build (Join-Path $projectRoot 'AetherChat.Plugin') -c Release "-p:DalamudLibPath=$DalamudLibPath" "-p:RestoreConfigFile=$nugetConfig"
if ($LASTEXITCODE -ne 0) { throw '插件编译失败。' }
& $Dotnet run --project (Join-Path $projectRoot 'AetherChat.Tests') -c Release "-p:RestoreConfigFile=$nugetConfig"
if ($LASTEXITCODE -ne 0) { throw '验证测试未通过，不生成发布包。' }
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$dist = Join-Path $projectRoot "dist\$version"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$bin = Join-Path $projectRoot 'AetherChat.Plugin\bin\Release\net10.0-windows'
$files = @('AetherChat.dll', 'AetherChat.Core.dll', 'AetherChat.deps.json', 'AetherChat.json')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $bin $name) -Destination (Join-Path $dist $name) -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $dist 'README.md') -Force
Compress-Archive -LiteralPath (($files + 'README.md') | ForEach-Object { Join-Path $dist $_ }) -DestinationPath (Join-Path $dist "AetherChat-$version.zip") -Force
Write-Host "构建与验证通过。开发插件路径：$(Join-Path $dist 'AetherChat.dll')"
