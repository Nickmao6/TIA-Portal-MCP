# TIA Portal V21 MCP Server（中文）

> English: [README.md](README.md)

用自然语言驱动西门子 TIA Portal V21，自动创建 PLC 程序块并在博途界面里可见，附 PLCSIM 仿真验证能力。

> **第一次用请先看** [`docs/使用说明.md`](docs/使用说明.md) —— 面向不写代码的用户，讲清楚了怎么用、怎么配、有哪些安全边界。
>
> **接入 AI Agent** 请看 [`docs/MCP-AGENT-GUIDE.md`](docs/MCP-AGENT-GUIDE.md) —— 调用顺序、工具约定、错误码和安全规则。

## 架构（双进程硬约束）

```
MCP 客户端
   │  stdio / JSON-RPC
   ▼
C:\path\to\PLCMCP\mcp-server\  →  TiaMcpServer.exe     [.NET 8，唯一持有 MCP 连接]
   │  启动子进程，传参数，读回单行 JSON 信封
   ▼
C:\path\to\PLCMCP\engine\       →  TiaEngine.exe        [net48，唯一接触 Openness]
   │  .NET Framework Remoting
   ▼
Siemens.Engineering (D:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48)
   │
   ▼
TIA Portal V21（博途，必须已运行且已加载项目）
```

**为什么必须双进程**：Openness 使用 .NET Framework Remoting API，无法在 .NET 8 进程内运行。

**程序集解析**：西门子 DLL 不可再分发，因此关闭 Copy Local（`<Private>false</Private>`），并在 worker 启动第一件事注册 `AssemblyResolve` 钩子，从 D 盘 PublicAPI 目录加载。

## 目录结构

```
C:\path\to\PLCMCP\
├── engine\                    net48 worker（老式 csproj，legacy MSBuild 路径）
│   ├── TiaEngine.csproj       TargetFrameworkVersion v4.8
│   ├── Program.cs             模式分发 + JSON 信封 + AssemblyResolve 钩子
│   ├── TiaSession.cs          Openness 会话封装（6 个操作）
│   ├── Json.cs                零依赖 JSON 输出
│   └── Models.cs              错误模型 / 结果模型
├── mcp-server\                .NET 8 MCP 宿主
│   ├── McpHost.csproj         net8.0 + ModelContextProtocol 2.2.0
│   ├── Program.cs             15 个工具定义 + 转发到 worker
│   └── mcp-probe.ps1          stdio 协议自测脚本
├── ladsim\                    LAD(梯形图)生成与解析工具链
│   ├── ladgen.py              SimaticML 底层生成器(触点/线圈/串联/并联)
│   ├── ladgen_cli.py          通用生成器: JSON 规格 -> LAD XML
│   ├── lad-pipeline.ps1       ★ 一键工作流(导出→生成→建变量→导入→编译→检查→逻辑仿真)
│   ├── lad_logic_sim.py       ★ 梯形图逻辑仿真器(不依赖 PLCSIM, 本机验证行为)
│   ├── spec_motor.json        电机启停回路规格示例
│   ├── build_motor_full.py    电机回路一键生成
│   ├── analyze_ladder.py      梯形图解读 + 重复驱动检查
│   ├── simaticml_inspect.py   SimaticML 结构剖析器
│   ├── LAD-SPEC.md            ★ V21 格式权威规格(所有踩坑记录)
│   ├── schema\                从博途程序集提取的官方 XSD
│   └── samples\               真实导出样本
├── tools\ildump\              IL 反编译工具(从博途 DLL 挖 XML 格式)
├── _research_tiautil\         TiaUtilities 参考源码
├── artifacts\                 生成的 SCL 产物
└── docs\                      文档
```

## LAD 梯形图生成（本项目核心能力）

用自然语言描述电路 → 生成 SimaticML XML → 导入博途 → 编译验证。

### 一键执行（推荐）

```powershell
powershell -File C:\path\to\PLCMCP\ladsim\lad-pipeline.ps1 `
    -Spec C:\path\to\PLCMCP\ladsim\spec_motor.json -Block Main -Plc PLC_1
```

自动完成 6 步：导出基线 → 生成 XML → 建变量 → 导入博途 → 编译读诊断 → 解读+查重复驱动。
加 `-DryRun` 可只预演不导入。

### 手动分步

```bash
# 1) 导出当前 OB1 作为基线（保留你手画的段1）
TiaEngine.exe --mode export_block --block Main --out cur.xml --plc PLC_1

# 2) 用 JSON 规格生成梯形图
python ladsim\ladgen_cli.py ladsim\spec_motor.json -o out.xml --base cur.xml --emit-tags

# 3) 导入 + 编译
TiaEngine.exe --mode import_lad_xml --xml out.xml --plc PLC_1
TiaEngine.exe --mode compile_plc --plc PLC_1

# 4) 解读 + 自动查重复驱动
python ladsim\analyze_ladder.py out.xml
```

规格 JSON 格式：
```json
{
  "tags": { "启动按钮": "%I0.0", "接触器": "%Q0.0" },
  "keep_first_segment": true,
  "networks": [
    { "title": "电机主回路", "rungs": [
        [ { "k": "nc", "tag": "停止按钮" },
          { "k": "nc", "tag": "热保护" },
          { "k": "par", "branches": [
              { "k": "no", "tag": "启动按钮" },
              { "k": "no", "tag": "接触器" } ] },
          { "k": "coil", "tag": "接触器" } ] ] }
  ]
}
```

元件类型：`no` 常开触点 / `nc` 常闭触点 / `coil` 线圈 / `scoil` S线圈 /
`rcoil` R线圈 / `not` 取反 / `par` 并联组。

**完整格式规格见 [ladsim/LAD-SPEC.md](ladsim/LAD-SPEC.md)** —— 里面记录了 V21 SimaticML 的
全部权威依据（来自博途内嵌 XSD、TiaUtilities 源码、IL 反编译、以及博途报错反推），
包含所有踩过的坑，避免重复摸索。

## 编译

```powershell
$dotnet = 'dotnet'

# net48 worker（老式 csproj 走 legacy 路径，不需要 .NET 4.8 Dev Pack）
& $dotnet msbuild C:\path\to\PLCMCP\engine\TiaEngine.csproj /t:Build /p:Configuration=Debug `
    /p:FrameworkPathOverride='C:\Windows\Microsoft.NET\Framework64\v4.0.30319' /v:m /nologo

# .NET 8 MCP 宿主
& $dotnet build C:\path\to\PLCMCP\mcp-server\McpHost.csproj -c Debug -v m /nologo
```

`FrameworkPathOverride` 是关键：让 MSBuild 直接吃 .NET Framework 运行时目录的程序集，绕开缺失的 Dev Pack 引用程序集。

## JSON 信封协议

所有输出一律单个 JSON 信封，stdout 干净，日志走 stderr。

成功：
```json
{"ok":true,"mode":"browse_tree","dryRun":false,"result":{...},"error":null}
```
失败：
```json
{"ok":false,"mode":"browse_tree","dryRun":false,"result":null,
 "error":{"code":"NO_TIA_INSTANCE","message":"未找到运行中的 TIA Portal V21 实例，请先打开博途并加载项目"}}
```

`dryRun` 字段**始终存在**，调用方据此判断是预演还是真执行。

## MCP 工具（15 个）

| 工具 | 权限 | 说明 |
|------|------|------|
| `tia_list_projects` | `[READ-ONLY]` | 列出运行中的博途实例与已打开项目。**无需先连接**，博途关闭时返回空列表 |
| `tia_browse_tree` | `[READ-ONLY]` | 浏览 PLC 程序块树 + 变量表 + UDT，返回已占用块号与 I/O 地址 |
| `tia_import_scl` | `[WRITE]` | 从 SCL 文件导入 FB/FC/OB/DB。`dry_run` 默认 `true` |
| `tia_import_lad_xml` | `[WRITE]` | 从 SimaticML XML 导入 LAD/FBD 图形块。`dry_run` 默认 `true` |
| `tia_compile_plc` | `[WRITE]` | 编译 PLC 并读回完整诊断（Path / Severity / 完整描述，不截断） |
| `tia_plcsim_run` | `[WRITE]` | PLCSIM 控制与变量读写 |
| `tia_save_project` | `[WRITE]` | 显式保存当前项目 |
| `tia_ensure_tags` | `[WRITE]` | 批量创建 PLC 变量，默认 dry_run |
| `tia_export_block` | `[READ-ONLY]` | 把程序块导出为 SimaticML XML |
| `tia_ensure_watch_table` | `[WRITE]` | 创建/打开监控表，便于在线监视 |
| `tia_list_devices` | `[READ-ONLY]` | 列出当前项目的设备 |
| `tia_create_project` | `[WRITE]` | 新建项目 |
| `tia_open_project` | `[WRITE]` | 打开项目 |
| `tia_close_project` | `[WRITE]` | 关闭项目 |
| `tia_add_device` | `[WRITE]` | 添加 PLC/HMI 设备 |

每个工具的 description 都以 `[READ-ONLY]` 或 `[WRITE]` 开头，并映射到 MCP 标准注解 `readOnlyHint`，方便在客户端配不同权限。

## 安全红线（已在代码中固化）

| 红线 | 实现方式 |
|------|---------|
| 保存必须显式 | 只有 `tia_save_project` 会保存；其他工具不会隐式保存 |
| 删除只在 CLI | `delete_blocks` / `delete_tags` 保留在 TiaEngine 命令行，刻意不暴露为 MCP 工具 |
| 不改 OB1 原有逻辑 | 本期只导入新 FB；OB1 追加需用户先看方案并确认 |
| 不改硬件组态 | 代码中**不存在**硬件组态/网络配置 API 调用 |
| 写入默认 dry_run | 工具签名 `bool? dry_run`，未传时默认 `true`；真正执行必须显式传 `dry_run:false` |
| 编译失败不猜测改动 | 必须依据编译器返回的原始诊断（Path + 完整描述）修改 |
| 失败最多重试 5 次 | 由调用方纪律保证，工具本身不做无限重试 |

## 前置条件

1. **TIA Portal V21 已启动**，且**已加载目标项目**（worker 采用 Attach 模式，不自行启动博途）
2. 当前用户属于 `Siemens TIA Openness` 组（已验证：`legion` 是成员）
3. TIA 安装在 `D:\Program Files\Siemens\Automation\Portal V21`

## MCP 客户端配置

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "E:\\PLCMCP\\mcp-server\\bin\\Debug\\net8.0\\TiaMcpServer.exe",
      "args": []
    }
  }
}
```

## 发布给别人（不需要 .NET SDK）

已经生成绿色发布包：

```
C:\path\to\PLCMCP\dist\TiaMcpServer-win-x64.zip
```

解压后直接运行 `TiaMcpServer.exe`（self-contained .NET 8，不用装 SDK），但对方仍然需要：

1. Windows 10/11 x64
2. **TIA Portal V21 + Openness**，且当前用户属于 `Siemens TIA Openness` 组
3. TIA 装在默认 `D:\Program Files\Siemens\Automation\Portal V21`；装在别处要设环境变量：
   ```powershell
   setx TIA_PORTAL_ROOT "C:\Program Files\Siemens\Automation\Portal V21"
   ```
4. 编辑发布包里的 `tia-scope.txt`，写入允许操作的项目目录

首次运行可执行 `tools\Add-OpennessAllowList.ps1`（管理员）把 `TiaEngine.exe` 加入 TIA Openness 白名单，避免反复点“允许”。

发布包结构：

```
TiaMcpServer-win-x64\
├── TiaMcpServer.exe
├── *.dll / *.json          ← self-contained 运行时，缺一不可
├── engine\TiaEngine.exe    ← net48 worker
├── tia-scope.txt
└── README-发布版.md
```

## 自测

```powershell
# 协议层：握手 + 工具列表
C:\path\to\PLCMCP\mcp-server\mcp-probe.ps1

# 端到端：通过 MCP 协议真实调用
C:\path\to\PLCMCP\mcp-server\mcp-probe.ps1 -ToolName tia_list_projects -ArgsJson '{}'

# 直接调 worker
C:\path\to\PLCMCP\engine\bin\Debug\TiaEngine.exe --mode list_projects
C:\path\to\PLCMCP\engine\bin\Debug\TiaEngine.exe --mode browse_tree
```

## PLCSIM 说明

`S7PLCSIMV21.exe`（PLCSIM V21 标准版）是 Electron 桌面应用，**没有公开的进程外 API**。因此：

- `start` / `stop`：返回当前 PLCSIM 运行状态 + 人工操作指引
- `read` / `write`：返回 `PLCSIM_NOT_SUPPORTED`，并给出替代方案

若需要真正的程序化变量读写，可行路径是 **PLCSIM Advanced**（提供 REST API + S7 协议 + 虚拟 IP），当前机器的 `PLCSIMADV` 目录仅有适配器配置组件，未见完整安装。

下载到 PLCSIM 可在博途界面手动完成（"下载到设备" → 选择 PLCSIM）。