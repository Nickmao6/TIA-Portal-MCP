# HMI（WinCC）学习记录

> 2026-10-16 起。**TP900 Comfort**（9 寸，订货号 `6AV2 124-0JC01-0AX0`，V17）。
> 注：`6AV2124-0JC01-0AX0` = TP900 **Comfort**（不是 KTP900 Basic，之前记错已更正）。

## 一、访问链路（已打通）

```
Project.Devices
  └─ Device "HMI_1"
       └─ DeviceItem "HMI_RT_1"          ← 只有这个有 Software（其它是接口/设备本体）
            └─ GetService<SoftwareContainer>().Software
                 = Siemens.Engineering.Hmi.HmiTarget
```

注意：HMI 设备的 Software 类型命名空间是 **`Siemens.Engineering.Hmi`**（不是 WinCC）。
其它 DeviceItem（`HMI_1`、`HMI_1.IE_CP_1`、`HMI_1.MPI/DP_CP_1`）`GetService<SoftwareContainer>` 返回 **null**。

## 二、HmiTarget 成员

| 成员 | 类型 | 含义 |
|---|---|---|
| ScreenFolder | ScreenSystemFolder | 画面根文件夹（.Screens = 画面集合） |
| TagFolder | TagSystemFolder | HMI 变量根（.DefaultTagTable / .TagTables） |
| Connections | ConnectionComposition | HMI↔PLC 连接（.Count / .Item） |
| ScreenOverview | ScreenOverview | 画面总览 |
| ScreenPopupFolder / ScreenSlideinFolder / ScreenTemplateFolder | | 弹出/滑入/模板画面 |
| GraphicLists / TextLists | | 图形列表 / 文本列表 |
| Cycles / VBScriptFolder | | 循环 / 脚本 |

## 三、画面（Screen）

- 访问：`ScreenFolder.Screens`（ScreenComposition），元素类型 `Siemens.Engineering.Hmi.Screen.Screen`
- 画面对象**有 `Export(FileInfo, ExportOptions)` 方法** —— 跟 PLC 块一样能导出 XML！
- 也有 `GetAttribute/SetAttribute/GetAttributeInfos`（通用属性访问）、`Delete()`

### 画面 XML 结构（导出样板，空画面）

```xml
<Document>
  <Engineering version="V21" />
  <DocumentInfo>…</DocumentInfo>
  <Hmi.Screen.Screen ID="0">
    <AttributeList>
      <ActiveLayer>0</ActiveLayer>
      <BackColor>182, 182, 182</BackColor>
      <GridColor>0, 0, 0</GridColor>
      <Height>480</Height>
      <Name>画面_1</Name>
      <Number>1</Number>
      <Visible>true</Visible>
      <Width>800</Width>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="HelpText">…</MultilingualText>
    </ObjectList>
  </Hmi.Screen.Screen>
</Document>
```

- 根元素 `Hmi.Screen.Screen`，AttributeList 存画面尺寸/背景色/名称
- `ObjectList` 里放画面上的**对象**（按钮/指示灯/IO 域等）—— 空画面只有 HelpText

## 四、当前项目状态

- 画面：`画面_1`（1 个，空）
- HMI 变量：0 个
- 连接（Connections）：0（**尚未建立 HMI Connection**，设备网络上的"连 PLC"不等于 HMI 连接）

## 五、画面的属性 / 颜色怎么改（2026-10-22 实测）

### 结论：颜色**不是属性**，只能走 XML 导入

- `Screen.GetAttributeInfos()` 只返回 **1 项**（`Name`）
- `Screen.GetAttribute("BackColor")` → 抛异常（属性不存在）
- 但导出 XML 里明明有 `<BackColor>` / `<GridColor>`

### 正确路线：导出 → 改 XML → 导入覆盖

```csharp
// 导入不在 Screen 上, 在**画面集合**上:
// Siemens.Engineering.Hmi.Screen.ScreenComposition
IList<Screen> Import(FileInfo path, ImportOptions importOptions)
```

| 对象 | 方法 |
|---|---|
| `Screen` | 只有 `Export(FileInfo, ExportOptions)` |
| `ScreenComposition`（.Screens） | **`Import(FileInfo, ImportOptions)`** ← 导入在这 |
| `HmiTarget` | `ImportScreenGlobalElements` / `ImportScreenOverview` |
| `ScreenComposition` | `CreateFrom(ScreenLibraryTypeVersion)` / `CreateFrom(MasterCopy)` |

### 实测：把背景色和网格色改成纯白

```xml
<BackColor>182, 182, 182</BackColor>   →  <BackColor>255, 255, 255</BackColor>
<GridColor>0, 0, 0</GridColor>         →  <GridColor>255, 255, 255</GridColor>
```

再用 `ImportOptions.Override` 导入同名画面，回读确认：

```
BackColor = 255, 255, 255  ✓
GridColor = 255, 255, 255  ✓
```

> 坑：TIA 导出的 XML 带 **BOM**，读要用 `utf-8-sig`，写回用 `utf-8`。

## 五之二、画面对象结构：矩形 / 线（2026-10-22 实测）

画面上的对象**不是直接挂在 Screen 下**，而是挂在一个「层」里：

```
Hmi.Screen.Screen
  └─ ObjectList
       ├─ MultilingualText  (HelpText)
       └─ Hmi.Screen.ScreenLayer  ID="3"  CompositionName="Layers"
            └─ ObjectList
                 ├─ Hmi.Screen.Line       ID="4"  CompositionName="ScreenItems"
                 ├─ Hmi.Screen.Rectangle  ID="5"  CompositionName="ScreenItems"
                 ├─ Hmi.Screen.Rectangle  ID="6"  CompositionName="ScreenItems"
                 └─ Hmi.Screen.Line       ID="7"  CompositionName="ScreenItems"
```

层本身的属性：

```xml
<Hmi.Screen.ScreenLayer ID="3" CompositionName="Layers">
  <AttributeList>
    <Index>0</Index>
    <Name />
    <VisibleES>true</VisibleES>
  </AttributeList>
```

### 矩形 `Hmi.Screen.Rectangle`

```xml
<Hmi.Screen.Rectangle ID="5" CompositionName="ScreenItems">
  <AttributeList>
    <BackColor>217, 217, 217</BackColor>
    <BackFillStyle>Solid</BackFillStyle>
    <BorderColor>24, 28, 49</BorderColor>
    <BorderWidth>1</BorderWidth>
    <EdgeStyle>Solid</EdgeStyle>
    <Flashing>None</Flashing>
    <Height>12</Height>
    <Left>146</Left>
    <ObjectName>矩形_2</ObjectName>
    <RoundCornerHeight>0</RoundCornerHeight>
    <RoundCornerWidth>0</RoundCornerWidth>
    <TabIndex>-1</TabIndex>
    <Top>247</Top>
    <UseDesignColorSchema>false</UseDesignColorSchema>
    <Width>401</Width>
  </AttributeList>
</Hmi.Screen.Rectangle>
```

| 属性 | 含义 |
|---|---|
| `Left` / `Top` / `Width` / `Height` | 位置尺寸（像素） |
| `BackColor` + `BackFillStyle` | 填充色 + 填充方式（Solid/Transparent） |
| `BorderColor` / `BorderWidth` / `EdgeStyle` | 边框色/线宽/线型 |
| `RoundCornerWidth` / `RoundCornerHeight` | 圆角（0=直角） |
| `ObjectName` | 对象名（矩形_2、矩形_3…） |
| `TabIndex` / `Flashing` / `UseDesignColorSchema` | 制表位、闪烁、设计色彩方案 |

### 线 `Hmi.Screen.Line`

```xml
<Hmi.Screen.Line ID="4" CompositionName="ScreenItems">
  <AttributeList>
    <BackColor>255, 255, 255</BackColor>
    <Color>24, 28, 49</Color>
    <EndLeft>173</EndLeft> <EndTop>207</EndTop>
    <StartLeft>147</StartLeft> <StartTop>258</StartTop>
    <EndStyle>NoEnd</EndStyle> <StartStyle>NoEnd</StartStyle>
    <LineEndShapeStyle>Round</LineEndShapeStyle>
    <FillStyle>Transparent</FillStyle>
    <LineWidth>1</LineWidth>
    <Left>147</Left> <Top>207</Top> <Width>26</Width> <Height>51</Height>
    <Style>Solid</Style>
    <ObjectName>线_1</ObjectName>
  </AttributeList>
</Hmi.Screen.Line>
```

关键：线的端点用 **`StartLeft/StartTop` + `EndLeft/EndTop`**（绝对坐标），
`Left/Top/Width/Height` 只是外接框；`Style`=Solid/Dashed 等。

> 实测：画面上 2 个矩形（401×12，一上一下）+ 2 条斜线（26×51），
> 拼成一个"立体盒子/传送带"的视觉效果。**这套结构我现在可以直接生成。**

## 六、已打通的引擎模式

| mode | 作用 |
|---|---|
| probe_hmi | 列 HmiTarget 成员 |
| probe_hmi_detail | 列 ScreenFolder/TagFolder/Connections 结构 |
| probe_hmi_screens | 列画面名 / HMI 变量名 / 连接数 |
| probe_hmi_screen_detail | 列画面对象的方法（找 Export） |
| probe_hmi_import | 列 ScreenFolder / ScreenComposition / Screen 的 Import-Export-Create |
| export_hmi_screen | 导出画面 XML |
| **import_hmi_screen** | **导入画面 XML（改属性/加控件的唯一途径）** |
| hmi_screen_attrs | 列画面属性（只有 Name） |

## 七、待学

- **控件**（按钮/指示灯/IO 域/文本域/图形）的 XML + 变量绑定方式 —— 需要样板
- HMI 变量（TagTable.Tags）的创建/连接
- HMI Connection 的建立（HMI↔PLC）

## 八、坑（已踩）

- **加了 HMI 设备后，博途若有未处理的 HMI 相关模态对话框（连接/下载/编译提示），整个 Openness 接口会卡死**，连 `list_projects` 都挂起。做 HMI 操作前先确认博途 UI 无弹窗。
- 反射探测 HmiTarget **只列属性名不调 getter**（某些 getter 会阻塞）。
- **`GraphicList.Export` 会让博途整个崩掉**（2026-10-22 21:13:45，`ErrorReports.xml` 记 `Reason="Openness"`，丢失约 30 分钟未保存改动）。已在代码里硬禁用。
- **图形库（Siemens 自带图）位置**：`D:\Program Files\Siemens\Automation\Portal V21\Lib\Graphics\`。
  正常应有 `Equipment\ / Technology\ / Navigate & operate\ / Plant products\` 实体文件夹；
  本机原来**只有一个 `Graphics_All.zip`**（8002 条目：WMF 3008 / PNG 2876 / EMF 1778 / SVG 130），
  结果 TIA 图形浏览器只显示文件夹、点开没有图。
  **修复**：把 zip 解压到同目录（7792 文件 / 210 目录），保留原 zip。解压后：
  ```
  Equipment            3521
  Technology           1138
  Navigate & operate   2109
  Plant products       1024
  ```
  例：`Equipment\Automation [EMF]\Cabinets\Animate\` 下有 20 个 emf 帧文件。
  注意 PowerShell 里路径含 `[EMF]` 会被当通配符，要用 `-LiteralPath` 或 python 读。

### 两个"图形"别混淆

| | 位置 | 说明 |
|---|---|---|
| **图形库** | `...\Portal V21\Lib\Graphics\` | Siemens 自带 8002 张，全局共享 |
| **项目图形资源** | 项目内部（`GraphicLists`） | 拖进画面后生成的 `图形_1`、`图形_2`… |
| **画面引用** | `GraphicView` 的 `<Picture><Name>图形_N</Name>` | 按**名字**引用，资源没了就空白 |
