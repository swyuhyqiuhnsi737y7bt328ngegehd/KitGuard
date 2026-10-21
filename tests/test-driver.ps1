$ErrorActionPreference = 'Continue'
$logPath = Join-Path $env:TEMP 'kitguard_test.log'
function L($m) { $s = (Get-Date -Format 'HH:mm:ss') + ' ' + $m; Add-Content -Path $logPath -Value $s; Write-Host $s }

L '==== KitGuard 驱动自检开始 ===='
$assoc = 'Registry::HKEY_CLASSES_ROOT\exefile\shell\open\command'
$orig = $null
try { $orig = (Get-ItemProperty -Path $assoc -ErrorAction Stop).'(default)' } catch { }
L ('原始 exefile 命令 = [' + $orig + ']')

$tmpExe = Join-Path $env:TEMP 'SuperKiller.exe'

try {
    L ('服务状态: ' + ((sc.exe query KitGuardDrv | Out-String).Trim()))
    L ('驱动状态: ' + ((reg.exe query 'HKLM\SOFTWARE\KitGuardDrv' 2>&1 | Out-String).Trim()))

    $o1 = cmd /c 'reg add "HKCR\exefile\shell\open\command" /ve /d "calc.exe" /f 2>&1'
    $c1 = $LASTEXITCODE
    $v1 = (Get-ItemProperty -Path $assoc -ErrorAction SilentlyContinue).'(default)'
    L ('TEST1 关联劫持: exit=' + $c1 + ' value=[' + $v1 + '] out=' + (($o1 | Out-String).Trim()))

    $o2 = cmd /c 'reg add "HKCR\.kgtest" /ve /d "ok" /f 2>&1'
    L ('TEST2 非保护键写入: exit=' + $LASTEXITCODE + ' out=' + (($o2 | Out-String).Trim()))

    Copy-Item -Path (Join-Path $env:SystemRoot 'System32\notepad.exe') -Destination $tmpExe -Force
    Start-Process $tmpExe
    Start-Sleep -Seconds 2
    $p = Get-Process SuperKiller -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        $o3 = cmd /c 'taskkill /F /IM SuperKiller.exe 2>&1'
        Start-Sleep -Milliseconds 800
        $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
        L ('TEST3 杀受保护进程: exit=' + $LASTEXITCODE + ' 存活=' + [bool]$alive + ' out=' + (($o3 | Out-String).Trim()))
    } else {
        L 'TEST3 未能启动测试进程（跳过）'
    }
}
finally {
    L '---- 清理 ----'
    $null = sc.exe stop KitGuardDrv 2>&1
    Start-Sleep -Seconds 2
    if ($orig) {
        try { Set-ItemProperty -Path $assoc -Name '(default)' -Value $orig -ErrorAction Stop; L '关联已恢复' }
        catch { L ('关联恢复失败: ' + $_.Exception.Message) }
    }
    Get-Process SuperKiller -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $tmpExe -Force -ErrorAction SilentlyContinue
    $null = cmd /c 'reg delete "HKCR\.kgtest" /f 2>&1'
    L '完成。日志: ' + $logPath
}
