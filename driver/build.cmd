@echo off
setlocal
set "BASE=%~dp0"
set "VC=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207"
set "WDKINC=C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0"
set "WDKLIB=C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0"

if not exist "%VC%\bin\Hostx64\x64\cl.exe" (
  echo [错误] 未找到 MSVC，请修改脚本里的 VC 路径
  pause & exit /b 1
)
if not exist "%WDKINC%\km" (
  echo [错误] 未找到 WDK，请安装 Windows Driver Kit 10
  pause & exit /b 1
)

set "CL=%VC%\bin\Hostx64\x64\cl.exe"
set "LNK=%VC%\bin\Hostx64\x64\link.exe"
set "INCLUDE=%WDKINC%\km;%WDKINC%\km\crt;%WDKINC%\shared;%VC%\include"
set "LIB=%WDKLIB%\km\x64;%WDKLIB%\km\x64\crt;%VC%\lib\x64"

cd /d "%BASE%"
"%CL%" /nologo /c /utf-8 /kernel /GS- /W3 /D_WIN64 /D_AMD64_ /DAMD64 /D_WIN32_WINNT=0x0A00 /DNTDDI_VERSION=0x0A000000 /DWINVER=0x0A00 KitGuardDrv.c
if errorlevel 1 (echo [失败] 编译错误 & pause & exit /b 1)

"%LNK%" /nologo /DRIVER /SUBSYSTEM:NATIVE /ENTRY:DriverEntry /MACHINE:X64 /OUT:KitGuardDrv.sys KitGuardDrv.obj ntoskrnl.lib hal.lib BufferOverflowK.lib
if errorlevel 1 (echo [失败] 链接错误 & pause & exit /b 2)

echo [OK] 生成 KitGuardDrv.sys
powershell -NoProfile -ExecutionPolicy Bypass -File "%BASE%sign.ps1"
if errorlevel 1 (echo [失败] 签名失败 & pause & exit /b 3)
echo [OK] 已签名
pause
