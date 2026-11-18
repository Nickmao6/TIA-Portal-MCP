# PLCMCP 项目介绍（作品集版）

> 给 PDF 生成 Agent 的说明：本文是项目事实来源，请严格按文中内容排版，不要编造用户量、Star 数、商业客户或未实现的 HMI 能力。作者名可先用「Nickmao6」，最终由用户替换。

---

## 封面

**项目名：PLCMCP**

**副标题：让 AI Agent 直接操作西门子 TIA Portal**

**一句话：**

> 一个开源的 MCP 服务器，把 Siemens Openness 封装成 15 个标准工具，让 Claude、Codex、DSH 等 AI Agent 用自然语言完成建项目、加设备、建变量、导入 SCL/LAD、编译和保存。

**作者：** Nickmao6  
**项目类型：** 个人开源项目 / AI × 工业自动化  
**技术栈：** C# / .NET 8 / .NET Framework 4.8 / Python / SimaticML / MCP / Siemens Openness  
**代码仓库：** https://github.com/Nickmao6/TIA-Portal-MCP  
**许可证：** MIT

---

## 1. 项目背景

西门子 TIA Portal 是工业自动化领域最常用的 PLC/HMI 工程平台之一。它提供了 Openness API，可以用程序创建项目、添加设备、生成程序块、编译和读写变量。

但 Openness 有几个现实门槛：

- 只支持 .NET Framework，不能直接在 .NET 8 / .NET Core 进程中运行
- API 庞大，学习成本高
- 很多操作需要在 TIA 界面里反复点击
- AI Agent 无法直接调用 Openness，只能“告诉人怎么做”

PLCMCP 想解决的是：

> **把 TIA Portal 的工程能力封装成 AI Agent 能直接调用的标准 MCP 工具，让“自然语言 → 博途工程”成为一条可执行的链路。**

---

## 2. 项目定位

PLCMCP 是一个 **MCP（Model Context Protocol）服务器**，不是 TIA Portal 的替代品，也不是西门子官方产品。

它做三件事：

1. **连接**：把 AI 客户端和本机 TIA Portal 连接起来
2. **封装**：把 Openness 的常用工程操作封装成 15 个工具
3. **约束**：用白名单、dry-run、显式保存等机制，防止 AI 误改工程

适合的场景：

- PLC 教学与学习：让 Agent 生成初学者风格的梯形图
- 工程原型：快速建项目、加设备、建变量、导入程序
- AI Agent 工具链：给 Claude Code / Codex / DSH 等 Agent 增加“操作博途”的能力
- 工业自动化方向的个人作品集

---

## 3. 系统架构

```text
MCP 客户端（Claude / Codex / DSH / 其他支持 MCP 的 Agent）
        │  stdio / JSON-RPC
        ▼
TiaMcpServer.exe（.NET 8）
        │  启动子进程，传递参数，读取单行 JSON
        ▼
TiaEngine.exe（.NET Framework 4.8）
        ▼
Siemens.Engineering（Openness）
        ▼
TIA Portal V21（本机已打开的项目）
```

### 为什么必须是双进程

Openness 使用 .NET Framework Remoting API，无法在 .NET 8 进程内加载。  
所以采用：

- **MCP Host（.NET 8）**：唯一持有 MCP 连接，负责工具定义、参数校验、进程调度
- **Worker（net48）**：唯一接触 Openness，负责实际 TIA 操作

两个进程之间用 **单行 JSON 信封** 通信：

```json
{"ok":true,"mode":"browse_tree","dryRun":false,"result":{...},"error":null}
```

stdout 只输出最终 JSON，日志走 stderr，保证 MCP 协议不被污染。

---

## 4. 核心功能

### 4.1 15 个 MCP 工具

| 类别 | 工具 |
|---|---|
| 项目 | `tia_list_projects`、`tia_create_project`、`tia_open_project`、`tia_close_project` |
| 设备 | `tia_list_devices`、`tia_add_device` |
| 查看 | `tia_browse_tree` |
| 变量 | `tia_ensure_tags` |
| 程序 | `tia_import_scl`、`tia_import_lad_xml`、`tia_export_block` |
| 编译 | `tia_compile_plc` |
| 保存 | `tia_save_project` |
| 监视 | `tia_ensure_watch_table` |
| 仿真 | `tia_plcsim_run` |

工具描述统一带 `[READ-ONLY]` / `[WRITE]` 前缀，方便 MCP 客户端做权限控制。

### 4.2 LAD 梯形图生成

除了导入手写 XML，项目还包含一套 Python 工具链：

- `ladgen.py`：SimaticML 底层生成器
- `ladgen_cli.py`：JSON 规格 → LAD XML
- `LAD-SPEC.md`：V21 SimaticML 格式规格与踩坑记录
- `lad_behavior_sim.py` / `lad_logic_sim.py`：不依赖 PLCSIM 的逻辑仿真

支持元件：

- 常开/常闭触点、线圈、S/R 线圈、取反
- 串联、并联分支
- 比较、MOVE、ADD/SUB、TON/TOF/TP、CTU/CTD/CTUD

### 4.3 初学者 / 高级两种写法

同一套工具可以生成两种风格：

**初学者风格**

- 中文变量名
- `%M` / `%Q`，不依赖 `%I`
- OB1 里少量程序段
- 简单串并联、自锁、定时器
- 逻辑直白，适合教学

**高级风格**

- FB + 多背景 DB
- 结构体、数组、间接寻址
- 状态机、步序控制
- SCL 的 FOR/WHILE/CASE、PEEK/POKE
- 复杂数据类型、UDT

> 说明：高级 LAD 中的 PID、运动控制、工艺对象需要 TIA 的工艺对象功能；`ladgen` 不生成所有高级 LAD，这类场景用 SCL 或 TIA 原生功能。

---

## 5. 技术难点与解决

### 5.1 .NET 8 与 .NET Framework 的边界

**问题：** Openness 不能进 .NET 8 进程。  
**解决：** 双进程 + JSON 信封 + AssemblyResolve 从 TIA 安装目录加载 Siemens DLL。

### 5.2 TIA 的 Openness 授权弹窗

**问题：** 外部程序访问 TIA 会弹“是否允许”，每次重新编译 worker 都会重新弹。  
**解决：**

- 研究出 TIA 的 AllowList 注册表机制：`HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList\<exe>\Entry`
- 记录 `Path`、`DateModified`、`FileHash`
- 提供 `Add-OpennessAllowList.ps1`，用管理员权限把 worker 写入白名单

### 5.3 LAD SimaticML 的坑

- `UId` 必须是十进制整数，十六进制会报 `not valid Int32`
- 一个程序段只能有一根电源线
- 多个输出要用并联分支挂在同一根 Powerrail 上
- `par` 不能作为分支首元件和其他分支共用电源线
- 块处于 inconsistent 时无法导出，要先编译/保存

### 5.4 HMI 的硬限制

- HMI 变量可以用绝对地址导入
- 但 Openness 读不到 `ControllerTag`，无法批量建立符号链接
- WinCC Comfort 没有 DataLog / TrendView 类，数据记录和趋势只能 UI 手动建
- 画面 XML 导入有崩溃风险，必须先保存并关闭画面编辑器

### 5.5 时钟/系统存储器地址冲突

启用时钟存储器 MB0 和系统存储器 MB1 后，`M0.x`、`M1.x` 会被系统占用。项目中的变量必须迁到其他 M 区，否则出现地址冲突。

### 5.6 PLCSIM 标准版没有进程外 API

PLCSIM V21 标准版是 Electron 桌面应用，没有公开的变量读写 API。  
因此 `tia_plcsim_run` 只做状态提示；真正的程序化读写需要 PLCSIM Advanced。

---

## 6. 安全设计

PLCMCP 不是“给 AI 无限权限”，而是带边界的工程工具：

| 机制 | 作用 |
|---|---|
| `tia-scope.txt` 项目白名单 | 只允许操作指定目录下的项目 |
| `dry_run` 默认 true | 写操作先预演，确认后才执行 |
| 显式 `tia_save_project` | 不会隐式保存工程 |
| 删除只在 CLI | `delete_blocks` / `delete_tags` 不暴露给 MCP |
| 编译诊断完整返回 | 不猜测错误，按 Path/描述修 |
| 串行 worker 锁 | 避免并发写同一个工程 |

---

## 7. 项目成果

- **15 个 MCP 工具**
- **44 个 TiaEngine 模式**
- **编译 0 错误 / 0 警告**
- **self-contained 发布包约 35 MB**，不需要目标机器安装 .NET SDK
- 已用于实际教学项目：
  - 水塔水位控制系统：CPU 1214C DC/DC/DC + TP900 Comfort，中文变量、11 个程序段、41 个 HMI 变量、分段水位显示
  - 自动洗衣机：循环控制、进水/洗涤/排水/报警逻辑修复
  - 温室大棚、红绿灯等 LAD 练习
- 配套文档：
  - `docs/MCP-AGENT-GUIDE.md`：Agent 调用顺序与工具约定
  - `docs/AGENT-PITFALLS.md`：真实踩坑记录
  - `docs/TIA-Openness-操作记忆.md`：Openness 操作记忆
  - `ladsim/LAD-SPEC.md`：SimaticML 格式规格

---

## 8. 这个项目算作品集吗？

**算，而且是一个比较完整的“AI × 工业自动化”作品集项目。**

它展示的不只是“会写 PLC”，而是：

1. **系统设计**：.NET 8 / net48 双进程架构
2. **工业领域知识**：TIA Portal、Openness、LAD、SCL、HMI
3. **AI Agent 工程**：MCP 工具设计、工具描述、权限与安全边界
4. **代码生成**：Python 生成 SimaticML XML
5. **调试能力**：从 TIA 报错、崩溃、注册表中反推机制
6. **技术写作**：规格文档、Agent 指南、踩坑记录

适合投递的方向：

- 工业自动化软件 / PLC 工具链
- AI Agent / MCP 生态
- 智能制造、工业互联网
- 自动化测试、工程效率工具

### 简历项目 bullet 参考

> **PLCMCP — TIA Portal MCP 服务器（个人开源）**
> - 设计 .NET 8 MCP Host + .NET Framework 4.8 Openness Worker 双进程架构，解决 Openness 无法在 .NET 8 进程运行的问题
> - 封装 15 个 MCP 工具，支持建项目/加设备/建变量/导入 SCL 与 LAD/编译/保存
> - 开发 Python SimaticML 生成器，支持触点、线圈、定时器、比较、并联分支等 LAD 元件
> - 建立项目白名单、dry-run、显式保存等安全机制，防止 AI 误改工程
> - 输出 Agent 使用指南与真实踩坑文档，项目已开源

---

## 9. 边界与反思

这个项目不是“工业级商业产品”，它的价值在于：

- 把 Openness 的能力用 AI Agent 友好的方式重新组织
- 把踩过的坑写成可复用的知识
- 证明个人可以在 AI + 工业软件交叉领域做出完整工具链

已知限制：

- HMI 数据记录/趋势、符号变量链接需要人工在 TIA UI 完成
- PLCSIM 标准版没有进程外读写 API
- 高级 LAD（PID、运动控制）需要 TIA 工艺对象，不由 `ladgen` 生成
- 目前以单机、单项目、Windows 环境为主

下一步可以继续做：

- HMI 只读/导出工具
- 常驻 worker，减少进程启动和授权次数
- PLCSIM Advanced 接入
- 端到端自动化测试
- GitHub Actions / 发布流程

---

## 10. 项目信息

- **GitHub：** https://github.com/Nickmao6/TIA-Portal-MCP
- **License：** MIT
- **运行环境：** Windows 10/11 x64 + TIA Portal V21 + Openness
- **仓库结构：**
  - `mcp-server/`：.NET 8 MCP Host
  - `engine/`：.NET Framework 4.8 Openness Worker
  - `ladsim/`：LAD 生成与仿真工具链
  - `docs/`：使用说明、Agent 指南、踩坑记录
  - `tools/`：辅助脚本

---

## 给 PDF 生成 Agent 的排版要求

- 语言：中文
- 页面：A4，6–8 页
- 风格：工业软件 / 技术作品集，蓝灰配色，简洁，不要花哨
- 封面：项目名 + 副标题 + 作者 + GitHub 地址
- 第 2 页：项目背景 + 一句话介绍 + 架构图
- 第 3–4 页：核心功能、15 个工具表、LAD 生成
- 第 5–6 页：技术难点与解决（每个难点用“问题 → 解决 → 收获”卡片）
- 第 7 页：项目成果 + 作品集价值 + 简历 bullet
- 第 8 页：边界与反思 + 项目信息
- 图表：架构图用流程图；工具表用表格；难点用卡片；不要放未提供的截图
- 不要编造数据：没有用户量、Star 数、商业客户、性能数字
- 作者名可先用 `Nickmao6`，由用户最终替换
