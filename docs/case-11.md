# 案例 11：Timer 泄漏 —— `System.Threading.Timer` 从不 Dispose

> 目标：掌握**定时器泄漏**这类「定时器队列扣住状态」的问题。每个未 Dispose 的 `System.Threading.Timer` 在定时器队列里登记一个 `TimerQueueTimer`，它强引用着回调+状态；如果 Timer 又被根住不终结，状态（及其大 payload）永远不回收。练熟 `!dumpheap -stat` 对 `Timer/TimerQueueTimer` 的统计与 `!gcroot` 的定时器根链。

## 1. 复现的内存问题

- **现象**：`System.Threading.Timer` / `TimerQueueTimer` / `TimerJob` 三者数量**同步线性增长**，其 `byte[64KB]` payload 一起被扣住，堆不断变大。
- **本质**：`System.Threading.Timer` 有终结器（`TimerHolder`）。**只要 Timer 不可达，终结器就会把它 Dispose、移出定时器队列**，泄漏被掩盖。所以真正的 Timer 泄漏需要两个条件同时成立：
  1. Timer 被**根住**（静态字段/长期对象），终结器永远不跑；
  2. 从不 `Dispose()` → 定时器队列里的 `TimerQueueTimer` 永远存在，它强引用 `state`（这里是 `TimerJob`）。

## 2. 根因机理

```text
GC Root
├── static List<Timer>（根住每个 Timer，阻止终结器）
│     └── System.Threading.Timer
│           └── TimerHolder（有终结器，但根住后不会跑）
│                 └── TimerQueueTimer      ← 定时器队列的登记项
│                       └── state: TimerJob ── Payload: byte[64KB]
└── 定时器队列 List<TimerQueue> → TimerQueue
      └── TimerQueueTimer …（每 1s 触发一次，永久有效）
```

- `Timer` 对象 → `TimerHolder` → `TimerQueueTimer`：队列持强引用 `TimerQueueTimer`，`TimerQueueTimer` 持强引用 `state`。
- 只要 Timer 被根住 + 不 Dispose，`TimerQueueTimer` 就永久在队列里，`state` 及其引用的对象（payload）永不回收。
- **关键教学点**：不根住 Timer 时，终结器会掩盖泄漏（见第 8 节「对比」）——生产里「创建 Timer 后没引用」反而会被自动回收，真正危险的是「被根住但从不 Dispose」。

## 3. 与案例 01 / 06 的对比

| | 案例 01 | 案例 06 | 案例 11 |
|---|---|---|---|
| 根 | 静态 `List` 强引用对象 | GC 句柄表强句柄 | **静态 `List<Timer>` + 定时器队列 + .NET Timer 线程** |
| 中间节点 | `List`/数组 | 无 | **`Timer → TimerHolder → TimerQueueTimer`**（定时器三件套）|
| 新增命令 | `!gcroot` | `!gchandles` | **`!dumpheap -stat` 看 `Timer/TimerHolder/TimerQueueTimer` 同步增长 + `!gcroot` 定时器链** |
| 修复 | 缓存上限 | `GCHandle.Free()` | **`Timer.Dispose()` / `using` / `PeriodicTimer`** |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 11    # 仓库根目录运行场景 11；默认 64KB payload、每 100ms 建一个 Timer，从不 Dispose
# 自定义：dotnet run -c Release -- 11 64 100
```

每个 job 建一个 `new System.Threading.Timer(cb, job, 1s, 1s)`（每秒触发一次、永不复用），Timer 加入静态 `Timers` 列表根住。运行约 65 秒（≈600 个）后抓 dump。

## 5. 抓取转储

进程活着：`dotnet-dump collect -p <PID> -o dump11.dmp`（或 procdump）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `!dumpheap -stat` | 按类型统计 | **`System.Threading.Timer` / `TimerHolder` / `TimerQueueTimer` / 业务状态类型四者计数同步**（≈ job 数），`byte[]` 占大头 |
| 3 | `!dumpheap -type ...TimerJob -short` | 列状态实例 | 数百个实例 |
| 4 | `!gcroot <状态实例>` | **追溯根** | 根链出现 `Timer → TimerHolder → TimerQueueTimer`（定时器链）；还会看到 `List<TimerQueue> → TimerQueue → TimerQueueTimer`（队列本身）与 `.NET Timer` 线程（`TimerQueue.TimerThread`）|
| 5 | `!do <状态实例>` | 看字段 | 业务字段 + `Payload`（大 `byte[]`）|
| 6 | `!threads` / `~*k` | 找到 `.NET Timer` 线程 | 有一个线程在 `TimerQueue.TimerThread`（定时器线程）|

## 7. 判定标准

1. `!dumpheap -stat`：**`Timer` + `TimerHolder` + `TimerQueueTimer` 三者计数相等且 ≈ 业务状态实例数**，四者同步增长。
2. `!gcroot <状态实例>`：根链经过 **`Timer → TimerHolder → TimerQueueTimer`**（定时器三件套），或直接由**定时器队列**（`List<TimerQueue> → TimerQueue → TimerQueueTimer`）持有。
3. 存在 **`.NET Timer` 线程**（`System.Threading.TimerQueue.TimerThread`）——定时器线程在遍历队列。
4. 与案例 01（静态 List 直引）区分：这里中间节点是定时器基础设施对象。

## 8. 修复方向（对照）

- **必须 Dispose**：每次 `new System.Threading.Timer(...)` 都要配对 `timer.Dispose()`（`using` 或持有字段、生命周期结束即释放）。
- **`PeriodicTimer`**：.NET 6+ 的新 API，天然 `IAsyncEnumerable`，`await` 消费完自动结束，不易泄漏。
- **别把 Timer 当一次性随手对象**：要么完全不根住（靠终结器兜底），要么根住就必须显式 Dispose——两套方案别混。
- 排查手法：`!dumpheap -stat` 里 `TimerQueueTimer` 计数持续增长 = 有 Timer 没 Dispose。

## 9. 本次实测结果（leak11.dmp，dotnet-dump 144MB）

进程日志（64KB payload、100ms/个、静态根住 Timer）：

```
  jobs=    200  heap=  12.6 MB  ws=  35.8 MB  elapsed=  21.7s
  jobs=    400  heap=  25.2 MB  ws=  47.2 MB  elapsed=  43.5s
  jobs=    600  heap=  37.7 MB  ws=  67.2 MB  elapsed=  65.3s   ← 线性增长
```

SOS 关键输出：

- **`!dumpheap -stat`** →
  ```
  System.Threading.Timer           631
  System.Threading.TimerHolder     631
  System.Threading.TimerQueueTimer 631
  DumpAnalysis.TimerJob            631
  System.Byte[]                    633  /  41,371,992 B   ← 64KB payload 被扣住
  System.Threading.Timer[]           3                     ← 静态 Timers List 底层数组
  ```
  **四者计数完全相等（631）**——每个 Timer 一个 TimerHolder、一个 TimerQueueTimer，扣住一个 job。
- **`!do <TimerJob 02c0298100d0>`** → 字段 `Id=286`、`Payload` → `byte[]`（64KB）。
- **`!gcroot 02c0298100d0`** → 三条根路径全部指向同一个 job：
  ```
  ┌─ 静态 Timers 列表：Thread Run 的 List<Timer> → Timer[] → Timer → TimerHolder
  │     → TimerQueueTimer → … → TimerJob
  ├─ 定时器队列：HandleTable → Object[] → List<TimerQueue> → TimerQueue[]
  │     → TimerQueue → TimerQueueTimer → … → TimerJob
  └─ .NET Timer 线程：Thread 9860 (TimerQueue.TimerThread) → List<TimerQueue>
        → TimerQueue → TimerQueueTimer → … → TimerJob
  ```
- **判定闭环**：`Timer(631) = TimerHolder(631) = TimerQueueTimer(631) = TimerJob(631)`，每个 job 都经 `Timer → TimerHolder → TimerQueueTimer` 被定时器队列扣住，`.NET Timer` 线程持续触发——**未 Dispose 的 Timer 根住状态与 payload，实锤**。

> 对比：若只 `new Timer(...)` 不根住（`_ = new Timer(...)`），`TimerHolder` 终结器会 Dispose 掉 Timer、移出队列，`TimerQueueTimer` 计数不会累积——泄漏被掩盖。本案例特意**静态根住 Timer** 来复现真实的「根住 + 不 Dispose」泄漏。
