# .NET 性能分析指南 · 工具链 + 方法论

> 与仓库的 WinDbg MCP（专攻**内存/故障类** dump）互补：本文覆盖 **CPU / 分配 / GC / 延迟 / 线程池**性能问题的完整分析流程。先测量、再采样、最后定位热点，不要靠猜。

---

## 1. 工具链一览

| 工具 | 干什么 | 典型场景 | 安装 |
|---|---|---|---|
| **`dotnet-counters`** | 实时计数器：CPU、GC、JIT、异常、线程池 | **第一步**：看症状、确认方向 | `dotnet tool install -g dotnet-counters` |
| **`dotnet-trace`** | EventPipe 采样：CPU/GC/分配/JIT/线程 | **第二步**：抓 profile 定位热点 | `dotnet tool install -g dotnet-trace` |
| **`dotnet-stack`** | 打印所有线程托管栈（一次性快照） | 「卡死/高延迟瞬间在干嘛」 | `dotnet tool install -g dotnet-stack` |
| **`dotnet-gcdump`** | 托管堆 GC dump（`.gcdump`，轻量） | 分配/内存压力、进程还活着时 | `dotnet tool install -g dotnet-gcdump` |
| **`dotnet-dump`** | 完整进程 dump | 内存泄漏、死锁、崩溃 | 见仓库 WINDBG-MCP-GUIDE |
| **`dotnet-monitor`** | 生产环境按需/按规则采集 artifacts | 线上远程采集 | 容器/侧车 |
| **PerfView / VS Profiler** | 打开 `.nettrace` 深度分析 | 第三步：人肉分析热点/火焰图 | PerfView 从 GitHub；VS 自带 |
| **BenchmarkDotNet** | 微基准 | 对比方案、验证修复 | NuGet |

工具选择逻辑（**按症状分**）：

```text
高 CPU ──────────▶ dotnet-counters(确认CPU) → dotnet-trace 线程采样 → report topN / PerfView CPU Stacks
慢但 CPU 低（I/O/锁/等待）▶ dotnet-stack（此刻在干嘛）→ dotnet-trace 线程事件 → 找阻塞/排队
GC/分配压力 ─────▶ dotnet-counters(%Time in GC / 分配率) → dotnet-gcdump 看堆 → gc-verbose 找分配点
启动慢 ──────────▶ dotnet-trace 启动 profile → JIT / ReadyToRun 分析
生产异常 ────────▶ dotnet-monitor / 计数器埋点 → 触发采集
```

## 2. 方法论：四步定位瓶颈

### 第 1 步：先测量，别猜

`dotnet-counters` 挂 5–10 秒看方向：

```powershell
dotnet-counters collect --process-id <PID> --counters System.Runtime --duration 6 -o counters.csv
```

关键计数器：

| 计数器 | 危险信号 |
|---|---|
| `dotnet.process.cpu.time`（user/sys）| user ≈ 核数 = CPU 密集 |
| `dotnet.gc.heap.total_allocated` | 每秒上 GB = 分配风暴 |
| `dotnet.gc.collections[gen0]` | 每秒几十次 = GC 在拼命回收 |
| `dotnet.gc.last_collection.heap.size` | gen2/LOH 大 = 晋升/大对象 |
| `dotnet.thread_pool.queue.length` | 持续 > 0 = 饥饿前兆 |
| `dotnet.monitor.lock_contentions` | 飙升 = 锁争用 |
| `dotnet.timer.count` | 持续增长 = Timer 泄漏 |

### 第 2 步：按症状采样

**CPU 高**（Windows，`dotnet-sampled-thread-time` ~100Hz 采样线程栈）：

```powershell
dotnet-trace collect --process-id <PID> --profile dotnet-sampled-thread-time --duration 00:00:15 -o cpu.nettrace
```

> Linux 上可用 `dotnet-trace collect-linux`（内核级、含原生栈）；`cpu-sampling` 是其专属 profile。

**分配/GC 压力**：

```powershell
dotnet-trace collect --process-id <PID> --profile gc-verbose -o gc.nettrace   # GCAllocationTick：>85KB 分配带托管栈
dotnet-gcdump collect --process-id <PID> -o heap.gcdump                        # 堆：谁最多、谁引用谁
```

**卡顿/延迟**（进程活着、CPU 不高）：

```powershell
dotnet-stack <PID>    # 一次性快照：此刻所有线程在等什么
```

### 第 3 步：定位热点

`dotnet-trace report topN` 直接命令行出 Top 耗时方法：

```powershell
dotnet-trace report cpu.nettrace topN -n 20              # 独占时间（方法自己花的）
dotnet-trace report cpu.nettrace topN -n 8 --inclusive   # 包含时间（含它调用的）
```

跨平台火焰图：

```powershell
dotnet-trace convert cpu.nettrace --format speedscope    # → .speedscope.json
# 浏览器打开 https://speedscope.app 拖入文件
```

深挖：用 PerfView 打开 `.nettrace`，看 **CPU Stacks** / **CallTree**。

### 第 4 步：对照修复，重测对比

- 修前拿基线（`report topN` / 计数器）→ 修后重测 → 对比热点占比与分配率（和两次 dump diff 法同理）。
- 微观验证用 BenchmarkDotNet，避免主观判断。

## 3. 常见「看似正常实则损耗」的坑

| 模式 | 表现 | 怎么发现 |
|---|---|---|
| **sync-over-async**（`.Result`/`.Wait()`）| 线程池被占住、响应变慢 | `dotnet-stack` 看 `Task.Wait` 链（仓库案例 10）|
| **锁嵌套/顺序不一致** | 死锁、高锁争用 | `dotnet-counters` lock_contentions、`dotnet-stack`（案例 07）|
| **热路径分配**（LINQ 闭包、boxing、字符串拼接）| CPU 高 + 分配率飙升 | `gc-verbose` 的 GCAllocationTick（案例 12）|
| **异常作控制流** | CPU 高、无热点分配 | `dotnet-counters` exception-count、trace 看 Throw |
| **启动 JIT 成本** | 启动慢 | 启动 profile + `ReadyToRun`/AOT |
| **GC 参数不当** | gen2 频繁、STW 长 | `% Time in GC`、`gc-verbose` |

## 4. 实测案例（仓库案例 12，CPU 热点）

完整流程见 [docs/case-12.md](case-12.md)。一句话结果：

1. `dotnet-counters`：CPU ≈ 1 核、分配 **1.3 GB/s**、gen0 GC **50/s** → 症状。
2. `dotnet-trace --profile dotnet-sampled-thread-time` 采样 15s。
3. `dotnet-trace report ... topN`：`BuildRecords`（每记录字符串插值）独占 **89.87%**，而看着很忙的 `ComputeAggregate` 仅 **0.01%**。

## 5. 与内存分析（WinDbg MCP）的分工

| | WinDbg MCP（仓库主力）| dotnet-counters / trace / gcdump |
|---|---|---|
| 问题 | 内存泄漏、崩溃、死锁、栈溢出 | CPU、分配、GC、延迟、线程池饥饿 |
| 产物 | `.dmp` | `.nettrace` / `.gcdump` / 计数器 |
| 时机 | 崩溃后 / 卡死时 | 运行中实时 |
| 案例 | 01–11 | 12 |

两个工具箱覆盖 .NET 诊断的完整闭环：**先计数器看症状 → trace/gcdump 定位 → 必要时 dump 深挖**。
