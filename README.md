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

---

## 状态

- 1670 个资产 / 6254 个函数，**0 空体**，1 处未支持节点（`BP_EntryPointActor`，非战斗逻辑）
- 37 万行直译代码 **0 错误编译通过**
- 自对弈 400 局：**0 异常 / 0 卡死 / 0 非法动作 / 0% 未实现调用**
- 全卡 1638 张强制演练：**0 异常 / 0 未实现调用**
- 触发点覆盖 **61/61**（卡牌注册的触发点，引擎全部会发）
- 宿主 API：卡牌逻辑可达的缺口 **0 个**（另外 341 个仅 UI / 平台可达，无头模拟不会走到）
- 已知未做：`WhichChooseOne`（三选一）恒返回第 0 支，是确定性的但不是真实决策点

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

- 1670 个资产 / 6254 个函数，**0 空体**，1 处未支持节点（`BP_EntryPointActor` 的
  ubergraph，非战斗逻辑）
- 36 万行直译代码 **0 错误编译通过**
- 200 局自对弈：**0 异常 / 0 卡死 / 0 非法动作**，宿主未实现调用 1.39%（12 个函数）
- 单卡端到端验证（`panzer_iii_e` 进场加攻 / 离场撤销 / 三个入口等价）**12/12 通过**

---

## 版权

`cards.json` 与 `KardsSim/Generated/` 是从游戏客户端资产派生的，仅供本地研究 /
AI 训练，请勿再分发。游戏资产本身（`_input/`）不入库。

