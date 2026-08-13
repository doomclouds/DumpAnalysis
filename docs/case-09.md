# 案例 09：访问违例 —— P/Invoke 传坏指针（AccessViolation）

> 目标：掌握 **Access Violation（`0xC0000005`）** 这类「真正崩溃」的分析。这是互操作（interop）最经典的翻车：原生代码通过一个坏指针写内存，进程立刻崩溃。学会在 dump 里认故障地址 + 故障帧，并理解「空指针 → NRE」与「非空坏地址 → AV」的区别。

## 1. 复现的故障

- **现象**：P/Invoke 调用原生函数（如 `RtlZeroMemory`）时，传了一个**非空但未映射**的目标地址。原生代码一写入就触发 `STATUS_ACCESS_VIOLATION (0xC0000005)`，进程崩溃。
- **本质**：托管→原生边界最常见的错误——**指针生命周期/有效性**问题：use-after-free、悬垂指针、越界偏移、或干脆拿了个坏地址就往下传。

## 2. 根因机理

```text
托管代码                                       原生代码
─────────────────────────────              ─────────────────────────────
Scenario09.Run()
   └─ RtlZeroMemory(dest=0x100000000, 4) ─▶ kernel32!RtlZeroMemory ─▶ ntdll!memset
         ▲   坏指针：非空、未映射               │
         │                                     └── 对 0x100000000 写入 4 字节
      P/Invoke 边界（Marshal 直接透传指针）                 │
                                                        ▼
                                        STATUS_ACCESS_VIOLATION (0xC0000005)
                                                        │
                                          ProcessCLRException → HandleFatalError
                                                        │
                                          WatsonLastChance → 终止进程
```

- **为什么用非空地址**：对**空指针**解引用，JIT 会做空检查，抛托管 `NullReferenceException`（不是真 AV）。要用**非空、未映射**的地址，才真正落到硬件、触发 `0xC0000005`。
- 真实世界的同类：`Marshal.FreeHGlobal` 后继续用（use-after-free）、释放后回调（release-after-call）、原生库算错偏移越界写。

## 3. 与案例 08（栈溢出）的对比

| | 案例 08 栈溢出 | 案例 09 访问违例 |
|---|---|---|
| 崩溃码 | 栈耗尽（运行时识别）| **`0xC0000005`**（硬件 AV）|
| 故障位置 | 递归 JIT 帧 | **原生 `memset`/`RtlZeroMemory`**（P/Invoke 内）|
| 关键概念 | 栈帧累积 | **坏指针 + 未映射地址** |
| 空指针处理 | — | JIT 转成 NRE（`0xC0000005` 的「前提是**非空**坏地址」）|

## 4. 复现步骤

```powershell
dotnet run -c Release -- 09    # 仓库根目录运行场景 09；RtlZeroMemory 写未映射地址，立即崩溃
```

程序调用 `RtlZeroMemory(dest=0x100000000, 4)`，写入瞬间 AV。崩溃太快，用「崩溃时自动转储」（同案例 08）：

```powershell
$env:DOTNET_DbgEnableMiniDump = "1"
$env:DOTNET_DbgMiniDumpType   = "4"
$env:DOTNET_DbgMiniDumpName   = "C:\path\leak09.dmp"
dotnet run -c Release -- 09
```

## 5. 抓取转储

同案例 08：用 `DOTNET_DbgEnableMiniDump=1` 崩溃转储（或 WER LocalDumps）。崩溃后 dump 写盘，进程退出。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `!analyze -v`（`open_cdb_dump` 自动跑）| 看崩溃栈 | 栈里能看到 `KiUserExceptionDispatch → ntdll!memset`（故障写入点）→ JIT P/Invoke 桩 → `CallDescrWorkerInternal`（托管→原生转换）|
| 2 | `~0s; kb <N>` | **看故障帧 + 参数** | `memset+0x12f`（故障指令）；调用方参数里 `dest=0x100000000`、`len=4`——就是传进去的坏指针 |
| 3 | 对照进程 stderr | 确认崩溃码 | `Fatal error. 0xC0000005 at ...RtlZeroMemory(IntPtr, UIntPtr)` |
| 4 | `.exr -1` / 异常记录 | 看原始异常（可选）| 崩溃转储的 last-event 可能被崩溃处理器覆盖，以 stderr + 栈为准 |

> 避坑：用 `DOTNET_DbgEnableMiniDump=1` 时，`!analyze` 的 `Failure.Bucket` 会显示 `LaunchCreateDump`（崩溃处理器打点写 dump 的痕迹）。**真正的崩溃证据在栈里**：`KiUserExceptionDispatch → memset` + 传参的坏地址。

## 7. 判定标准

1. stderr 报 **`Fatal error. 0xC0000005`** 且指向 P/Invoke 方法（`RtlZeroMemory`）。
2. 栈里有 **`KiUserExceptionDispatch` → `ntdll!memset`（或故障原生函数）** 帧，下方是托管→原生转换（`CallDescrWorkerInternal`）。
3. 调用帧参数能看出**坏指针值**（本案例 `0x100000000`）——这就是故障地址。
4. `!eeheap -gc` 健康 → 与内存泄漏/托管堆无关，是原生指针错误。

## 8. 修复方向（对照）

- **指针生命周期**：`Marshal.AllocHGlobal`/`FreeHGlobal` 必须配对；释放后立即置 `IntPtr.Zero` 防悬垂。
- **用 SafeHandle**：`SafeHandle`/`SafeBuffer` 自动释放、可校验有效性，避免裸指针穿过 P/Invoke。
- **边界校验**：传给原生代码的长度/偏移要 clamp；原生侧拿到的 buffer 大小必须与分配一致（防越界写）。
- 能托管就托管：`byte[]`/`Span<byte>` + `Marshal` 桩，减少直接裸指针透传。

## 9. 本次实测结果（leak09.dmp，.NET 崩溃转储 101MB）

进程 stderr：

```
Fatal error.
0xC0000005
   at DumpAnalysis.Scenario09_AccessViolation.RtlZeroMemory(IntPtr, UIntPtr)
   at DumpAnalysis.Scenario09_AccessViolation.Run(System.String[])
   at DumpAnalysis.Program.Main(System.String[])
```

SOS / 原生分析关键输出：

- **`~0s; kb 16`**（故障线程栈，自顶向下）→
  ```
  ntdll!NtWaitForSingleObject
  coreclr!LaunchCreateDump              ← 崩溃处理器（写 dump）
  coreclr!CreateCrashDumpIfEnabled
  coreclr!WatsonLastChance
  coreclr!EEPolicy::HandleFatalError / LogFatalError
  coreclr!ProcessCLRException
  ntdll!RtlpExecuteHandlerForException / RtlDispatchException
  ntdll!KiUserExceptionDispatch
  ntdll!memset+0x12f                    ← ★ 故障指令（对坏地址写入）
  0x00007ffe d08839bc                   ← JIT P/Invoke 桩（RtlZeroMemory）
  0x00007ffe d0883241 : 00000001`00000000  00000000`00000004  ← 传参 dest=0x100000000, len=4
  coreclr!CallDescrWorkerInternal       ← 托管→原生调用转换
  ...
  ```
- **判定闭环**：P/Invoke `RtlZeroMemory(dest=0x100000000, len=4)` 进入原生 `ntdll!memset`，对 **4GB 处未映射地址**写入 → `0xC0000005`；崩溃处理器兜底写 dump、进程终止。**坏指针穿透托管边界 = 原生 AV，实锤**。
