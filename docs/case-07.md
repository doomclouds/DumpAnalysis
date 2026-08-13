# 案例 07：线程死锁 —— 交叉锁顺序（crossed lock ordering）

> 目标：掌握用 WinDbg + SOS 定位「托管死锁」的标准流程。这是**故障类**（进程不崩溃、永久阻塞），不是内存泄漏——dump 里堆是健康的，元凶在**线程的等待关系**上。练熟 `!threads`、`~*kn`、`!syncblk`、`!clrstack`。

## 1. 复现的故障

- **现象**：进程「卡死」，不再推进任何工作；线程数不增长；CPU 趋近 0。
- **本质**：两个线程各自持有一把锁，又都在等对方手里的那把锁——`lock(A)→lock(B)` 与 `lock(B)→lock(A)` 交叉。谁都无法继续，除非对方先放手；而对方也在等自己，于是永久死锁。

## 2. 根因机理

```text
GC Root → 线程对象（进程级的并发实体，不是内存问题）
            ├── DeadlockT1: 持有 lock A（对象 bc88）──等待──▶ lock B（bca0，被 T2 持有）
            └── DeadlockT2: 持有 lock B（对象 bca0）──等待──▶ lock A（bc88，被 T1 持有）
                              ↑ 谁也不让谁 → 死锁
```

- C# `lock` 是 `Monitor.Enter/Exit` 的语法糖，底层是 `AwareLock`，走 **SyncBlock**（同步块表）。
- 锁的顺序不一致是死锁的**必要条件**：如果所有线程都按同一顺序拿锁，就不会交叉等待。
- 真实世界：多线程 + 多把锁 + 嵌套调用，是最常见的死锁温床（转账 A→B / B→A 是教科书例子）。

## 3. 与案例 01-06 的本质区别

| | 案例 01-06 | 案例 07 |
|---|---|---|
| 问题类型 | **内存泄漏**（堆增长）| **故障 / 死锁**（不崩溃但卡死）|
| 内存现象 | 托管堆 / 原生 / 句柄增长 | **堆健康**，几乎不变 |
| 分析工具 | `!dumpheap` / `!eeheap` / `!gcroot`… | **`!threads` / `~*kn` / `!syncblk` / `!clrstack`** |
| 判据 | 某类型 / 引用链增长 | **两个线程互等对方的锁，形成环** |
| 修复 | 缓存/Dispose/取消订阅/压缩 | **统一锁顺序** / 避免嵌套锁 / 用 `lock`+超时 |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 07    # 仓库根目录运行场景 07；两线程交叉拿锁，永久阻塞
```

程序启动后，两线程各自持有第一把锁、等对方第二把锁，打印 `>>> Both threads are deadlocked` 后每秒报告一次。**等它确认死锁后抓 dump**（进程不崩溃，直接抓即可）。

## 5. 抓取转储

同前：`dotnet-dump collect -p <PID> -o dump07.dmp`（死锁是「活着但卡住」，完整转储能拍下所有线程的等待状态）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `!threads` | 列出托管线程 | 能看到 `DeadlockT1`、`DeadlockT2` 两个线程 |
| 3 | `~*kn` | **全部线程的原生栈** | 两个 `DeadlockT*` 线程都停在 `AwareLock::EnterEpilog` → `Monitor_Enter_Slowpath`（等 Monitor）|
| 4 | `!syncblk` | **同步块表：谁持有哪把锁** | 两个 SyncBlock 各被一个线程持有（`Owning Thread` 列）|
| 5 | `~<n>e !clrstack` | 对两个死锁线程各看一次托管栈 | 分别停在 lambda 的 `lock`（`Monitor.Enter`）——T1 等 `LockB`、T2 等 `LockA` |
| 6 | 合并 `!syncblk` + `!clrstack` | **形成死锁环** | T1 持 A 等 B；T2 持 B 等 A——谁都不让谁 |

## 7. 判定标准

1. `~*kn` 出现**两个以上线程**停在 `AwareLock::EnterEpilog`（Monitor 等待），而不是正常的 `UserSleep`/等待 IO。
2. `!syncblk` 显示这些线程**各自持有不同的锁**（`Owning Thread` 两两不同）。
3. 把 `!syncblk`（谁持有）与 `!clrstack`（谁在等）合并，能画出一个**等待环**：T1 持 A 等 B，T2 持 B 等 A → 死锁成立。
4. （可选）用 `!dlk` 让 SOS 自动检测死锁；生产环境更常用 `~*e !clrstack` 手工核对等待关系。

## 8. 修复方向（对照）

- **统一锁顺序**：所有线程按相同顺序获取多把锁（如总是先 A 后 B），消除交叉。
- **避免嵌套锁**：尽量单把锁；必须多把时，缩小持锁临界区。
- 用 `Monitor.TryEnter(timeout)` / `lock (obj) with timeout` 等带超时的方案，失败即重试或报错，避免永久卡死。
- 写多线程单元测试（`Deadlock` 检测工具 / 静态分析）提前暴露锁顺序问题。

## 9. 本次实测结果（leak07.dmp，dotnet-dump 98MB）

进程日志（两线程交叉持锁后永久阻塞）：

```
  [t2] holding B, waiting on A ...
  [t1] holding A, waiting on B ...
  >>> Both threads are deadlocked (t1 holds A waits B; t2 holds B waits A).
  deadlocked for 0.0s  threads=10  ws=20.4MB
  deadlocked for 1.0s  threads=10  ws=20.5MB
  deadlocked for 2.0s  threads=10  ws=21.3MB   ← 内存稳定，线程全在等锁
```

SOS 关键输出：

- **`~*kn`** → 线程 8（`DeadlockT1`）与线程 9（`DeadlockT2`）都停在：
  ```
  coreclr!AwareLock::EnterEpilogHelper
  coreclr!AwareLock::EnterEpilog
  coreclr!Monitor_Enter_Slowpath          ← 在等一个 Monitor 锁
  System_Private_CoreLib+0x3c3bd7
  ```
  （其余线程：主线程在 `UserSleep` 报告循环、Worker/EventPipe/Debugger/Finalizer/TieredComp 各司其职、两个 Worker 空闲在 `NtWaitForWorkViaWorkerFactory`。）
- **`!syncblk`** →
  ```
  Index  MonitorHeld  Recursion  Owning Thread Info          Owner
     2          3         1  000002818CD86070 46dc  8  000002819140bc88 System.Object
     3          3         1  000002818CD863A0 4404  9  000002819140bca0 System.Object
  ```
  线程 8（TID 46dc）持有 `bc88`；线程 9（TID 4404）持有 `bca0`。
- **`~8e !clrstack`** / **`~9e !clrstack`** →
  - 线程 8：`Scenario07_Deadlock+<>c.<Run>b__4_0()` 停在 `Monitor.Enter`（= `lock (LockB)`，T1 的 lambda）
  - 线程 9：`Scenario07_Deadlock+<>c.<Run>b__4_2()` 停在 `Monitor.Enter`（= `lock (LockA)`，T2 的 lambda）
- **判定闭环**（`!syncblk` × `!clrstack` 合并）：
  ```
  DeadlockT1 (46dc) 持 bc88(A)  ──等──▶ bca0(B)  [被 T2 持]
  DeadlockT2 (4404) 持 bca0(B)  ──等──▶ bc88(A)  [被 T1 持]
  ```
  两个线程互等对方的锁，形成环 → **交叉锁顺序死锁，实锤**。
