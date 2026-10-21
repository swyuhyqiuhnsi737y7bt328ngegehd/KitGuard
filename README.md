# KitGuard —— 实验性内核保护驱动

> ## 警告
> 这是**实验性质**的代码，仅用于**自己控制的隔离测试环境**（虚拟机 / 测试机）。
> 它会开启测试签名、关闭驱动签名强制校验、修改内核对象标志位，
> **会明显降低系统安全性**，可能导致蓝屏、与安全软件冲突、系统不稳定。
> 请勿在生产机、他人机器或联网环境中使用。使用风险自负。

## 这是什么

给 360 急救箱这类安全工具做的一层“护栏”，包含两部分：

1. usermode/KitGuard.cs —— 用户态守护（C#）：备份/秒级恢复急救箱文件、双进程互护、开机自启；
2. driver/KitGuardDrv.c —— 内核驱动（C）：拦截对文件关联键的劫持、阻止终止受保护进程。

本项目重点在内核驱动，用户态部分还在，但尚未移植成 C。

## 目录结构

    KitGuard/
    ├─ README.md                     本文件
    ├─ driver/
    │  ├─ KitGuardDrv.c              内核驱动源码（注册表回调 + 进程回调）
    │  ├─ build.cmd                  编译 + 签名（需要 WDK 10 + MSVC）
    │  └─ sign.ps1                   自签名证书 + signtool 签名
    ├─ usermode/
    │  ├─ KitGuard.cs                用户态守护源码（C#）
    │  └─ build.cmd                  用系统自带 csc 编译
    ├─ scripts/
    │  ├─ enable-testsigning.cmd     开启测试模式 + 禁用驱动签名强制（需重启）
    │  ├─ disable-testsigning.cmd    恢复默认
    │  ├─ install-driver.cmd         导入证书、创建并启动驱动服务
    │  └─ uninstall-driver.cmd       停止/删除驱动服务、移除证书
    ├─ tests/
    │  └─ test-driver.ps1            驱动自检（劫持拦截 / 进程保护 / 恢复）
    └─ docs/
       └─ ob-bypass-notes.md         Ob 回调注册失败的原理分析与实测记录

## 快速开始

前提：Windows 10/11 x64、安装 WDK 10 与 Visual Studio Build Tools（cl.exe）、管理员权限。

第 1 步：开启测试模式并禁用驱动签名强制（管理员运行，完成后重启）

    scripts\enable-testsigning.cmd

重启后右下角应出现“测试模式”水印。

第 2 步：编译并签名驱动

    driver\build.cmd

若 MSVC 或 WDK 路径不同，修改 build.cmd 顶部的 VC / WDKINC / WDKLIB 变量。

第 3 步：安装驱动（管理员）

    scripts\install-driver.cmd

第 4 步：自检（管理员 PowerShell）

    powershell -ExecutionPolicy Bypass -File tests\test-driver.ps1

预期结果：

    TEST1 关联劫持: exit=1 value=["%1" %*]        <- 被拒绝
    TEST2 非保护键写入: exit=0                    <- 正常放行
    TEST3 杀受保护进程: exit=1 存活=True          <- 杀不掉

第 5 步：卸载（管理员）

    scripts\uninstall-driver.cmd

恢复系统设置（管理员，完成后重启）：

    scripts\disable-testsigning.cmd

## 已验证的能力

2026-10-21，在 VMware 的 Windows 10 19045 测试模式虚拟机中实测：

    sc query KitGuardDrv        -> STATE: 4 RUNNING
    HKLM\SOFTWARE\KitGuardDrv   -> RegStatus=0x0 ObStatus=0x0
    TEST1 改 .exe 关联          -> 拒绝访问，注册表值保持不变
    TEST3 taskkill 受保护进程   -> 拒绝访问，进程存活
    sc stop 后再 taskkill       -> 成功（对照验证）

## 工作原理

### 1. 注册表回调（防改后缀名对应关系）

CmRegisterCallbackEx 拦截 RegNtPreSetValueKey / DeleteValueKey / DeleteKey / RenameKey，
检查目标键路径是否以受保护后缀结尾，例如：

    \CLASSES\.EXE
    \CLASSES\EXEFILE\SHELL\OPEN\COMMAND
    \CLASSES\BATFILE\SHELL\OPEN\COMMAND
    ...

命中且调用进程不在可信名单（KitGuard.exe、services.exe、msiexec.exe、lsass.exe、SYSTEM）时返回 STATUS_ACCESS_DENIED。

### 2. 进程回调（防杀进程）

ObRegisterCallbacks 在进程句柄打开/复制时，剥离非可信进程对受保护镜像的：

    PROCESS_TERMINATE / PROCESS_VM_WRITE / PROCESS_CREATE_THREAD / PROCESS_SUSPEND_RESUME

受保护镜像名单在 KitGuardDrv.c 的 KgIsProtectedProcessName 中（SuperKiller.exe 等）。

### 3. KLDR 标志位（实验性）

Windows 只在“签名镜像”上允许 Ob 回调。测试签名驱动会得到 STATUS_ACCESS_DENIED。
驱动在注册回调前把本镜像 KLDR_DATA_TABLE_ENTRY.Flags（+0x68）的 0x20 位置 1，
从而通过检查。详细反汇编与实测见 docs/ob-bypass-notes.md。

## 已知限制与风险

- 驱动只能加载在测试签名或关闭驱动签名强制的环境；
- KLDR 标志位修改是绕过系统完整性检查的做法，属于实验性技术；
- 进程保护目前只覆盖“终止/写入/创建线程”类权限，不防调试器附加以外的所有攻击面；
- notepad.exe 曾用于测试进程保护，会让 PowerShell 的 Start-Process 卡住，正式名单已移除；
- 文件删除/改名保护尚未实现（计划用 minifilter，测试签名可用）；
- 用户态守护仍是 C#，尚未移植成 C；
- 未做正式签名，任何安全软件都可能拦截；
- 蓝屏/数据丢失风险自担，务必在快照中测试。

## 免责声明

本项目仅用于学习 Windows 内核与安全工具保护机制，作者不对任何滥用或由此造成的损失负责。
在不符合法律或授权范围的设备上使用本代码产生的后果由使用者承担。
