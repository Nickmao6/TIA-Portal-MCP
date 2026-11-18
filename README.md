# TIA Portal MCP Server

**PLCMCP** — an open-source MCP server that lets AI agents drive Siemens TIA Portal V21 through the Openness API.

> [中文说明](README.zh-CN.md)

## What it does

PLCMCP exposes TIA Portal engineering operations as standard MCP tools, so an AI client can:

- create and open projects
- add PLC and HMI devices
- browse program blocks and tags
- create PLC tags in batches
- import SCL and LAD (SimaticML) blocks
- compile a PLC and read full diagnostics
- export blocks as SimaticML XML
- save projects explicitly
- create watch tables for online monitoring
- query PLCSIM status

## Architecture

```text
MCP client (Claude, Codex, DSH, etc.)
        │  stdio / JSON-RPC
        ▼
TiaMcpServer.exe        [.NET 8, owns the MCP connection]
        │  child process + single-line JSON envelope
        ▼
TiaEngine.exe           [.NET Framework 4.8, owns Openness]
        ▼
Siemens.Engineering
        ▼
TIA Portal V21 (already running with a project open)
```

Openness uses .NET Framework Remoting and cannot run inside a .NET 8 process, hence the two-process design. The MCP host never calls Openness directly.

## MCP tools (16)

| Category | Tools |
|---|---|
| Projects | `tia_list_projects`, `tia_create_project`, `tia_open_project`, `tia_close_project` |
| Devices | `tia_list_devices`, `tia_add_device` |
| Inspect | `tia_browse_tree` |
| Tags | `tia_ensure_tags`, `tia_ensure_instance_db` |
| Program | `tia_import_scl`, `tia_import_lad_xml`, `tia_export_block` |
| Compile | `tia_compile_plc` |
| Save | `tia_save_project` |
| Watch | `tia_ensure_watch_table` |
| Simulation | `tia_plcsim_run` |

Every tool description is prefixed with `[READ-ONLY]` or `[WRITE]` so MCP clients can map permissions.

`delete_blocks` and `delete_tags` are intentionally CLI-only in `TiaEngine.exe`; they are **not** exposed to AI clients.

## Quick start

### Requirements

1. Windows 10/11 x64
2. TIA Portal V21 with Openness installed
3. The current Windows user is in the `Siemens TIA Openness` group
4. .NET 8 SDK for building; the release package is self-contained and does not need an SDK
5. TIA Portal is started and the target project is open

### Build

```powershell
dotnet msbuild engine\TiaEngine.csproj /t:Build /p:Configuration=Debug `
  /p:FrameworkPathOverride="C:\Windows\Microsoft.NET\Framework64\v4.0.30319" /v:m /nologo

dotnet build mcp-server\McpHost.csproj -c Debug -v m
```

### Configure

1. Copy `tia-scope.example.txt` to `tia-scope.txt`
2. Add the directories that contain projects the Agent may touch
3. Add the server to your MCP client:

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "C:\\path\\to\\PLCMCP\\mcp-server\\bin\\Debug\\net8.0\\TiaMcpServer.exe",
      "args": []
    }
  }
}
```

5. Start TIA Portal and open a project before using the tools.

## Portable release

`dist\TiaMcpServer-win-x64.zip` contains a self-contained .NET 8 build:

```text
TiaMcpServer-win-x64\
├── TiaMcpServer.exe
├── *.dll / *.json
├── engine\TiaEngine.exe
├── tools\Add-OpennessAllowList.ps1
├── tia-scope.txt
└── README-发布版.md
```

The target machine does **not** need WorkBuddy or the .NET SDK. It still needs TIA Portal V21 + Openness.

If TIA is not installed at `D:\Program Files\Siemens\Automation\Portal V21`, set:

```powershell
setx TIA_PORTAL_ROOT "C:\Program Files\Siemens\Automation\Portal V21"
```

## Openness permission prompt

TIA asks for confirmation the first time an executable accesses Openness. “Always allow” stores the executable path and file hash under:

```text
HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList\<exe>\Entry
```

The prompt returns only when `TiaEngine.exe` is rebuilt/replaced (hash changes) or moved. The release package includes:

```text
tools\Add-OpennessAllowList.ps1
```

Run it as administrator once per machine/build to add the worker to the allowlist.

## Safety

- `tia-scope.txt` limits which project directories can be touched
- Write tools default to `dry_run = true`
- `tia_save_project` is the only tool that saves; no implicit saves
- Delete operations stay in the CLI, not in the MCP surface
- Compilation diagnostics are returned in full; do not guess fixes

## Known limitations

- **HMI**: Openness can import/export some screen XML, but Comfort panels do not expose symbolic tag links or data logging/trend objects. Those must be created in the TIA UI.
- **PLCSIM**: the standard PLCSIM V21 app has no documented out-of-process variable read/write API. `tia_plcsim_run` returns `PLCSIM_NOT_SUPPORTED` for `read`/`write`; PLCSIM Advanced is required for programmatic I/O.
- **Advanced LAD**: `ladgen` supports contacts, coils, timers, counters, comparisons, math and branches. PID/motion/technology objects must be authored with TIA technology objects or SCL.
- **Single project**: TIA Portal allows only one project open at a time; close the current project before creating/opening another.

## Documentation

- [Agent guide](docs/MCP-AGENT-GUIDE.md) — call order, tool contracts, error codes
- [Agent pitfalls](docs/AGENT-PITFALLS.md) — real TIA/Openness traps
- [Motion control notes](docs/MOTION-CONTROL-NOTES.md) — tested S7-1200 motion instructions and LAD wiring
- [Chinese usage guide](docs/使用说明.md)
- [LAD SimaticML spec](ladsim/LAD-SPEC.md)
- [Portfolio project description](docs/PLCMCP-项目介绍-作品集.md)

## License

MIT
