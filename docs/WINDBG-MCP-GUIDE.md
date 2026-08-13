# 用 WinDbg MCP 做 .NET dump 分析 · 完整教程

> 目标：从零学会用 **WinDbg MCP**（[`mcp-windbg`](https://github.com/svnscha/mcp-windbg)，驱动 `cdb.exe` + SOS）分析 .NET 进程的 dump。读完这篇，你就能对仓库里 6 个经典场景做完整分析。

---

## 1. 这是什么

[WinDbg MCP](https://github.com/svnscha/mcp-windbg) 是一个 MCP 服务器，它把微软调试器 `cdb.exe` 包装成一组可调用的工具，让你在 Claude Code 里直接对 Windows 崩溃/内存转储（.dmp）执行调试命令。对 .NET 来说，关键是加载 **SOS 扩展**——它暴露了 `!dumpheap`、`!gcroot`、`!finalizequeue` 等专查托管堆的命令。

```text
Claude Code ──MCP──▶ mcp-windbg ──▶ cdb.exe ──加载──▶ SOS (sos.dll)
   你                                  转储(.dmp)         托管堆分析
```

## 2. 安装 mcp-windbg

> 上游仓库：[svnscha/mcp-windbg](https://github.com/svnscha/mcp-windbg) · 文档 <https://svnscha.github.io/mcp-windbg/> · PyPI <https://pypi.org/project/mcp-windbg/>

### 前置

- Windows + [Debugging Tools for Windows](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/)（或 WinDbg，提供 `cdb.exe`/`kd.exe`，自动探测）
- Python 3.10+

### 1) 安装包

```bash
pip install mcp-windbg
```

### 2) 注册到 Claude Code

```bash
claude mcp add mcp-windbg -s user -e _NT_SYMBOL_PATH="SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols" -- python -m mcp_windbg
```

> `_NT_SYMBOL_PATH` 让 cdb 自动从微软符号服务器拉符号并缓存到 `C:\Symbols`；本仓库分析时也可用 `C:\ProgramData\dbg\sym` 作缓存（见下第 5 节 `open_cdb_dump` 的 `symbols_path` 参数）。

### 3) 其他客户端（VS Code / Copilot）

按 `F1` → **MCP: Open User Configuration**，加入：

```json
{
  "servers": {
    "mcp_windbg": {
      "type": "stdio",
      "command": "python",
      "args": ["-m", "mcp_windbg"],
      "env": {
        "_NT_SYMBOL_PATH": "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols"
      }
    }
  }
}
```

重启客户端。注册成功后，会话里会出现 `mcp__mcp-windbg__*` 工具。

## 3. 前置条件

| 依赖 | 用途 | 检查 |
|---|---|---|
| .NET 10 SDK | 运行本仓库场景 | `dotnet --list-sdks` |
| `dotnet-dump` | 抓取完整转储 | `dotnet tool install -g dotnet-dump` |
| Debugging Tools for Windows | `cdb.exe`（WinDbg MCP 的底层） | `C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe` |
| [`mcp-windbg`](https://github.com/svnscha/mcp-windbg) MCP 服务器 | 已接入 Claude Code 的工具（安装见上文）| 会话里有 `mcp__mcp-windbg__*` 工具 |
| 符号 | 解析 .NET 运行时内部符号 | 联网即可从微软符号服务器自动下载 |

> **符号缓存**：传 `srv*C:\ProgramData\dbg\sym*https://msdl.microsoft.com/download/symbols`，符号会缓存到 `C:\ProgramData\dbg\sym`，第二次分析就很快。

## 4. 工具清单（mcp-windbg 提供）

| 工具 | 作用 | 常用参数 |
|---|---|---|
| `open_cdb_dump` | 打开一个转储，自动跑 `.lastevent` + `!analyze -v`，返回 `session_id` | `dump_path`、`symbols_path`、`include_modules`、`timeout_seconds` |
| `run_cdb_command` | 在打开的会话里执行任意调试命令 | `session_id`、`command`、`timeout_seconds` |
| `close_cdb_session` | 关闭会话，释放资源 | `session_id` |
| `list_dumps` | 列出目录里的 .dmp | `directory_path`、`recursive` |
| `open_cdb_remote` | 附加到运行中的调试服务器（实时进程） | `connection_string` |

## 5. 标准分析流程（7 步）

### 第 1 步：跑场景，拿到 PID

```powershell
dotnet run -c Release -- 01 512 100 0
```

程序会打印 `PID`、运行时、GC 模式。它一边稳定分配一边打印堆大小——这就是"泄漏在发生"的现场。

### 第 2 步：抓完整转储

```powershell
dotnet-dump collect -p <PID> -o leak.dmp
```

> 必须用**完整转储**（默认）。原生内存、线程栈都在里面。非托管泄漏（场景 05）尤其依赖完整转储。

### 第 3 步：打开转储（关键参数）

```
open_cdb_dump
  dump_path:    C:\...\leak.dmp
  symbols_path: srv*C:\ProgramData\dbg\sym*https://msdl.microsoft.com/download/symbols
  timeout_seconds: 600
```

> ⚠️ **避坑**：`open_cdb_dump` 会**自动执行 `!analyze -v`**（原生崩溃分析，对托管转储意义不大却很慢）。首次下载 `coreclr.pdb`（44MB）很容易跑满默认 180s 超时。**务必传 `timeout_seconds=600`**；符号缓存命中后只需 ~40s。

### 第 4 步：加载 SOS 并确认

```
run_cdb_command: .loadby sos coreclr
run_cdb_command: !eeversion
```

预期输出包含 `CLR.Version: 10.0.x`。`!analyze` 报告的 `Break instruction exception ... Thread::UserSleep` 是正常现象（进程当时在 `Thread.Sleep` 里），不是崩溃。

### 第 5 步：第一刀——谁在吃内存

```
run_cdb_command: !dumpheap -stat
```

按类型统计实例数和总字节。**总字节最大的行就是嫌疑对象**。五类问题的 `!dumpheap -stat` 各有特征：

| 特征 | 指向 |
|---|---|
| 某个业务类型 + 其大数组同步增长 | 引用泄漏（静态集合 / 事件）|
| `Free` 行 TotalSize 巨大 | LOH 碎片化 |
| 没有大类型，但进程内存巨大 | 非托管泄漏（换 `!address`）|

### 第 6 步：按场景追根

- **引用泄漏**：`!dumpheap -mt <MT> -short` 拿地址 → `!gcroot <地址>` 看根链。
- **终结器**：`!finalizequeue` 看 `Ready for finalization` 积压。
- **事件**：`!dumpheap -type System.EventHandler` → `!do` 委托看 `_invocationList`。
- **碎片化**：`!dumpheap -type Free` 看空洞尺寸。
- **句柄**：`!gchandles -stat` 看 Strong 句柄数。
- **非托管**：`!address -summary` 看私有提交；`!address -f:PAGE_READWRITE,MEM_PRIVATE` 看具体块。

每个场景的完整命令表见 `docs/case-0x.md`。

### 第 7 步：收尾

```
close_cdb_session
```

## 6. SOS 命令速查

### 堆概况
| 命令 | 看什么 |
|---|---|
| `!eeheap -gc` | 各代段数/大小、LOH、POH——先看 LOH 是不是很大 |
| `!dumpheap -stat` | 全堆按类型统计（实例数 + 总字节）|
| `!dumpheap -type <名>` | 列出某类型的实例；`-min <字节>` 只看大对象 |
| `!dumpheap -type Free` | 列出空闲块（碎片化分析）|

### 追根
| 命令 | 看什么 |
|---|---|
| `!gcroot <地址>` | 对象被谁引用、根是什么（静态/栈/句柄/终结队列）|
| `!gchandles [-stat]` | GC 句柄表（Strong/Pinned/Weak…）|
| `!finalizequeue` | 终结队列与 fReachable 积压 |
| `!do <地址>` | dump 对象字段（看引用、委托 `_invocationList`）|

### 对象/线程
| 命令 | 看什么 |
|---|---|
| `!dumpstack` / `!clrstack` | 托管栈 |
| `!threads` | 托管线程列表 |
| `~*k` | 所有线程的原生栈 |
| `!address -summary` | 原生内存总量分布（找非托管泄漏）|

## 7. 常见坑（都是实战踩过的）

1. **`!analyze -v` 超时**：`open_cdb_dump` 自动跑它 → 传 `timeout_seconds=600`；符号缓存后变快。
2. **工作集不变 ≠ 没泄漏**：`AllocHGlobal` 不触碰页面不进 WS，要看 **Private Bytes / Commit**（`!address`）。
3. **diff 两份 dump 必须同一进程实例**：跨进程 MT 地址不同，无法对比。
4. **Release 下本地变量不可见**：`!clrstack -a` 会显示 `<no data>`，别指望看局部变量，改用堆分析。
5. **`_invocationList` / `_invocationCount`**：事件泄漏要 `!do` 组合委托看这两个字段，`_invocationCount` 就是订阅者数。

## 8. 两个进阶技巧

### 两次转储 diff 法（不预知代码先定位）
抓 t1/t2 两份 dump，对比 `!dumpheap -stat`：**增长最多的类型 = 泄漏源**。详见 `docs/case-diff.md`。

### 定位分配栈（找"谁在分配"）
dump 只能回答"谁活着/谁在涨"，要问"谁分配的"用 ETW：

```powershell
dotnet-trace collect -p <PID> --profile gc-verbose -o trace.nettrace
```

`GCAllocationTick` 事件为 >85KB 的分配带上托管调用栈，按 `AllocationSize` 排序即可看到分配大对象的代码位置。

---

下一步：运行 `docs/case-01.md` 的第一个场景，把上面的 7 步完整走一遍。
