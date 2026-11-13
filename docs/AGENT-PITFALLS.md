# 踩坑记录（Agent 必读）

这些是 PLCMCP 开发和使用过程中真实踩过的坑，按类别整理。**接入本 MCP 的 Agent 在操作前先读一遍，能少走弯路、少崩博途。**

## 1. TIA 基础限制

### 1.1 同一时间只能打开一个项目
`Projects.Create` / `Projects.Open` 时如果已有项目打开，会报：
```
Another project is already open
```
正确顺序：`tia_save_project` → `tia_close_project(旧项目)` → 再创建/打开新项目。

### 1.2 新建项目后必须保存
Openness 新建项目/设备后，如果不调用 `project.Save()`（即 `tia_save_project`），一旦 TIA 崩溃或重开，刚建的设备和变量会全部丢失。**每次创建/导入后立即保存。**

### 1.3 必须用 `TiaPortalMode.WithUserInterface`（attach 模式）
直接 `new TiaPortal(TiaPortalMode.WithoutUserInterface)` 会报：
```
EngineeringSecurityException: Security error. The operation has timed out.
```
本 MCP 采用 attach 模式：**必须先手动启动 TIA Portal，worker 再 Attach**。

## 2. Openness 访问授权（弹“是否允许”）

TIA 对外部程序弹授权框，点“始终允许”后写入注册表：
```
HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList\<exe>\Entry
  Path / DateModified / FileHash
```
规则：

- **同一个 exe、不重新编译** → 点一次后不再弹
- **重新编译 exe，哈希变了** → TIA 当成新程序，又弹
- 重启电脑不会清除白名单

发布包里提供 `tools\Add-OpennessAllowList.ps1`，可预先写入白名单。**Agent 不要承诺“零弹窗”**；新编译的 worker 需要用户重新授权一次。

## 3. HMI（WinCC）硬限制

| 需求 | 是否可行 |
|---|---|
| HMI 变量绝对地址导入 | ✅ 可行，`AddressAccessMode=Absolute` + `LogicalAddress=MW10` |
| HMI 变量符号链接（PLC 变量列显示名字） | ❌ Openness 读不到 `ControllerTag`，只能 UI 手动拖 |
| WinCC Comfort 数据记录 / 趋势视图 | ❌ `WinCC.dll` 只有枚举，无 DataLog/TrendView 类 |
| 画面 XML 导入 | ⚠️ 可导入简单对象，但有崩溃风险，必须先保存并关闭画面编辑器 |
| `<Hmi.Screen.TrendView>` 元素 | ❌ 报 `Class ... is not supported` |

**Agent 不要承诺“全自动 HMI”。** 数据记录、趋势视图、符号链接必须在 TIA UI 里手动建。

### 3.1 HMI 变量导入的正确姿势
- 用绝对地址：`<LogicalAddress>MW10</LogicalAddress>`（不带 `%`）
- 不要写 `<ControllerTag>`，否则报 `controller tag not found`，严重时直接崩 TIA
- 一个 XML 一个变量，循环 `TagComposition.Import`
- 改 HMI 变量地址：先 `Tag.Delete()`，再用新地址 XML 重新导入；`SetAttribute("LogicalAddress")` 会报 `not supported`

### 3.2 HMI 连接
- 用 XML 导入的 `Connection_1` 只有驱动/协议，没有伙伴站，HMI 编译报“证书组态无效”
- 在 TIA UI 手动建的连接才有 `站=PLC_1、伙伴=PLC_1、节点=CPU`，是有效连接
- 处理：HMI 变量 XML 的连接名写 UI 建的连接名，删掉无效的 `Connection_1`，并把 PLC 的「防护与安全 → 访问控制」设为禁用

## 4. 时钟/系统存储器地址冲突

CPU 属性里启用时钟存储器 MB0 + 系统存储器 MB1 后，TIA 自动生成：
```
Clock_Byte=%MB0  Clock_10Hz=%M0.0 ... Clock_1Hz=%M0.5 ... Clock_0.5Hz=%M0.7
System_Byte=%MB1  FirstScan=%M1.0 DiagStatusUpdate=%M1.1 AlwaysTRUE=%M1.2 AlwaysFALSE=%M1.3
```
**M0.x / M1.x 不能再当普通变量用**。新建 CPU 后若要开时钟，先把变量挪出 MB0/MB1。

## 5. PLC 程序块导入/编译

### 5.1 LAD XML
- `UId` 必须是**十进制整数**，十六进制如 `"30A"` 会报 `not valid Int32`
- 一个 CompileUnit（程序段）只能有**一根电源线**；多个输出用并联分支挂同一根 Powerrail
- `par`（OR 并联）不能作为分支首元件和其它分支共用电源线，要单独一段
- 重复驱动同一位要检查，否则逻辑互相覆盖

### 5.2 导出块
- 块处于 inconsistent 时导出报 `EXPORT_INCONSISTENT_BLOCK`
- 先 `tia_compile_plc` 再 `tia_export_block`；编辑器里改完块要 Ctrl+S

### 5.3 定时器实例
- FB 里用 `IEC_TIMER` 静态变量做多重实例，避免需要系统实例 DB
- FC 的 Temp 存不住 TON/CTU/R_TRIG 状态，实例必须放 FB Static 或全局 DB

## 6. PLCSIM

- PLCSIM V21 标准版是 Electron 桌面应用，**没有进程外变量读写 API**
- `tia_plcsim_run` 的 `read`/`write` 返回 `PLCSIM_NOT_SUPPORTED`
- 真程序化读写需 PLCSIM Advanced（REST API + S7 协议）
- 下载到 PLCSIM 目前需要人工在 TIA 里点“下载到设备”

## 7. 工程白名单 tia-scope.txt

- 所有项目操作前会检查 `tia-scope.txt`，路径不在范围内报 `PROJECT_NOT_ALLOWED`
- 新建项目的目录也必须在授权范围内
- 改文件后立即生效，无需重编译

## 8. Agent 调用纪律

1. 写操作先 `dry_run`，确认无误再传 `dry_run=false`
2. 每次写后 `tia_compile_plc` 并读诊断；**不要猜错误**，按诊断 Path/描述改
3. 保存只在需要落盘时调用 `tia_save_project`
4. 不要假设设备名/变量名存在，先 `tia_browse_tree` / `tia_list_devices` 确认
5. 覆盖已有块必须显式 `overwrite=true`，禁止静默覆盖
6. 同一项目串行操作；worker 已有互斥锁，但客户端别并发写
