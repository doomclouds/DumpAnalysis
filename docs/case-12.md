# 案例 12：CPU 热点 —— 隐藏的分配瓶颈

> 目标：走一遍 **CPU 性能分析**的完整流程（区别于前 11 个案例的内存/故障类）。用 `dotnet-counters` 看症状 → `dotnet-trace` 采样定位热点 → `dotnet-trace report topN` 一锤定音。理解「看起来忙的代码不一定是热点」。

## 1. 复现的性能问题

- **现象**：进程占满约 1 核 CPU，每秒分配 **~1.3 GB**，gen0 GC 每秒 **50+ 次**——CPU 高、分配风暴。
- **本质**：主循环里有两个「功能」：`ComputeAggregate`（纯数值求和）和 `BuildRecords`（为每条记录做**字符串插值**：`$"{Id:D6}|{Name}|{now:O}|{Guid:N}"`）。后者每调用一次就建一个 `DefaultInterpolatedStringHandler`、格式化 `DateTime`、格式化 `Guid`、分配多个字符串——**CPU + 分配双高**，但看着不起眼。

## 2. 根因机理

```text
主循环 Run()
├── ComputeAggregate(numbers)     # 纯数值：foreach 累加平方和
│     └── 独占 CPU ≈ 0.01%        ← 看着忙，其实便宜
└── BuildRecords(2000)            # 2000 条 × string 插值
      └── $"{Id:D6}|{Name}|{now:O}|{Guid:N}"
            ├── DefaultInterpolatedStringHandler（堆分配）
            ├── DateTime.ToString("O")（格式化 + 分配）
            ├── Guid.ToString("N")（格式化 + 分配）
            └── string 拼接/List.Add
      └── 独占 CPU ≈ 89.87%       ← 真正的热点
```

- 每循环 `BuildRecords` 格式化 2000 条 → 每秒 ~1.3 GB 分配 → GC 拼命回收 → 分配 + GC 吃掉 CPU。
- **教学点**：不能靠「猜」——`ComputeAggregate` 有一万个数字的循环，看着更「忙」，实际只占 0.01%；真正的热点藏在每记录的字符串格式化里。

## 3. 与案例 01-11 的关系

| | 案例 01-11 | 案例 12 |
|---|---|---|
| 问题类型 | 内存泄漏 / 崩溃 / 死锁 | **性能 / CPU 热点** |
| 分析工具 | WinDbg MCP（SOS / `!analyze`）| **`dotnet-counters` / `dotnet-trace` / PerfView** |
| 判据 | 堆 / 根链 / 异常 | **`report topN` 的独占时间占比** |
| 输出 | `.dmp` | `.nettrace`（`dotnet-trace` 产物）|

## 4. 复现步骤

```powershell
dotnet run -c Release -- 12    # 仓库根目录运行场景 12；默认 30s、每循环 2000 条格式化
# 自定义：dotnet run -c Release -- 12 60 2000
```

程序打印迭代进度，纯 CPU 密集。运行期间挂工具采样。

## 5. 性能分析步骤（对应工具）

### 第 1 步：先测量（dotnet-counters）

```powershell
dotnet-counters collect --process-id <PID> --counters System.Runtime --duration 6 -o counters.csv
```

看三个信号：**CPU time、`heap.total_allocated`、`gc.collections(gen0)`**。

### 第 2 步：采样定位热点（dotnet-trace）

Windows 上用 `dotnet-sampled-thread-time` profile（~100Hz 采样线程栈；`cpu-sampling` 是 Linux `collect-linux` 专用）：

```powershell
dotnet-trace collect --process-id <PID> --profile dotnet-sampled-thread-time --duration 00:00:15 -o cpu.nettrace
```

### 第 3 步：report topN 一锤定音

```powershell
dotnet-trace report cpu.nettrace topN -n 20          # 独占时间 Top
dotnet-trace report cpu.nettrace topN -n 8 --inclusive   # 包含时间 Top（含调用链）
```

或转火焰图跨平台看：`dotnet-trace convert cpu.nettrace --format speedscope` → speedscope.app。

## 6. 判定标准

1. `dotnet-counters`：CPU time ≈ 1 核跑满；`total_allocated` 每秒上 GB；gen0 收集每秒几十次。
2. `dotnet-trace report topN`：某个方法**独占时间 >50%**——它就是热点（本案例 `BuildRecords` 89.87%）。
3. 反证：看着「忙」的方法（`ComputeAggregate`）独占时间 <1%，证明「看着忙 ≠ 热点」。

## 7. 修复方向（对照）

- **别在热循环里做字符串格式化**：`DateTime`/`Guid` 格式化和字符串插值每调一次就分配。改为：格式一次复用、用 `ToString` 的廉价等价物、或批量缓冲再一次性写。
- **减少分配**：`ArrayPool`/`StringBuilder` 缓冲、`ValueStringBuilder`、避免每次分配 `DefaultInterpolatedStringHandler`。
- **衡量优先**：修之前先 `report topN` 拿到基线，修之后重测对比（和 diff 法同理）。
- 定位分配来源更细的：`dotnet-trace --profile gc-verbose` → `GCAllocationTick` 事件带托管栈。

## 8. 本次实测结果（case12-cpu.nettrace，15s 采样）

**第 1 步 · dotnet-counters（症状）**：

```
dotnet.process.cpu.time (user)        ≈ 1.1 s / sec      ← 约 1 核跑满
dotnet.gc.heap.total_allocated        ≈ 1.27–1.40 GB/s   ← 分配风暴
dotnet.gc.collections (gen0)          ≈ 50–56 次/秒      ← GC 在拼命回收
```

**第 2/3 步 · dotnet-trace report topN**：

```
Top 19 Functions (Exclusive)
1. Scenario12_CpuHotspot.BuildRecords(int32)   89.87%   ← 热点（隐藏瓶颈）
2. StreamWriter.Flush(bool,bool)                 8.02%   ← 报告循环的 Console.WriteLine
3. Thread.<PollGC>...                            1.85%   ← GC 轮询
...
11. Scenario12_CpuHotspot.ComputeAggregate(int32[])  0.01%  ← 看着忙，实际便宜

Top 8 Functions (Inclusive)
4. Scenario12_CpuHotspot.Run(...)               100%
5. Scenario12_CpuHotspot.BuildRecords(int32)    91.73%  ← Run 的耗时几乎全在 BuildRecords
```

- **判定闭环**：CPU ≈1 核 + 分配 1.3GB/s + gen0 GC 50/s（症状）→ `report topN` 显示 `BuildRecords` 独占 **89.87%**、包含 **91.73%**，而 `ComputeAggregate` 仅 **0.01%** → **每记录字符串插值 = 隐藏瓶颈，实锤**。
- 产物：`traces/case12-cpu.nettrace`（PerfView/VS 可开）、`traces/case12-cpu.speedscope.json`（speedscope.app 可开）、`traces/case12-counters.csv`。
