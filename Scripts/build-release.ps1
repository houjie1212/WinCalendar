param(
    [string]$NuGetSource
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK is required.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'WinCalendar.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric components.' }
$publishRoot = Join-Path $projectRoot 'publish'
$stage = [IO.Path]::GetFullPath((Join-Path $publishRoot 'release-stage'))
if ($stage -ne [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish\release-stage'))) { throw 'Unsafe staging path.' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$output = Join-Path $publishRoot 'releases'
New-Item -ItemType Directory -Path $output -Force | Out-Null
# 始终从干净目录发布，避免把用户缓存或历史构建文件写入发行包。
dotnet build (Join-Path $projectRoot 'Checks\Checks.csproj') --disable-build-servers -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Checks build failed.' }
dotnet (Join-Path $projectRoot 'Checks\bin\Debug\net8.0-windows\Checks.dll')
if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
$arguments = @('publish', (Join-Path $projectRoot 'WinCalendar.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false', '--disable-build-servers', '-p:UseSharedCompilation=false', '-o', $stage)
if ($NuGetSource) { $arguments += @('--source', $NuGetSource) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stage }
Copy-Item -LiteralPath (Join-Path $projectRoot 'Licenses') -Destination $stage -Recurse
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/') } | Sort-Object)
[IO.File]::WriteAllText((Join-Path $stage 'release-files.json'), (ConvertTo-Json -InputObject $files), [Text.UTF8Encoding]::new($false))
$name = "WinCalendar-$version-win-x64.zip"
$zip = Join-Path $output $name
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $name`n", [Text.UTF8Encoding]::new($false))
Write-Output "Release files: $output"
