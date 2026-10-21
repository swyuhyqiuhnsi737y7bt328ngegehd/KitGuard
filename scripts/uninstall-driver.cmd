@echo off
setlocal
title KitGuard - 卸载内核驱动

net session >nul 2>&1
if errorlevel 1 (
    echo [错误] 需要以管理员身份运行此脚本。
    pause
    exit /b 1
)

echo [1/3] 停止驱动...
sc stop KitGuardDrv

echo [2/3] 删除驱动服务...
sc delete KitGuardDrv

echo [3/3] 移除测试证书...
certutil -delstore Root "KitGuard Test"
certutil -delstore TrustedPublisher "KitGuard Test"

echo.
echo 完成。
pause
