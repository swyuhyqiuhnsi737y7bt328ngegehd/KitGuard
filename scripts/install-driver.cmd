@echo off
setlocal
title KitGuard - 安装内核驱动

net session >nul 2>&1
if errorlevel 1 (
    echo [错误] 需要以管理员身份运行此脚本。
    pause
    exit /b 1
)

set "BASE=%~dp0.."
set "SYS=%BASE%\driver\KitGuardDrv.sys"
set "CER=%BASE%\driver\KitGuardTest.cer"

if not exist "%SYS%" (
    echo [错误] 未找到 %SYS%
    echo 请先运行 driver\build.cmd 编译并签名。
    pause
    exit /b 1
)
if not exist "%CER%" (
    echo [错误] 未找到测试证书 %CER%
    pause
    exit /b 1
)

echo [1/4] 导入测试证书到 受信任的根证书颁发机构 / 受信任的发布者...
certutil -addstore -f Root "%CER%"
certutil -addstore -f TrustedPublisher "%CER%"

echo [2/4] 创建驱动服务...
sc create KitGuardDrv type= kernel start= demand binPath= "%SYS%" DisplayName= "KitGuard Kernel Driver"

echo [3/4] 启动驱动...
sc start KitGuardDrv

echo [4/4] 状态：
sc query KitGuardDrv
echo.
echo 查看保护状态：reg query HKLM\SOFTWARE\KitGuardDrv
pause
