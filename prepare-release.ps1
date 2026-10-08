param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$')]
    [string]$Repository,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$built = Join-Path $projectRoot "dist\$version"
$manifestPath = Join-Path $built 'AetherChat.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw '请先运行 build.ps1 构建并验证插件。' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$pluginVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $built 'AetherChat.dll')).Version.ToString()
$coreVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $built 'AetherChat.Core.dll')).Version.ToString()
if ($manifest.AssemblyVersion -ne $pluginVersion -or $coreVersion -ne $pluginVersion -or ([version]$pluginVersion).ToString(3) -ne $version) {
    throw '源码版本、DLL 或清单版本不一致，拒绝打包。请重新构建。'
}
if ($manifest.InternalName -ne 'AetherChat' -or $manifest.DalamudApiLevel -ne 15) { throw '插件名称或 API 级别不符合当前发布目标。' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "dist\publish\$version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$packageName = "AetherChat-$version.zip"
$stage = Join-Path $output 'package'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$files = @('AetherChat.dll', 'AetherChat.Core.dll', 'AetherChat.deps.json', 'AetherChat.json', 'README.md')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $built $name) -Destination (Join-Path $stage $name) -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
$repositoryUrl = "https://github.com/$Repository"
$manifest.RepoUrl = $repositoryUrl
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'AetherChat.json') -Encoding utf8NoBOM
$package = Join-Path $output $packageName
# Explicit file allowlist excludes caches, local settings, keys and Dalamud's own assemblies.
Compress-Archive -LiteralPath ($files | ForEach-Object { Join-Path $stage $_ }) -DestinationPath $package -Force
$download = "$repositoryUrl/releases/download/v$version/$packageName"
$entry = @{} + $manifest
$entry.IsHide = $false
$entry.IsTestingExclusive = $false
$entry.DownloadLinkInstall = $download
$entry.DownloadLinkUpdate = $download
$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
ConvertTo-Json -InputObject @($entry) -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'repo.json') -Encoding utf8NoBOM
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $packageName" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
$repoJsonUrl = "https://raw.githubusercontent.com/$Repository/main/repo.json"
@"
# AetherChat $version

国服 Dalamud API 15。支持本机 OpenAI 兼容接口、GPT、DeepSeek、Claude；按频道和关键词触发，由当前角色发言。

支持悄悄话、说话、小队（含跨服）、团队（联盟）和部队；各频道可独立开关。关键词留空时回复已开启频道中的他人消息。提供四组模型连接配置、人设提示词、上下文设置、冷却与限流、预览和命令开关。

## 通过第三方插件库安装

在游戏中打开 /xlsettings，在实验性设置的自定义插件库中添加：

$repoJsonUrl

保存后打开 /xlplugins，搜索 AetherChat 或灵语并安装，再输入 /aichat 配置。

从开发版迁移时先禁用旧开发插件，避免同时加载两份。首次使用需要自行配置模型服务；每个用户使用自己的 API Key 或本机接口。
"@ | Set-Content -LiteralPath (Join-Path $output 'RELEASE-NOTES.md') -Encoding utf8NoBOM
[pscustomobject]@{ Repository = $Repository; Tag = "v$version"; Package = $package; Manifest = (Join-Path $output 'repo.json'); RepositoryUrl = $repoJsonUrl; Sha256 = $hash }
