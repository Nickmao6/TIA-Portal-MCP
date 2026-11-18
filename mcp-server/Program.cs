using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcpServer
{
    /// <summary>
    /// TiaMcpServer — .NET 8 MCP stdio host (the "main agent" interface).
    ///
    /// Architecture (hard constraint from the user):
    ///   MCP client → this .NET 8 host (stdio) → spawn net48 worker TiaEngine.exe → Openness → TIA Portal
    ///
    /// Openness uses .NET Framework Remoting APIs that cannot run inside a .NET 8 process, so we
    /// keep a child net48 worker (engine/TiaEngine.exe) and exchange newline-delimited JSON over
    /// its stdin/stdout. This host holds the MCP connection and serializes every call; it never
    /// calls Openness itself.
    ///
    /// Safety rules baked into the tools:
    ///   - Destructive delete modes stay CLI-only; they are intentionally NOT exposed as MCP tools.
    ///   - Write tools that create content default to dry_run = true; real execution requires an explicit flag.
    ///   - Saving is explicit only via tia_save_project; no implicit Save in other tools.
    ///   - No hardware/network configuration change.
    ///   - Every tool description is prefixed [READ-ONLY] or [WRITE] for client permission config.
    /// </summary>
    [McpServerToolType]
    public class TiaTools
    {
        // worker executable path, resolved under the workspace (relative to this assembly/current dir)
        private static readonly string WorkerPath = BuildWorkerPath();

        private static string BuildWorkerPath()
        {
            // 发布/拷贝给别人时，允许用环境变量指定 worker；
            // 否则优先找 exe 同目录下的 engine\TiaEngine.exe（绿色发布包结构）。
            var env = Environment.GetEnvironmentVariable("TIA_ENGINE_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);

            var candidates = new List<string>
            {
                // 发布包结构: TiaMcpServer.exe 同目录\engine\TiaEngine.exe
                Path.Combine(AppContext.BaseDirectory, "engine", "TiaEngine.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "engine", "TiaEngine.exe"),
                // running from workspace root: C:\path\to\PLCMCP\engine\bin\Debug\TiaEngine.exe
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "engine", "bin", "Debug", "TiaEngine.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "engine", "bin", "Debug", "TiaEngine.exe"),
                // fallback for a checkout where the current directory is the repo root
                Path.Combine(Directory.GetCurrentDirectory(), "engine", "bin", "Debug", "TiaEngine.exe"),
            };
            foreach (var c in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(c);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            // fallback (will fail clearly at call time)
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "engine", "bin", "Debug", "TiaEngine.exe"));
        }

        // =====================================================================
        // 1. [READ-ONLY] tia_list_projects
        // =====================================================================
        [McpServerTool(Name = "tia_list_projects", ReadOnly = true, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[READ-ONLY] 列出运行中的 TIA Portal V21 实例及其打开的项目。返回: {ok,mode,dryRun,result:{instances:[{pid,projects:[{name,path}]}]},error}")]
        public async Task<CallToolResult> ListProjects(CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "list_projects");
        }

        // =====================================================================
        // 2. [READ-ONLY] tia_browse_tree
        // =====================================================================
        [McpServerTool(Name = "tia_browse_tree", ReadOnly = true, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[READ-ONLY] 浏览指定 PLC 的程序块树 + 变量表 + 数据类型。plc_name 可选（默认第一个 PLC）。返回 {ok,result:{plc,blocks:[{name,type,number,lang,path}],tags:{inputs,outputs,memory},udts},error}")]
        public async Task<CallToolResult> BrowseTree(
            [Description("PLC 软件名称，可选，默认取第一个 PLC")] string? plc_name,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "browse_tree", "--plc", plc_name ?? "");
        }

        // =====================================================================
        // 3. [WRITE] tia_import_scl
        // =====================================================================
        [McpServerTool(Name = "tia_import_scl", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 从 SCL 文件导入并生成 FB/FC/OB/DB。scl_path 必填；plc_name 可选；dry_run 默认 true（只报告将创建什么，绝不写入）；overwrite 默认 false（已存在块则跳过）。返回 {ok,result:{created,skipped,failed,details},error}")]
        public async Task<CallToolResult> ImportScl(
            [Description("SCL 文件绝对路径，必填")] string scl_path,
            [Description("PLC 软件名称，可选")] string? plc_name,
            [Description("预演模式，默认 true：只报告将要创建什么，绝不写入")] bool? dry_run,
            [Description("覆盖模式，默认 false：已存在的块将跳过")] bool? overwrite,
            CancellationToken ct)
        {
            var args = new List<string> { "--mode", "import_scl", "--scl", scl_path, "--plc", plc_name ?? "" };
            if (dry_run ?? true) args.Add("--dry-run");
            if (overwrite ?? false) args.Add("--overwrite");
            return await RunWorker(ct, args.ToArray());
        }

        // =====================================================================
        // 4. [WRITE] tia_import_lad_xml
        // =====================================================================
        [McpServerTool(Name = "tia_import_lad_xml", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 从 SimaticML XML 导入并生成 LAD/FBD 图形程序块。xml_path 必填；plc_name 可选；dry_run 默认 true；overwrite 默认 false（同名块存在时拒绝，不静默覆盖）。返回 {ok,result:{created,skipped,failed},error}")]
        public async Task<CallToolResult> ImportLadXml(
            [Description("SimaticML XML 文件绝对路径，必填")] string xml_path,
            [Description("PLC 软件名称，可选")] string? plc_name,
            [Description("预演模式，默认 true")] bool? dry_run,
            [Description("覆盖模式，默认 false：工程里已有同名块时拒绝导入，不静默覆盖")] bool? overwrite,
            CancellationToken ct)
        {
            var args = new List<string> { "--mode", "import_lad_xml", "--xml", xml_path, "--plc", plc_name ?? "" };
            if (dry_run ?? true) args.Add("--dry-run");
            if (overwrite ?? false) args.Add("--overwrite");
            return await RunWorker(ct, args.ToArray());
        }

        // =====================================================================
        // 5. [WRITE] tia_compile_plc
        // =====================================================================
        [McpServerTool(Name = "tia_compile_plc", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 编译指定 PLC（或第一个 PLC）并返回完整诊断。plc_name 可选。返回 {ok,result:{state,errorCount,warningCount,errors,warnings,infos},error}。error/warning 每项含 Path 和完整描述")]
        public async Task<CallToolResult> CompilePlc(
            [Description("PLC 软件名称，可选")] string? plc_name,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "compile_plc", "--plc", plc_name ?? "");
        }

        // =====================================================================
        // 6. [WRITE] tia_plcsim_run
        // =====================================================================
        [McpServerTool(Name = "tia_plcsim_run", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] PLCSIM 仿真控制与变量读写。action ∈ {start,stop,write,read}；tag_name、value、plc_name 可选。注意：PLCSIM V21 标准版无进程外读写 API，read/write 会返回 PLCSIM_NOT_SUPPORTED 提示替代方案")]
        public async Task<CallToolResult> PlcSim(
            [Description("action: start|stop|write|read，必填")] string action,
            [Description("变量名/地址，如 %Q0.0")] string? tag_name,
            [Description("写入值，如 true/false/数字")] string? value,
            [Description("PLC 软件名称，可选")] string? plc_name,
            CancellationToken ct)
        {
            var args = new List<string> { "--mode", "plcsim", "--action", action };
            if (!string.IsNullOrEmpty(tag_name)) { args.Add("--tag"); args.Add(tag_name); }
            if (!string.IsNullOrEmpty(value)) { args.Add("--value"); args.Add(value); }
            if (!string.IsNullOrEmpty(plc_name)) { args.Add("--plc"); args.Add(plc_name); }
            return await RunWorker(ct, args.ToArray());
        }

        // =====================================================================
        // 7. [WRITE] tia_save_project
        // =====================================================================
        [McpServerTool(Name = "tia_save_project", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 保存当前打开的项目到磁盘。plc_name 可选（仅用于定位项目）。返回 {ok,result:{project,saved},error}")]
        public async Task<CallToolResult> SaveProject(
            [Description("PLC 软件名称，可选")] string? plc_name,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "save_project", "--plc", plc_name ?? "");
        }

        // =====================================================================
        // 8. [WRITE] tia_ensure_tags
        // =====================================================================
        [McpServerTool(Name = "tia_ensure_tags", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 批量创建 PLC 变量。tags 格式 Name@Address[@Type]，多个用逗号分隔，例如 启动@%M0.0@Bool,计数@%MW10@Int；dry_run 默认 true。返回 {ok,result:{table,created,skipped,repaired,failed},error}")]
        public async Task<CallToolResult> EnsureTags(
            [Description("变量规格，多个用逗号分隔，如 启动@%M0.0@Bool,计数@%MW10@Int")] string tags,
            [Description("PLC 软件名称，可选")] string? plc_name,
            [Description("预演模式，默认 true")] bool? dry_run,
            CancellationToken ct)
        {
            var args = new List<string> { "--mode", "ensure_tags", "--tags", tags, "--plc", plc_name ?? "" };
            if (dry_run ?? true) args.Add("--dry-run");
            return await RunWorker(ct, args.ToArray());
        }

        // =====================================================================
        // 9. [READ-ONLY] tia_export_block
        // =====================================================================
        [McpServerTool(Name = "tia_export_block", ReadOnly = true, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[READ-ONLY] 把指定程序块导出为 SimaticML XML，只读工程、不修改项目。block 和 out_path 必填。返回 {ok,result:{block,type,lang,path,bytes},error}")]
        public async Task<CallToolResult> ExportBlock(
            [Description("程序块名称，如 Main 或 FB_水塔控制")] string block,
            [Description("导出 XML 的绝对路径")] string out_path,
            [Description("PLC 软件名称，可选")] string? plc_name,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "export_block", "--block", block, "--out", out_path, "--plc", plc_name ?? "");
        }

        // =====================================================================
        // 10. [WRITE] tia_ensure_watch_table
        // =====================================================================
        [McpServerTool(Name = "tia_ensure_watch_table", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 创建或打开监控表，便于 PLCSIM/在线监视。table 是表名，tags 是变量名列表（逗号分隔）。返回 {ok,result:{table,tags},error}")]
        public async Task<CallToolResult> EnsureWatchTable(
            [Description("监控表名称，如 监控表")] string table,
            [Description("变量名列表，逗号分隔，如 启动,停止,系统运行")] string tags,
            [Description("PLC 软件名称，可选")] string? plc_name,
            CancellationToken ct)
        {
            var args = new List<string> { "--mode", "ensure_watch_table", "--table", table, "--tags", tags, "--plc", plc_name ?? "" };
            return await RunWorker(ct, args.ToArray());
        }

        // =====================================================================
        // 11. [READ-ONLY] tia_list_devices
        // =====================================================================
        [McpServerTool(Name = "tia_list_devices", ReadOnly = true, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[READ-ONLY] 列出当前项目的设备（PLC/HMI）及 TypeIdentifier。返回 {ok,result:[{name,type}],error}")]
        public async Task<CallToolResult> ListDevices(
            [Description("PLC 软件名称，可选，用于定位项目")] string? plc_name,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "list_devices", "--plc", plc_name ?? "");
        }

        // =====================================================================
        // 12. [WRITE] tia_create_project
        // =====================================================================
        [McpServerTool(Name = "tia_create_project", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 在指定目录新建 TIA 项目，并保存到磁盘。目录必须在 tia-scope.txt 授权范围内。返回 {ok,result:{project,path,saved},error}")]
        public async Task<CallToolResult> CreateProject(
            [Description("项目目录，绝对路径，如 C:\\Users\\legion\\Documents\\Automation\\MCP测试项目")] string project_dir,
            [Description("项目名称，不含 .ap21")] string project_name,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "create_project", "--dir", project_dir, "--name", project_name);
        }

        // =====================================================================
        // 13. [WRITE] tia_open_project
        // =====================================================================
        [McpServerTool(Name = "tia_open_project", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 在已经运行的 TIA Portal 中打开指定 .ap21 项目。路径必须在 tia-scope.txt 授权范围内。返回 {ok,result:{project,path},error}")]
        public async Task<CallToolResult> OpenProject(
            [Description("项目 .ap21 文件绝对路径")] string project_path,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "open_project", "--path", project_path);
        }

        // =====================================================================
        // 14. [WRITE] tia_close_project
        // =====================================================================
        [McpServerTool(Name = "tia_close_project", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 关闭指定的已打开项目（不会自动保存；需要保存请先调用 tia_save_project）。返回 {ok,result:{project,closed},error}")]
        public async Task<CallToolResult> CloseProject(
            [Description("项目 .ap21 文件绝对路径")] string project_path,
            CancellationToken ct)
        {
            return await RunWorker(ct, "--mode", "close_project", "--path", project_path);
        }

        // =====================================================================
        // 15. [WRITE] tia_add_device
        // =====================================================================
        [McpServerTool(Name = "tia_add_device", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 在当前项目中添加设备（CPU/HMI）。type_identifier 如 OrderNumber:6ES7214-1AG40-0XB0/V4.7。返回 {ok,result:{name,type,created},error}")]
        public async Task<CallToolResult> AddDevice(
            [Description("订货号 TypeIdentifier，如 OrderNumber:6ES7214-1AG40-0XB0/V4.7")] string type_identifier,
            [Description("设备名称，如 PLC_2")] string device_name,
            [Description("设备项名称，可选，默认同 device_name")] string? device_item_name,
            CancellationToken ct)
        {
            var args = new List<string> { "--mode", "add_device", "--type", type_identifier, "--name", device_name };
            if (!string.IsNullOrWhiteSpace(device_item_name)) { args.Add("--device-name"); args.Add(device_item_name); }
            return await RunWorker(ct, args.ToArray());
        }

        // =====================================================================
        // 16. [WRITE] tia_ensure_instance_db
        // =====================================================================
        [McpServerTool(Name = "tia_ensure_instance_db", ReadOnly = false, Destructive = false, OpenWorld = false,
            UseStructuredContent = false)]
        [Description("[WRITE] 为已存在的 FB（含 MC_* 系统工艺块）创建实例 DB。工艺块先通过 tia_import_lad_xml 导入后，TIA 才会生成对应系统 FB；然后调用本工具创建 <名称>_DB。返回 {ok,result:{status,detail},error}")]
        public async Task<CallToolResult> EnsureInstanceDb(
            [Description("FB 名称，如 MC_Reset、MC_Halt、MC_MoveRelative")] string fb_name,
            [Description("要创建的实例 DB 名称，如 MC_Reset_DB")] string db_name,
            [Description("PLC 软件名称，可选")] string? plc_name,
            [Description("只预演，默认 true")] bool? dry_run,
            CancellationToken ct)
        {
            var args = new List<string>
            {
                "--mode", "ensure_idb",
                "--fb", fb_name,
                "--db", db_name,
                "--plc", plc_name ?? ""
            };
            if (dry_run ?? true) args.Add("--dry-run");
            return await RunWorker(ct, args.ToArray());
        }

        // =====================================================================
        // Worker invocation: spawn TiaEngine.exe, pass args, return its JSON envelope verbatim.
        // =====================================================================
        // 串行化锁: 每次工具调用都会起一个 net48 worker, 而 TIA Openness 对同一工程的
// 并发写操作没有保护。之前注释声称 "serializes every call" 但实际没有任何同步,
// 两个并发写调用会互相踩踏。这里用信号量把 worker 调用真正串起来。
        private static readonly SemaphoreSlim WorkerGate = new SemaphoreSlim(1, 1);

        private static async Task<CallToolResult> RunWorker(CancellationToken ct, params string[] args)
        {
            // 注意: 排队等待也要能被取消, 避免客户端断开后一直挂着。
            try
            {
                await WorkerGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return TextResult(BadEnvelope("WORKER_CANCELLED", "等待前一个 worker 完成时被取消。"));
            }
            try
            {
                return await RunWorkerCore(ct, args).ConfigureAwait(false);
            }
            finally
            {
                WorkerGate.Release();
            }
        }

        private static async Task<CallToolResult> RunWorkerCore(CancellationToken ct, params string[] args)
        {
            if (!File.Exists(WorkerPath))
            {
                return TextResult(BadEnvelope("WORKER_NOT_FOUND",
                    "找不到 net48 worker: " + WorkerPath + "。请先在工作区编译 engine (TiaEngine.csproj)。"));
            }

            var psi = new ProcessStartInfo
            {
                FileName = WorkerPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                return TextResult(BadEnvelope("WORKER_START_FAILED", "启动 worker 失败: " + ex.Message));
            }

            var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(10)); // compile can be slow
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                return TextResult(BadEnvelope("WORKER_TIMEOUT", "worker 运行超时 (>10 分钟)，已终止。"));
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(stdout))
            {
                return TextResult(BadEnvelope("WORKER_NO_OUTPUT",
                    "worker 没有输出。退出码=" + proc.ExitCode + "，stderr=" + Trim(stderr)));
            }

            // The envelope is the JSON on stdout. Forward it as the tool's text content.
            return TextResult(stdout.Trim());
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(空)";
            s = s.Trim();
            return s.Length <= 4000 ? s : s.Substring(0, 4000) + "...(截断)";
        }

        private static string BadEnvelope(string code, string message)
        {
            return "{\"ok\":false,\"mode\":\"?\",\"dryRun\":false,\"result\":null,\"error\":{"
                + "\"code\":\"" + Esc(code) + "\",\"message\":\"" + Esc(message) + "\"}}";
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static CallToolResult TextResult(string text)
        {
            return new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
            };
        }
    }

    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // stdio transport; register our single tool type.
            // Logs MUST go to stderr (LogToStandardErrorThreshold) so stdout carries only JSON-RPC.
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<TiaTools>();

            using var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
    }
}