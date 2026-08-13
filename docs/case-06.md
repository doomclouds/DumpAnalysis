# 案例 06：GC 句柄泄漏 —— GCHandle.Alloc 后从不 Free

> 目标：掌握"**句柄表强引用**"这第六类根。互操作/PInvoke 代码最常见——`GCHandle.Alloc` 拿到句柄后忘了 `Free`，GC 句柄表就永远强扣着目标对象。练熟新命令 `!gchandles`。

## 1. 复现的内存问题

- **现象**：`HandleLeakedItem` 及其 `Payload`（byte[64KB]）数量持续增长，即使没有任何业务根引用它们。
- **本质**：`GCHandle.Alloc(obj, GCHandleType.Normal)` 在 **GC 句柄表**里登记了一个强句柄。句柄表是 GC 根——只要句柄不 `Free`，目标对象及其引用的一切永远存活。

## 2. 根因机理

```text
GC Handle Table（GC 根）
   ├── 强句柄#0 ──────────→ HandleLeakedItem#0 ── Payload: byte[64KB]
   ├── 强句柄#1 ──────────→ HandleLeakedItem#1 ── Payload: byte[64KB]
   └── 强句柄#n ──────────→ HandleLeakedItem#n ── Payload: byte[64KB]
        ↑
   GCHandle.Alloc(...) 后从未调用 GCHandle.Free()
```

- `GCHandle` 是常见于"把托管对象传给原生代码"的场景：原生侧保存指针、托管侧必须显式 `Free`。忘了 `Free`，句柄表就永久扣住对象。
- 与案例 01 的关键区别：案例 01 的根是"静态字段 → List → 数组 → 对象"（有中间节点）；这里是**句柄直接指向对象**，中间没有任何业务对象。
- `GCHandleType.Pinned` 还会额外把对象**钉住**（GC 无法搬动它），长期积累会造成堆碎片——是句柄泄漏的加强版危害。

## 3. 与案例 01 的对比（都是"强句柄"根，但形状不同）

| | 案例 01 | 案例 06 |
|---|---|---|
| 根来源 | 静态字段（编译器用句柄承载） | **显式 `GCHandle.Alloc`** |
| 引用链 | 句柄 → `Object[]` → `List` → `数组` → 对象 | **句柄 → 对象**（无中间节点）|
| 数量对应 | `List` 的元素数 | **句柄数 = 对象数（`!gchandles` 直接可见）** |
| 修复 | 清空集合 / 加淘汰 | **`GCHandle.Free()` / 用 SafeHandle** |
| 新增命令 | `!gcroot` | **`!gchandles`** |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 06    # 仓库根目录运行场景 06；默认每 50ms Alloc 一个强句柄，从不 Free
# 自定义：dotnet run -c Release -- 06 50 0
```

64KB payload（未达 LOH 阈值，留在 gen0/1/2）。运行约 40 秒（≈800 个句柄）后抓 dump。

## 5. 抓取转储

同前：`dotnet-dump collect -p <PID> -o dump06.dmp`（或 procdump / 任务管理器）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `!dumpheap -stat` | 按类型统计 | `HandleLeakedItem` 与 `byte[]` 数量随运行时间增长 |
| 3 | **`!gchandles`** | **查看 GC 句柄表** | `Strong Handles` 数量 ≈ 泄漏句柄数（远超基线）|
| 4 | `!dumpheap -mt ...HandleLeakedItem -short` | 列实例 | 数百个实例 |
| 5 | `!gcroot <实例>` | 追溯根 | **句柄表强句柄直接指向该对象**，无中间节点 |
| 6 | `!eeheap -gc` | 看堆分布 | 对象晋升到 gen2（被句柄扣住逐代存活）|

## 7. 判定标准

1. `!gchandles`：`Strong Handles` 数量与 `HandleLeakedItem` 实例数一致（约等于泄漏量）。
2. `!gcroot <对象>`：根是句柄表强句柄，**直接指向对象**——没有经过 List/数组/委托等中间对象。
3. `!dumpheap -stat`：`HandleLeakedItem` 计数随运行时间线性增长，但没有任何"集合"持有它们。

## 8. 修复方向（对照）

- **配对释放**：每次 `GCHandle.Alloc` 都要有对应的 `GCHandle.Free()`（`try/finally` 或 `using`）。
- 用 `SafeHandle`/`CriticalHandle` 包装原生资源，释放路径不易遗漏。
- 能避免就避免：优先 `Span<T>`/`MemoryMarshal` 完成互操作，或 `Marshal.AllocHGlobal` + 显式释放（见案例 05）。
- 检查是否会用到 `GCHandleType.Pinned`：频繁 Alloc/Free Pinned 句柄也会引起堆碎片。

## 9. 本次实测结果（leak06.dmp，dotnet-dump 150MB）

进程日志（64KB payload、50ms/句柄）：

```
handles=200 managedHeap=12.6MB
handles=400 managedHeap=25.1MB
handles=600 managedHeap=37.6MB   ← 每 200 句柄 ≈ 12.6MB（=200×64KB），完全对账
```

SOS 关键输出：

- **`!gchandles -stat`** →
  ```
  Statistics:  HandleLeakedItem  646 个
  Handles:
      Strong Handles:       660   ← 646 泄漏 + ~14 运行时基线
      Pinned Handles:        1
      Weak Short Handles:   13
      Dependent Handles:     1
  ```
- `!dumpheap -stat` → `HandleLeakedItem` **646 个**；`System.Byte[]` 647 个 / **42.4MB**（64KB payload）；`GCHandle[]` 3 个（List 内部数组）。
- `!gcroot <对象 0240c76fd258>` →
  ```
  HandleTable:
      00000240c4631200 (strong handle)
            -> 0240c76fd258  HandleLeakedItem
  Found 1 unique roots.
  ```
  **句柄直指对象，无中间节点**——这就是 GCHandle 泄漏的签名（对比案例 01 的 `句柄→Object[]→List→数组→对象` 长链）。
- **判定闭环**：Strong 句柄数(646) = `HandleLeakedItem` 实例数(646)，实锤"每个对象都被一个未释放的强句柄扣住"。
