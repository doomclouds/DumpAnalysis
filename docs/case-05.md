# 案例 05：非托管内存泄漏 —— Marshal.AllocHGlobal 不释放

> 目标：掌握"**不是所有内存都在 GC 堆上**"的关键认知——原生内存 GC 看不见也收不回。学会跳出 SOS、用原生命令（`!address` / `!heap`）定位原生泄漏，并学会用"托管堆 vs 原生内存"的对比来识别它。

## 1. 复现的内存问题

- **现象**：进程工作集/私有提交持续增长，但托管堆几乎不变。
- **本质**：`Marshal.AllocHGlobal` 在托管堆**之外**分配原生内存。GC 看不到它、不会回收它，必须显式 `Marshal.FreeHGlobal` 释放。这里只分配不释放，原生内存无限增长。

## 2. 根因机理

```text
托管堆（GC 管理，能自动回收）        原生内存（GC 管不到，必须手动释放）
┌────────────────────────────┐     ┌───────────────────────────────┐
│ 几 MB 的 .NET 对象         │     │ [4MB 块] [4MB 块] [4MB 块] ... │
│ !eeheap 能看到，一直很小   │     │ 只 AllocHGlobal，从不 Free     │
└────────────────────────────┘     └───────────────────────────────┘
      GC 无感知                           谁都不管 → 只涨不还
```

- `Marshal.AllocHGlobal` 底层走 `LocalAlloc`，大块由 OS 以 `VirtualAlloc` 背书——每块是独立的 MEM_PRIVATE/MEM_COMMIT 内存区。
- 所以分析时必须看**原生内存布局**（`!address`）或**进程堆**（`!heap`），而不是 SOS 的托管堆命令。

## 3. 与案例 01-04 的本质区别

| | 案例 01-04 | 案例 05 |
|---|---|---|
| 内存位置 | **托管堆**（GC 可见） | **原生内存**（GC 不可见） |
| 分析工具 | SOS（`!dumpheap`/`!eeheap`/`!gcroot`…） | **原生命令（`!address`/`!heap`）** |
| 判据 | 托管堆 / 某类型 / Free 增长 | **托管堆很小 vs 原生私有提交巨大** |
| 修复 | 缓存/Dispose/取消订阅/压缩 | **`Marshal.FreeHGlobal` / SafeHandle / 托管缓冲池** |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 05    # 仓库根目录运行场景 05；默认每 400ms AllocHGlobal 一个 4MB 块，从不 Free
# 自定义：dotnet run -c Release -- 05 4 400 0
```

运行约 40 秒（≈100 个 4MB 块 ≈ 400MB 原生）后抓 dump。

## 5. 抓取转储

同前：`dotnet-dump collect -p <PID> -o dump05.dmp`（完整转储包含原生 MEM_COMMIT 区域，务必用完整转储）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `!eeheap -gc` | **看托管堆是否健康** | GC 已提交很小（几 MB），无异常 → 排除托管问题 |
| 3 | `!dumpheap -stat` | 再确认托管侧 | 没有吃内存的业务类型/Free → 托管侧干净 |
| 4 | `!address -summary` | **看原生内存总量** | 私有已提交巨大（≈ 全部原生块之和），`Other`/私有占大头 |
| 5 | `!address -f:MEM_PRIVATE` | 列出原生内存区 | 一串大小一致（=blockMb）的 MEM_COMMIT/MEM_PRIVATE 区域，数量 ≈ 分配的块数 |
| 6 | `!heap -s` | 若小块走进程堆，看默认堆 | 默认堆提交量随块数增长 |

> 判据核心：**第 2 步的 `!eeheap -gc`（小）与第 4 步的 `!address -summary`（大）之间的巨大落差**，就是原生泄漏的大小。

## 7. 判定标准

1. `!eeheap -gc`：托管堆已提交几 MB，与进程总内存差一个数量级以上。
2. `!dumpheap -stat`：没有任何类型显著占内存——**托管侧找不到元凶**。
3. `!address -summary`：私有/`Other` 已提交 ≈ 泄漏总量；`!address -f:MEM_PRIVATE` 里能看到成片同尺寸的原生块。
4. 对照进程日志：`nativeMB≈` 与 `ws` 同步上涨，而 `managedHeap` 恒定。

## 8. 修复方向（对照）

- **配对释放**：每次 `Marshal.AllocHGlobal` 都要有对应的 `Marshal.FreeHGlobal`（用 `try/finally` 或 `using` 包装）。
- 用 `SafeHandle`/`SafeBuffer` 或 .NET 6+ 的 `NativeMemory.Alloc/Free`，让释放路径不易遗漏。
- 能托管的就托管：优先 `ArrayPool<byte>`、`byte[]`、`Memory<byte>` 等，避免原生分配。
- 排查手法：抓 t1/t2 两份 dump，`!address -summary` 私有提交的增长量 = 泄漏速率。

## 9. 本次实测结果（leak05.dmp，dotnet-dump 520MB）

进程日志（4MB 块，400ms/个，逐页触碰）：

```
blocks=20 nativeMB≈80  managedHeap=0.1MB  ws=103.4MB
blocks=40 nativeMB≈160 managedHeap=0.1MB  ws=183.5MB
blocks=60 nativeMB≈240 managedHeap=0.1MB  ws=263.4MB
blocks=80 nativeMB≈320 managedHeap=0.1MB  ws=343.4MB   ← 托管堆恒定，WS 随原生涨
```

> 注意：`AllocHGlobal` 只提交虚拟内存，不触碰页面就不进工作集。所以演示里加了"每页写一字节"；真实场景看内存要同时看 **Working Set** 和 **Private Bytes/Committed**。

SOS + 原生分析关键输出：

- `!eeheap -gc` → **GC 已分配 76KB / 已提交 217KB**，各代几乎全空——托管侧完全健康。
- `!address -summary` → **MEM_COMMIT 599MB**，其中 **Heap 408MB、PAGE_READWRITE 405MB**。
- `!heap -s` → 各 NT 堆都很小（~8MB）→ 大块不是小堆里的，是 `LocalAlloc` 的 VirtualAlloc 大块。
- `!address -f:PAGE_READWRITE,MEM_PRIVATE` → **成片 `0x00401000`（4MB+4KB 块头）的 `Heap [ID:0; Type: Large Block]` 区域，约 100 个 ≈ 400MB**——就是每个 `AllocHGlobal(4MB)`，与 `nativeMB≈` 完全对账。
- **判定闭环**：托管堆 217KB vs 原生私有提交 405MB——元凶不在 GC 堆上，是未释放的原生块。
