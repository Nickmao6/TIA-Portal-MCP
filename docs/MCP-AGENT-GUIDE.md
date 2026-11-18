# MCP Agent 使用指南

这份指南给接入本 MCP 的 AI Agent 看。目标是让 Agent 少猜、少误操作。

## 0. 前置检查

1. TIA Portal V21 已启动
2. 目标项目已打开（或先用 `tia_open_project` 打开）
3. 当前 Windows 用户属于 `Siemens TIA Openness` 组
4. `tia-scope.txt` 包含目标项目所在目录

先调用：

```text
tia_list_projects
```

确认项目名和路径，再调用：

```text
tia_browse_tree(plc_name="PLC_1")
```

看程序块和变量表。

## 1. 新建项目

TIA 同一时间只允许一个项目打开。如果已有项目：

```text
tia_save_project
tia_close_project(project_path="旧项目.ap21")
```

再：

```text
tia_create_project(project_dir="C:\\Users\\你\\Documents\\Automation", project_name="新项目")
tia_add_device(type_identifier="OrderNumber:6ES7214-1AG40-0XB0/V4.7", device_name="PLC_1")
tia_add_device(type_identifier="OrderNumber:6AV2124-0JC01-0AX0/17.0.0.0", device_name="HMI_1")
```

> `create_project` 的目录必须在 `tia-scope.txt` 授权范围内。

## 2. 建变量

格式：`名称@地址@类型`，多个用逗号分隔：

```text
tia_ensure_tags(tags="启动@%M0.0@Bool,停止@%M0.1@Bool,计数@%MW10@Int", dry_run=false)
```

默认 `dry_run=true`，Agent 应先预演，确认后再传 `false`。

## 3. 导入程序

- SCL：`tia_import_scl(scl_path="...", dry_run=false)`
- LAD：`tia_import_lad_xml(xml_path="...", dry_run=false)`
- 覆盖已有块：显式传 `overwrite=true`，不要默认覆盖
- 导入后调用 `tia_compile_plc`，以编译诊断为准

## 4. 工艺指令（运动控制）

详细格式见 [`MOTION-CONTROL-NOTES.md`](MOTION-CONTROL-NOTES.md)。

已验证可用：

- `MC_Power`
- `MC_MoveJog`
- `MC_Home`
- `MC_MoveAbsolute`
- `MC_Reset`
- `MC_Halt`
- `MC_MoveRelative`
- `MC_MoveVelocity`

规则：

- 工艺块在 LAD 里是 `<Part Name="MC_*" Version="8.0">`，不是普通 `<Call>`
- 先导入 Part，TIA 才会生成系统 FB；再用 `tia_ensure_instance_db` 建实例 DB
- 所有输出引脚都要接 `OpenCon` 或变量
- `MC_CommandTable`、`MC_ChangeDynamic`、`MC_WriteParam`、`MC_ReadParam` 需要真实工艺对象/参数，暂不自动生成

## 5. 保存

只有 `tia_save_project` 会保存。导入/改标签后如果要落盘，显式调用它。

## 6. HMI

当前 MCP 只覆盖 PLC 程序。HMI 的：

- WinCC Comfort 符号变量链接
- 数据记录 / 趋势视图
- 复杂画面导入

这些 Openness 不支持，必须由人在 TIA UI 里做。Agent 不要承诺“全自动 HMI”。

## 7. PLCSIM

`tia_plcsim_run` 只能做有限控制。PLCSIM V21 标准版没有进程外变量读写 API：

- `start` / `stop` 只是状态提示
- `read` / `write` 会返回 `PLCSIM_NOT_SUPPORTED`

下载到 PLCSIM 需要人工在 TIA 里完成，或在支持 PLCSIM Advanced 的环境里另行接入。

## 8. 安全规则

- 不要调用不存在的 `delete_*` 工具；删除只在 TiaEngine CLI
- 不要修改 `tia-scope.txt` 去绕过项目范围
- 写入前先 `dry_run`
- 每次写操作后编译并报告诊断
- 不猜测错误，按编译器返回的 Path/描述改

## 9. 常见错误

| 错误码 | 处理 |
|---|---|
| `NO_TIA_INSTANCE` | 启动 TIA Portal |
| `NO_PROJECT` | 打开项目 |
| `PROJECT_NOT_ALLOWED` | 把项目目录加入 `tia-scope.txt` |
| `PROJECT_ALREADY_OPEN` | 先保存并关闭当前项目 |
| `EXPORT_INCONSISTENT_BLOCK` | 在 TIA 保存/编译块后再导出 |
| `WORKER_NOT_FOUND` | 检查 `engine\TiaEngine.exe` 是否存在 |
