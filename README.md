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
# 自对弈（默认走直译产物），并打印宿主 API 覆盖率
dotnet run --project KardsSim -c Release -- --mode selfplay --games 200

# 用旧的文本解析效果表跑（对照用）
dotnet run --project KardsSim -c Release -- --mode selfplay --games 200 --legacy

# HTTP 接口，默认 http://127.0.0.1:8642/
dotnet run --project KardsSim -c Release

# 其它：dump / coverage / fuzz
```

自对弈会打印**宿主未实现调用清单**（函数名 + 调用次数），这是「还缺哪些宿主 API」
的实测依据，而不是靠猜。

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

## 已修过的坑（都是静默出错型，不测发现不了）

| 症状 | 根因 |
|---|---|
| 判断永远为假、整段效果被跳过 | 无 context 调用成员函数时接收者发成了 `Val.Nothing` |
| 整张卡抛 `KeyNotFound` | 局部变量直读 `L["x"]`；Kismet 局部槽是零初始化的 |
| 条件判断走反 | 宿主规则回调只回 `bool`，函数**返回值被丢掉** |
| 遍历型卡拿不到元素 | `Array_Get` 的结果走 out 形参，宿主没回写 |
| 卡面文本变成类型名 | `EX_TextConst` 的 `Value.ToString()` 不是字面量，要按 `TextLiteralType` 取字段 |
| 栈溢出 | 依赖蓝图只当签名表、没转译（见上） |
| 编译报 CS0131 | `SetArray/SetSet/SetMap` 的属性是赋值目标，被当成右值发射 |

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
