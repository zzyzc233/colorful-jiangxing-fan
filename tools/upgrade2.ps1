$log = 'D:\x15-fan-control\logs\upgrade-out.txt'
Start-Transcript -Path $log -Force | Out-Null
try {
    $ErrorActionPreference = 'Continue'
    $appDir = 'D:\x15-fan-control\app'
    $pubDir = 'D:\x15-fan-control\src\FanSilencer\bin\Release\net8.0-windows\win-x64\publish'

    # 1) 优雅退出旧实例（WM_QUIT → 复原 CC3.0 快照）；10 秒不退则强杀
    foreach ($p in (Get-Process -Name FanSilencer -ErrorAction SilentlyContinue)) {
        Write-Output "graceful exit request -> pid $($p.Id)"
        & powershell -NoProfile -ExecutionPolicy Bypass -File 'D:\x15-fan-control\tools\post-quit.ps1' $p.Id
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Name FanSilencer -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    foreach ($p in (Get-Process -Name FanSilencer -ErrorAction SilentlyContinue)) {
        Write-Output "force kill pid $($p.Id)"
        $p.Kill()
    }
    Start-Sleep -Milliseconds 1000

    # 2) 逐文件强拷部署（保留 config.json / curve.json / logs）
    $copied = 0
    Get-ChildItem -LiteralPath $pubDir -File | ForEach-Object {
        if ($_.Name -in @('config.json', 'curve.json')) { return }
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $appDir $_.Name) -Force
        $copied++
    }
    Write-Output "copied $copied files"

    # 3) 校验关键文件确实换新
    $srcDll = Get-Item (Join-Path $pubDir 'FanSilencer.dll')
    $dstDll = Get-Item (Join-Path $appDir 'FanSilencer.dll')
    Write-Output ("dll src={0} bytes [{1}]  dst={2} bytes [{3}]" -f $srcDll.Length, $srcDll.LastWriteTime, $dstDll.Length, $dstDll.LastWriteTime)
    if ($srcDll.Length -ne $dstDll.Length) {
        Write-Output "VERIFY FAIL - not starting"
        exit 5
    }

    # 4) 启动新版
    Start-Process -FilePath (Join-Path $appDir 'FanSilencer.exe')
    Write-Output "started new instance"
    Start-Sleep -Seconds 2
    Get-Process -Name FanSilencer -ErrorAction SilentlyContinue | ForEach-Object { Write-Output "running pid $($_.Id)" }
    Write-Output "upgrade2 done"
}
finally {
    Stop-Transcript | Out-Null
}
