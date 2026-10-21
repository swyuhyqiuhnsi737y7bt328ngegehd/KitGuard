@echo off
setlocal
title KitGuard - 构建用户态守护程序

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [错误] 未找到 csc.exe，需要 .NET Framework 4.x
    pause
    exit /b 1
)

cd /d "%~dp0"
"%CSC%" /nologo /optimize+ /target:exe /out:KitGuard.exe /r:System.dll /r:System.Core.dll /codepage:65001 KitGuard.cs
if errorlevel 1 (
    echo [失败] 编译错误
    pause
    exit /b 1
)
echo [OK] 生成 KitGuard.exe
pause
