# 案例 01：托管堆泄漏 —— 静态集合无限增长

> 目标：掌握用 WinDbg + SOS 定位「托管内存泄漏」的标准流程，练熟两个最核心命令 `!dumpheap -stat` 与 `!gcroot`。

## 1. 复现的内存问题

- **现象**：进程托管堆（gen2 + LOH）随时间持续增长，工作集（Working Set）同步上涨，GC 无法回收。
- **本质**：这是「可达对象累积」，不是对象"漏"了，而是它们被一个**始终存活的根**强引用着，GC 认为它们还活着。

## 2. 根因机理

```text
GC Root (static field Program.Cache)
   └── List<StaticLeakItem>._items[]
         ├── StaticLeakItem#0 ── Data: byte[512KB]   (LOH)
         ├── StaticLeakItem#1 ── Data: byte[512KB]   (LOH)
         └── ...
```

- `static` 字段是 GC 根，进程存活期间始终有效 → `List` 存活 → 每个元素存活 → 其内部大 `byte[]` 存活。
- 小对象晋升到 **gen2**；`byte[512KB]` 超过 85KB 阈值，直接分配在 **LOH**。
- 因此泄漏同时体现在 gen2 与 LOH 两个区域。

## 3. 复现步骤

```powershell
dotnet run -c Release -- 01    # 仓库根目录运行场景 01，记录打印出的 PID
# 或自定义：dotnet run -c Release -- 01 1024 100 0   (1MB/条, 100ms, 无限)
```

默认 512KB/条、100ms/条 ≈ 5 MB/s。运行约 30 秒（~150MB）后抓 dump。

## 4. 抓取转储（三选一）

```powershell
# A. dotnet-dump（最 .NET 原生，产出完整转储）
dotnet tool install -g dotnet-dump
dotnet-dump collect -p <PID>

# B. Sysinternals procdump（完整内存转储）
procdump -ma <PID> C:\path\leak.dmp

# C. 任务管理器 → 右键进程 → 创建转储文件
```

> 进阶：在 t1 和 t2 两个时刻各抓一份，对比两次 `!dumpheap -stat`，**数量增长最多的那个类型**就是泄漏源（"两次转储 diff 法"）。

## 5. WinDbg/SOS 分析步骤（对应 MCP 工具）

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `!eeversion` | 确认运行时可被 SOS 识别 | 显示 .NET 10 版本 |
| 2 | `.loadby sos coreclr` | 加载与 coreclr.dll 匹配的 SOS | 无需输出 |
| 3 | `!eeheap -gc` | 查看 gen0/1/2、LOH、POH 大小与边界 | gen2 + LOH 占大头 |
| 4 | `!dumpheap -stat` | 按类型统计实例数与总字节，**定位吃内存的类型** | `StaticLeakItem` 与 `System.Byte[]` 名列前茅 |
| 5 | `!dumpheap -type StaticLeakItem` | 列出该类型所有实例地址 | 数千个实例 |
| 6 | `!dumpheap -type System.Byte[] -min 85000` | 只看 LOH 上的大缓冲区 | 每个 ≈512KB |
| 7 | `!do <address>` | 查看单个对象字段 | 看到 `Data` / `Description` / `Id` |
| 8 | `!gcroot <address>` | **沿引用链回溯到根，一锤定音** | 链末端是 `static Program.Cache` |

MCP 映射：

- 用 `open_cdb_dump` 打开 `.dmp`，`symbols_path` 传 `srv*https://msdl.microsoft.com/download/symbols`，并勾选 `include_modules`。
- 其余命令用 `run_cdb_command` 逐条执行。

## 6. 判定标准（三条证据指向同一根因）

1. `!eeheap -gc`：gen2 + LOH 合计 ≈ 泄漏总量，且远大于 gen0/gen1。
2. `!dumpheap -stat`：`StaticLeakItem` + `System.Byte[]` 的实例数/字节数主导整个堆。
3. `!gcroot <任一实例>`：引用链最终终止于 `static Program.Cache`（而非栈、句柄或其它临时根）。

## 7. 修复方向（对照）

- 给缓存加容量上限 + 淘汰策略（LRU）；或用 `WeakReference<T>` / `ConditionalWeakTable`；或明确移除不再需要的元素。
- 修好后再抓 dump，`!dumpheap -stat` 中 `StaticLeakItem` 计数不再随运行时间增长。

## 8. 本次实测结果（leak01.dmp，dotnet-dump 464MB）

进程日志（512KB/条、100ms/条）：

```
  items=    100  managedHeap=  50.1 MB  ws=  72.1 MB  elapsed=  10.8s
  items=    200  managedHeap= 100.1 MB  ws= 122.7 MB  elapsed=  21.6s
  items=    300  managedHeap= 150.1 MB  ws= 172.6 MB  elapsed=  32.5s
  items=    400  managedHeap= 200.2 MB  ws= 222.6 MB  elapsed=  43.3s
  items=    500  managedHeap= 250.2 MB  ws= 272.6 MB  elapsed=  54.2s
  items=    600  managedHeap= 300.2 MB  ws= 323.4 MB  elapsed=  65.0s
  items=    700  managedHeap= 350.2 MB  ws= 450.7 MB  elapsed=  76.6s
```

（dump 在 ~68s 抓取，捕获 684 个实例。）

SOS 关键输出：

- **`!eeheap -gc`** → Small object heap（gen0/1/2）合计仅 ~200KB；**Large object heap 11 个段、合计 ~342MB**（GC 总堆 358,869,752 字节）。512KB 的 `Data` 越过 85KB 阈值，全部进 LOH。
- **`!dumpheap -stat`** →
  ```
  DumpAnalysis.StaticLeakItem     684         32,832
  DumpAnalysis.StaticLeakItem[]     2          8,240   ← List 底层数组（扩容过）
  System.Byte[]                   686    358,633,040   ← 主导：684 个 512KB payload
  System.String                   894        112,848   ← Description
  ```
- **`!dumpheap -type System.Byte[] -min 85000 -stat`** → **684 个 / 358,629,408 字节**，每个 524,288 字节（=512KB），全部 ≥ 85KB → 全在 LOH。
- **`!do <StaticLeakItem 01d80a8020c8>`** → 字段 `Id=7`、`Description`、`CreatedAt`、`Data`（指向 `byte[]`）。`!do` 该 `Data` → `Size 524312(0x80018)`、`524288 elements`、内容 `ZZZ…`（0x5A 填充，触页提交）。
- **`!gcroot 01d80a8020c8`** →
  ```
  HandleTable:
      000001d8084a13e8 (strong handle)         ← 静态字段承载句柄
           -> 01d808800028  System.Object[]    ← 静态数据
           -> 01d80b00d220  List<StaticLeakItem>   ← 即 static Program.Cache
           -> 01d80d418d00  StaticLeakItem[]   ← List 底层数组
           -> 01d80a8020c8  StaticLeakItem
  ```
  根链终止于**静态 `Cache`**（`HandleTable → Object[] → List → 数组 → 对象`），与第 2 节的机理图完全一致。
- **判定闭环**：LOH 大 `byte[]` 数(684) = `StaticLeakItem` 实例数(684)，684 × 512KB ≈ 342MB，实锤「每个对象拖着一个 512KB LOH 缓冲区，被静态 `Cache` 永久强引用」。
