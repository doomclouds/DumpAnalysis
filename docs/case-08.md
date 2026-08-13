# 案例 08：栈溢出 —— 无界递归（StackOverflowException）

> 目标：掌握**最「暴力」的崩溃类**——进程直接终止、无法捕获。理解为什么 `StackOverflowException` 在 .NET Core 里不可 catch，以及如何在 dump 里认出「深递归栈」。练习 `!analyze -v` 与原生 `k` 栈。

## 1. 复现的故障

- **现象**：方法无限自我调用，线程栈被递归帧占满，触及栈底后进程**立即终止**（不经过托管异常处理、不跑终结器、不给第二次机会）。
- **本质**：**原生栈耗尽**。每次调用在栈上压入返回地址/局部变量；递归没有终止条件 → 栈帧无限累积 → 越过线程栈底（StackBase）→ 触发 `STATUS_STACK_OVERFLOW`，运行时的向量异常处理接管。

## 2. 根因机理

```text
线程原生栈（默认 1MB，StackBase / StackLimit 界定）
┌────────────────────────────────────────────┐
│  Recurse(0)      ← 栈帧                    │
│  Recurse(1)      ← 栈帧                    │
│  Recurse(2)      ← 栈帧                    │
│  ...            ← 16,000+ 层同函数帧       │
│  Recurse(16057)                            │
│  Recurse(16058)  ← 压栈时溢出 → STATUS_STACK_OVERFLOW
└────────────────────────────────────────────┘
  运行时: CLRVectoredExceptionHandler → EEPolicy::HandleStackOverflow
          → HandleFatalStackOverflow → WatsonLastChance → 终止进程
```

- .NET Core 的 `StackOverflowException` **不可捕获**（不像 OOM 可以 `try/catch`）：运行时把它当致命错误，直接终止。
- 经典诱因：XML/JSON 深层嵌套的递归解析、目录树/对象图无限递归、状态机缺终止条件、`+=` 意外自我委托等。
- 与「正常崩溃」区别：栈溢出常发生在**任意非主线程**（可能是终结器线程/线程池线程），dump 的故障线程栈会是一串**相同地址的递归帧**。

## 3. 与案例 07（死锁）的对比

| | 案例 07 死锁 | 案例 08 栈溢出 |
|---|---|---|
| 进程状态 | **活着但卡死** | **直接崩溃退出** |
| 根因 | 锁等待环 | 原生栈耗尽 |
| 分析入口 | `!threads` / `!syncblk` / `!clrstack` | **`!analyze -v` / `k`（原生栈）** |
| 栈特征 | 两个线程停在 `Monitor.Enter` | **一串相同返回地址的递归帧 + 递减的深度参数** |
| 抓 dump 方式 | 进程活着，`dotnet-dump collect` | **崩溃瞬间**：WER 或 `DOTNET_DbgEnableMiniDump=1` 崩溃转储 |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 08    # 仓库根目录运行场景 08；无界递归，约 16k 层后崩溃
```

程序打印 `depth=0` 后瞬间递归到 ~16000 层崩溃。崩溃太快，**没法在运行时 attach**，必须用「崩溃时自动转储」。

## 5. 抓取转储（崩溃瞬间）

崩溃太快，两种方式取 dump：

- **A. .NET 崩溃转储（推荐，本案例采用）**：启动前设环境变量，进程一崩溃就写 dump：
  ```powershell
  $env:DOTNET_DbgEnableMiniDump = "1"
  $env:DOTNET_DbgMiniDumpType   = "4"      # 4 = full，含完整堆
  $env:DOTNET_DbgMiniDumpName   = "C:\path\leak08.dmp"
  dotnet run -c Release -- 08
  ```
- **B. WER LocalDumps**：在注册表 `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\DumpAnalysis.exe` 配 `DumpFolder`/`DumpType`，崩溃时由 WER 写 dump。

> 注意：A 的 dump 是在**崩溃处理器里**打的断点后写的，所以 `!analyze -v` 的 `Failure.Bucket` 会显示 `CreateCrashDumpIfEnabled`（崩溃转储功能的痕迹），但栈里能看到 `EEPolicy::HandleStackOverflow` 与下方整串递归帧——证据不受影响。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `!analyze -v`（`open_cdb_dump` 自动跑）| 看崩溃栈 | 栈顶是 `EEPolicy::HandleStackOverflow → HandleFatalStackOverflow → WatsonLastChance`（运行时栈溢出处理链）|
| 2 | `~0s; k <N>` | **看故障线程原生栈** | 崩溃处理器下方是**一大串相同返回地址的递归帧**，参数递减（= 递归深度）|
| 3 | `!threads` / 进程 stderr | 对照递归函数 | stderr 有 `Stack overflow. Repeated 16058 times: at ...Recurse(Int32)` |
| 4 | `!eeheap -gc`（可选）| 确认堆健康 | 堆不是问题——这是栈问题，不是内存泄漏 |

## 7. 判定标准

1. `!analyze -v` 栈里有 `EEPolicy::HandleStackOverflow` / `HandleFatalStackOverflow`——运行时的栈溢出致命处理。
2. 故障线程原生栈出现**同一返回地址反复出现**（几千上万帧），帧参数逐层递减（递归深度）。
3. 进程 stderr 报 `Stack overflow. Repeated N times: at <方法>`。
4. 堆（`!eeheap -gc`）健康 → 与内存泄漏无关，纯粹是栈问题。

## 8. 修复方向（对照）

- **补终止条件**：递归必须有明确的 base case；对未知深度的数据（XML/JSON/目录树）改用**显式栈/循环**（`Stack<T>` 迭代替代递归）。
- **限制深度**：解析嵌套结构时设最大深度（如 JSON.NET 的 `MaxDepth`）。
- **减小栈帧**：别在递归方法里放大的局部 buffer；改用 `async`/`Task`（各有自己的线程池栈）或增大线程栈（`Thread` 构造参数，治标）。
- 真需要深度递归时，考虑在**独立线程 + 自定栈大小**上跑，把溢出限制在可控线程。

## 9. 本次实测结果（leak08.dmp，.NET 崩溃转储 103MB）

进程 stderr（崩溃时 .NET 的报告）：

```
Stack overflow.
Repeated 16058 times:
--------------------------------
   at DumpAnalysis.Scenario08_StackOverflow.Recurse(Int32)
--------------------------------
   at DumpAnalysis.Scenario08_StackOverflow.Run(System.String[])
   at DumpAnalysis.Program.Main(System.String[])
```

SOS / 原生分析关键输出：

- **`!analyze -v`** → `Failure.Bucket: BREAKPOINT_80000003_coreclr.dll!CreateCrashDumpIfEnabled`（因为用 `DOTNET_DbgEnableMiniDump=1` 崩溃转储；真正的栈溢出证据在栈里）。
- **`~0s; k 18`**（故障线程栈顶）→
  ```
  ntdll!NtWaitForSingleObject
  coreclr!CreateCrashDumpIfEnabled      ← 崩溃处理器（写 dump）
  coreclr!WatsonLastChance
  coreclr!EEPolicy::HandleFatalStackOverflow   ← 致命栈溢出处理
  coreclr!EEPolicy::HandleStackOverflow        ← 栈溢出入口
  coreclr!CLRVectoredExceptionHandler(Shim)
  ntdll!RtlpCallVectoredHandlers / RtlDispatchException / KiUserExceptionDispatch
  00007ffe`d08938b9   ← 递归帧（Recurse 的 JIT 代码，返回地址全部相同）
  00007ffe`d08938b9   ← 参数 0x3ec2 / 0x3ec1 / 0x3ec0 … 逐层递减（= 递归深度倒着数）
  00007ffe`d08938b9   ×16058
  ...
  ```
- **判定闭环**：栈顶是 `EEPolicy::HandleStackOverflow`（运行时认定栈溢出）→ 下方 16,000+ 个**完全相同返回地址**的帧、参数从 ~0x3ec2 递减 → 与 stderr 的 `Repeated 16058 times: Recurse(Int32)` 完全对账 → **无界递归耗尽线程栈，实锤**。
