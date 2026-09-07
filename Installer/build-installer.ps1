param(
    [string]$IsccPath = 'ISCC.exe',
    [string]$NuGetSource
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command $IsccPath -ErrorAction SilentlyContinue)) { throw 'Inno Setup compiler not found. Supply -IsccPath.' }
# 复用独立发行流程，包含运行时、许可证和在线更新所需的文件清单。
$releaseArguments = @{}
if ($NuGetSource) { $releaseArguments.NuGetSource = $NuGetSource }
& (Join-Path $projectRoot 'Scripts\build-release.ps1') @releaseArguments
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'WinCalendar.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$stage = Join-Path $projectRoot 'publish\release-stage'
$output = Join-Path $projectRoot 'publish\installer'
& $IsccPath "/DAppVersion=$version" "/DPublishDir=$stage" "/DOutputDir=$output" (Join-Path $PSScriptRoot 'WinCalendar.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$name = "WinCalendar-Setup-$version-x64.exe"
$hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $name`n", [Text.UTF8Encoding]::new($false))
Write-Output "Installer: $(Join-Path $output $name)"
