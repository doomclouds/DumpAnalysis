# DumpAnalysis

经典 .NET 内存问题复现场景 + **用 [WinDbg MCP](https://github.com/svnscha/mcp-windbg)（cdb + SOS）分析 dump** 的教学仓库。

每个场景是一个小型的 .NET 10 控制台程序，稳定复现一类经典内存问题；配套文档从「怎么复现」到「抓 dump」到「用 SOS 命令一步步定位根因」全流程教一遍。跑完六个场景，你就掌握了托管内存分析的基本功。

## 它教什么

**内存泄漏类（01–06）**

| # | 场景 | 问题 | 签名命令 |
|---|---|---|---|
| 01 | static-leak | 托管堆泄漏：静态集合无限增长（gen2 + LOH） | `!dumpheap -stat`、`!gcroot`、`!eeheap -gc` |
| 02 | finalizer | 终结器积压：从不 Dispose + 慢终结器 → fReachable 堆积 | `!finalizequeue`、`!dumpheap -stat` |
| 03 | event-leak | 事件订阅泄漏：静态发布者 + 从不取消订阅 → 委托链扣住订阅者 | `!gcroot`、`!do` 委托字段 + `_invocationCount` |
| 04 | loh-frag | LOH 碎片化：2MB 洞 + 2.5MB 请求 → Free 空洞无法复用 | `!dumpheap -type Free`、`!eeheap -gc` |
| 05 | native-leak | 非托管泄漏：`Marshal.AllocHGlobal` 从不 Free → 原生私有提交增长 | `!address -summary`、`!heap -s` |
| 06 | gchandle | GC 句柄泄漏：`GCHandle.Alloc` 从不 Free → 句柄表强扣对象 | `!gchandles`、`!gcroot` |
| 11 | timer-leak | Timer 泄漏：静态根住 + 从不 Dispose → 定时器队列扣住状态 | `!dumpheap -stat`（`Timer/TimerQueueTimer`）、`!gcroot` |

**故障类（07–10）**

| # | 场景 | 问题 | 签名命令 |
|---|---|---|---|
| 07 | deadlock | 死锁：两线程交叉持锁互等 → 永久阻塞 | `!threads`、`~*k`、`!syncblk`、`!clrstack` |
| 08 | stack-overflow | 栈溢出：无界递归 → 进程崩溃 | `!analyze -v`、`k`（深递归帧）|
| 09 | access-violation | 访问违例：P/Invoke 传坏指针 → 原生 `0xC0000005` | `!analyze -v`、`kb`（faulting address）|
| 10 | sync-over-async | 线程池饥饿：`.GetResult()` 阻塞 async 续体 | `~*k`、`!clrstack`（`Task.Wait` 链）|

**性能类（12）**

| # | 场景 | 问题 | 签名命令 |
|---|---|---|---|
| 12 | cpu-hotspot | CPU 热点：热循环字符串格式化 → 1 核 + 1.3GB/s 分配 | `dotnet-counters`、`dotnet-trace report topN` |

配套方法：**两次转储 diff 法**（`docs/case-diff.md`）——不预知代码，用 t1/t2 两份 dump 对比出"谁在涨"。

## 仓库结构

```
DumpAnalysis/
├── DumpAnalysis.slnx           # 解决方案
├── LICENSE                     # MIT
├── README.md                   # 本文件
├── docs/
│   ├── WINDBG-MCP-GUIDE.md     # ★ WinDbg MCP 完整教程
│   ├── PERF-ANALYSIS-GUIDE.md  # ★ dotnet-counters / dotnet-trace 性能分析指南
│   ├── case-01.md … case-12.md # 每个场景的分析剧本
│   └── case-diff.md            # 两次转储 diff 法
├── src/DumpAnalysis/           # 单工程，含全部场景（任务调度式）
│   ├── Program.cs              # 入口：按场景名分发到 Run(...)
│   ├── DumpUtil.cs             # 共享辅助（参数解析 / 打印 PID）
│   └── Scenarios/Scenario0x_*.cs
└── dumps/                      # 抓取到的转储（.gitignore 忽略）
```

## 快速上手

```powershell
# 1) 构建
dotnet build -c Release

# 2) 列出场景
dotnet run -c Release -- --help

# 3) 运行一个场景（会打印 PID）
dotnet run -c Release -- 01 512 100 0      # 场景 01，512KB/条，100ms
dotnet run -c Release -- native-leak 4 400 0   # 也可用别名

# 4) 另开一个终端，抓完整转储（需先装 dotnet-dump）
dotnet tool install -g dotnet-dump
dotnet-dump collect -p <PID> -o leak.dmp

# 5) 用 WinDbg MCP 分析转储 —— 见 docs/WINDBG-MCP-GUIDE.md
```

## 分析一个 dump 的最短流程（WinDbg MCP）

1. **打开转储**：`open_cdb_dump`（`dump_path` 指向 `.dmp`，`symbols_path` 填 `srv*C:\ProgramData\dbg\sym*https://msdl.microsoft.com/download/symbols`，`timeout_seconds=600`）。
2. **加载 SOS**：`run_cdb_command` 执行 `.loadby sos coreclr`。
3. **第一刀 `!dumpheap -stat`**：看哪个类型吃内存最多。
4. **按场景追根**：`!gcroot` / `!finalizequeue` / `!dumpheap -type Free` / `!gchandles` / `!address`（每个场景的完整命令表见 `docs/case-0x.md`）。
5. **收尾**：`close_cdb_session`。

> 避坑：`open_cdb_dump` 会自动跑 `!analyze -v`，首次下载符号可能超时——给足 `timeout_seconds=600` 即可。

## 环境要求

- .NET 10 SDK（`dotnet --list-sdks`）
- `dotnet-dump`（抓取转储）或 Sysinternals `procdump`
- WinDbg / Debugging Tools for Windows（`cdb.exe`）
- WinDbg MCP 服务器（[`mcp-windbg`](https://github.com/svnscha/mcp-windbg)）——安装见 [docs/WINDBG-MCP-GUIDE.md](docs/WINDBG-MCP-GUIDE.md) 第 2 节

## 文档

- **教程**：[docs/WINDBG-MCP-GUIDE.md](docs/WINDBG-MCP-GUIDE.md) —— mcp-windbg 安装、工具清单、前置、标准流程、SOS 速查、符号与超时坑
- **性能指南**：[docs/PERF-ANALYSIS-GUIDE.md](docs/PERF-ANALYSIS-GUIDE.md) —— dotnet-counters / dotnet-trace / gcdump 工具链 + 定位热点方法论
- **场景剧本**：`docs/case-01.md` … `case-12.md`
- **方法论**：[docs/case-diff.md](docs/case-diff.md)

## License

[MIT](LICENSE) · Copyright (c) 2026 DumpAnalysis Contributors
