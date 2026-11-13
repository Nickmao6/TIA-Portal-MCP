# TIA Portal V21 LAD(SimaticML)生成规格 —— 实测 + 权威依据

> 本文所有结论都来自：① 博途 Openness 自己导出的真实文件 ② 博途程序集内嵌的官方 XSD
> ③ 博途导入时报出的原始错误。**没有任何一条是猜的。**

## 一、权威 schema 的位置（重大发现）

博途安装包里**没有**随包的 SimaticML XSD，但**程序集内部内嵌了 55 个 XSD 资源**：

```
D:\Program Files\Siemens\Automation\Portal V21\Bin\Siemens.Simatic.Lang.BlockLogic.dll
```

提取方法（PowerShell）：

```powershell
$asm = [System.Reflection.Assembly]::LoadFrom('...\Siemens.Simatic.Lang.BlockLogic.dll')
foreach ($r in $asm.GetManifestResourceNames()) {
  if ($r -match '\.xsd$') {
    $s = $asm.GetManifestResourceStream($r)
    $rd = New-Object System.IO.StreamReader($s)
    [IO.File]::WriteAllText("out\$($r -split '_Resource\.')[-1]", $rd.ReadToEnd(), [Text.Encoding]::UTF8)
  }
}
```

最关键的两个：
- `SW.PlcBlocks.LADFBD_v5.xsd` —— **LAD/FBD 的官方规格**（V21 用）
- `SW.PlcBlocks.Access_v5.xsd` —— 操作数/连线定义

`Siemens.Simatic.Lang.FLDiagram.dll` 里的 `FlgV5.xsd` **是旧的内部格式，不要参考**
（它要求 `Part/@Gate` 必填，而 V21 导出根本没这个属性）。

## 二、文档骨架

```xml
<Document>
  <Engineering version="V21"/>
  <DocumentInfo>…</DocumentInfo>
  <SW.Blocks.OB ID="0">              <!-- ID 是十六进制字符串 -->
    <AttributeList>…</AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="Comment">…</MultilingualText>
      <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">   <!-- 一个"程序段" -->
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
              <Parts>…</Parts>
              <Wires>…</Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>…Comment / Title…</ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.OB>
</Document>
```

## 三、Parts（元件）

```xml
<Access Scope="GlobalVariable" UId="21">      <!-- 操作数：符号寻址，引用 PLC 变量名 -->
  <Symbol><Component Name="启动按钮"/></Symbol>
</Access>
<Part Name="Contact" UId="23"/>              <!-- 常开触点 -->
<Part Name="Coil"   UId="24"/>              <!-- 线圈 -->
```

**常闭触点**（实测通过的写法）：
```xml
<Part Name="Contact" UId="23">
  <Negated Name="operand"/>                  <!-- 关键：属性名是 Name，值必须是 operand -->
</Part>
```
- 属性名是 **`Name`**，不是 `PinName`/`ParaNum`（那是旧 FlgV5 的写法，会报"未声明该属性"）
- 值必须是 **`operand`**，不是 `in`。博途原话：*"在 LAD 编程语言中，只有在触点的操作数输入处才可进行取反"*

## 四、Wires（连线）

```xml
<Wire UId="25"><Powerrail/><NameCon UId="23" Name="in"/></Wire>       <!-- 左母线 -->
<Wire UId="26"><IdentCon UId="21"/><NameCon UId="23" Name="operand"/></Wire>  <!-- 接操作数 -->
<Wire UId="27"><NameCon UId="23" Name="out"/><NameCon UId="24" Name="in"/></Wire> <!-- 串联 -->
```

规则：
- 线圈后面**不需要**"右母线"连线（TIA 自己导出时就没有）
- 同一引脚名不能出现在多条 Wire 上：报 *"connection 'in' … is used multiple times"*

## 五、两套编号，别混用

| 位置 | 类型 | 例 |
|------|------|-----|
| 文档级 `ID`（CompileUnit / MultilingualText） | **十六进制字符串** | `ID="3"` `ID="A"` `ID="B"` |
| `FlgNet` 内部 `UId` | **十进制整数** | `UId="21"` `UId="1000"` |

把 UId 写成 `"100A"` 会报：*"UId 属性无效 – 值 100A 不是有效的 Int32 值"*。

## 六、必须用 lxml，不能用 ElementTree

ElementTree 回写时会把默认命名空间改成 `ns0:` 前缀，博途报：*"无法为父元素添加 ns0:Member"*。
必须 `lxml.etree`，并用 `etree.Element('FlgNet', nsmap={None: FLGNET_NS})` 保留默认命名空间。

## 七、导入失败的原子性（重要）

导入失败时**博途不会破坏原有块** —— 实测多次失败后 OB1 仍然编译正常。
所以可以安全地逐个试探候选写法。

但反之：**块有编译错误时无法导出**（报 *"Inconsistent blocks … cannot be exported"*），
所以"先导出→再生成→再导入"的流程在块报错时会卡住，需要先修好编译。

## 八、并行分支（已攻克！）

**并联不是"多根线接一个点"，而是用一个 OR 部件表示。**

依据：工作区 `_research_tiautil/PAROZZZ/`（TiaUtilities 源码）的真实实现

`SimaticML/Blocks/FlagNet/nPart/Part.cs` 的 PartType 枚举（注意确切字符串）：

```csharp
[SimaticEnum("Contact")] CONTACT
[SimaticEnum("Coil")]    COIL
[SimaticEnum("SCoil")]   SET_COIL      // S 线圈
[SimaticEnum("RCoil")]   RESET_COIL    // R 线圈
[SimaticEnum("Not")]     NOT
[SimaticEnum("O")]       OR            // ← OR 部件名是 "O"，不是 "OR"
```

`SimaticPart.OrPart.GetPart`：
```csharp
part.TemplateValue     = $"{PartList.Count}";  // 输入数
part.TemplateValueName = "Card";
part.TemplateValueType = "Cardinality";
```

`SimaticLADSegment.CreateORInputWireConnections`：
```csharp
compileUnit.CreateWire()
    .CreateNameCon(orPart,  orPart.InputConName + (i + 1))  // "in1","in2",...
    .CreateNameCon(branchPart, branchPart.OutputConName);   // "out"
```

### 完整结构（实测导入+编译通过）

```xml
<Parts>
  <Part Name="Contact" UId="a"/>          <!-- 停止按钮(常闭) -->
  <Part Name="Contact" UId="b"><Negated Name="operand"/></Part>   <!-- 热保护(常闭) -->
  <Part Name="Contact" UId="c"/>          <!-- 启动按钮 -->
  <Part Name="Contact" UId="d"/>          <!-- 接触器(自保持) -->
  <Part Name="O" UId="e">
    <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>
  </Part>
  <Part Name="Coil" UId="f"/>            <!-- 接触器线圈 -->
</Parts>
<Wires>
  <Wire UId="w1"><Powerrail/><NameCon UId="a" Name="in"/></Wire>
  <Wire UId="w2"><NameCon UId="a" Name="out"/><NameCon UId="b" Name="in"/></Wire>
  <!-- 关键: 分叉点必须放在同一根 Wire 里, 一个 out 喂多个 in -->
  <Wire UId="w3"><NameCon UId="b" Name="out"/><NameCon UId="c" Name="in"/><NameCon UId="d" Name="in"/></Wire>
  <Wire UId="w4"><NameCon UId="c" Name="out"/><NameCon UId="e" Name="in1"/></Wire>
  <Wire UId="w5"><NameCon UId="d" Name="out"/><NameCon UId="e" Name="in2"/></Wire>
  <Wire UId="w6"><NameCon UId="e" Name="out"/><NameCon UId="f" Name="in"/></Wire>
</Wires>
```

### 硬性规则（都是博途报错换来的）

| 规则 | 违反时的报错 |
|------|-------------|
| 一个引脚名不能出现在多根 `Wire` 里 | `connection 'out' … is used multiple times at the cables` |
| 分叉必须合并进**同一根** Wire | 同上 |
| `Powerrail` 线只能挂一个连接点 | `电源线(UId=X)中包含与(UId=Y)的无效连接` |
| `UId` 全局唯一且为整数 | `at least two definitions for UIds` |

## 九、S / R 线圈（已实测通过）

```xml
<Part Name="SCoil" UId="n"/>   <!-- S 置位线圈 -->
<Part Name="RCoil" UId="n"/>   <!-- R 复位线圈 -->
```

实测：导入 + 编译 0 错误，回导出确认类型被博途原样保留。
配合 `nc`/`no` 触点即可实现 **IEC 标准启停回路**（不需要并联）：

```
段1: [停止 常开][热保护 常开][启动 常开] → (S 接触器)
段2: [停止 常闭] | [热保护 常闭]           → (R 接触器)
```

> 注意 S 回路用**常开**的停止/热保护（要求"未按下且正常"才允许置位），
> R 回路用**常闭**（按下停止或热保护动作就复位）。

### 已排除的错误写法（避免重复踩坑）

| 尝试 | 博途报错 |
|------|---------|
| `Part/@Variant="S"` | `未声明"Variant"特性` |
| `TemplateValue Name="Variant"` | `The node 'TemplateValue' with the name 'Variant' … is invalid` |
| `OpenCon` 共用同一 UId | `at least two definitions for UIds` |

> S/R 线圈的正确写法已知但未实测：`<Part Name="SCoil">` / `<Part Name="RCoil">`。

## 十、定时器 TON / TOF / TP / TONR（2026-10 全部实测通过）

依据：博途里手画的 TON/TOF/TP/TONR 真实样本 + 自行生成后导入编译验证。

### 四者结构完全一致，只有一个例外

```xml
<Part Name="TON"  Version="1.0" UId="27">   <!-- TOF / TP / TONR 只是名字不同 -->
  <Instance Scope="GlobalVariable" UId="28">
    <Component Name="IEC_Timer_0_DB"/>
  </Instance>
  <TemplateValue Name="time_type" Type="Type">Time</TemplateValue>
</Part>
```

| 定时器 | Instance 绑定 | 引脚 |
|--------|--------------|------|
| **TON**（接通延时） | 必填 | `IN` `Q` `PT` `ET` |
| **TOF**（断开延时） | 必填 | `IN` `Q` `PT` `ET` |
| **TP**（脉冲） | 必填 | `IN` `Q` `PT` `ET` |
| **TONR**（累积延时） | 必填 | `IN` `Q` `PT` `ET` **`R`** ← 唯一差异 |

### 参数与接线

```xml
<!-- 预设时间: 字面量常量 -->
<Access Scope="TypedConstant" UId="22">
  <Constant><ConstantValue>T#3s</ConstantValue></Constant>
</Access>

<Wires>
  <Wire><Powerrail/><NameCon UId="触点" Name="in"/></Wire>          <!-- 触点: in 小写 -->
  <Wire><NameCon UId="触点" Name="out"/><NameCon UId="定时器" Name="IN"/></Wire>
  <Wire><IdentCon UId="常量"/><NameCon UId="定时器" Name="PT"/></Wire>
  <Wire><NameCon UId="定时器" Name="ET"/><IdentCon UId="Access"/></Wire>
  <Wire><NameCon UId="定时器" Name="R"/><IdentCon UId="Access"/></Wire>  <!-- 仅 TONR -->
</Wires>
```

### ⚠ 引脚名大小写（最容易踩）

| 元件 | 输入 | 输出 |
|------|------|------|
| 触点 / 线圈 | `in` / `out`（**小写**） | |
| **定时器** | **`IN`** / **`Q`**（**大写**） | |

写错博途会报：`references a connection named 'out' ... which does not exist`

### ⚠ 必须接齐 IN / Q / ET（TONR 还要 R）

缺任一个就报 `The connection with the name 'ET' is not connected to the object with UID ...`

`ET`（已耗时间）类型必须是 **Time**，变量用 **MD**（双字）：
`已耗时间 %MD68 Time` ✓ ／ `%M0.0 Bool` ✗（报 *data type Bool 不匹配*）

### 其他要点

- `Version="1.0"` 属性必带
- `<Instance>` 绑定实例 DB（`IEC_Timer_0_DB_1` 等），博途**自动生成**，位于「系统块」下，类型 IDB
- **实例 DB 必须真实存在**，否则报 `Missing instance DB`
- 定时器**前面必须有布尔逻辑**（触点），直接接电源轨报 *A preceding Boolean logic operation is missing*
- 实例 DB 不在主 `BlockGroup` 下，普通 `export_block` 找不到（需走系统块路径）

### 生成用法（JSON 规格）

```json
{ "k": "no",  "tag": "启动按钮" },
{ "k": "TON", "instance": "IEC_Timer_0_DB", "pt": "T#3s", "et": "已耗时间" },
{ "k": "coil","tag": "延时指示" }
```

TONR 加 `r`：
```json
{ "k": "TONR", "instance": "IEC_Timer_0_DB_3", "pt": "T#4s",
  "et": "累积时间", "r": "复位按钮" }
```

---

### TONR 的 R 端（复位）接法 —— 实测要点

官方定义（[S7-1200 定时器手册](https://www.ad.siemens.com.cn/productportal/prods/s7-1200_plc_easy_plus/07-program/03-instruction/01-basic/01-timer.html)）：
> **R 是电平信号**：只要 R = "1"，Q 输出 "0"，ET 立即停止并回到 0。

接线必须和主触点**共享同一根左母线**，各自 out 分别去 IN / R：

```xml
<Wire>  <!-- 一根母线挂两个 in -->
  <Powerrail/>
  <NameCon UId="主触点" Name="in"/>
  <NameCon UId="复位触点" Name="in"/>
</Wire>
<Wire><NameCon UId="主触点"   Name="out"/><NameCon UId="TONR" Name="IN"/></Wire>
<Wire><NameCon UId="复位触点" Name="out"/><NameCon UId="TONR" Name="R"/></Wire>
```

**踩过的坑**：
| 错误做法 | 博途报错 |
|---------|---------|
| 复位触点单独再拉一根电源线 | `在 LAD 中，程序段中只能包含一个电源线` |
| 主触点重复接母线 | `connection 'in' … is used multiple times` |
| 复位触点的 in 不并进母线 | `该程序段不符合 LAD 规则` |

### ⚠ 定时器必须接 Q 或 ET 才会计时

官方明确：**只有 Q 点或 ET 连接了变量（或使用背景 DB 的 Q/ET），定时器才会开始计时并更新时间。**
所以 Q/ET **不能悬空** —— 这就是为什么 ET 必须接走、Q 建议接线圈。

### ⚠ 实例 DB 不会自动创建

- 复用博途已建的（`IEC_Timer_0_DB_1` 等）✓
- 随便起个新名字 → 报 `Missing instance DB`
- 实例 DB 位于「系统块 > 程序资源」，类型 IDB

### ★ 干净工程的解法：多重实例（2026-10 实测通过）

`CreateInstanceDB(name, autoNum, number, fbName)` 只认**工程里存在的 FB**；拿系统 FB 名
试会报 `Block 'IEC_TIMER' does not exist at the object with UID ''`。而 V21 的
`PlcBlockComposition` **没有** `CreateDataBlock`。所以在**全新工程**里无法直接建定时器实例 DB。

**解法：把定时器声明成 FB 的 Static 变量**（多重实例），用 FB 自己的实例 DB 承载：

```xml
<!-- 1) FB 接口里声明 IEC_TIMER 类型的静态量 —— 博途接受这个类型 -->
<Section Name="Static">
  <Member Name="秒脉冲定时器" Datatype="IEC_TIMER" Remanence="NonRetain" Accessibility="Public" />
</Section>

<!-- 2) 梯形图里把定时器实例指向这个静态量(作用域是 LocalVariable) -->
<Part Name="TON" Version="1.0" UId="24">
  <Instance Scope="LocalVariable" UId="25">
    <Component Name="秒脉冲定时器"/>
  </Instance>
  <TemplateValue Name="time_type" Type="Type">Time</TemplateValue>
</Part>
```

实测：导入 + 编译 **0 错误**，定时器可正常使用，且**不需要任何外部实例 DB**。

> 导出后博途会把 `IEC_TIMER` 静态量展开出 `PT` / `ET` / `IN` / `Q` 成员。
> 生成器用法：spec 里写 `"instance_scope": "LocalVariable"`。

**结论：新工程一律走多重实例；只有复用别人已建好的实例 DB 时才用 `GlobalVariable`。**

---

## 十一、计数器 CTU / CTD（2026-10 实测通过）

### 结构与定时器的差异

```xml
<!-- 计数器(注意模板参数名不同!) -->
<Part Name="CTU" Version="1.0" UId="27">
  <Instance Scope="GlobalVariable" UId="28">
    <Component Name="IEC_Counter_0_DB"/>      ← Counter 不是 Timer
  </Instance>
  <TemplateValue Name="value_type" Type="Type">Int</TemplateValue>   ← value_type!
</Part>

<!-- 对比定时器 -->
<Part Name="TON" Version="1.0" UId="27">
  <TemplateValue Name="time_type" Type="Type">Time</TemplateValue>  ← time_type
</Part>
```

### 引脚对照

| | 定时器 | 计数器 CTU | 计数器 CTD | **计数器 CTUD** |
|---|--------|-----------|-----------|----------------|
| 加/计输入 | `IN` | **`CU`** | — | **`CU`** |
| 减输入 | — | — | **`CD`** | **`CD`** |
| 装载 | — | — | **`LD`** | **`LD`** |
| 复位 | `R`（仅 TONR） | **`R`** | — | **`R`** |
| 预设值 | `PT`（Time） | **`PV`** | **`PV`** | **`PV`** |
| 当前值 | `ET`（Time） | **`CV`** | **`CV`** | **`CV`** |
| 加到输出 | `Q` | `Q` | `Q` | **`Q` + `QU`** |
| 减到输出 | — | — | — | **`QD`** |
| 实例 DB | `IEC_Timer_0_DB_x` | `IEC_Counter_0_DB_x` | 同左 | 同左 |

### CTUD 特别注意

- 元素名是 **全大写 `CTUD`**（和 CTU/CTD 一样）
- **`CD` 必须接**，否则报 *"The connection with the name 'CD' is not connected"*
- **`QD` / `QU` 也必须接**（CTUD 有两个计数到指示），逐一报错
- `R` / `LD` 在样板上是**触点**接的（从母线经触点），不是裸变量

```xml
<Part Name="CTUD" Version="1.0" UId="29">
  <Instance Scope="GlobalVariable" UId="30">
    <Component Name="IEC_Counter_0_DB"/>
  </Instance>
  <TemplateValue Name="value_type" Type="Type">Int</TemplateValue>
</Part>
```

### 生成用法

```json
{ "k": "CTUD", "instance": "IEC_Counter_0_DB", "pv": 10,
  "cv": "计数值", "cd": "减计数按钮",
  "r": "复位按钮", "ld": "装载按钮",
  "qu": "加到标志", "qd": "减到标志" }
```

`cd_contact` / `ld_contact` / `r_contact` 可切换成"触点接法"（默认 R/LD 走触点）。

---

## 十二、数学运算 Add / Sub / Mul / Div / Mod（2026-10 实测通过）

```xml
<!-- 数值(如 PV=10) → LiteralConstant, 必须带 ConstantType -->
<Access Scope="LiteralConstant" UId="23">
  <Constant>
    <ConstantType>Int</ConstantType>
    <ConstantValue>10</ConstantValue>
  </Constant>
</Access>

<!-- 时间(如 PT=T#3s) → TypedConstant, 只有 ConstantValue -->
<Access Scope="TypedConstant" UId="22">
  <Constant><ConstantValue>T#3s</ConstantValue></Constant>
</Access>
```

数值缺 `ConstantType` 会报：`'ConstantValue' has the invalid value '10'`

### ⚠ CV 必须接走

与定时器的 ET 同理 —— 官方规定计数器要 **Q 或 CV 接了变量**才会真正计数。CV 类型要和 `value_type` 匹配（Int → `%MW`/`%MD`，Bool 会报类型不匹配）。

### 生成用法

```json
{ "k": "CTU", "instance": "IEC_Counter_0_DB", "pv": 10,
  "cv": "ctu次数存储", "r": "复位触点" }

{ "k": "CTD", "instance": "IEC_Counter_0_DB_1", "pv": 10,
  "cv": "ctd次数存储", "ld": "装载触点" }
```

`pv` 传数字用字面量，传字符串则当变量名。`CTUD` 支持用 `in_pin` 指定用 `CU` 还是 `CD`。

---

## 十二、CALL 调用 FB（2026-10 实测通过）

CALL **不是 `<Part>`，而是独立元素 `<Call>`** —— 这是与所有其他元件最大的结构差异。

### 1. FB 的引脚声明（块接口 / Interface）

```xml
<Interface>
  <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
    <Section Name="Input">
      <Member Name="输入" Datatype="Bool" Remanence="NonRetain" Accessibility="Public">
        <AttributeList>
          <BooleanAttribute Name="ExternalAccessible" SystemDefined="true">true</BooleanAttribute>
          <BooleanAttribute Name="ExternalVisible"   SystemDefined="true">true</BooleanAttribute>
          <BooleanAttribute Name="ExternalWritable"   SystemDefined="true">true</BooleanAttribute>
        </AttributeList>
      </Member>
    </Section>
    <Section Name="Output"> … </Section>
    <Section Name="InOut">  … </Section>
    <Section Name="Static"/>   <!-- 背景数据(多重实例就装在这) -->
    <Section Name="Temp"/>
    <Section Name="Constant"/>
  </Sections>
</Interface>
```

三个方向分区：`Input` / `Output` / `InOut`，对应博途「块接口」里的三个折叠区。

> ⚠ 没有引脚的 FB 在多重实例下会报 *"数据类型 X 导致直接或间接递归"*。

### 2. CALL 指令（写在 OB 的网络里）

```xml
<Parts>
  <!-- 引脚接的变量, 和普通触点线圈一样是 Access -->
  <Access Scope="GlobalVariable" UId="21"><Symbol><Component Name="Tag_11"/></Symbol></Access>
  <Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="Tag_12"/></Symbol></Access>
  <Access Scope="GlobalVariable" UId="23"><Symbol><Component Name="Tag_13"/></Symbol></Access>

  <Call UId="24">
    <CallInfo Name="the_test" BlockType="FB">        <!-- 被调用的 FB -->
      <Instance Scope="GlobalVariable" UId="25">
        <Component Name="the_test_DB_1"/>            <!-- 绑定的背景 DB -->
      </Instance>
      <!-- 引脚声明(与 FB 的 Interface 一致) -->
      <Parameter Name="输入"     Section="Input"  Type="Bool"/>
      <Parameter Name="输出"     Section="Output" Type="Bool"/>
      <Parameter Name="输入输出" Section="InOut"  Type="Bool"/>
    </CallInfo>
  </Call>
</Parts>
<Wires>
  <Wire><Powerrail/><NameCon UId="24" Name="en"/></Wire>          <!-- 使能(小写 en) -->
  <Wire><IdentCon UId="21"/><NameCon UId="24" Name="输入"/></Wire>
  <Wire><IdentCon UId="22"/><NameCon UId="24" Name="输入输出"/></Wire>
  <Wire><NameCon UId="24" Name="输出"/><IdentCon UId="23"/></Wire>  <!-- 输出方向相反 -->
</Wires>
```

### 3. 要点

| 要点 | 说明 |
|------|------|
| 使能引脚是 **`en`**（小写），不是 `IN` | 还有 `eno`（错误输出） |
| 引脚名 = FB 里 Member 的名字 | 中文引脚就直接用中文，**NameCon Name="输入"** |
| 输入引脚线：`IdentCon → NameCon` | 输出引脚线反过来：`NameCon → IdentCon` |
| `<Parameter>` 要和 FB 的 Interface 对应 | Section 填 `Input`/`Output`/`InOut` |
| 多重实例绑定 `the_test_DB_1` | 单个实例时是 `xxx_1_DB` 这类独立 DB |
| 背景 DB 在「系统块 > 程序资源」 | 类型 IDB，不在主 BlockGroup 下 |

---

## 十三、数学运算 Add / Sub / Mul / Div / Mod（2026-10 实测通过）

### 结构

```xml
<!-- Add 和 Mul 有 Card; Sub / Div / Mod 没有! -->
<Part Name="Add" UId="26" DisabledENO="true">
  <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>
  <TemplateValue Name="SrcType" Type="Type">Int</TemplateValue>
</Part>

<Part Name="Sub" UId="26" DisabledENO="true">
  <TemplateValue Name="SrcType" Type="Type">Int</TemplateValue>   ← 没有 Card!
</Part>
```

接线：
```xml
<Wire><Powerrail/><NameCon UId="触点" Name="in"/></Wire>
<Wire><NameCon UId="触点" Name="out"/><NameCon UId="Add" Name="en"/></Wire>
<Wire><IdentCon UId="变量1"/><NameCon UId="Add" Name="in1"/></Wire>   ← 小写 in1
<Wire><IdentCon UId="常量2"/><NameCon UId="Add" Name="in2"/></Wire>
<Wire><NameCon UId="Add" Name="out"/><IdentCon UId="结果"/></Wire>
```

### ⚠ 三个坑

| 坑 | 博途报错 |
|----|---------|
| 元素名写成全大写 `ADD` | 找不到（XML 里是**混合大小写** `Add`/`Sub`/`Mul`/`Div`/`Mod`） |
| 给 `Sub`/`Div`/`Mod` 加 `Card` | `The node 'TemplateValue' with the name 'Card' … is invalid at the element 'Sub'` |
| 引脚写成大写 `IN1` | 引脚是**小写** `in1`/`in2`/`out`/`en` |

### Card 的意义

`Card` = 输入个数。**只有 `Add` 和 `Mul` 支持可变个数**（可以写成 3 输入、4 输入），其余三个固定两输入。

### SrcType 数据类型

`Int` / `DInt` / `Real` / `LInt`… 决定运算精度和字长（对应 M / MD / MD / ML）。

### 生成用法

```json
{ "k": "no", "tag": "启动按钮" },
{ "k": "ADD", "inputs": ["计算输入1", 1], "out": "计算结果" }

{ "k": "MOD", "inputs": ["计算输入2", 10], "out": "计算余数" }
```

`inputs` 里数字会自动变成字面量，字符串当变量名。`vtype` 可指定 `Int`/`DInt`/`Real`。

---

## 十四、MOVE / SR / RS（2026-10 实测通过）

### MOVE（传送值）

```xml
<Part Name="Move" UId="25" DisabledENO="true">
  <TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>
</Part>
<Wire><NameCon UId="触点" Name="out"/><NameCon UId="Move" Name="en"/></Wire>
<Wire><IdentCon UId="源"/><NameCon UId="Move" Name="in"/></Wire>
<Wire><NameCon UId="Move" Name="out1"/><IdentCon UId="目标"/></Wire>
```

- 混合大小写：界面 `MOVE`，XML `Move`
- `Card` = 输出个数 → 引脚 `out1` / `out2` / `out3`
- 无 `SrcType`（不像加减乘除）

### SR / RS（置位复位触发器）

**XML 名是 `Sr` / `Rs`（首字母大写），不是全大写！**

```xml
<Part Name="Sr" UId="25"/>          <!-- RS 则是 <Part Name="Rs"/> -->
<Wires>
  <Wire><Powerrail/><NameCon UId="触点1" Name="in"/></Wire>
  <Wire><IdentCon UId="Access"/><NameCon UId="触点1" Name="operand"/></Wire>
  <Wire><NameCon UId="触点1" Name="out"/><NameCon UId="Sr" Name="s"/></Wire>
  <Wire><IdentCon UId="Access"/><NameCon UId="Sr" Name="r1"/></Wire>
  <Wire><IdentCon UId="Q变量"/><NameCon UId="Sr" Name="operand"/></Wire>  <!-- ★必须 -->
</Wires>
```

| | 置位端 | 复位端 | 优先级 |
|---|--------|--------|--------|
| **SR** | `s` | `r1` | **S 优先** |
| **RS** | `r` | `s1` | **R 优先** |

**两个关键点**：
1. `operand` **必须接**（存 Q 状态），否则报 *"'operand' is not connected"*
2. 两端**都从母线经触点接入**，不能把变量直接怼到引脚（报 *"需要线圈/分配"*）；
   一段只能有一根母线，用 `left_rail_multi` 把两个触点并上去

---

## 十五、比较运算 Ge / Gt / Eq / Lt / Le / Ne（2026-10 实测通过）

界面显示 `>=` `>` `==` `<>` `<` `<=`，**XML 里是混合大小写的两字母缩写**：

| 界面 | XML | 含义 |
|------|-----|------|
| `>=` | `Ge` | 大于等于 |
| `>` | `Gt` | 大于 |
| `==` | `Eq` | 等于 |
| `<>` | `Ne` | 不等于 |
| `<` | `Lt` | 小于 |
| `<=` | `Le` | 小于等于 |

```xml
<Part Name="Ge" UId="26">
  <TemplateValue Name="SrcType" Type="Type">Int</TemplateValue>
</Part>
<Wires>
  <Wire><NameCon UId="触点" Name="out"/><NameCon UId="Ge" Name="pre"/></Wire>   <!-- pre=被比较值 -->
  <Wire><IdentCon UId="变量A"/><NameCon UId="Ge" Name="in1"/></Wire>              <!-- in1=左侧 -->
  <Wire><IdentCon UId="常量B"/><NameCon UId="Ge" Name="in2"/></Wire>              <!-- in2=右侧 -->
  <Wire><NameCon UId="Ge" Name="out"/><NameCon UId="线圈" Name="in"/></Wire>
</Wires>
```

### 两个关键限制（实测报错）

| 限制 | 博途报错 |
|------|---------|
| **不能带 `DisabledENO` 属性** | `所用 CPU 中的该指令不支持属性"DisableENO"` |
| **输出必须接线圈**（不能悬空） | `需要线圈/分配` |
| **SrcType 必须与实际操作数一致** | `The data type Time of the actual parameter does not match the data type Int of the formal parameter` |

> 引脚名是**小写** `pre` / `in1` / `in2` / `out`（对比定时器是大写 `IN`）。
> 第三条最容易踩：用 `已耗时间`(Time) 做比较却声明 `SrcType=Int` 就会报类型不匹配 ——
> 比 Time 就要写 `"vtype": "Time"`。

### 生成用法

```json
{ "k": "no",   "tag": "启动按钮" },
{ "k": "GE",  "vtype": "Int", "values": ["计算输入1", 50] },
{ "k": "coil", "tag": "条件1" }
```

`values` = `[被比较值, 左侧值, 右侧值]`；数字自动变字面量，字符串当变量名。

---

## 十六、类型转换 Convert（2026-10 实测通过）

**一个元件覆盖所有类型组合** —— 不需要逐个学，换 `SrcType`/`DestType` 的值即可。

```xml
<Part Name="Convert" UId="n" DisabledENO="true">
  <TemplateValue Name="SrcType"  Type="Type">Int</TemplateType>
  <TemplateValue Name="DestType" Type="Type">DInt</TemplateValue>
</Part>
```

引脚（小写）：`en`（使能）/ `in` / `out`

常用组合：Int↔DInt、DInt→Real、Real→Int、Int→Word/DWord/Byte、Time→DInt 等。

### 生成用法

```json
{ "k": "no",   "tag": "启动按钮" },
{ "k": "CONV", "src_type": "Int", "dest_type": "DInt",
  "in": "计算输入1", "out": "转换结果" }
```

> 实测时要注意：**目标变量类型要和 `dest_type` 一致**，否则报类型不匹配。

---

## 十七、FB / FC 内部梯形图（2026-10 实测通过）

### 结构与 OB 完全一样，只有两处不同

```xml
<!-- 外层元素名 -->
OB:  <SW.Blocks.OB  ID="0">
FB:  <SW.Blocks.FB  ID="0">

<!-- FB 多一个 Interface(块接口), OB 没有 -->
<Interface>
  <Sections xmlns=".../SW/Interface/v5">
    <Section Name="Input">  <Member Name="输入" Datatype="Bool"/></Section>
    <Section Name="Output"> <Member Name="输出" Datatype="Bool"/></Section>
    <Section Name="InOut">  <Member Name="输入输出" Datatype="Bool"/></Section>
    <Section Name="Static"> <Member Name="静态" Datatype="Bool"/></Section>
    <Section Name="Temp">   <Member Name="暂存变量" Datatype="Bool"/></Section>
    <Section Name="Constant"><Member Name="constand_" Datatype="Bool"/></Section>
  </Sections>
</Interface>
```

### ★ 关键：作用域不同

```xml
<!-- PLC 全局变量表里的标签 -->
<Access Scope="GlobalVariable" UId="21"><Symbol><Component Name="启动按钮"/></Symbol></Access>

<!-- FB 内部引用自己的引脚/静态变量 -->
<Access Scope="LocalVariable"  UId="21"><Symbol><Component Name="输入"/></Symbol></Access>
```

**三个易错点（我全踩了）**：

| 错误写法 | 结果 |
|---------|------|
| `Scope="Local"` | ✗ 报 `Tag "输入" not defined` |
| `Component Name="#输入"`（加 # 前缀） | ✗ 同样报 not defined |
| `Scope="GlobalVariable"` 引用引脚 | ✗ 同样报 not defined |

✅ 正确就是 **`Scope="LocalVariable"` + 纯引脚名（无 `#`）**。

> 生成器里用 spec 的 `local: true` 标记即可自动切换作用域。

### 生成用法（FB 内部）

```json
{ "networks": [
  { "title": "输入自保持到静态变量", "rungs": [[
      { "k": "par", "local": true, "branches": [
          { "k": "no", "tag": "输入", "local": true },
          { "k": "no", "tag": "静态", "local": true } ]},
      { "k": "coil", "tag": "静态", "local": true } ]]},
  { "title": "静态驱动输出", "rungs": [[
      { "k": "no", "tag": "静态", "local": true },
      { "k": "coil", "tag": "输出", "local": true } ]]}
]}
```

### 三个概念怎么选（给初学者的判断标准）

| 概念 | 用在什么时候 | 判断标准 |
|------|-------------|---------|
| **PLC 变量表（默认变量表）** | 直接写逻辑，引用 I/O 点和参数 | 逻辑只用一遍 → 用这个，最简单 |
| **块接口 + FB** | 同一段逻辑要用在多处（10 台电机 → 调 10 次） | 有**重复**电路才值得包 FB |
| **DB 数据块** | 一组参数要整体读写（配方、报警记录、HMI 共享） | 需要**打包管理**时才用 |

**不必为了用而用。** 没有重复电路时，直接在 OB1 里用默认变量表更简单。

---

## 十六、⚠ 触点类型必须与物理接法匹配（本项目踩过的真实坑）

**编译器不会报错，但电路行为完全相反。**

停止按钮和热继电器通常是**物理常闭接法**（NC 触点接进 PLC 输入）：

| 物理状态 | 输入值 |
|---------|-------|
| 正常 / 未按下 | **1** |
| 按下 / 动作 | **0** |

所以梯形图里**必须用常开触点**（`no`）：

```
停止按钮(%I0.1) 平时=1 → 常开触点导通 → 电机可以运行   ✅
停止按钮(%I0.1) 按下=0 → 常开触点断开 → 电机停止       ✅
```

如果误用**常闭触点**（`nc`），结果完全反过来：
- 正常时（输入=1）常闭触点**断开** → 电机根本转不起来
- 按下停止（输入=0）常闭触点**反而导通** → 电机吸合

| 按钮接法 | 输入正常值 | 梯形图应使用 |
|---------|-----------|------------|
| 物理常闭（NC 按钮/热继电器） | 1 | **常开触点** `no` |
| 物理常开（NO 按钮） | 0 | **常闭触点** `nc` |

**这类错误只有行为仿真能发现**，所以 `lad_logic_sim.py` 是必需环节：
纯布尔逻辑（电机启停、互锁、报警联锁、顺序控制）在本机就能验，
不必依赖 PLCSIM 下载/启动那一套。

## 十七、实战经验：写一个完整项目才暴露的规则（2026-10）

做完整交通灯控制器时才踩到的硬规则，**单元测试发现不了，只有搭真实程序才会撞上**：

### 1. 一个引脚只能出现在一根 `Wire` 里

典型撞法：数学运算结果既想存进变量、又想接着往下串：

```json
{ "k": "MOD", "inputs": ["阶段", 2], "out": "阶段奇偶" },   // ← out 存变量
{ "k": "EQ",  "values": ["阶段奇偶", 0] }                   // ← 又想串下去
```

报错：`The connection named 'out' at the part with UId '…' is used multiple times at the cables.`

**解法**：要么只用引脚串（比较的对象用 `values` 里的**变量**），要么拆成两段网络：
先算出结果存进 Static 变量，下一段再读该变量。

### 2. 并联支路的入口引脚名**不统一**

并联时上游要接到支路的入口，但各元件的入口引脚名不同：

| 元件 | 入口引脚 |
|------|---------|
| 触点 / 线圈 / MOVE / SR | `in` |
| 比较 `Eq`/`Gt`/… | **`pre`** |
| 数学 `Add`/`Sub`/… / 转换 | **`en`** |
| 定时器 | **`IN`**（大写） |
| 计数器 | **`CU`** |

写死 `'in'` 会报引脚不存在。生成器里用 `entry_pin_of()` 按类型取。

### 3. 一个程序段只能有一根电源线

同一个网络里放两个 rung（各自 `left_rail`）会报：
`在 LAD 中，程序段中只能包含一个电源线。`

**解法**：一个网络 = 一个 rung；需要多个入口时用 `left_rail_multi` 并到同一根母线。

### 4. 网络顺序会改变行为（冷启动问题）

自保持网络如果在初始化网络**前面**，冷启动时"运行中"会先被置 1，
导致"停止时才初始化"的段永远不执行 —— 状态机从错误的初值起步。

**解法**：把初始化段放在最前面，或单独做首次扫描初始化。

### 5. 自复位定时器产生"每秒一个扫描周期"的脉冲

做秒脉冲的经典写法（无需额外元件）：

```xml
<Wire><Powerrail/><NameCon UId="NC触点" Name="in"/></Wire>       <!-- 常闭，操作数=自己 -->
<Wire><IdentCon 秒脉冲/><NameCon UId="NC触点" Name="operand"/></Wire>
<Wire><NameCon UId="NC触点" Name="out"/><NameCon UId="TON" Name="IN"/></Wire>
<Wire><NameCon UId="TON" Name="Q"/><NameCon UId="线圈" Name="in"/></Wire>
<Wire><IdentCon 秒脉冲/><NameCon UId="线圈" Name="operand"/></Wire>
<Wire><IdentCon T#1s/><NameCon UId="TON" Name="PT"/></Wire>
<Wire><NameCon UId="TON" Name="ET"/><IdentCon 耗时变量/></Wire>   <!-- ET 必须接走 -->
```

TON 的 `Q` 每个周期只导通**一个扫描周期**，正好当"秒脉冲"用来递减倒计时。

### 6. 闪烁（方波）用两个定时器级联

```
[NC 闪灯B] → TON_A(PT=T#1s) → 闪烁位
[闪烁位]   → TON_B(PT=T#1s) → 闪灯B
```
得到 1s 亮 / 1s 灭的方波。单个自复位 TON 只能给出"一个扫描周期宽"的脉冲，不能当闪烁。

### 7. 步进(状态机)的最小骨架

```
阶段(Int) + 倒计时(Int) + 秒脉冲
每个秒脉冲: 倒计时 > 0 时减 1
倒计时 == 0: 阶段 = 阶段 + 1；阶段 > N 时归 0；再按新阶段装载倒计时
```

---

## 十八、当前可用的能力

已实测导入 + 编译 0 错误：

**结构**
- **OB 和 FB/FC 内部都能写梯形图**（FB 用 `Scope="LocalVariable"` 引用自己的引脚/静态变量）
- OR 并联（**支路内可串联多个元件**）、串联、一根母线挂多个入口

**基本元件**
- 常开触点、**常闭触点**（`Negated Name="operand"`）、线圈、S线圈(`SCoil`)、R线圈(`RCoil`)、NOT
- **SR / RS** 双稳态触发器（`Sr`/`Rs`）

**数据与逻辑**
- **MOVE**（多输出 out1/out2…）
- **数学 `Add`/`Sub`/`Mul`/`Div`/`Mod`**
- **比较 `Ge`/`Gt`/`Eq`/`Ne`/`Lt`/`Le`**（pre/in1/in2/out）
- **类型转换 `Convert`**（SrcType/DestType，一个元件覆盖全部组合）

**时序与计数**
- **定时器 `TON`/`TOF`/`TP`/`TONR`**
- **计数器 `CTU`/`CTD`/`CTUD`**
- **多重实例定时器**（FB 的 `IEC_TIMER` 静态量，干净工程可用）

**结构化**
- **CALL 调用 FB** + **实例 DB 自动创建**（工程内的 FB 可用此方式）
- **FB 骨架生成**（从 JSON 接口定义生成可导入的 FB）

**辅助能力**
- 字面量常量（时间 `T#3s` / 数值带类型）
- 中文变量自动创建、**地址冲突检测**
- 实例 DB 自动编号
- 增量追加（不动已有段）、重复驱动检查
- **梯形图逻辑翻译**（`dump_ladder.py`，人工核对）
- **扫描级行为仿真**（`lad_behavior_sim.py`，验证状态机/定时/互斥，不依赖 PLCSIM）
- 工程清空/块删除（`delete_blocks` / `delete_tags` / 空块模板）

---

## 十九、时钟存储器字节 / 系统存储器字节（2026-10 实测学会）

S7-1200/1500 在 **CPU 设备组态**里可以启用两个 M 字节，提供**硬件方波**和**系统状态位**，
省掉软件定时器。官方说明见
[西门子产品门户](https://www.ad.siemens.com.cn/productportal/Prods/S7-1200_PLC_EASY_PLUS/08-Function/03-System_Clock_Memory.html)。

### 时钟存储器字节（本例配到 MB0）

| 位 | 周期 | 频率 | 变量名(本例) |
|---|---|---|---|
| M0.0 | 0.1s | 10Hz | Clock_10Hz |
| M0.1 | 0.2s | 5Hz | Clock_5Hz |
| M0.2 | 0.4s | 2.5Hz | Clock_2.5Hz |
| M0.3 | 0.5s | 2Hz | Clock_2Hz |
| M0.4 | 0.8s | 1.25Hz | Clock_1.25Hz |
| M0.5 | 1.0s | **1Hz** | Clock_1Hz |
| M0.6 | 1.6s | 0.625Hz | Clock_0.625Hz |
| M0.7 | 2.0s | **0.5Hz** | Clock_0.5Hz |

**注意是方波**（0.5s 高 / 0.5s 低），不是脉冲。做"每秒一个扫描周期的脉冲"要配
**上升沿检测**（P 触点）；做"闪烁"直接用 `Clock_1Hz` 或 `Clock_0.5Hz` 当触点即可。

### 系统存储器字节（本例配到 MB1）

| 位 | 含义 |
|---|---|
| M1.0 | FirstScan 首次循环（启动 OB 后第一个扫描周期为 1） |
| M1.1 | DiagStatusUpdate 诊断状态已更改 |
| M1.2 | AlwaysTRUE 始终为 1 |
| M1.3 | AlwaysFALSE 始终为 0 |

### 引用方式 = 普通全局变量

时钟位/系统位一旦启用，就是**普通的 M 位**，梯形图里引用方式和任何变量一样：

```xml
<Access Scope="GlobalVariable" UId="22">
  <Symbol><Component Name="Clock_1Hz"/></Symbol>   <!-- 指向 %M0.5 -->
</Access>
<Part Name="Contact" UId="27"/>                    <!-- 普通常开触点 -->
```

**没有特殊 XML 结构**，就是一个全局变量 + 触点。所以我的 MCP 能**引用**这些位
（建变量、写进梯形图），不需要任何新能力。

### ⚠ Openness 限制（关键）

- **启用**时钟/系统存储器字节 = **设备组态操作**，S7-1200 的 Openness **没有暴露这个 API**：
  - `GetAttributeInfos()` 列出的 CPU 属性里没有 Clock/Memory 项
  - 13 个候选属性名全部报 `"not supported by type 'DeviceItemImpl'"`
  - 网上专门文章标题即 *"S7-1200 SystemMemoryByte Openness API Missing - Workaround"*
- 结论：**启用这一步只能用户在博途 UI 里手动做一次**（CPU 属性 → 系统与时钟存储器 → 勾选 → 设地址 → 重新下载）。
- 一旦启用，之后 MCP 就能自由引用这些位（就是普通 M 变量）。

> 与 PLCSIM 变量读写、HMI 是同一类"TIA 界面能点、Openness 动不了"的架构限制。

### 与软件定时器的关系

之前做秒脉冲用"自复位 TON"、做闪烁用"级联 TON"，是因为**干净工程里建不了定时器实例 DB
（多重实例之前没打通）+ 时钟字节 Openness 配不了**。现在两条路都通了：
- 时钟字节启用后，**闪烁直接用一个 `Clock_1Hz` 触点**，零定时器；
- 秒脉冲可用 `Clock_1Hz` + 上升沿，或继续用自复位 TON（多重实例）。

---

## 二十、上升沿 / 下降沿（P / N 触点，2026-10 实测学会）

边沿检测：只在信号**从 0 变 1**（上升沿）或**从 1 变 0**（下降沿）的那一个扫描周期导通。

### 结构

```xml
<Part Name="PContact" UId="31"/>   <!-- 上升沿 -->
<Part Name="NContact" UId="32"/>   <!-- 下降沿 -->
```

都是**空 Part，没有子元素**，也没有 TemplateValue。引脚**四个**：

| 引脚 | 含义 | 接什么 |
|---|---|---|
| `pre` | 输入电源 | 上一个元件的 `out`（或母线） |
| `operand` | 被检测信号 | 信号变量（如 %M3.0） |
| `bit` | 边沿存储位 | 存上一扫描的信号状态（如 %M10.0） |
| `out` | 输出 | 下一个元件的入口 |

### 接线（真实样板）

```
<Wire><NameCon 上触点 out/><NameCon PContact pre/></Wire>         <!-- 电源流 -->
<Wire><IdentCon 存储位/><NameCon PContact bit/></Wire>             <!-- 存储位 -->
<Wire><IdentCon 信号/><NameCon PContact operand/></Wire>           <!-- 信号 -->
<Wire><NameCon PContact out/><NameCon 下一个 pre/></Wire>          <!-- 输出 -->
```

### 关键差异（对比普通触点）

| | 普通触点 Contact | 边沿触点 PContact/NContact |
|---|---|---|
| 入口引脚 | `in` | **`pre`** |
| 操作数 | 1 个 `operand` | **2 个：`operand`(信号) + `bit`(存储位)** |
| 导通条件 | 电平（信号=1 就通） | **边沿**（信号跳变的那个扫描周期才通） |

> 与比较元件的入口引脚 `pre` 是同一套命名 —— 凡是有"额外操作数"的元件，电源流入口都叫 `pre`。

### 用途

- 用时钟位（方波）做"每秒一个脉冲"：`Clock_1Hz` 接上升沿，只在 0→1 跳变的扫描周期导通一次
- 按钮"按下瞬间触发一次"（不重复触发）
- 检测状态变化（故障产生、到位瞬间）

### 生成用法

```json
{ "k": "p", "tag": "信号", "store": "存储位", "local": true }   // 上升沿
{ "k": "n", "tag": "信号", "store": "存储位", "local": true }   // 下降沿
```

---

## 二十一、OB 类型 / OpenCon 空引脚 / 指令调用（PID_Compact，2026-10 实测学会）

### 1. OB 类型靠 `SecondaryType` 区分

| OB | SecondaryType | 特有属性 | Interface Input 成员 |
|---|---|---|---|
| OB1 主程序 | `ProgramCycle` | — | `Initial_Call`(Bool) + `Remanence`(Bool) |
| OB30 循环中断 | `CyclicInterrupt` | **`CyclicTime`**(ms) + **`PhaseOffset`** | `Initial_Call`(Bool) + **`Event_Count`**(Int) |
| OB100 启动 | `Startup` | — | `Initial_Call`(Bool) |

> OB100 实测：`Number=100`、`EventClass=Startup`、`PriorityNumber=1`，导入编译 0 错误。
> **这是"上电/暖启动做初始化"的编程替代方案**（CPU 的启动模式属性改不了，见第二十二章第 6 节）。

```xml
<CyclicTime>100</CyclicTime>      <!-- 循环周期 ms: 100 = 100ms -->
<PhaseOffset>0</PhaseOffset>      <!-- 相位偏移 -->
<SecondaryType>CyclicInterrupt</SecondaryType>
<Number>30</Number>
```

- **OB 号必须真实存在**：`Number=0` 会报 `'0' is not a valid OB number`。循环中断用 30~38。
- OB 的 Interface 只有 `Input` / `Temp` / `Constant` 三个 Section（没有 Output/InOut/Static），
  且 Input 里的成员都是 `Informative="true"`（系统提供，不可改）。
- 多个空程序段 = 多个 `<SW.Blocks.CompileUnit>`，每个里面是自闭合的 `<NetworkSource />`。

### 2. `OpenCon` —— 空引脚（之前不知道的元素）

调用指令时**故意不接**的引脚，不是"省略不写"，而是显式写成 OpenCon：

```xml
<!-- 输入引脚空着 -->
<Wire><OpenCon UId="26"/><NameCon UId="24" Name="Input_PER"/></Wire>
<!-- 输出引脚空着 -->
<Wire><NameCon UId="24" Name="SetpointLimit_H"/><OpenCon UId="37"/></Wire>
```

`OpenCon` 的 `UId` 是它自己的独立编号（不引用任何 Access/Part），只要不重复即可。
21 个引脚全空也能编译 0 错误。

### 3. 指令调用（以 PID_Compact 为例）

```xml
<Part Name="PID_Compact" Version="2.3" UId="24">
  <Instance Scope="GlobalVariable" UId="25">
    <Component Name="PID_Compact_2" />     <!-- 工艺对象 DB，全局单实例 -->
  </Instance>
</Part>
```

- **必须有 `Version` 属性**（PID_Compact 是 `2.3`），否则导入报错。
- 实例是 `Scope="GlobalVariable"`（区别于 IEC_TIMER 多重实例的 `LocalVariable`）。
- 接线规则与 CALL 完全一致：
  - 输入：`<IdentCon 变量/> → <NameCon 引脚/>`
  - 输出：`<NameCon 引脚/> → <IdentCon 变量/>`
  - `en` 接 `<Powerrail />`

**PID_Compact 全部 21 个引脚**：

| 输入 | 输出 |
|---|---|
| `en` `Setpoint` `Input` `Input_PER` `Disturbance` `ManualEnable` `ManualValue` `ErrorAck` `Reset` `ModeActivate` `Mode` | `Output` `Output_PER` `Output_PWM` `ScaledInput` `SetpointLimit_H` `SetpointLimit_L` `InputWarning_H` `InputWarning_L` `State` `Error` `ErrorBits` |

- `Setpoint` / `Input` / `Output` 是 **Real**（工程量）
- `Input_PER` / `Output_PER` 是 **Int**（模拟量原始值，0~27648）
- `Output_PWM` 是 Bool，`State` / `ErrorBits` 是 Int/Word

### 4. 模拟量通道不需要特殊 XML（重要）

`Output_PER` 接 `%QW96` 时，块 XML 里**只有标签名**，没有 `<Address>` 节点：

```xml
<Access Scope="GlobalVariable" UId="23">
  <Symbol><Component Name="输出电压" /></Symbol>   <!-- 底层是 %QW96 -->
</Access>
```

**结论：模拟量通道就是普通标签**，按标签名引用即可，生成器无需任何特殊处理。

> 编译警告 `Inputs or outputs are used that do not exist in the configured hardware`
> 是因为 %QW96 超出 1212C 本体输出范围（Q0.0~Q0.5），属硬件不符，与 XML 结构无关。

### 5. 工艺对象（TO）参数读写 —— 已实测可读可写

用户把 PID 参数全改成非默认值（40/30/1.0/2.0/90/10/20），用来做**已知答案测试**。
结果：读出来全部一致，写也能生效。

#### 访问链路

```
PlcSoftware.TechnologicalObjectGroup      // TechnologicalInstanceDBGroup
  .TechnologicalObjects                   // ★ 全部工艺对象（.Groups 是用户文件夹，通常为空）
  → TechnologicalInstanceDB               // 例：PID_Compact_2
      .Parameters : TechnologicalParameterComposition
      → TechnologicalParameter { Name = "Config.InputUpperLimit", Value = 40 }
```

- `Name` 是**全点分名**（`Config.InputUpperLimit`、`Retain.CtrlParams.Gain`）
- 值在 **`Value`**（object 类型，可读**可写**；类型跟原值走：Real/Bool/Int）
- 实测参数总数 **83 个**
- 写操作：`param.Value = newVal` 即可，读回一致

#### PID_Compact 参数名对照（UI ↔ 内部名）

| UI | 内部参数名 | 实测值 |
|---|---|---|
| 控制器类型（常规/%） | `PhysicalQuantity` / `PhysicalUnit` | 0 / 0 |
| 反转控制逻辑 | `Config.InvertControl` | False |
| CPU 重启后激活 Mode | `RunModeByStartup` | True |
| 初始 Mode | `Mode` | — |
| Input 用 PER | `Config.InputPerOn` | — |
| Output 选择（模拟量/PWM/工程量） | `_Config.OutputSelect` | 0 |
| 过程值限值 上/下 | `Config.InputUpperLimit` / `Config.InputLowerLimit` | 40 / 0 |
| 过程值标定 | `Config.InputScaling.*PointOut/PointIn` | — |
| 过程值监视 警告上/下 | `Config.InputUpperWarning` / `Config.InputLowerWarning` | 30 / 0 |
| PWM 最短接通/关闭 | `Config.MinimumOnTime` / `Config.MinimumOffTime` | 1 / 2 |
| 输出值限值 上/下 | `Config.OutputUpperLimit` / `Config.OutputLowerLimit` | 90 / 10 |
| 错误未决替代输出 | `SubstituteOutput`（值）+ `SetSubstituteOutput`（使能） | 20 / True |
| P / I / D | `Retain.CtrlParams.Gain` / `.Ti` / `.Td` | 1 / 20 / 0 |
| D 滤波 | `Retain.CtrlParams.TdFiltRatio` | 0.2 |
| 采样时间 | `Retain.CtrlParams.Cycle` | — |
| 自整定 | `PIDSelfTune.SUT.TuneRule` / `PIDSelfTune.TIR.TuneRule` | — |
| 设定值限值 | `Config.SetpointUpperLimit` / `Config.SetpointLowerLimit` | — |

### 6. 坑：OB 的 `CyclicTime` 导入时被忽略

```xml
<CyclicTime>100</CyclicTime>   <!-- 导入时不会生效！ -->
```

- 新建出来的 OB：`CyclicTime = 0` → 编译报 **`循环时间: The value must be between 1 and 60000`**
- 修法：导入后 `block.SetAttribute("CyclicTime", n)`（`PhaseOffset` 同理）
- **已在 `import_lad_xml` 里自动补设**：读 XML 里声明的值 → 导入后 SetAttribute → 读回校验，
  结果里用 `reason` 报告。实测补设后 `CyclicTime = 100`、编译 0 错误。

### 7. 参数/属性修改的铁律

- **默认值 ≠ 用户意图**：只读到默认值时，"用户特意设置" 和 "出厂默认没人管" 无法区分。
- **参数只从当前任务来**：新回路的限值/PID 必须来自该工艺或用户指定，
  **禁止沿用上一个项目的值**。
- 写之前必须：读原值 → 报告 old/new → 写 → 读回校验；可回滚（dryRun 先看）。

---

## 二十二、工艺对象创建 / 硬件组态（2026-10 实测）

### 1. 创建工艺对象（PID_Compact）

```csharp
// PlcSoftware.TechnologicalObjectGroup : TechnologicalInstanceDBGroup
//   .TechnologicalObjects : TechnologicalInstanceDBComposition
comp.Create("PID_照明", "PID_Compact", new Version(2, 3));
```

- 三个参数就是已有 TO 上的三个属性：`Name` / `OfSystemLibElement`(="PID_Compact") / `OfSystemLibVersion`(="2.3")
- 组合上还有 `CreateFrom(MasterCopy, MasterCopyMode)`、`Find(name)`
- TO 有 **`Delete()`** —— 可回滚
- 实测：`PID_照明` 创建成功，83 个参数齐全

### 2. P/I/D 到底改哪个：`CtrlParamsBackUp` 而不是 `Retain`

| 参数 | 可写 | 说明 |
|---|---|---|
| `Retain.CtrlParams.Gain/Ti/Td` | ❌ **只读** | 报 `'set_Value' is not supported ... The property 'Value' is read-only`。这是**保留性运行值** |
| `CtrlParamsBackUp.Gain/Ti/Td` | ✅ | **配置值**（下载到 CPU 的那套），UI 上"PID 参数"页改的就是它 |

实测：`CtrlParamsBackUp.Gain 1→2.5`、`Ti 20→60`、`Td 0→0` 全部成功。

### 3. 控制器类型（物理量）的取值

`PhysicalQuantity`（Int16，**不是枚举**，只能试）：

| 值 | 含义 |
|---|---|
| 0 | 常规（默认） |
| 6 | **照明**（由用户在 UI 切换后实测读出，待二次确认） |

`PhysicalUnit` 也是 Int16，默认 0。

### 4. 新 TO 的出厂默认值（建出来就是这样）

| 参数 | 默认 |
|---|---|
| `Config.InputUpperLimit` | 120 |
| `Config.InputLowerLimit` | 0 |
| `Config.InputUpperWarning` | 3.402822E+38（float 上限＝不报警） |
| `Config.InputLowerWarning` | -3.402822E+38 |
| `Config.MinimumOnTime` / `MinimumOffTime` | 0 / 0 |
| `Config.OutputUpperLimit` / `LowerLimit` | 100 / 0 |
| `CtrlParamsBackUp.Gain` / `Ti` / `Td` | 1 / 20 / 0 |
| `RunModeByStartup` | True |

### 5. 扩展设备（信号模块）—— 已打通

#### 5.1 API 与签名

```csharp
// Rack_0 (System:Rack.S71200) 或 PLC_1 上都有:
Boolean CanPlugNew(String typeIdentifier, String name, Int32 positionNumber)   // 试插(不改工程)
DeviceItem PlugNew(String typeIdentifier, String name, Int32 positionNumber)   // 真插
Boolean CanPlugCopy / CanPlugMove / PlugCopy / PlugMove
IList<HardwareObject> GetPlugLocations()
```

#### 5.2 TypeIdentifier 的正确写法（实测）

```
OrderNumber:6ES7 234-4HE32-0XB0/V2.0      ← SM 1234 AI4/AQ2 ✅ 唯一可用版本
OrderNumber:6ES7234-4HE32-0XB0/V2.0       ← 无空格写法也认
```

- **必须带 `/V<固件版本>`**，且版本号必须对：这个模块只有 `/V2.0` 可用，
  试 `/V1.0`、`/V3.0`、`/V4.0`、`/V4.1`、`/V4.2` 全部返回 `CanPlugNew=false`。
- 用 `CanPlugNew` 批量试错是**零风险**的（不真插），这是找到正确写法的可靠办法。

#### 5.3 插槽地图（1212C 实测）

| 宿主 | 位号 | 插什么 | 数量 |
|---|---|---|---|
| **PLC_1**（CPU 本体） | 3 | **信号板 SB / 通信板 CB**（插在 CPU 正面） | 1 |
| **Rack_0** | **2、3** | **信号模块 SM**（DI/DO/AI/AQ） | 2（1212C 上限） |
| **Rack_0** | **101、102、103** | **通信模块 CM**（RS232/RS485/RS422） | 3 |

实测依据：
- SM 1234 只在 `2`/`3` 可插（`CanPlugNew` ✓），在 101/102/103 不可插
- CM 1241 RS232（`6ES7 241-1AH32-0XB0`）**只在** 101/102/103 可插，在 2/3 不可插
- SB 1223 DI8/DO8（`6ES7 223-0BD30-0XB0`）**只在 PLC_1 的 3** 可插
- 插了 1 个 SM 后 `GetPlugLocations()` 只剩 `3`、`101`、`102`、`103`

#### 5.4 模块 I/O 地址（实测值）

SM 1234 AI4/AQ2 在**位号 2** 的默认地址：

| 通道 | 地址 |
|---|---|
| AI 通道 0~3 | `%IW96`、`%IW98`、`%IW100`、`%IW102` |
| AQ 通道 0~1 | `%QW96`、`%QW98` |

**实证**：把输出变量从 `%QW100`（越界）改成 `%QW98` 后，
编译从 `WithWarnings(1)` 变为 **`Successful(0 错误 0 警告)`** ——
说明地址正确、警告确实是"地址超出已组态硬件"造成的。

#### 5.5 模块通道参数（测量类型/量程/滤波/积分时间）—— 读不到

UI 上「模拟量输入 → 通道 0」里的这些，Openness **全部不暴露**：

- 模块 `DeviceItem.GetAttributeInfos()` 只有 11 个通用属性（Author/OrderNumber/FirmwareVersion/TypeName/位号…），**没有任何通道参数**
- 模块**没有通道级子对象**（子树只有它自己一层）
- 反查全 API：`IntegrationTime`、`FilterFrequency`、`FloatingMeanValueFilterActive`、
  `IIRLowPassFilterActive`、`NotchFilterActive`、`OutputFilterUp/Down` 等类型**没有任何公开属性使用它们**（孤儿类型）

所以：**测量类型（电压/电流）、电压范围（±10V 等）、滤波等级、积分时间、诊断开关
—— 只能在界面里设，API 改不了。**

> 同一个坑的第三次出现（启动参数、时钟/系统存储器、模块通道参数），
> 结论统一为：**硬件组态参数 Openness 基本不给，能给的只有"插哪个模块/插哪个位号"。**

### 6. 暖启动 / RUN 模式 / 系统与时钟存储器 —— 未暴露（三条独立证据）

CPU 属性页里的这些设置 **Openness 完全不给**：

| UI 上的项 | Openness |
|---|---|
| 上电后启动：暖启动 - RUN 模式 | ❌ |
| 比较预设与实际组态：即便不匹配也启动 | ❌ |
| 组态时间 60000 ms | ❌ |
| OB 应该可中断 | ❌ |
| 系统和时钟存储器（时钟/系统字节的启用与地址） | ❌ |

**证据 1 — 权威属性清单**：`PLC_1.GetAttributeInfos()` 共 33 项
（OrderNumber / FirmwareVersion / WebserverActivate / SNMP* / PnDnsConfiguration …），
**没有任何启动或存储器相关项**。

**证据 2 — 候选名全被拒**：`StartupMode` `StartupType` `WarmStart` `ColdRestart` `ColdStart`
`RetentiveMemory` `StartupObjectNumbers` `MinCycleTime` `CycleTime` `CycleMonitoringTime`
`OperatingMode` `RunStopMode` `StartupBehavior` `RestartMode` `KeepRetentiveMemory`，
以及直接拿类型名试的 `StartupActionAfterPowerOn` `StartupComparisonPresetToActualModule`
`StartupDelay` —— **全部 `is not supported by type`**。

**证据 3 — 反查全 API 的宿主属性（决定性）**：
程序集里确实存在这三个枚举，但遍历 `Siemens.Engineering.*` 的全部公开属性后，
**没有任何属性使用它们**（孤儿类型，只服务内部 AML 层）；
`ClockMemory` / `SystemMemory` 连类型都不存在。

另外 `Device` 上**没有** `Export` / `Import` / `CAx` / `Aml` 方法 ——
"导出硬件组态 AML → 改 XML → 导入"这条路也不通。

**替代方案（能做的）**：

| 想做的事 | 做法 |
|---|---|
| 上电/暖启动执行一次初始化 | 建 **OB100**（`SecondaryType="Startup"`，实测可生成，编译 0 错误） |
| 判断是否首次扫描 | 系统存储字节的 `FirstScan` 位（`%M1.0`，需 UI 启用系统存储字节） |
| 循环时间/通信负载等 | 无 API，只能 UI 设 |

### 7. 实测交付样本：照明 PID 闭环

| 项 | 内容 |
|---|---|
| 工艺对象 | `PID_照明`（PID_Compact 2.3） |
| 控制器类型 | `PhysicalQuantity = 6`（照明） |
| 过程值 | 0 ~ 2000（lx 量级） |
| 警告 | 下限 200 / 上限 1800 |
| 输出限值 | 0 ~ 100 |
| PWM 最短通/断 | 0.5s / 0.5s |
| 替代输出 | 0（`SetSubstituteOutput = True`） |
| P / I / D | 2.5 / 60 s / 0 |
| 变量 | `照度设定值` %MD400、`照度实测值` %MD404、`照明输出` %MD408（均 Real） |
| 调用块 | `照明PID控制` [OB33]，`CyclicTime = 200`ms，`PriorityNumber = 11` |
| 编译结果 | **0 错误** |

---

## 二十三、还没打通的

| 项 | 原因 |
|---|---|
| 跳转 JMP / Label | 未验证（样板里没有 Label 目标） |
| MOVE_BLK | 未验证（大概率需要绑数据块） |
| **PLCSIM 自动下载 / 启停 / 读写变量** | **架构限制**：西门子未开放 API，只能手动 |
| **HMI** | 见 `docs/HMI学习记录.md`：画面能导出，对象结构待学 |
| **时钟/系统存储器字节的"启用"** | **架构限制**：S7-1200 Openness 未暴露，需 UI 手动（启用后可用） |
| **工艺对象（TO）的"创建"** | 待验证：TO 是在 TIA 里放置 PID_Compact 指令时自动生成的，Openness 未找到创建 API。<br>**但已有 TO 的参数可读可写**（83 个参数，见第二十一章第 5 节） |
