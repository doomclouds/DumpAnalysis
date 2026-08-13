# 案例 02：终结器积压 —— finalizer backlog / fReachable 队列

> 目标：与案例 01（静态根强引用）对照，掌握第二类托管内存问题——对象没有业务根引用，却被**终结队列**卡住无法回收。练熟新命令 `!finalizequeue`。

## 1. 复现的内存问题

- **现象**：托管堆随时间增长，`FinalizerBacklogItem` 与其内部 `byte[]` 的数量不断累积；进程内已有对象明明"没人再引用"，却一直不被回收。
- **本质**：对象带终结器（finalizer）却从未被 `Dispose()`，且终结器执行很慢，导致**唯一的终结器线程成为瓶颈**，`fReachable`（"已就绪待终结"）队列无限堆积，堆内对象逐代晋升、迟迟无法释放。

## 2. 根因机理

```text
分配 new FinalizerBacklogItem()（带 ~终结器，未 Dispose）
   │  对象不可达时，GC 把它从"待终结列表"挪到 fReachable（Ready for finalization）
   ▼
fReachable 队列（被终结器线程视为根）
   ├── 对象被 fReachable 引用 → 本次 GC 不回收，反而晋升到 gen1
   ├── 它引用的 byte[80KB] 同样存活 → 跟着晋升
   └── 终结器线程 1 秒处理 1 个（因为终结器里 Thread.Sleep(1000)）
       而分配速度远快于终结速度 → 队列越积越长，堆持续增长
```

- 关键：**任何带终结器的对象，只要不被显式 `Dispose`，就比普通对象多活至少一次 GC**（要先经 fReachable 等终结器跑完，下次 GC 才回收）。
- 终结器一旦变慢或阻塞（真实世界：终结器里做慢 IO、等锁、原生调用卡住），整个 fReachable 队列就卡死——这是生产事故里典型的"内存悄悄涨到爆"。

## 3. 与案例 01 的区别

| | 案例 01 | 案例 02 |
|---|---|---|
| 引用来源 | 静态字段 `Program.Cache` 强引用 | **没有业务根**，被 GC 终结队列（fReachable）引用 |
| 修复方式 | 给缓存加上限/用弱引用 | `using`/`Dispose()` + `GC.SuppressFinalize`，并让终结器快速执行 |
| 新增命令 | `!dumpheap -stat`、`!gcroot` | **`!finalizequeue`** |
| 主要堆区 | LOH（byte[1MB]） | gen0/1/2（byte[80KB] 未达 LOH 阈值） |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 02    # 仓库根目录运行场景 02；每 50ms 分配一个 80KB、带终结器但从不 Dispose 的对象
# 自定义：dotnet run -c Release -- 02 80 50 0
```

程序每 50 个对象强制 `GC.Collect(2)` 一次（模拟真实 gen0 压力触发 GC），把不可达的终结对象推入 fReachable；终结器 `Thread.Sleep(1000)` 模拟慢清理。运行约 40 秒后抓 dump。

## 5. 抓取转储

同案例 01：`dotnet-dump collect -p <PID> -o dump02.dmp`（或 procdump / 任务管理器）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `!finalizequeue` | **查看终结队列：各代待终结对象 + fReachable 积压** | `Ready for finalization` 数量很大（几百个）且不断增长 |
| 3 | `!dumpheap -stat` | 按类型统计 | `FinalizerBacklogItem` 与 `System.Byte[]` 数量随运行时间增长 |
| 4 | `!dumpheap -type FinalizerBacklogItem` | 列出该类型实例 | 数百个实例，地址大多落在 gen1/gen2 |
| 5 | `!do <地址>` | 查看单个对象 | 看到 `Data` 字段，确认对象"还活着" |
| 6 | `!gcroot <地址>` | 追溯根 | 根指向 **finalization queue / fReachable**（而非栈或静态字段）|
| 7 | `!threads` / `~*k` | 找到终结器线程 | 有一个线程停在不含用户栈帧、专跑终结器的线程上（或睡在 `Thread.Sleep`）|

## 7. 判定标准

1. `!finalizequeue`：`Ready for finalization`（fReachable）积压数量大，远超过终结器线程能及时处理的量。
2. `!dumpheap -stat`：带终结器的类型 `FinalizerBacklogItem` 数量随运行时间线性增长（而不是"某时刻被清理"）。
3. `!gcroot`：对任一实例，引用链最终终止于**终结队列**——证明它不是被业务代码引用，而是被 GC 的 fReachable 机制"扣住"。

## 8. 修复方向（对照）

- 正确释放：`using` / `try-finally` + `Dispose()` + `GC.SuppressFinalize(this)`。
- 终结器永远只做**快速、幂等**的清理（释放原生句柄），绝不在终结器里做慢 IO / 等待 / 拿锁。
- 若终结器被迫慢，可考虑改用 `IDisposable` + 引用计数或专门的清理线程。
- 修好后：`!finalizequeue` 的 `Ready for finalization` 应长期接近 0。

## 9. 本次实测结果（leak02.dmp，dotnet-dump 154MB）

进程日志（80KB/条、50ms、强制 GC 每 50 条）：

```
allocated=200 finalized=9  backlog=191
allocated=400 finalized=21 backlog=379
allocated=600 finalized=33 backlog=567   ← 终结速度 1个/s 远小于分配速度
```

SOS 关键输出：

- `!finalizequeue` → `generation 0: 1`, `generation 1: 24`, `generation 2: 0`, **`Ready for finalization 563 objects`**；统计中 `FinalizerBacklogItem` 610 个。
- `!eeheap -gc` → **gen2 12 个段 ≈ 48MB**（积压对象逐代晋升），LOH 为 0（缓冲 80KB 未达 85KB 阈值）。
- `!dumpheap -stat` → `FinalizerBacklogItem` 614 个；`System.Byte[]` **616 个 / 50,317,248 B ≈ 48MB**。
- `!gcroot <某实例>` →
  ```
  Finalizer Queue:
      000002281299ac30 (finalizer root)
            -> 02284e414040  FinalizerBacklogItem
  Found 1 unique roots.
  ```
  **根 = 终结队列**，无任何业务引用——与案例 01 的静态句柄根形成对照。
