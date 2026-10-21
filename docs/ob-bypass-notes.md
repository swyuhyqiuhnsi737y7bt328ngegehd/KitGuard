# ObRegisterCallbacks 注册失败原因与 KLDR 标志位

## 现象

测试签名驱动加载成功，但 DriverEntry 里：

    ObRegisterCallbacks(&obReg, &g_ObRegistration) == 0xC0000022 (STATUS_ACCESS_DENIED)

系统提示（微软官方问答中的原文）：

    The callback routines do not reside in a signed kernel binary image

testsigning / nointegritychecks 都无法改变这个结果。

## 反汇编定位（Windows 10 19045，ntoskrnl.exe）

ObRegisterCallbacks 在逐个校验 OperationRegistration 的 PreOperation/PostOperation 时：

    mov  rcx, [rsi+10h]        ; PreOperation
    mov  edx, 1
    call image-lookup          ; 在 PsLoadedModuleList 中按地址找镜像
    mov  rcx, rax
    test rcx, rcx
    je   -> STATUS_ACCESS_DENIED
    mov  edi, 0x20
    test dword ptr [rax+68h], edi   ; KLDR_DATA_TABLE_ENTRY.Flags & 0x20
    je   -> STATUS_ACCESS_DENIED

KLDR_DATA_TABLE_ENTRY 布局（x64）：

    0x00 InLoadOrderLinks
    0x10 ExceptionTable
    0x18 ExceptionTableSize
    0x20 GpValue
    0x28 NonPagedDebugInfo
    0x30 DllBase
    0x38 EntryPoint
    0x40 SizeOfImage
    0x48 FullDllName
    0x58 BaseDllName
    0x68 Flags          <-- 检查点
    0x6C LoadCount

## 处理

在 DriverEntry 中、调用 ObRegisterCallbacks 之前：

    PULONG pFlags = (PULONG)((PUCHAR)DriverObject->DriverSection + 0x68);
    *pFlags |= 0x20;

## 实测

    HKLM\SOFTWARE\KitGuardDrv
        RegStatus      0x0
        ObStatus       0x0          (注册成功)
        LdrFlagsBefore 0x41104000
        LdrFlagsAfter  0x41104020

之后：

    reg add HKCR\exefile\shell\open\command -> 拒绝访问，值不变
    taskkill /F /IM notepad.exe             -> 拒绝访问，进程存活
    sc stop KitGuardDrv 后再 taskkill       -> 成功（对照）

## 风险

- 这是绕过 Windows 代码完整性检查的做法，属于实验性技术；
- 仅适用于已能加载驱动的测试环境（测试签名/关闭 DSE）；
- 修改 KLDR 标志可能影响驱动加载/卸载行为，需在快照中测试；
- 不要在非隔离环境使用。
