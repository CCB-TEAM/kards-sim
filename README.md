# kards-sim

KARDS 本地对局模拟器：把客户端蓝图**直接转译成 C#**，用它来训练 / 评测 AI。

不是「读卡面文本猜效果」，而是把游戏自己的 Kismet 字节码逐条翻译成 C# 控制流，
所以卡牌逻辑与客户端一致，没有猜测成分。

---

## 核心思路

| 层 | 做什么 |
|---|---|
| `KismetDecompiler`（另一个仓库） | 补出反编译器的控制流边界（`EX_ComputedJump` 分发入口） |
| `KardsTranspiler` | **Kismet AST → C#**，一个函数一个静态方法，一条语句一个 label |
| `KardsSim/Generated` | 1670 个资产的直译产物，约 37 万行 |
| `KardsSim/Kismet` | 运行时：`IHost` 接口 + 值模型 + 纯函数/引擎库实现 |
| `KardsSim/Bridge` | 把直译产物接到游戏引擎（这是「hook 缝」） |
| `KardsSim/Engine` | 对局规则、动作合法性、回合流程 |

**关键点：** Kismet 字节码自带跳转（`EX_Jump` / `EX_JumpIfNot` / `EX_ComputedJump`），
所以直译成 `label + goto` 是 1:1 的，**不需要重建控制流图**。
`EX_PushExecutionFlow` / `EX_PopExecutionFlow` 用一个真的 `Stack<int>` + `switch` 承载。

**为什么不用反编译出来的伪代码：** 伪代码是有损的；`FunctionExport.ScriptBytecode`
是 UAssetAPI 解析好的 `KismetExpression[]` AST，无损。转译走 AST。

**为什么不用文本解析：** 见 `KardsSim/Effects/EffectPlanner.cs`（保留作对照，已不启用）。
文本解析只能覆盖简单卡，且永远有偏差。

---

## 目录

```
KardsTranspiler/           Kismet AST → C#。核心是 Emitter.cs
KardsSim/
  Generated/               直译产物（1670 个 .g.cs，已入库，克隆即可编译）
  Kismet/                  IHost / Val / KObj / Host（纯函数 + 引擎库）
  Bridge/
    EngineHost.cs          分派器：原语 → 转译产物递归
    CardDispatch.cs        触发器 → 卡牌事件函数
  Core/                    GameState / CardDef / 枚举 / RNG
  Engine/                  对局规则、动作、伤害、回合
  Server/HttpServer.cs     HTTP 接口（给不同 AI 后端用）
  Ai/Encoder.cs            状态/动作编码
KardsDataExtract/          从资产里抽 cards.json
cards.json                 1906 张卡的定义（运行时需要）
```

---

## 构建

```bash
dotnet build KardsSim/KardsSim.csproj -c Release
```

`Generated/` 已在仓库里，直接编译即可，**不需要游戏资产**。

想跳过生成代码编译（只调引擎）：

```bash
dotnet build KardsSim/KardsSim.csproj -c Release -p:SkipGenerated=true
```

---

## 运行

```bash
# 自对弈（默认走直译产物），并打印宿主 API 覆盖率 + 触发点完整性检查
dotnet run --project KardsSim -c Release -- --mode selfplay --games 200

# 用旧的文本解析效果表跑（对照用）
dotnet run --project KardsSim -c Release -- --mode selfplay --games 200 --legacy

# HTTP 接口，默认 http://127.0.0.1:8642/
dotnet run --project KardsSim -c Release -- --mode serve

# 机制单测（带断言的，能失败）
dotnet run --project KardsSim -c Release -- --mode tests
```

---

## 七个审计模式

跑多少局自对弈都不能证明「完备」—— 随机对局抽不到那些卡，等于没测。
所以除了对局，还有七个专门查完备性的模式：

| 模式 | 查什么 | 为什么需要 |
|---|---|---|
| `apicheck` | 静态全扫 `H.Call` 的所有目标，判断有没有地方接 | 不依赖采样运气；能区分「卡牌逻辑可达」和「仅 UI 可达」 |
| `smoke` | 逐卡强制触发它注册的**每一个**触发点 | 自对弈抽不到的卡在这里一定被跑到 |
| `triggerhits` | 运行期实测每个触发点命中多少次 | 「引擎发了」不等于「有卡接住」，静态审计看不出发空跑 |
| `triggers` | 对照「卡牌注册 / 引擎发出 / 游戏侧分派器」三张表 | 找出白板卡 |
| `rules` | 客户端规则库（`cardsCheckFunctions`）的 18 条否决理由 vs 引擎侧现状，外加一个 Guard 行为抽检 | 规则只在客户端存在时，AI 会学到不存在的规则 |
| `paramaudit` | 把引擎原语的实现和 UHT 头文件签名逐条比对，找「UHT 是 void 带 out、实现却直接 return」 | 这类缺陷不报错、不进 `Unhandled`，只是值永远传不回来 |
| `repro1` | issue #1 的端到端复现：击杀 → 变老兵 → 打 HQ 3 点 | 跨「事件载荷 → 持久帧变量 → 老兵换用 → HQ 合成对象」四层，是回归哨兵 |

### out 参数：两类静默失败

`paramaudit` 存在的理由值得单独说，因为这类缺陷最难发现：

- **写错通道**：调用点读的是 **out 槽**（`Val.Out(__v => L["x"] = __v)`），
  实现却 `return` 了值。调用方永远读到空值。
  反过来（调用点读返回值、实现却写 out 槽）同样恒为空。
  判断依据是**调用点形态**，不是函数名。
- **根本没判出 out**：签名查不到时转译器按 in 处理，
  而 out 实参在调用点是个裸局部变量，被调方写进去的值传不回来。
  这类靠 `NativeOutParams` 从调用点证据反推（见该文件的文档）。

两种都表现为「效果跑了但什么都没发生」：不抛异常、不进 `Unhandled`、
自对弈的「未实现调用 0%」照样是绿的。

### 为什么需要多个而不是一个

这三个层次会给出**互相矛盾**的结论，而这正是它们的价值：

- **静态覆盖**说 61/61 完整 —— 因为它只扫 `Fire(Trigger.X)` 字面量，
  包括那些**从来没被调用过**的辅助函数。本轮就是这么抓出 23 个死包装函数的。
- **运行期命中**说 34 个触发点空跑 —— 但其中大多数只是随机对局没抽到对应卡。
- **smoke** 才能分辨：强制触发后 0 异常 0 未实现，说明分派是通的，空跑纯粹是采样问题。

`apicheck` 把 2814 个调用目标分成两类：**卡牌逻辑可达**（从卡牌 ubergraph 或
`BP_CardFunctions` / `BP_GameState_Battle` 发起）和**仅 UI / 平台可达**。
前者是 0 —— 这才是「完备」的可验证定义。

---

## 触发点是怎么接上的

游戏的触发分发链路（已从直译产物里确认）：

```
卡牌 CDO.usedTriggers  (TArray<ERegisteredCardFunction>)
    ↓ CreateCardObject 遍历
UpdateCardFunctionTriggerMap(triggerEnum, cardID)
    ↓ 写入
GameState.CardFunctionTriggers  (map<triggerID, set<cardID>>)
    ↓ FetchAllCardsWithEventTrigger 查
Execute*Events  →  逐个调响应卡的事件函数
```

**「什么时候发哪个触发点 / 谁响应 / 什么顺序」这些信息游戏自己就有** ——
63 个 `Execute*Events` 函数把 triggerID 硬编码在里面。

引擎侧对应的是 `Engine/GameEngine.Triggers.cs` 里的 43 个语义化包装
（`FireTurnStartTriggers` / `FireDestroyTriggers` …），集中在一处免得散落漏掉。

**注意**：包装函数必须写成 `if (x) Fire(Trigger.A, c); else Fire(Trigger.B, c);`
这种显式分支，不能写 `Fire(x ? A : B, c)` —— 审计是静态扫字面量的，三元表达式它看不见。

---

## 根因：Intel（情报）

`cipher` 值 = 打出时随机翻开对手手牌的张数，然后通知注册了 `OnIntelTriggered` 的卡。

两个容易搞错的地方：

1. **Intel 不是关键字位，也不是标签**，是卡上的数值字段 `cipher`
   （`AddIntelToCard` 会把它 clamp 到 0..9）。数据在 `cards.json` 的 `raw.cipher`。
2. **`OnIntelTriggered` 的第二个形参是整数，不是卡对象** —— 而其他所有触发点的
   第二个形参都是「相关卡」。混用不炸但结果全错，所以单开了
   `CardDispatch.FireWith` 走显式实参。

翻牌只加 `seenByCipher` 标记，**不动手牌顺序**：顺序是对局的可见信息，
洗错地方会让同一 seed 跑出不同结果（`--mode tests` 有断言守这条）。

---

## 已修过的坑（都是静默出错型，不测发现不了）

| 症状 | 根因 |
|---|---|
| 判断永远为假、整段效果被跳过 | 无 context 调用成员函数时接收者发成了 `Val.Nothing` |
| 整张卡抛 `KeyNotFound` | 局部变量直读 `L["x"]`；Kismet 局部槽是零初始化的 |
| 条件判断走反 | 宿主规则回调只回 `bool`，函数**返回值被丢掉** |
| 遍历型卡拿不到元素 | `Array_Get` 的结果走 out 形参，宿主没回写 |
| 卡面文本变成类型名 | `EX_TextConst` 的 `Value.ToString()` 不是字面量，要按 `TextLiteralType` 取字段 |
| 栈溢出 | 依赖蓝图只当签名表、没转译（见上） |
| 栈溢出（第二类） | 转发壳：`A.Foo` 调 `B.Foo`，`B` 没 `Foo` 就落回兜底分支调回 `A.Foo` |
| 编译报 CS0131 | `SetArray/SetSet/SetMap` 的属性是赋值目标，被当成右值发射 |
| 原语读到错的参数 | 调用约定是 `{接收者, 实参...}`，`a[0]` 是接收者，实参从 `a[1]` 起 |
| 追加进数组的项全丢 | 局部 TArray 没初始化：UE 零初始化为空数组，直译产物读成 `Nothing`，`Array_Add(Nothing, x)` 是空操作 |
| 牌打出后「找不到自己」 | `FindCard` 只搜场上和手牌，而 order 打出后立刻进弃牌堆 → `GetCardFromID(自己)` 返回空，整条效果链断掉 |
| 所有战斗都不掉血 | `OnCardDealDamage_ModifyDamageDealt` 缺原生默认体（只 24/1638 张卡覆盖它）→ 「不修改伤害」变成「伤害归零」 |
| 每次 select 都取错分支 | `SelectInt/SelectString/…` 的实参顺序是 `(A, B, 条件)`，不是 `(条件, A, B)` |
| 所有单位都能移动+攻击 | 客户端是「移动与攻击二选一」，只有坦克和带 `CanMoveAndAttackInTheSameTurn` 的单位例外 |
| 加指挥点槽被卡在 12 | `getMaxPossibleKredits` 应给**绝对上限 24**（12 只是每回合自然增长的上限） |
| 50 张反制指令从不触发 | 读的是一个谁都不写的 `gotcha` 布尔字段；真实字段是 `gotchaActivated`（int，>0 = 已激活） |
| 持续站场的光环完全不生效 | `OnEnterPlay` / `OnLeaveBoardOrOwner` 这些**裸自事件**不在 `ERegisteredCardFunction` 里，注册触发点那条路找不到它们，必须在真实时机由引擎直接补调 |
| 光环离场后撤销不掉、加成永久残留 | 撤销要在「牌还站在场上」时做（客户端是 `ExecuteOnBeforeLeaveBoardOrOwnerEvents`）；在 `FireDestroyTriggers` 里调时牌已经标了 `Destroyed`、`location` 变成弃牌堆，`GetCardsToTheLeft(self)` 恒空 |
| 两个光环叠加时互删 | 攻击加成只记总额，撤销只能清零 → 改成客户端那样按 `instigatorID` 记账（`buffsFromCards`） |
| 「是否被本卡加成过」判断永远错 | `isBuffedByCard` 是卡上的原生方法，实参是 `(instigatorID, out)`，接收者才是卡；按 `a[1]` 取卡等于拿一个整数当卡查 |

---

## 状态

- 1671 个资产 / 6434 个函数，**0 空体**，1 处未支持节点（`BP_EntryPointActor`，非战斗逻辑）
- 47.5 万行直译代码 **0 错误编译通过**
- 自对弈 150 局：**0 异常 / 0 卡死 / 0 非法动作 / 0 未实现调用**
- 全卡 1638 张强制演练：**0 异常 / 0 未实现调用**
- 触发点覆盖 **61/61**（卡牌注册的触发点，引擎全部会发）
- 宿主 API：卡牌逻辑可达的缺口 **0 个**（另外 424 个仅 UI / 平台可达，无头模拟不会走到）
- 动作空间：出牌 = 手牌 × 目标 × 三选一分支，另有抉择区与 EndTurn，
  `stateSize=264`、`actionSize=349`
- 已知未做：没有 UI 就无法复现的选择点 —— 部分卡（如 `card_unit_hampshire_regiment`）
  的 Develop 结果在客户端是**UI 直接做的**，它们没有自己的 `OnHandTargetSelected`，
  无头模拟里选择能被发起和结算，但效果不落地
- 已知未做：卡组 40 张（`Rules.DeckSize=30`，wiki 说 40，但客户端 CDO 说 30，倾向信客户端）

---

## 重新生成直译产物

需要游戏资产（`.../Content/Blueprints/Cards` 全量导出 + `m.usmap`）：

```bash
dotnet run --project KardsTranspiler -c Release -- \
  --in  <Cards 目录> \
  --usmap <m.usmap> \
  --out KardsSim/KardsSim/Generated \
  --sigdeps <依赖蓝图目录>
```

`--sigdeps` 指向 `BP_GameState_Battle` + `Library/*` 等**卡牌目录之外**的蓝图。
它们不只是签名来源，**也要一起转译**——否则宿主按名字找不到 `GetXxx`，
会退回 `BP_CardFunctions` 里的同名转发壳，而壳又调回 `GameStateRef.GetXxx`，
名字一样、接收者没落地，就无限递归到栈溢出。

`--sigdeps` 还必须带 `BP_CardFunctions.uasset`（卡牌 API 的 out/in 只能从它的
`FunctionExport.LoadedProperties` 拿，头文件覆盖不全）。

---

## 转译器怎么定 out / in

这是最容易错的地方，做成了四级回退：

1. 声明资产自己的 `FunctionExport.LoadedProperties`（有 `CPF_OutParm` / `CPF_ConstParm` 原生标志）——最权威
2. 跨资产蓝图扫描，按函数名索引（`EX_LocalVirtualFunction` / `EX_VirtualFunction` 只带名字、不带 `FPackageIndex`）
3. UHT 头文件里 `T&` vs `const T&`
4. `EngineLibraryParams` 里给引擎库手写的表

同理，**接收者**按 import 链第二层（声明类）判定：
`Function → Class → Package`。`/Script/kards` 下的是成员函数（接收者 `self`），
`KismetArrayLibrary` 之类的是静态库（接收者 `Val.Ref("<库名>")`）。

---


## 状态

- 1671 个资产 / 6434 个函数，**0 空体**，1 处未支持节点（`BP_EntryPointActor` 的
  ubergraph，非战斗逻辑）
- 47.5 万行直译代码 **0 错误编译通过**
- 自对弈 150 局：**0 异常 / 0 卡死 / 0 非法动作**，宿主未实现调用 **0%**
- 单卡机制验证（`--mode tests`）**67/67 通过**：Intel、事件载荷、老兵升级、Blitz、
  数据表、三选一、CDO 标志位、移动/攻击二选一、指挥点槽 24、反制指令、二段式抉择，
  以及持续站场光环的加/撤（`card_unit_flaming_matilda_anzac`）

---

## 出牌效果是怎么接上的（本轮补的关键一环）

卡牌效果的主入口叫 `OnPlayedFromHand`（1638 张卡里 **942 张**有它），
但它**不是** `ERegisteredCardFunction` 的成员，所以按 Trigger 枚举名找函数的触发分发
永远找不到它。客户端走的是 `BP_CardFunctions.CardPlayedFromHand` → `OnPlayedFromHand`；
无头模拟里出牌流程是引擎自己实现的，就必须自己补这一步。

以前写的是 `RunCardEffect(c, Trigger.NotAvailable)`，而 `CardDispatch.Fire` 对
`NotAvailable` 是直接 `return false` —— 于是 **691 张 order 与 241 张部署单位的效果
全都不执行，且不报错**。现在由 `EngineHost.PlayCardFromHand` 显式调它。

同理，Intel 原本指望 `CardPlayedFromHand` 里的 `SetCardsSeenByCipher` 来发，
而那条链引擎不走，所以引擎现在自己发一次（`EngineHost.ApplyIntel`）。

**另一条死路**：蓝图侧的触发登记表 `CardFunctionTriggers` / `AllCardsInBattle`
从来没被填充过（实测恒为 0），所以 `FetchAllCardsWithEventTrigger` 那条路是死的 ——
所有触发点实际只走引擎自己的 `FireTrigger` → `CardDispatch`。改动时不要指望蓝图那条路。

---

## 持续站场的效果（光环）：四个入口 + 按来源记账

「站在场上就一直生效」的效果（光环 / 位置型）走的是**另一套入口**：
`BP_CardFunctions` 的分派器直接调卡上的 `OnEnterPlay`、`OnLeaveBoardOrOwner`、
`OnMoveToFrontline`、`OnMoveFromFrontline`。这些名字不在 Trigger 枚举里，
按触发点找函数永远找不到，引擎必须在真实时机自己补调
（`EngineHost.InvokeBareEvent`）。没覆盖这个可选事件的卡在 `FnIndex` 里查不到，
是正常 no-op，不会污染 `Unhandled`。

放置时机有讲究，四个入口里有两个踩过坑：

| 入口 | 调用点 | 时机要点 |
|---|---|---|
| `OnEnterPlay(card, method=1)` | `DoPlayCard` | 落在场上、`LocationNumber` 排完之后 |
| `OnMoveToFrontline(card, forceMove, moveCost)` | `TryPlaceOnFrontline` | 同上 |
| `OnMoveFromFrontline(card)` | `DoMove` 退到支援线 | 同上 |
| `OnLeaveBoardOrOwner(card, goingTo, method)` | `DestroyCard` **最前面** | **必须在这张牌还站在场上时调** |

最后一条是这一轮的关键：客户端的销毁流程第一步是
`ExecuteOnBeforeLeaveBoardOrOwnerEvents`（先取旧位置，再 `CardLocationMoved`）。
放在 `FireDestroyTriggers` 里调就晚了 —— 那时牌已经标了 `Destroyed`、
`location` 也变成弃牌堆，而光环卡撤销时要先 `GetCardsToTheLeft(self)` 找回那些牌，
那道查询的第一条守卫正是「本卡 `location` ∈ 场上（5/6/7）」，于是恒空：
加成留在邻居身上再也撤不掉，比没有光环更糟。

**加成必须按来源记账**。客户端把这份账存在卡上的 `buffsFromCards` 映射里
（键 = `instigatorID`），`ChangeBuffsFromCards` 增减、`isBuffedByCard` 查询都读它。
只记总额（`BuffAttack`）的话，`ChangeAttack(邻居, self.cardID, 0, 4)` 这种
「撤销我这一个来源」只能退化成「把总额清零」——两个光环叠在同一张卡上时，
先离场的那张会把另一张的加成一起抹掉。现在：

- `Card.AttackBuffBySource`（来源 InstanceId → 该来源加了多少）
  + `Card.BuffAttackFromNoSource`（`instigatorID ≤ 0` 的匿名加成）；
- `ChangeAttack` 的 `changeType`：`0/1` 按来源累加、`2` = SetValue（清空后只留这一个）、
  `4` = 只撤销该来源那一份（全池 64 处调用点都带自己的 `cardID` 当 `instigatorID`）；
- 总额与镜像 `attackBuff` 由 `WriteAttackBuff` 重算，保证「总额 = 各来源之和」；
- `isBuffedByCard` / `getCardsBuffedByThisCard` 改成精确查来源表。注意前者的实参布局是
  `card.isBuffedByCard(instigatorID, out isBuffed)` —— `a[0]` 才是卡，写成 `a[1]`
  等于拿一个整数当卡查，恒 false，撤销光环的那道 `!isBuffedByCard(...)` 守卫永远不通过。

`GetCardsToTheLeft` / `GetCardsToTheRight` 本身是 `BP_CardFunctions` 的蓝图函数
（`Card / unitsOnly / includeCovert / out cards`，语义是「同一 `location` 上、
`locationNumber` 更小/更大」），宿主**故意不实现**，放行给蓝图；
引擎只负责把 `location` / `locationNumber` 喂对（`RefreshLocationNumbers`，
最左为 0，与 `SetRightLeftMostWhenPlayed` 的判定一致）。自己再写一份迟早漂移，
而且很容易漏掉「本卡在场上」那道守卫。

回归哨兵是 `--mode tests` 里的 `AuraAppliesAndRetracts`（11 条断言）：
左邻各 +2、右侧与自身不加、按来源记账、镜像一致、两个光环叠加为 +4、
先走的那张只撤掉自己的 +2、两张都走之后回到基础值、没有未实现的宿主调用。

---

## 选择点：哪些已经是动作，哪些还不是

| 选择 | 机制 | 现状 |
|---|---|---|
| 出牌目标 | 卡自己的 `CanPlayFromHand` + CDO 的 `selectTargetOnPlayedFromHand`（401 张） | **是动作**（`GameAction.TargetId`） |
| 三选一 | 卡上的 `ChooseOne` 成员决定分支（48 张，各 2 支） | **是动作**（`GameAction.ChoiceIndex`） |
| Develop / 选手牌 | UI 往返：`NotifySelectCardToDrawPending` 通知 → `OnHandTargetSelected` 回调 | **是动作**（二段式，见下） |

前两类是**出牌前**就能定的：卡牌逻辑同步执行，没有挂起/恢复机制
（直译产物里 `AwaitInput` 一次都没被调用），所以选择只能前置。

第三类相反 —— 效果跑到一半才需要选。客户端靠 UI 往返完成，引擎把它做成两段式：

1. 效果体调 `NotifySelectCardToDrawPending` → 宿主记下**待决选择**（`GameEngine.Pending`）；
2. 有 `Pending` 时 `LegalActions` **只**返回候选项（`ActionType.ChooseCard`），
   对局不会在你没选之前往下走；
3. 选定后回调发起卡自己的 `OnHandTargetSelected(选中的卡ID, instigatorID)`。

HTTP 侧有**抉择预览**：`GET /games/{id}/state` 会给出
`pendingChoice.options[{index,id,label}]`，AI 后端据此决策。

`WhichChooseOne` 以前恒返回第 0 支（确定但不是决策点），现在读卡的 `ChooseOne` 成员，
引擎在出牌前写入；没写时退回可插拔决策器，保证不接 AI 时仍可复现。

---

## 版权

`cards.json` 与 `KardsSim/Generated/` 是从游戏客户端资产派生的，仅供本地研究 /
AI 训练，请勿再分发。游戏资产本身（`_input/`）不入库。

