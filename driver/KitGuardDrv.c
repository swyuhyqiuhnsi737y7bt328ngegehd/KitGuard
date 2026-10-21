/*
 * KitGuardDrv.c —— KitGuard 内核驱动（实验性质）
 *
 * 功能：
 *   1) 注册表回调：拦截对 Windows 文件关联键的写入/删除
 *      （.exe/.bat/.cmd/.com/.scr/.pif/.lnk 及 exefile 等 ProgID 的 shell\open\command）
 *   2) 进程回调：剥离非可信进程对受保护进程的 PROCESS_TERMINATE / PROCESS_VM_WRITE /
 *      PROCESS_CREATE_THREAD / PROCESS_SUSPEND_RESUME 权限
 *
 * 注意（重要）：
 *   - 本驱动只能加载在开启测试签名或关闭驱动签名强制的环境中；
 *   - 为了让 Windows 允许注册 Ob 回调，这里修改了本驱动镜像的
 *     KLDR_DATA_TABLE_ENTRY.Flags（+0x68）的 0x20 位。
 *     这是绕过系统代码完整性检查的做法，属于实验性技术，只在隔离测试环境使用。
 *   - 未正式签名，可能存在蓝屏、与安全软件冲突等风险。
 */

#include <ntddk.h>

#define KG_POOL_TAG 'GdGK'
#define KG_ALTITUDE_OB L"321000"

#ifndef PROCESS_TERMINATE
#define PROCESS_TERMINATE 0x0001
#endif
#ifndef PROCESS_CREATE_THREAD
#define PROCESS_CREATE_THREAD 0x0002
#endif
#ifndef PROCESS_VM_WRITE
#define PROCESS_VM_WRITE 0x0020
#endif
#ifndef PROCESS_SUSPEND_RESUME
#define PROCESS_SUSPEND_RESUME 0x0800
#endif

NTSYSAPI PCHAR NTAPI PsGetProcessImageFileName(PEPROCESS Process);

static LARGE_INTEGER g_RegCookie;
static PVOID g_ObRegistration = NULL;
static volatile LONG g_BlockedReg = 0;
static volatile LONG g_BlockedProc = 0;
static ULONG g_LdrFlagsBefore = 0xDEADBEEF;
static ULONG g_LdrFlagsAfter = 0;

static const PWSTR g_RegSuffixes[] = {
    L"\\CLASSES\\.EXE",
    L"\\CLASSES\\.BAT",
    L"\\CLASSES\\.CMD",
    L"\\CLASSES\\.COM",
    L"\\CLASSES\\.SCR",
    L"\\CLASSES\\.PIF",
    L"\\CLASSES\\.LNK",
    L"\\CLASSES\\EXEFILE\\SHELL\\OPEN\\COMMAND",
    L"\\CLASSES\\BATFILE\\SHELL\\OPEN\\COMMAND",
    L"\\CLASSES\\CMDFILE\\SHELL\\OPEN\\COMMAND",
    L"\\CLASSES\\COMFILE\\SHELL\\OPEN\\COMMAND",
    L"\\CLASSES\\SCRFILE\\SHELL\\OPEN\\COMMAND",
    L"\\CLASSES\\PIFIFILE\\SHELL\\OPEN\\COMMAND",
    L"\\CLASSES\\LNKFILE\\SHELL\\OPEN\\COMMAND",
    L"\\CLASSES\\EXEFILE"
};

static BOOLEAN KgNameEqualsA(const CHAR *a, const CHAR *b)
{
    CHAR ca, cb;
    if (a == NULL || b == NULL) return FALSE;
    while (*a != 0 && *b != 0)
    {
        ca = *a; cb = *b;
        if (ca >= 'a' && ca <= 'z') ca = (CHAR)(ca - 32);
        if (cb >= 'a' && cb <= 'z') cb = (CHAR)(cb - 32);
        if (ca != cb) return FALSE;
        a++; b++;
    }
    return (*a == 0 && *b == 0);
}

static BOOLEAN KgSuffixMatch(PCUNICODE_STRING Path, PCWSTR Suffix)
{
    SIZE_T slen = wcslen(Suffix);
    SIZE_T plen;
    SIZE_T i;
    if (Path == NULL || Path->Buffer == NULL) return FALSE;
    plen = Path->Length / sizeof(WCHAR);
    if (plen < slen) return FALSE;
    for (i = 0; i < slen; i++)
    {
        WCHAR a = Path->Buffer[plen - slen + i];
        if (a >= L'a' && a <= L'z') a = (WCHAR)(a - 32);
        if (a != Suffix[i]) return FALSE;
    }
    return TRUE;
}

static BOOLEAN KgIsProtectedKeyPath(PCUNICODE_STRING Path)
{
    ULONG i;
    if (Path == NULL || Path->Buffer == NULL) return FALSE;
    for (i = 0; i < RTL_NUMBER_OF(g_RegSuffixes); i++)
    {
        if (KgSuffixMatch(Path, g_RegSuffixes[i])) return TRUE;
    }
    return FALSE;
}

static BOOLEAN KgIsTrustedCaller(VOID)
{
    CHAR *image;
    if (PsGetCurrentProcessId() == (HANDLE)4) return TRUE;
    image = PsGetProcessImageFileName(PsGetCurrentProcess());
    if (image != NULL)
    {
        if (KgNameEqualsA(image, "KitGuard.exe")) return TRUE;
        if (KgNameEqualsA(image, "services.exe")) return TRUE;
        if (KgNameEqualsA(image, "msiexec.exe")) return TRUE;
        if (KgNameEqualsA(image, "lsass.exe")) return TRUE;
    }
    return FALSE;
}

static BOOLEAN KgIsProtectedProcessName(const CHAR *Image)
{
    if (Image == NULL) return FALSE;
    if (KgNameEqualsA(Image, "SuperKiller.exe")) return TRUE;
    if (KgNameEqualsA(Image, "SuperKillller.exe")) return TRUE;
    if (KgNameEqualsA(Image, "FirstAidBox.exe")) return TRUE;
    if (KgNameEqualsA(Image, "KitGuard.exe")) return TRUE;
    return FALSE;
}

static NTSTATUS KgRegCallback(PVOID CallbackContext, PVOID Argument1, PVOID Argument2)
{
    REG_NOTIFY_CLASS cls = (REG_NOTIFY_CLASS)(ULONG_PTR)Argument1;
    PVOID keyObject = NULL;
    PCUNICODE_STRING name = NULL;
    NTSTATUS st;

    UNREFERENCED_PARAMETER(CallbackContext);

    switch (cls)
    {
    case RegNtPreSetValueKey:
        keyObject = ((PREG_SET_VALUE_KEY_INFORMATION)Argument2)->Object;
        break;
    case RegNtPreDeleteValueKey:
        keyObject = ((PREG_DELETE_VALUE_KEY_INFORMATION)Argument2)->Object;
        break;
    case RegNtPreDeleteKey:
        keyObject = ((PREG_DELETE_KEY_INFORMATION)Argument2)->Object;
        break;
    case RegNtPreRenameKey:
        keyObject = ((PREG_RENAME_KEY_INFORMATION)Argument2)->Object;
        break;
    default:
        return STATUS_SUCCESS;
    }

    if (keyObject == NULL) return STATUS_SUCCESS;

    st = CmCallbackGetKeyObjectIDEx(&g_RegCookie, keyObject, NULL, &name, 0);
    if (NT_SUCCESS(st) && name != NULL)
    {
        if (KgIsProtectedKeyPath(name))
        {
            if (!KgIsTrustedCaller())
            {
                InterlockedIncrement(&g_BlockedReg);
                DbgPrint("[KitGuardDrv] DENY reg write: %wZ (pid %p)\n", name, PsGetCurrentProcessId());
                if (name != NULL) ExFreePool(name);
                return STATUS_ACCESS_DENIED;
            }
        }
        if (name != NULL) ExFreePool(name);
    }
    else if (name != NULL)
    {
        ExFreePool(name);
    }
    return STATUS_SUCCESS;
}

static OB_PREOP_CALLBACK_STATUS KgPreProcessHandle(PVOID RegistrationContext, POB_PRE_OPERATION_INFORMATION Info)
{
    PEPROCESS target;
    PEPROCESS caller;
    PCHAR targetImage;
    PCHAR callerImage;
    ULONG *access;

    UNREFERENCED_PARAMETER(RegistrationContext);

    if (Info->KernelHandle) return OB_PREOP_SUCCESS;
    if (Info->ObjectType != *PsProcessType) return OB_PREOP_SUCCESS;

    target = (PEPROCESS)Info->Object;
    targetImage = PsGetProcessImageFileName(target);
    if (!KgIsProtectedProcessName(targetImage)) return OB_PREOP_SUCCESS;

    caller = PsGetCurrentProcess();
    callerImage = PsGetProcessImageFileName(caller);
    if (caller == target) return OB_PREOP_SUCCESS;
    if (KgIsProtectedProcessName(callerImage)) return OB_PREOP_SUCCESS;
    if (PsGetCurrentProcessId() == (HANDLE)4) return OB_PREOP_SUCCESS;

    if (Info->Operation == OB_OPERATION_HANDLE_CREATE)
    {
        access = &Info->Parameters->CreateHandleInformation.DesiredAccess;
        *access &= ~(PROCESS_TERMINATE | PROCESS_VM_WRITE | PROCESS_CREATE_THREAD | PROCESS_SUSPEND_RESUME);
    }
    else if (Info->Operation == OB_OPERATION_HANDLE_DUPLICATE)
    {
        access = &Info->Parameters->DuplicateHandleInformation.DesiredAccess;
        *access &= ~(PROCESS_TERMINATE | PROCESS_VM_WRITE | PROCESS_CREATE_THREAD | PROCESS_SUSPEND_RESUME);
    }

    InterlockedIncrement(&g_BlockedProc);
    DbgPrint("[KitGuardDrv] strip access on %s by %s\n", targetImage ? targetImage : "?", callerImage ? callerImage : "?");
    return OB_PREOP_SUCCESS;
}

static void KgSetValue(HANDLE hKey, PCWSTR name, ULONG value)
{
    UNICODE_STRING vn;
    RtlInitUnicodeString(&vn, name);
    ZwSetValueKey(hKey, &vn, 0, REG_DWORD, &value, sizeof(value));
}

static void KgReportStatus(NTSTATUS regStatus, NTSTATUS obStatus, ULONG loaded)
{
    UNICODE_STRING keyName;
    OBJECT_ATTRIBUTES oa;
    HANDLE hKey = NULL;
    ULONG disposition = 0;

    RtlInitUnicodeString(&keyName, L"\\Registry\\Machine\\SOFTWARE\\KitGuardDrv");
    InitializeObjectAttributes(&oa, &keyName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);
    if (!NT_SUCCESS(ZwCreateKey(&hKey, KEY_WRITE, &oa, 0, NULL, REG_OPTION_NON_VOLATILE, &disposition)))
        return;

    KgSetValue(hKey, L"Version", 1);
    KgSetValue(hKey, L"Loaded", loaded);
    KgSetValue(hKey, L"RegStatus", (ULONG)regStatus);
    KgSetValue(hKey, L"ObStatus", (ULONG)obStatus);
    KgSetValue(hKey, L"RegBlocked", (ULONG)g_BlockedReg);
    KgSetValue(hKey, L"ProcBlocked", (ULONG)g_BlockedProc);
    KgSetValue(hKey, L"LdrFlagsBefore", g_LdrFlagsBefore);
    KgSetValue(hKey, L"LdrFlagsAfter", g_LdrFlagsAfter);
    ZwClose(hKey);
}

static VOID KgDriverUnload(PDRIVER_OBJECT DriverObject)
{
    UNREFERENCED_PARAMETER(DriverObject);
    if (g_ObRegistration != NULL)
    {
        ObUnRegisterCallbacks(g_ObRegistration);
        g_ObRegistration = NULL;
    }
    if (g_RegCookie.QuadPart != 0)
    {
        CmUnRegisterCallback(g_RegCookie);
        g_RegCookie.QuadPart = 0;
    }
    KgReportStatus(STATUS_SUCCESS, STATUS_SUCCESS, 0);
    DbgPrint("[KitGuardDrv] unloaded. reg=%ld proc=%ld\n", g_BlockedReg, g_BlockedProc);
}

NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    NTSTATUS status;
    OB_CALLBACK_REGISTRATION obReg;
    OB_OPERATION_REGISTRATION obOps;
    UNICODE_STRING altitude;

    UNREFERENCED_PARAMETER(RegistryPath);

    DbgPrint("[KitGuardDrv] loading...\n");

    RtlInitUnicodeString(&altitude, L"KitGuardDrv");
    status = CmRegisterCallbackEx(KgRegCallback, &altitude, DriverObject, NULL, &g_RegCookie, NULL);
    if (!NT_SUCCESS(status))
    {
        DbgPrint("[KitGuardDrv] CmRegisterCallbackEx failed 0x%08X\n", status);
        return status;
    }

    /* 修改本驱动镜像 KLDR 表项 Flags 的 0x20 位：Ob 回调的"签名镜像"检查看这一位 */
    if (DriverObject->DriverSection != NULL)
    {
        PULONG pFlags = (PULONG)((PUCHAR)DriverObject->DriverSection + 0x68);
        g_LdrFlagsBefore = *pFlags;
        *pFlags = g_LdrFlagsBefore | 0x20;
        g_LdrFlagsAfter = *pFlags;
        DbgPrint("[KitGuardDrv] KLDR flags 0x%08X -> 0x%08X\n", g_LdrFlagsBefore, g_LdrFlagsAfter);
    }

    RtlZeroMemory(&obOps, sizeof(obOps));
    obOps.ObjectType = PsProcessType;
    obOps.Operations = OB_OPERATION_HANDLE_CREATE | OB_OPERATION_HANDLE_DUPLICATE;
    obOps.PreOperation = KgPreProcessHandle;
    obOps.PostOperation = NULL;

    RtlZeroMemory(&obReg, sizeof(obReg));
    obReg.Version = OB_FLT_REGISTRATION_VERSION;
    obReg.OperationRegistrationCount = 1;
    RtlInitUnicodeString(&obReg.Altitude, KG_ALTITUDE_OB);
    obReg.RegistrationContext = NULL;
    obReg.OperationRegistration = &obOps;

    status = ObRegisterCallbacks(&obReg, &g_ObRegistration);
    if (!NT_SUCCESS(status))
    {
        DbgPrint("[KitGuardDrv] ObRegisterCallbacks failed 0x%08X (registry protection still active)\n", status);
        g_ObRegistration = NULL;
    }

    KgReportStatus(STATUS_SUCCESS, status, 1);
    DriverObject->DriverUnload = KgDriverUnload;
    DbgPrint("[KitGuardDrv] loaded OK (ob=0x%08X)\n", status);
    return STATUS_SUCCESS;
}
