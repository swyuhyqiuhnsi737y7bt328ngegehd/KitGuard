@echo off
setlocal
title KitGuard - 开启测试模式 / 禁用驱动签名强制

net session >nul 2>&1
if errorlevel 1 (
    echo [错误] 需要以管理员身份运行此脚本。
    pause
    exit /b 1
)

echo ============================================================
echo  本操作会降低系统安全性，仅限隔离测试环境使用！
echo  将执行：
echo    1. bcdedit /set testsigning on
echo    2. bcdedit /set nointegritychecks on
echo    3. bcdedit /set loadoptions DISABLE_INTEGRITY_CHECKS
echo ============================================================
echo.
choice /c YN /m "确认继续吗"
if errorlevel 2 exit /b 0

echo.
echo [1/3] 开启测试签名模式...
bcdedit /set testsigning on
if errorlevel 1 (
    echo [失败] 无法设置 testsigning（可能被固件/安全启动限制）
    pause
    exit /b 1
)

echo [2/3] 关闭完整性检查...
bcdedit /set nointegritychecks on

echo [3/3] 禁用驱动签名强制校验...
bcdedit /set loadoptions DISABLE_INTEGRITY_CHECKS

echo.
echo ============================================================
echo  完成。必须重启后生效。
echo  重启后桌面右下角应显示“测试模式”。
echo  关闭/恢复请运行 disable-testsigning.cmd
echo ============================================================
pause
