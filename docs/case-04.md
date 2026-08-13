# 案例 04：LOH 碎片化 —— 空闲内存被切成无法复用的洞

> 目标：理解"**内存很高但对象没泄漏**"的第三类问题——大对象堆（LOH）碎片化。练习新命令 `!dumpheap -type Free`，并学会 LOH 压缩（Compaction）这个修复手段。

## 1. 复现的内存问题

- **现象**：进程工作集持续增长、LOH 越来越大，但**没有对象被错误地强引用**——每个活对象都有合法引用。
- **本质**：LOH（≥85KB 对象所在堆）**默认不压缩**。我们把 LOH 铺满 2MB 对象、释放一半形成 2MB 的"洞"，再不断分配 **2.5MB** 的对象——2.5MB 塞不进 2MB 的洞，LOH 只能继续扩展，洞永远空着。已提交内存里混着大量 `Free` 空洞，无法被复用来满足新请求。

## 2. 根因机理

```text
LOH（不压缩，空闲块只能容纳 ≤自身大小 的请求）
┌──────────────────────────────────────────────┐
│ [2MB 活] [2MB Free·洞] [2MB 活] [2MB Free·洞] │  ← 2MB 洞，只有 ≤2MB 的请求能用
│ [2.5MB] [2.5MB] [2.5MB] [2.5MB] ...          │  ← 2.5MB 请求 → 跳过洞，追加到末尾
└──────────────────────────────────────────────┘
   已提交 = 活对象 + Free 洞；洞永远空着 → 内存只涨不放
```

- LOH 分配器按"空闲列表"分配，但只找**不小于请求大小**的空闲块；2MB 的洞无法满足 2.5MB 请求，且洞之间隔着活对象无法合并。
- 这就是真实服务器里"缓存/请求缓冲区大小各异 + 不断换入换出"后内存悄悄涨到 OOM 的典型原因。

## 3. 与案例 01/02/03 的区别

| | 案例 01 | 案例 02 | 案例 03 | 案例 04 |
|---|---|---|---|---|
| 类型 | 对象被根扣住 | 终结队列扣住 | 事件委托链扣住 | **空闲内存无法复用（布局问题）** |
| 内存增长来源 | 活对象累积 | fReachable 积压 | 订阅者累积 | **Free 空洞 + 新扩展** |
| 新增命令 | `!gcroot` | `!finalizequeue` | `!dumparray`/委托字段 | **`!dumpheap -type Free`** |
| 修复 | 缓存上限 | `Dispose` | 取消订阅 | **LOH 压缩 `CompactOnce` / 复用缓冲** |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 04    # 仓库根目录运行场景 04；默认 2MB 洞 + 2.5MB 填充，每 300ms 一个
# 自定义：dotnet run -c Release -- 04 2 300 0
```

阶段 1 铺满 60 个 2MB 块并释放一半（形成 30 个 2MB 洞）；阶段 2 持续分配 2.5MB 填充。运行约 35 秒后抓 dump。

## 5. 抓取转储

同前：`dotnet-dump collect -p <PID> -o dump04.dmp`（或 procdump / 任务管理器）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `!eeheap -gc` | 看 LOH 段 | LOH 多段、已提交量远大于实际活对象 |
| 3 | `!dumpheap -stat` | 按类型统计 | `System.Byte[]` 大；**`Free` 行 TotalSize 巨大（≈ 30×2MB）** |
| 4 | `!dumpheap -type Free` | **列出所有空闲块** | 一串 ≈2MB 的 Free 块（洞），地址散布在活对象之间 |
| 5 | `!dumpheap -type System.Byte[] -min 2000000` | 看 LOH 上的大数组 | 2MB（被保留的）与 2.5MB（填充）两类 |
| 6 | `!gcroot <某2.5MB数组>` | 证明不是引用泄漏 | 链末端是 `Program.live`（List，合法根）→ 活对象被正确引用 |

## 7. 判定标准

1. `!eeheap -gc`：LOH 已提交内存明显大于其中活对象总量，且有多段。
2. `!dumpheap -stat`：`Free` 行 TotalSize 很大（≈所有洞之和）；与案例 01-03 不同，这里**没有"吃内存且数量增长"的业务类型**，吃内存的是一堆 Free 空洞。
3. `!dumpheap -type Free`：看到一排 ~2MB 的空闲块——它们就是"装不下 2.5MB 请求"的洞。
4. `!gcroot <填充对象>`：有合法引用链（`Program.live`），**证明不是引用泄漏**，而是布局碎片化。

## 8. 修复方向（对照）

- **触发 LOH 压缩**（一次性）：`GCSettings.LargeObjectHeapCompactionMode = LargeObjectHeapCompactionMode.CompactOnce; GC.Collect(2);`——压缩后 `Free` 洞被回收，LOH 已提交内存显著下降。代价是压缩 STW 时间较长，不能频繁做。
- **避免碎片产生的根因**：用 `ArrayPool<byte>`/缓冲池复用大缓冲；让请求缓冲区按固定档位对齐（如统一 2MB 而非 2.5MB）；减少 LOH 大对象的频繁分配/释放。
- 应用内验证：程序支持 `compactAfter=<N>` 参数，跑 N 个填充后自动压缩一次，进程日志打印压缩前后 `lohSize/lohFree` 对比。

## 9. 本次实测结果（leak04.dmp，dotnet-dump 526MB）

进程日志（2MB 洞 + 2.5MB 填充，300ms/个）：

```
fillers=20  lohSize=166MB  lohFree=56MB
fillers=40  lohSize=216MB  lohFree=56MB
fillers=60  lohSize=266MB  lohFree=56MB
fillers=100 lohSize=366MB  lohFree=56MB   ← lohSize 每 20 个涨 50MB，Free 死守 56MB
```

SOS 关键输出：

- `!eeheap -gc` → LOH **14 个段、每段 ~30MB、共 ~420MB 已提交**。
- `!dumpheap -stat` → **`Free` 163 块 / 58,745,160 B（≈56MB）**；`System.Byte[]` 146 个 / 345MB（活对象）。
- `!dumpheap -type Free -min 1000000` → **28 个恰好 2,097,240 B（2MB）的空闲块**，正是被释放的 2MB 洞，一个不多一个不少（其余为尾段小碎片）。
- `!gcroot <填充数组 02919ba00080>` → 引用链末端是 `Program.live`（`List<byte[]>`），**合法引用，非引用泄漏**。
- **判定**：LOH 已提交 420MB，其中 ~56MB 是装不下 2.5MB 请求的 Free 空洞——布局碎片化，而非对象被扣住。若触发 `CompactOnce` 压缩，Free 将大幅回收。
