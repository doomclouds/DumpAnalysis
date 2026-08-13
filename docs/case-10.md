# 案例 10：线程池饥饿 —— sync-over-async（`.GetResult()` 阻塞）

> 目标：掌握现代 .NET 最常见的「假死」——**线程池饥饿（thread-pool starvation）**。控制台/ASP.NET Core 无同步上下文，async 续体跑在线程池上；当池线程**同步阻塞**一个「续体也需要池线程」的 async 操作时，池被堵死。练熟 `~*k` 与 `!clrstack` 认 `Task.Wait` 链。

## 1. 复现的故障

- **现象**：进程不崩溃、不占 CPU，但**工作不再推进**：`Task.Run` 排队的任务永远不执行，只有极少数线程在跑，其余全阻塞在等待上。
- **本质**：`async` 方法的续体（continuation）在无 `SynchronizationContext` 时**调度到线程池**。如果某个池线程对「自己也需要池线程才能完成的 async 操作」做**同步阻塞**（`.GetAwaiter().GetResult()` / `.Result` / `.Wait()`），就会占用池线程等一个「需要池线程才能产生的完成」——池线程被这种循环占满后，排队的任务（包括这些续体）永远等不到线程。

## 2. 根因机理

```text
线程池（max=4，已全被占）
├── TP Worker#0 ── SyncOverAsync(0).GetResult() ──▶ 阻塞等 RunAsync(0) 完成
├── TP Worker#1 ── SyncOverAsync(1).GetResult() ──▶ 阻塞等 RunAsync(1) 完成
├── TP Worker#2 ── SyncOverAsync(2).GetResult() ──▶ 阻塞等 RunAsync(2) 完成
└── TP Worker#3 ── SyncOverAsync(3).GetResult() ──▶ 阻塞等 RunAsync(3) 完成

RunAsync(n) = await Task.Delay(2000); ...     ← 续体需要「空闲池线程」才能跑
   但 4 个池线程全在 GetResult() 里等它 → 续体排队 → 永不完成 → 死锁式饥饿
排队的 12 个 Task.Run（worker 4..15）→ 永远等不到池线程
```

- **为什么控制台最容易踩**：没有 UI 同步上下文，`await` 默认回线程池。`.GetResult()` 同步占住池线程 → 池线程「等自己人腾位置」。
- 生产场景：ASP.NET Core 请求里对 `await` 链做了 `.Result`；事件处理器里同步等 `Task.Run`；队列处理器阻塞等异步 IO 完成。突发并发一上来，池立即饿死。

## 3. 与案例 07（死锁）的对比

| | 案例 07 死锁 | 案例 10 线程池饥饿 |
|---|---|---|
| 阻塞对象 | **Monitor 锁**（lock 交叉）| **Task 完成事件**（`.GetResult()` 内部 `ManualResetEventSlim`）|
| 阻塞线程 | 任意两个业务线程 | **线程池 worker**（`PortableThreadPool+WorkerThread`）|
| 恢复机制 | 永远死锁 | **池线程注入**（默认会慢慢加线程）——所以是「饿死」而非绝对死锁；本案例用 `SetMaxThreads` 封顶使饥饿确定可见 |
| 栈特征 | `Monitor.Enter_Slowpath` / `AwareLock` | `Task.InternalWait → ManualResetEventSlim.Wait → Monitor.Wait`，最顶层是 `TaskAwaiter.HandleNonSuccessAndDebuggerNotification`（`.GetResult()` 的同步阻塞点）|
| 修复 | 统一锁顺序 | **别同步阻塞 async**：`await` 一路到底 / `ConfigureAwait(false)` |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 10          # 仓库根目录运行场景 10；默认 16 个阻塞调用者
dotnet run -c Release -- 10 16 50    # 自定义：16 个调用者，50ms 启动间隔
```

程序把池上限压到 4，然后排 16 个 `Task.Run`，每个在 `RunAsync(idx).GetAwaiter().GetResult()` 上同步阻塞。4 个池线程全被占住后，日志显示 `pool-available=0`、只有前 4 个 worker 打印 blocking。此时抓 dump。

## 5. 抓取转储

进程活着（不崩溃）：`dotnet-dump collect -p <PID> -o dump10.dmp`（或 procdump）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `~*k` | **全部线程原生栈** | 多个 `.NET TP Worker` 卡在 `SyncBlock::Wait / Monitor_Wait / Thread::Wait`；还有 `.NET TP Gate`（`WaitHandle_WaitOneCore`）、`.NET Timer` 线程 |
| 3 | `~<n>e !clrstack` | **看一个阻塞 worker 的托管栈** | 栈顶是 `Task.Wait` 链：`ManualResetEventSlim.Wait → Task.InternalWait → TaskAwaiter.HandleNonSuccessAndDebuggerNotification`（`.GetResult()` 同步点）→ 你自己的 `PoisonCaller` → `ThreadPoolWorkQueue.Dispatch`（证明它是池 worker）|
| 4 | 对照进程日志 | 确认池可用线程数 | `pool-available=0`，排队任务数上升（`!threadpool` 若可用可看 `queued`）|

## 7. 判定标准

1. **多个 `.NET TP Worker` 线程卡在同一 `Task.Wait` 链**（`SyncBlock::Wait` / `ManualResetEventSlim.Wait`），且栈顶由 `.GetResult()` 的 `TaskAwaiter.HandleNonSuccessAndDebuggerNotification` 触发。
2. 这些 worker 的底层是 `PortableThreadPool+WorkerThread.WorkerThreadStart`（线程池线程，不是业务 Thread）。
3. 池里**没有空闲 worker**（进程日志 `pool-available=0` 或 `!threadpool` 的 queued 上升），排队的 `Task.Run` 永远拿不到线程。
4. 与死锁区别：阻塞对象是 **Task 完成事件**（Task 内部 `ManualResetEventSlim`），不是 Monitor 锁交叉。

## 8. 修复方向（对照）

- **禁止 sync-over-async**：对 async 链用 `await` 一路到底；不要 `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`。
- **`ConfigureAwait(false)`**：库代码里把续体放回线程池，别绑定调用方上下文。
- 必须同步时用「真正异步化」：`ValueTask`、`SemaphoreSlim.WaitAsync` 等；或把阻塞工作丢到 `Task.Run` 的**独立**执行器，不占池。
- 监控 `ThreadPool.GetAvailableThreads` / `ThreadPool.QueueUserWorkItem` 排队数，逼近 0 即告警。

## 9. 本次实测结果（leak10.dmp，dotnet-dump 99MB）

进程日志（池封顶 4、16 个阻塞调用者）：

```
  pool capped to 4 workers (SetMinThreads=True, SetMaxThreads=True, GetMaxThreads worker=4, io=4)
  [worker  0] blocking on async work ...
  [worker  1] blocking on async work ...
  [worker  2] blocking on async work ...
  [worker  3] blocking on async work ...
  blocked-for   0.0s  pool-available=   0  threads=14  ws=  21.3MB
  blocked-for   1.0s  pool-available=   0  threads=14  ws=  21.4MB
  ...
```

（worker 4..15 的 `Task.Run` 永远排队——只有前 4 个占住池的打印了。）

SOS 关键输出：

- **`~*k`** → **4 个 `.NET TP Worker`**（线程 8/10/12/13）全卡在：
  ```
  coreclr!SyncBlock::Wait
  coreclr!Monitor_Wait
  System_Private_CoreLib+0x3d1484 / +0x3ec74f / +0x3ec497 / +0x44dc55   ← Task 完成等待链
  ```
  另有 `.NET TP Gate`（`WaitHandle_WaitOneCore`）、`.NET Timer`（管 `Task.Delay`）线程；worker 1/2/3 空闲在 `NtWaitForWorkViaWorkerFactory`。
- **`~8e !clrstack`**（一个阻塞 worker）→
  ```
  System.Threading.Monitor.Wait
  System.Threading.ManualResetEventSlim.Wait
  System.Threading.Tasks.Task.SpinThenBlockingWait
  System.Threading.Tasks.Task.InternalWaitCore / InternalWait
  System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification   ← .GetResult() 同步阻塞点
  DumpAnalysis.Scenario10_SyncOverAsync.PoisonCaller(Int32)
  DumpAnalysis.Scenario10_SyncOverAsync+<>c__DisplayClass0_0.<Run>b__0()
  System.Threading.ThreadPoolWorkQueue.Dispatch
  System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart
  ```
- **判定闭环**：4 个池 worker 全在 `PoisonCaller → GetResult() → Task.InternalWait → ManualResetEventSlim.Wait` 上阻塞，等 `RunAsync` 完成；而 `RunAsync` 的续体需要空闲池 worker——**池 = 4 全占，续体与 12 个排队任务全饿死**。与进程日志 `pool-available=0` 完全对账，**sync-over-async 线程池饥饿，实锤**。
