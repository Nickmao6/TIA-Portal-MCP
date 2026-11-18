# 运动控制工艺指令 LAD 写入笔记

基于 `步进电机学习` 项目实测（CPU 1215C DC/DC/DC，PTO 轴 `轴_1`）。

## 已成功写入并编译

从 `Main` 程序段 5 开始追加：

| 程序段 | 指令 | 实例 DB |
|---|---|---|
| 5 | `MC_Reset` | `MC_Reset_DB` |
| 6 | `MC_Halt` | `MC_Halt_DB` |
| 7 | `MC_MoveRelative` | `MC_MoveRelative_DB` |
| 8 | `MC_MoveVelocity` | `MC_MoveVelocity_DB` |

编译结果：`0 error / 0 warning`。

项目里原本已有：

- 程序段 1：`MC_Power`
- 程序段 2：`MC_MoveJog`
- 程序段 3：`MC_Home`
- 程序段 4：`MC_MoveAbsolute`

## 写入流程（Openness）

1. 导出当前 `Main` 为 SimaticML XML。
2. 在 XML 里追加一个 `SW.Blocks.CompileUnit`，里面是：

```xml
<Part Name="MC_Reset" Version="8.0" UId="...">
  <Instance Scope="GlobalVariable" UId="...">
    <Component Name="MC_Reset_DB"/>
  </Instance>
</Part>
```

3. 连接引脚：

- `Powerrail` → `Part.en`
- `轴_1` → `Part.Axis`
- `触发变量` → `Part.Execute`
- 其余输出引脚接 `OpenCon`，或接实际变量

4. 用 `tia_import_lad_xml` / `TiaEngine --mode import_lad_xml --overwrite` 导入 `Main`。
5. 导入后 TIA 会自动在 **系统块 > 程序资源** 下创建对应的 `MC_*` FB。
6. 用 `tia_ensure_instance_db` 创建实例 DB：

```text
tia_ensure_instance_db(fb_name="MC_Reset", db_name="MC_Reset_DB", dry_run=false)
```

7. 编译，确认 0 error。

> `TiaEngine.EnsureInstanceDb` 已允许 `MC_*` 系统块前缀；否则会误报 `FB_NOT_FOUND`。

## 引脚实测注意

### MC_Reset

- 输入：`Axis`、`Execute`、`Restart`
- 输出：`Done`、`Busy`、`Error`、`ErrorID`、`ErrorInfo`
- 漏接 `Restart` 会报 `connection Restart is not connected`

### MC_Halt

- 输入：`Axis`、`Execute`
- 输出：`Done`、`Busy`、`CommandAborted`、`Error`、`ErrorID`、`ErrorInfo`
- 本项目 V21 的 `MC_Halt` Part 没有 `Deceleration` 引脚，不要硬加

### MC_MoveRelative

- 输入：`Axis`、`Execute`、`Distance`、`Velocity`
- 输出：`Done`、`Busy`、`CommandAborted`、`Error`、`ErrorID`、`ErrorInfo`

### MC_MoveVelocity

- 输入：`Axis`、`Execute`、`Velocity`、`Direction`、`Current`、`PositionControlled`
- 输出：`InVelocity`、`Busy`、`CommandAborted`、`Error`、`ErrorID`、`ErrorInfo`

### 尚未接入的指令

以下 4 个不是“不会写 LAD”，而是需要真实的工艺对象/参数配置，不能空接：

- `MC_CommandTable`：需要命令表对象、`CommandTable`、`StartStep`、`EndStep`，以及 `CurrentStep`/`StepCode` 输出；空接会报数据类型错误
- `MC_ChangeDynamic`：需要动态参数（`ChangeRampUp/Down`、`RampUp/DownTime`、`ChangeEmergency`、`EmergencyRampTime`、`ChangeJerkTime`、`JerkTime` 等）
- `MC_WriteParam` / `MC_ReadParam`：引脚不是 `Axis`，而是 `TO`；还需要 `value_type` 的 `TemplateValue`，并指定真实参数索引/值类型

用户已明确：这 4 个暂时不学，不要自动往项目里硬塞。

## 地址注意

测试标签使用了：

- `复位触发 %M20.0`、`复位完成 %M20.1`
- `暂停触发 %M21.0`
- `相对定位触发 %M21.1`
- `速度运行触发 %M21.2`
- `命令表触发 %M21.3`
- `动态修改触发 %M22.0`
- `写参数触发 %M22.1`
- `读参数触发 %M22.2`
- `相对距离 %MD24`、`相对速度 %MD28`
- `暂停减速度 %MD32`、`速度设定 %MD36`
- `写参数索引 %MW40`、`写参数值 %MD42`
- `读参数索引 %MW46`、`读参数值 %MD48`

不要再用 `%M3.x`：`点动速度` 占用 `%MD3`，会地址冲突。

## 结论

- 基础运动控制：`MC_Power`、`MC_MoveJog`、`MC_Home`、`MC_MoveAbsolute`、`MC_Reset`、`MC_Halt`、`MC_MoveRelative`、`MC_MoveVelocity` 已验证可写入并编译
- 需要工艺对象配置的 4 个：暂缓
