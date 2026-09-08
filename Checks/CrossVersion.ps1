param([string]$NuGetSource)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$work = Join-Path $project ('publish/cross-version-checks/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
# 仅在隔离源码副本注入测试入口、数据目录和退出观测，不修改产品中的信任校验。
function Instrument([string]$source, [bool]$current) {
    $path = Join-Path $source 'App.cs'
    $text = [IO.File]::ReadAllText($path)
    $entry = @'
        Store.Root = Environment.GetEnvironmentVariable("WINCALENDAR_CROSS_ROOT") ?? throw new InvalidOperationException();
        if (args.Length == 2 && args[0] == "--cross-launch")
        {
            Store.Load();
            File.Copy(Store.PathFor("settings.json"), Store.PathFor("before.json"), true);
            string job = Path.Combine(UpdateService.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            UpdateService.VerifyHash(args[1], args[1] + ".sha256", Path.GetFileName(args[1]));
            UpdateService.Extract(args[1], Path.Combine(job, "payload"), new Version(1, 0, 2));
            UpdateInstaller.Launch(job).GetAwaiter().GetResult();
            return 0;
        }
        if (args.Contains("--update-failed")) { File.WriteAllText(Store.PathFor("rollback-started"), "yes"); return 0; }
'@
    $text = $text.Replace('        // 临时更新工作进程必须绕过主程序单实例互斥锁。', $entry + "`n        // 临时更新工作进程必须绕过主程序单实例互斥锁。")
    $text = $text.Replace('"Local\\WinCalendar"', '"Local\\WinCalendar.Cross." + Path.GetFileName(Store.Root)')
    if ($current) {
        $text = $text.Replace('            var window = new MainWindow', '            if (settings.Sources.Count != 1 || settings.Sources[0].Url != "https://example.com/private/TestToken?Key=AbC" || settings.Sources[0].Name != "合成订阅" || settings.Sources[0].Enabled || !settings.ShowChineseLunar) throw new InvalidDataException();' + [Environment]::NewLine + '            var window = new MainWindow')
        $text = $text.Replace('new MainWindow(settings, args.Contains("--preview"), deferStartup: updated)', 'new MainWindow(settings, true, snapshot: true, deferStartup: updated)')
        $text = $text.Replace('await UpdateInstaller.ConfirmStartup(args[1]);', 'if (Environment.GetEnvironmentVariable("WINCALENDAR_CROSS_FAIL_CONFIRM") == "1") throw new UpdateException("UpdateConfirmationFailed"); await UpdateInstaller.ConfirmStartup(args[1]);')
        $text = $text.Replace('await window.CompleteStartup();', 'await window.CompleteStartup(); File.WriteAllText(Store.PathFor("initialized"), "yes"); window.Cleanup(); app.Shutdown();')
        $text = $text.Replace('MessageBox.Show(L.T(key), "WinCalendar", MessageBoxButton.OK, MessageBoxImage.Error);', 'File.WriteAllText(Store.PathFor("startup-error"), key);')
        $text = $text.Replace('            app.Run();', '            if (!updated) app.Dispatcher.BeginInvoke(() => { File.WriteAllText(Store.PathFor("restarted"), "yes"); window.Cleanup(); app.Shutdown(); });' + "`n            app.Run();")
        $text = $text.Replace('MessageBox.Show(e is SettingsStorageException storage ? L.T(storage.Key) : L.T("Fatal") + "\n" + e.GetType().Name, "WinCalendar", MessageBoxButton.OK, MessageBoxImage.Error);', 'File.WriteAllText(Store.PathFor("startup-error"), e.GetType().Name);')
    }
    if (!$current) {
        $workerPath = Join-Path $source 'UpdateInstaller.cs'
        $workerText = [IO.File]::ReadAllText($workerPath)
        # 故意延迟写入子进程信息，覆盖真实旧程序的时序窗口。
        $workerText = $workerText.Replace('j.ChildId = child.Id;', 'if (Environment.GetEnvironmentVariable("WINCALENDAR_CROSS_FAIL_CONFIRM") != "1") Thread.Sleep(350); j.ChildId = child.Id;')
        $workerText = $workerText.Replace('j.Phase = "complete"; WriteJournal(job, j);', 'if (Environment.GetEnvironmentVariable("WINCALENDAR_CROSS_TIMEOUT") == "1") Thread.Sleep(12000); j.Phase = "complete"; WriteJournal(job, j);')
        [IO.File]::WriteAllText($workerPath, $workerText)
    }
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
function PublishFixture([string]$source, [string]$output) {
    $argsList = @('publish', (Join-Path $source 'WinCalendar.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:DebugType=None', '-p:DebugSymbols=false', '--disable-build-servers', '-p:UseSharedCompilation=false', '-o', $output)
    if ($NuGetSource) { $argsList += @('--source', $NuGetSource) }
    & dotnet @argsList
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed' }
    $files = @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/') })
    [IO.File]::WriteAllText((Join-Path $output 'release-files.json'), (ConvertTo-Json -InputObject $files))
}
function RunFixture([string]$exe, [string[]]$arguments, [string]$data, [bool]$fail = $false, [bool]$timeout = $false) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = 'Hidden'
    $info.Environment['WINCALENDAR_CROSS_ROOT'] = $data
    $info.Environment['WINCALENDAR_CROSS_TIMEOUT'] = $(if ($timeout) { '1' } else { '0' })
    $info.Environment['WINCALENDAR_CROSS_FAIL_CONFIRM'] = $(if ($fail) { '1' } else { '0' })
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($info)
}
$currentSource = Join-Path $work 'current-source'
New-Item -ItemType Directory -Path $currentSource | Out-Null
$tracked = & git -C $project ls-files
foreach ($relative in $tracked) {
    $destination = Join-Path $currentSource $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $project $relative) -Destination $destination
}
Instrument $currentSource $true
$currentBin = Join-Path $work 'current-bin'
PublishFixture $currentSource $currentBin
$zip = Join-Path $work 'WinCalendar-1.0.2-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($currentBin, $zip)
[IO.File]::WriteAllText($zip + '.sha256', (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($zip))
foreach ($version in @(@('1.0.0', '7f6e2bc'), @('1.0.1', '9267aea'))) {
    $source = Join-Path $work ('source-' + $version[0])
    $archive = Join-Path $work ($version[0] + '.zip')
    & git -C $project archive --format=zip "--output=$archive" $version[1]
    if ($LASTEXITCODE -ne 0) { throw 'History export failed' }
    Expand-Archive -LiteralPath $archive -DestinationPath $source
    Instrument $source $false
    $oldBin = Join-Path $work ('bin-' + $version[0])
    PublishFixture $source $oldBin
    foreach ($mode in @(0, 1, 2)) {
        $fail = $mode -eq 1
        $timeout = $mode -eq 2
        $case = Join-Path $work ($version[0] + '-' + $mode)
        $installed = Join-Path $case 'installed with spaces'
        $data = Join-Path $case 'data'
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        Copy-Item -LiteralPath $oldBin -Destination $installed -Recurse
        $config = @{ Sources = @(@{ Id = [guid]::NewGuid().ToString('N'); Name = '合成订阅'; Url = 'https://example.com/private/TestToken?Key=AbC'; Enabled = $false; Color = '#123456'; Kind = 0 }); ShowChineseLunar = $true }
        [IO.File]::WriteAllText((Join-Path $data 'settings.json'), (ConvertTo-Json -InputObject $config -Depth 5))
        $cacheFile = Join-Path $data ($config.Sources[0].Id + '.ics')
        [IO.File]::WriteAllText($cacheFile, 'synthetic-cache')
        $parent = RunFixture (Join-Path $installed 'WinCalendar.exe') @('--cross-launch', $zip) $data $fail $timeout
        if (!$parent.WaitForExit(30000) -or $parent.ExitCode -ne 0) { throw "Launch failed: $case" }
        $parent.Dispose()
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            $journal = Get-ChildItem -LiteralPath (Join-Path $data 'Updates') -Filter journal.json -Recurse | Select-Object -First 1
            $stream = [IO.FileStream]::new($journal.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try { $reader = [IO.StreamReader]::new($stream); $record = $reader.ReadToEnd() | ConvertFrom-Json } finally { $stream.Dispose() }
            $marker = Join-Path $data $(if ($timeout) { 'startup-error' } elseif ($fail) { 'rollback-started' } else { 'initialized' })
            if (($record.Phase -eq $(if ($fail) { 'rolledback' } else { 'complete' })) -and (Test-Path -LiteralPath $marker)) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if (!(Test-Path -LiteralPath $marker)) { throw "Upgrade did not finish: $case" }
        if ($fail -or $timeout) {
            if ((Get-FileHash -LiteralPath (Join-Path $data 'settings.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $data 'before.json')).Hash) { throw 'Configuration changed before confirmation' }
        } else {
            $saved = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
            if ($saved.Format -ne 1 -or !$saved.Sources[0].ProtectedUrl -or $saved.Sources[0].Id -ne $config.Sources[0].Id) { throw 'Configuration migration failed' }
            Start-Sleep -Milliseconds 500
            $restart = RunFixture (Join-Path $installed 'WinCalendar.exe') @('--background') $data
            if (!$restart.WaitForExit(15000) -or $restart.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $data 'restarted'))) { throw 'Restart failed' }
            $restart.Dispose()
        }
        if ([IO.File]::ReadAllText($cacheFile) -ne 'synthetic-cache') { throw 'Cache changed' }
        Write-Output "PASS historical $($version[0]) -> 1.0.2; rollback=$fail; timeout=$timeout; settings/cache preserved"
    }
}
Write-Output "Cross-version fixtures: $work"
