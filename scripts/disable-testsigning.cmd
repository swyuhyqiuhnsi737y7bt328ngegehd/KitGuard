@echo off
setlocal
title KitGuard - 恢复测试模式 / 驱动签名强制

net session >nul 2>&1
if errorlevel 1 (
    echo [错误] 需要以管理员身份运行此脚本。
    pause
    exit /b 1
)

echo 正在恢复默认设置...
bcdedit /set testsigning off
bcdedit /set nointegritychecks off
bcdedit /deletevalue loadoptions >nul 2>&1

echo.
echo 完成，重启后生效。
pause
