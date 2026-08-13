# 方法篇：两次转储 diff 法（动态取证）

> 适用范围：任意"内存随时间增长"的场景，用来**在不知道代码、不知道根的情况下，先定位"谁在涨"**。是 `!gcroot` 这类静态追根之前的**第一刀**。

## 1. 核心思想

同一个进程实例，在 t1、t2 两个时刻各抓一份转储，对比两者的 `!dumpheap -stat`：

- **数量/大小增长最多的类型（或类型簇）＝泄漏源**。
- 不需要预先知道代码或根在哪——diff 自己会"指认"。

```text
t1 dump  ─┐
          ├─ 对齐每个类型：t2.Count − t1.Count、t2.Size − t1.Size
t2 dump  ─┘        ↓
          Δ 最大的类型 = 泄漏对象；再对它 !gcroot 找根
```

## 2. 两个硬前提（不满足则结果无意义）

| 前提 | 原因 | 验证方法 |
|---|---|---|
| **同一进程实例** | 跨进程/跨运行，模块加载地址不同，同一类型的 MT 地址不同，无法按 MT 对齐 | 两边的 MT 应一致（案例 01 演示中 `StaticLeakItem` 两侧 MT 都是 `7ffed09720a8`）|
| **稳态后再抓 t1** | 启动期初始化对象会污染 diff | 先跑一会儿（如 10~15s）再抓 t1，t1/t2 间隔足够大（如 15s）|

## 3. 标准步骤

### ① 抓取两份转储

```powershell
$exe = "<你的程序>.exe"
$p = Start-Process $exe -PassThru

Start-Sleep -Seconds 15                      # 等稳态
dotnet-dump collect -p $p.Id -o heap_t1.dmp  # t1
Start-Sleep -Seconds 15                      # 等一段时间让内存继续涨
dotnet-dump collect -p $p.Id -o heap_t2.dmp  # t2
Stop-Process -Id $p.Id -Force
```

> 间隔越长、泄漏越快，diff 越明显。目标是让泄漏类型增长**一个数量级**以上于其它噪音。

### ② 分别跑 `!dumpheap -stat`（WinDbg MCP）

对 t1、t2 各开一个 cdb 会话，各执行：

```
.loadby sos coreclr
!dumpheap -stat
```

> 同一进程的两份 dump，MT 一致，直接按 MT/类名逐行对齐即可。

### ③ 做 diff，按 Δ 排序

逐行比较 `Count` 与 `TotalSize`。**真正需要看的通常只有 2~4 行**：其余运行时类型应基本不动。

### ④ 对增长最大的类型收尾

在 t2 里：

```
!dumpheap -mt <增长类型的 MT> -short   # 拿实例地址
!gcroot <地址>                         # 找到根，锁定代码
```

## 4. 解读规则：增长出现在哪一行，指向哪类问题

| `!dumpheap -stat` 里的增长 | 指向 | 对应案例 |
|---|---|---|
| 某业务类型 + 其内部大数组 同步涨 | **引用泄漏**（对象被强引用扣住） | 01 静态集合、03 事件订阅 |
| 某带终结器类型 涨，且 `!finalizequeue` 的 `Ready for finalization` 积压 | **终结器积压** | 02 |
| **`Free` 行** TotalSize 巨大/增长 | **堆碎片化**（空闲块无法复用） | 04 |
| 托管堆几乎不涨，但 `!address -summary` 私有提交巨大 | **非托管泄漏** | 05 |
| 都正常但物理内存涨 | JIT、原生 image、堆外 | — |

> 注意区分"**类型涨**"和"**Free 涨**"：前者是引用泄漏，后者是碎片化——两者都是内存涨，但修法完全不同。

## 5. 本次实测样板（案例 01，PID 6340）

t1 = 15s，t2 = 30s，1MB/条、150ms：

| 类型 | t1 | t2 | Δ |
|---|---|---|---|
| `StaticLeakItem` | 98 | 196 | **+98** |
| `System.Byte[]` | 100 / 98.0MB | 198 / 196.0MB | **+98 / +98.0MB** |
| `System.String` | 283 / 51KB | 381 / 62KB | +98（Description）|
| `Free` | 183 / 16KB | 287 / 30KB | +14KB（可忽略 → 非碎片化）|
| **堆总量** | 102.9MB | 205.7MB | **+102.8MB** |

**解读**：15s 内堆涨 103MB，全部可归因于 `+98 StaticLeakItem + 98×1MB byte[] + 98 字符串`，其它类型纹丝不动；`Free` 只 +14KB → 排除碎片化。随后 `!gcroot` 确认根 = `static Program.Cache`。

账目自洽：`+98 × 1MB ≈ 98MB ≈ 堆增长`。

## 6. 多个类型一起涨时：看"相关簇"

真实泄漏往往不止一个类型涨（如事件泄漏：`Subscriber` + `EventHandler` 同步涨）。把**同步涨的一组**看作一个簇，簇就是签名。不要盯着单个类型孤立判断。

## 7. 自动化

- `dotnet-dump analyze <dump> -c "dumpheap -stat" -c "exit"` 可以把 stat 导出到 stdout/文件，写脚本 diff 两边，输出 ΔTop10——即生产环境"堆 diff 告警"。
- 常见做法：CI/巡检定时抓 t1/t2，Δ 超过阈值（如增长类型占堆增长 >80%）即告警。

## 8. 局限

- **只能回答"谁在涨"，回答不了"为什么"**：需要再配合 `!gcroot` / `!finalizequeue` / `!address` 等定位根因。
- 泄漏极慢、间隔内增量小于噪音时 diff 不明显 → 拉大间隔或加大负载。
- 多路泄漏并存时，diff 只能给出"最大的一路"，其它路可能被淹没 → 逐个处理后再抓一对验证。
