# 案例 03：事件订阅泄漏 —— 静态发布者 + 从不取消订阅

> 目标：掌握**间接引用泄漏**——泄漏对象本身没被任何"业务集合"持有，却被一个**静态发布者的委托链**扣住。练习沿委托链追溯根，并用 `!dumparray` 查看事件委托的调用列表。

## 1. 复现的内存问题

- **现象**：`EventSubscriber` 与其 `Payload`（byte[64KB]）数量随时间增长，即使应用早已不再需要这些订阅者。
- **本质**：`EventPublisher` 被静态字段 `Program.Publisher` 持有（GC 根）。每个订阅者执行 `Publisher.DataReady += subscriber.OnDataReady` 后，就被发布者事件委托的 `_invocationList` 引用。**发布者不死，订阅者永远不死。**

## 2. 根因机理（委托链）

```text
GC Root (static Program.Publisher)
   └── EventPublisher
        └── DataReady 字段 (System.EventHandler, 即 MulticastDelegate)
             └── _invocationList: EventHandler[]   ← Delegate.Combine 生成的数组
                  ├── [0] EventHandler ── _target → Subscriber#0 ── Payload: byte[64KB]
                  ├── [1] EventHandler ── _target → Subscriber#1 ── Payload: byte[64KB]
                  └── [n] EventHandler ── _target → Subscriber#n ── Payload: byte[64KB]
```

- C# `event` 是"多播委托"：每次 `+=` 都做 `Delegate.Combine`。订阅者多了之后，事件字段指向一个"组合委托"，其 `_invocationList` 是保存所有子委托的数组，每个子委托的 `_target` 指向订阅者对象。
- 因此对象图里**中间节点是 `System.EventHandler` 与 `EventHandler[]`**——这是事件泄漏区别于集合泄漏的标志性特征。
- 泄漏对象会随 GC 晋升到 gen2（因为一直被引用），payload 也跟着占内存。

## 3. 与案例 01/02 的区别

| | 案例 01 | 案例 02 | 案例 03 |
|---|---|---|---|
| 根 | 静态 `List` | 终结队列 fReachable | **静态发布者的事件委托链** |
| 中间节点 | `List`/数组 | — | **`EventHandler` / `EventHandler[]`** |
| 新增命令 | `!gcroot` | `!finalizequeue` | **`!do` 委托字段 + `!dumparray _invocationList`** |
| 修复 | 缓存上限/弱引用 | `using`+`Dispose`+`SuppressFinalize` | **取消订阅 `-=`** / 弱事件模式 |

## 4. 复现步骤

```powershell
dotnet run -c Release -- 03    # 仓库根目录运行场景 03；每 50ms 创建一个订阅者并订阅到静态发布者，从不取消订阅
# 自定义：dotnet run -c Release -- 03 50 0
```

每 100 个订阅者触发一次事件（证明调用列表里真有这些处理器）并强制一次 gen2 GC（让泄漏的订阅者晋升到 gen2，便于观察）。运行约 50 秒后抓 dump。

## 5. 抓取转储

同前：`dotnet-dump collect -p <PID> -o dump03.dmp`（或 procdump / 任务管理器）。

## 6. WinDbg/SOS 分析步骤

| 步骤 | cdb 命令 | 用途 | 预期结论 |
|---|---|---|---|
| 1 | `.loadby sos coreclr` + `!eeversion` | 加载 SOS | 显示 .NET 10 |
| 2 | `!dumpheap -stat` | 按类型统计 | `EventSubscriber` 数量随运行时间增长，`byte[]` 占大头 |
| 3 | `!dumpheap -type ...EventSubscriber -short` | 列出订阅者实例 | 数百个实例，大多在 gen2 |
| 4 | `!gcroot <订阅者地址>` | **沿委托链回溯根** | 链中出现 `EventHandler` → `EventHandler[]` → 最终是 `Program.Publisher`（静态根）|
| 5 | `!dumpheap -type ...EventPublisher` + `!do <发布者>` | 拿到发布者、读出 `DataReady` 字段 | 字段值是 `System.EventHandler` 委托对象 |
| 6 | `!do <委托对象>` | 查看组合委托 | `_invocationCount` 大、`_invocationList` 指向 `EventHandler[]` |
| 7 | `!dumparray <_invocationList地址>` | **列出所有订阅者的委托入口** | 每个条目 `_target` 指向一个 `EventSubscriber` |

## 7. 判定标准

1. `!dumpheap -stat`：`EventSubscriber` 计数随运行时间线性增长。
2. `!gcroot <订阅者>`：引用链末端是 `static Program.Publisher`，**中间经过 `System.EventHandler` / `EventHandler[]`**——这就是"事件泄漏"的签名。
3. `!do` 发布者的 `DataReady` 字段：`_invocationCount` 很大，`!dumparray _invocationList` 能看到一排指向不同订阅者的委托——证据闭环。

## 8. 修复方向（对照）

- **必须取消订阅**：生命周期短的订阅者要在销毁时 `Publisher.DataReady -= subscriber.OnDataReady`。
- 用弱事件模式（如 `WeakEventManager`、`ConditionalWeakTable` 或自研弱引用委托包装），让发布者不"强扣"订阅者。
- 让发布者与订阅者的生命周期匹配：短命对象订阅短命事件；避免把订阅者挂到进程级静态事件上。
- 修好后：`!dumpheap -stat` 里 `EventSubscriber` 计数稳定，`_invocationCount` 不随运行时间增长。

## 9. 本次实测结果（leak03.dmp，dotnet-dump 154MB）

进程日志（50ms/订阅，强制 GC 每 100 个）：

```
subscribers=200 heap=12.6MB   subscribers=400 heap=25.1MB
subscribers=600 heap=37.7MB   subscribers=800 heap=50.2MB   ← 线性增长
```

SOS 关键输出：

- `!dumpheap -stat` → **`EventSubscriber` 806 个；`System.EventHandler` 813 个**（委托签名，每个订阅者一个委托）；`System.Byte[]` 807 个 / 50.4MB（payload 被扣住）。
- `!gcroot <订阅者 01e450002048>` →
  ```
  HandleTable (strong handle)
    -> System.Object[]
      -> EventPublisher
        -> System.EventHandler          (DataReady 事件字段 = 组合委托)
          -> System.Object[]            (_invocationList)
            -> System.EventHandler      (订阅者处理器委托)
              -> EventSubscriber        (_target)
  Found 2 unique roots.
  ```
  链的中间节点全是委托/委托数组——事件泄漏的签名。
- `!do <组合委托 01e456061a40>` → **`_invocationCount = 0x326 = 806`**，恰好等于订阅者数量；`_invocationList` 指向一个 1024 槽位的 `EventHandler[]`。
- **判定闭环**：`_invocationCount`(806) = `EventSubscriber` 实例数(806)，实锤"事件字段把 806 个订阅者全扣住"。
