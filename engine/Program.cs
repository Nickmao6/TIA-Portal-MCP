using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace TiaEngine
{
    /// <summary>
    /// TiaEngine — net48 Openness worker.
    /// Protocol: invoked with --mode &lt;mode&gt; plus mode-specific args; prints exactly ONE
    /// JSON envelope to stdout; all diagnostics go to stderr. stdout must never be polluted.
    ///
    /// Envelope (success): {"ok":true,"mode":"...","dryRun":bool,"result":{...},"error":null}
    /// Envelope (failure): {"ok":false,"mode":"...","result":null,"error":{"code":"...","message":"..."}}
    /// </summary>
    public static class Program
    {
        // TIA 安装路径可配置：默认 D 盘，换机器时设置环境变量 TIA_PORTAL_ROOT 即可。
        private static readonly string TiaPortalRoot =
            Environment.GetEnvironmentVariable("TIA_PORTAL_ROOT")
            ?? @"D:\Program Files\Siemens\Automation\Portal V21";

        private static readonly string TiaApiDir =
            Path.Combine(TiaPortalRoot, "PublicAPI", "V21", "net48");

        // 有些必需的 Siemens 程序集不在 PublicAPI\net48 里, 而在 Bin\PublicAPI 和
        // Bin\PublicAPI\Client。之前只搜 net48, 于是每次运行都刷出
        //   "resolve miss: Siemens.Engineering.Contract" 等 4 行
        // 它们只是靠 CLR 的默认探测侥幸解析成功。真失败时会被这些噪音掩盖。
        private static readonly string[] TiaSearchDirs = BuildSearchDirs();

        private static string[] BuildSearchDirs()
        {
            var root = TiaPortalRoot;
            return new[]
            {
                TiaApiDir,
                Path.Combine(root, "Bin", "PublicAPI"),
                Path.Combine(root, "Bin", "PublicAPI", "Client"),
                Path.Combine(root, "Bin"),
            };
        }

        private static int Main(string[] args)
        {
            // 1. stdout must carry ONLY the final envelope: buffer everything else.
            var stdoutMirror = new StringWriter();
            Console.SetOut(stdoutMirror);

            // 2. Register the AssemblyResolve hook BEFORE touching any Siemens type.
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            string mode = ReadSwitch(args, "--mode") ?? "help";
            Console.Error.WriteLine("[engine] mode=" + mode);
            // 注意: 只认 "--dry-run" 这个单独的 token。
            // 之前如果写成 "--dry-run false", HasFlag 仍会返回 true(只看 token 存在),
            // 语义完全反了 —— 用户以为在真写, 实际还在预演。这里显式把这种写法当错误。
            bool dryRun = HasFlag(args, "--dry-run") || HasFlag(args, "--dryrun");
            if (dryRun && HasFlagValue(args, "--dry-run", "false"))
            {
                Console.Error.WriteLine("[engine] 警告: --dry-run 后面跟了 false, 但本工具只认 token 存在与否; " +
                                        "要真正执行请**去掉** --dry-run, 不要写 '--dry-run false'。");
            }

            try
            {
                string payload;

                // list_projects enumerates TIA processes itself; it must work even when
                // no portal is running (that is exactly the state it reports on).
                if (mode == "list_projects")
                {
                    payload = TiaSession.ListProjectsDetached();
                    return Emit(mode, dryRun: false, result: payload, error: null);
                }

                // self_test 只测纯逻辑(不碰 Openness), 可以脱离博途跑。
                if (mode == "self_test")
                {
                    bool allOk = SelfTest.Run(out string summary);
                    return Emit(mode, dryRun: false, result: summary,
                        error: allOk ? (KeyValuePair<string, string>?)null
                                     : Err("SELF_TEST_FAILED", "自测失败(" + (summary) + "), 详见 result.failures"));
                }

                // 项目生命周期：这些模式不需要已经打开项目。
                if (mode == "create_project")
                {
                    payload = TiaSession.CreateProject(
                        RequireSwitch(args, "--dir", mode),
                        RequireSwitch(args, "--name", mode));
                    return Emit(mode, dryRun: false, result: payload, error: null);
                }
                if (mode == "open_project")
                {
                    payload = TiaSession.OpenProject(RequireSwitch(args, "--path", mode));
                    return Emit(mode, dryRun: false, result: payload, error: null);
                }
                if (mode == "close_project")
                {
                    payload = TiaSession.CloseProject(RequireSwitch(args, "--path", mode));
                    return Emit(mode, dryRun: false, result: payload, error: null);
                }

                using (var session = TiaSession.Connect(ReadSwitch(args, "--project")))
                {
                    switch (mode)
                    {
                        case "browse_tree":
                            payload = session.BrowseTree(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "list_devices":
                            payload = session.ListDevices();
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "add_device":
                            payload = session.AddDevice(
                                RequireSwitch(args, "--type", mode),
                                RequireSwitch(args, "--name", mode),
                                ReadSwitch(args, "--device-name") ?? ReadSwitch(args, "--name"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "import_scl":
                        {
                            string sclPath = RequireSwitch(args, "--scl", mode);
                            bool overwrite = HasFlag(args, "--overwrite");
                            var results = session.ImportScl(sclPath, ReadSwitch(args, "--plc"), dryRun, overwrite);
                            return Emit(mode, dryRun: dryRun, result: ImportResultsJson(results), error: null);
                        }

                        case "import_lad_xml":
                        {
                            string xmlPath = RequireSwitch(args, "--xml", mode);
                            // overwrite 默认 false: 不覆盖同名块, 避免静默重写已有程序。
                            bool ow = HasFlag(args, "--overwrite");
                            var results = session.ImportLadXml(xmlPath, ReadSwitch(args, "--plc"), dryRun, ow);
                            return Emit(mode, dryRun: dryRun, result: ImportResultsJson(results), error: null);
                        }

                        case "ensure_idb":
                            // Creates the instance DB that a CALL must bind to.
                            payload = session.EnsureInstanceDb(
                                RequireSwitch(args, "--db", mode),
                                RequireSwitch(args, "--fb", mode),
                                ReadSwitch(args, "--plc"), dryRun);
                            return Emit(mode, dryRun: dryRun, result: payload, error: null);

                        case "ensure_watch_table":
                            // Creates/opens a watch table for PLCSIM verification.
                            payload = session.EnsureWatchTable(
                                ReadSwitch(args, "--table") ?? "监控表",
                                RequireSwitch(args, "--tags", mode),
                                ReadSwitch(args, "--plc"),
                                !HasFlag(args, "--no-open"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_download":
                            payload = session.ProbeDownloadApi(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_device_attrs":
                            // 探测设备(CPU)硬件组态属性, 用于学习"时钟存储器字节"等配置。
                            payload = session.ProbeDeviceAttributes(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_clock_memory":
                            // 逐个读时钟存储器候选属性名, 确认真实属性名。
                            payload = session.ProbeClockMemory(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hmi":
                            // 探测 HMI(WinCC)设备的 Software 成员。
                            payload = session.ProbeHmi(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hmi_detail":
                            // 深入探测 HMI 的画面/变量/连接文件夹结构。
                            payload = session.ProbeHmiDetail(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hmi_screens":
                            // 列出 HMI 实际画面名 / 变量名 / 连接数。
                            payload = session.ProbeHmiScreens(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hmi_screen_detail":
                            // 探测单个画面对象的方法(找 Export/Import)。
                            payload = session.ProbeHmiScreenDetail(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "export_hmi_screen":
                            // 导出 HMI 画面 XML(学习画面结构)。
                            payload = session.ExportHmiScreen(RequireSwitch(args, "--screen", mode), RequireSwitch(args, "--out", mode), ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "hmi_screen_attrs":
                            // 列出 HMI 画面属性。
                            payload = session.HmiScreenAttrs(RequireSwitch(args, "--screen", mode), ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hmi_import":
                            // 探测 HMI 画面的 Import/Export 能力。
                            payload = session.ProbeHmiImport(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "import_hmi_screen":
                            // 导入 HMI 画面 XML。
                            payload = session.ImportHmiScreen(RequireSwitch(args, "--xml", mode), ReadSwitch(args, "--plc"),
                                HasFlag(args, "--overwrite"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hmi_graphics":
                            // 探测 HMI 图形资源库。
                            payload = session.ProbeHmiGraphics(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "export_hmi_graphiclist":
                            // 导出 HMI 图形列表(已禁用: 曾导致博途崩溃)。
                            payload = session.ExportHmiGraphicList(RequireSwitch(args, "--list", mode), RequireSwitch(args, "--out", mode), ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "save_project":
                            // 保存工程到磁盘。
                            payload = session.SaveProject(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_graphics_tree":
                            // 递归导出图形资源树。
                            payload = session.ProbeGraphicsTree(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "set_hmi_screen_attr":
                            // 设 HMI 画面属性(颜色用 "r,g,b")。
                            payload = session.SetHmiScreenAttr(RequireSwitch(args, "--screen", mode),
                                RequireSwitch(args, "--attr", mode), RequireSwitch(args, "--value", mode), ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_block_attrs":
                            // 读块的全部属性(诊断导入后属性是否生效)。
                            payload = session.ProbeBlockAttrs(RequireSwitch(args, "--block", mode), ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_tech_objects":
                            // 探测工艺对象(PID_Compact 等)的容器与参数。
                            payload = session.ProbeTechnologyObjects(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "get_to_params":
                            // 读工艺对象的全部参数值。
                            payload = session.GetTechParams(ReadSwitch(args, "--plc"), RequireSwitch(args, "--to", mode));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "set_to_param":
                            // 改工艺对象的单个参数(记录原值 + 读回校验)。
                            payload = session.SetTechParam(ReadSwitch(args, "--plc"), RequireSwitch(args, "--to", mode),
                                RequireSwitch(args, "--param", mode), RequireSwitch(args, "--value", mode),
                                HasFlag(args, "--dry-run"));
                            return Emit(mode, dryRun: HasFlag(args, "--dry-run"), result: payload, error: null);

                        case "probe_to_create":
                            // 探测工艺对象创建 API + 控制器类型枚举。
                            payload = session.ProbeTechObjectCreate(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "create_to":
                            // 创建工艺对象(如 PID_Compact)。
                            payload = session.CreateTechObject(ReadSwitch(args, "--plc"), RequireSwitch(args, "--name", mode),
                                ReadSwitch(args, "--element") ?? "PID_Compact", ReadSwitch(args, "--version") ?? "2.3");
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "set_to_params":
                            // 批量改工艺对象参数。"--params a=1;b=2"
                            payload = session.SetTechParams(ReadSwitch(args, "--plc"), RequireSwitch(args, "--to", mode),
                                RequireSwitch(args, "--params", mode), HasFlag(args, "--dry-run"));
                            return Emit(mode, dryRun: HasFlag(args, "--dry-run"), result: payload, error: null);

                        case "delete_to":
                            // 删除工艺对象(可回滚)。
                            payload = session.DeleteTechObject(ReadSwitch(args, "--plc"), RequireSwitch(args, "--to", mode));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hw":
                            // 探测扩展模块插槽 + CPU 启动模式属性。
                            payload = session.ProbeHardware(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_startup_api":
                            // 搜 Siemens 程序集里的 Startup 相关类型 + Device 的 Export/Import。
                            payload = session.ProbeStartupApi(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_hidden_attrs":
                            // 用程序集类型名挖隐藏属性(启动模式/时钟存储器等)。
                            payload = session.ProbeHiddenAttrs(ReadSwitch(args, "--plc"), ReadSwitch(args, "--keywords"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_type_usage":
                            // 反查: 哪些属性用了名字含关键字的类型。
                            payload = session.ProbeTypeUsage(ReadSwitch(args, "--keywords"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_can_plug":
                            // 用 CanPlugNew 试模块写法与插槽(不真插)。
                            payload = session.ProbeCanPlug(ReadSwitch(args, "--plc"), RequireSwitch(args, "--order", mode));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "plug_module":
                            // 往机架插一个模块。
                            payload = session.PlugModule(ReadSwitch(args, "--plc"),
                                RequireSwitch(args, "--type", mode), RequireSwitch(args, "--name", mode),
                                int.Parse(ReadSwitch(args, "--position") ?? "2"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "probe_item_tree":
                            // 递归遍历 DeviceItem 子树, 找通道级参数。
                            payload = session.ProbeItemTree(ReadSwitch(args, "--plc"), RequireSwitch(args, "--item", mode));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "unplug_module":
                            // 拔掉一个模块。
                            payload = session.UnplugModule(ReadSwitch(args, "--plc"), RequireSwitch(args, "--name", mode));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "set_device_attr":
                            // 读写设备/模块的通用属性。
                            payload = session.SetDeviceAttr(ReadSwitch(args, "--plc"), RequireSwitch(args, "--item", mode),
                                RequireSwitch(args, "--attr", mode), ReadSwitch(args, "--value") ?? "",
                                HasFlag(args, "--dry-run"));
                            return Emit(mode, dryRun: HasFlag(args, "--dry-run"), result: payload, error: null);

                        case "delete_blocks":
                            // Removes program blocks (OB/FB/FC/DB) by name.
                            // 刻意不做成 MCP 工具: 删除程序块风险高, 只允许显式的命令行调用。
                            payload = session.DeleteBlocks(
                                RequireSwitch(args, "--blocks", mode),
                                ReadSwitch(args, "--plc"), dryRun);
                            return Emit(mode, dryRun: dryRun, result: payload, error: null);

                        case "delete_tags":
                            // Removes PLC tags by name (repair path for mis-created tags).
                            payload = session.DeleteTags(
                                RequireSwitch(args, "--tags", mode),
                                ReadSwitch(args, "--plc"), dryRun);
                            return Emit(mode, dryRun: dryRun, result: payload, error: null);

                        case "ensure_tags":
                            // Creates the symbolic PLC tags that generated LAD networks reference.
                            // Internal mode (not an MCP tool); part of the LAD generation workflow.
                            payload = session.EnsureTags(
                                RequireSwitch(args, "--tags", mode),
                                ReadSwitch(args, "--plc"), dryRun);
                            return Emit(mode, dryRun: dryRun, result: payload, error: null);

                        case "compile_plc":
                            payload = session.CompilePlc(ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "export_block":
                            // read-only helper (not an MCP tool): dump a block as SimaticML XML
                            // so the LAD structure can be learned from a real block.
                            payload = session.ExportBlock(
                                RequireSwitch(args, "--block", mode),
                                RequireSwitch(args, "--out", mode),
                                ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        case "plcsim":
                            payload = session.PlcSim(
                                RequireSwitch(args, "--action", mode),
                                ReadSwitch(args, "--tag"),
                                ReadSwitch(args, "--value"),
                                ReadSwitch(args, "--plc"));
                            return Emit(mode, dryRun: false, result: payload, error: null);

                        default:
                            return Emit(mode, dryRun: false, result: null,
                                error: Err("UNKNOWN_MODE", "未知模式: " + mode +
                                    "（可用: list_projects, create_project, open_project, close_project, list_devices, add_device," +
                                    " browse_tree, import_scl, import_lad_xml, compile_plc, plcsim," +
                                    " export_block, ensure_tags, delete_tags, ensure_idb, delete_blocks," +
                                    " ensure_watch_table, probe_download, probe_blocks）"));
                    }
                }
            }
            catch (TiaException tex)
            {
                return Emit(mode, dryRun: dryRun, result: null, error: Err(tex.Code, tex.Message));
            }
            catch (Exception ex)
            {
                return Emit(mode, dryRun: dryRun, result: null,
                    error: Err("INTERNAL", ex.GetType().Name + ": " + ex.Message));
            }
        }

        // ---------- envelope ----------

        private static int Emit(string mode, bool dryRun, string result, KeyValuePair<string, string>? error)
        {
            // Restore the real stdout and emit exactly one line.
            var realStdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(realStdout, new UTF8Encoding(false)) { AutoFlush = true };

            string body;
            if (error.HasValue)
            {
                body = Json.Obj(new Dictionary<string, string>
                {
                    { "ok", Json.Bool(false) },
                    { "mode", Json.Str(mode) },
                    { "dryRun", Json.Bool(dryRun) },
                    { "result", Json.Null },
                    { "error", Json.Obj(new Dictionary<string, string>
                        {
                            { "code", Json.Str(error.Value.Key) },
                            { "message", Json.Str(error.Value.Value) }
                        })
                    }
                });
            }
            else
            {
                body = Json.Obj(new Dictionary<string, string>
                {
                    { "ok", Json.Bool(true) },
                    { "mode", Json.Str(mode) },
                    { "dryRun", Json.Bool(dryRun) },
                    { "result", result == null ? Json.Null : result },
                    { "error", Json.Null }
                });
            }
            writer.WriteLine(body);
            writer.Flush();
            return error.HasValue ? 1 : 0;
        }

        private static KeyValuePair<string, string> Err(string code, string message)
            => new KeyValuePair<string, string>(code, message);

        private static string ImportResultsJson(List<ImportResult> results)
        {
            var list = new List<string>();
            var created = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();
            foreach (var r in results)
            {
                list.Add(Json.Obj(new Dictionary<string, string>
                {
                    { "name", Json.Str(r.BlockName) },
                    { "status", Json.Str(r.Status) },
                    { "reason", Json.Str(r.Reason ?? "") }
                }));
                if (r.Status == "created") created.Add(Json.Str(r.BlockName));
                else if (r.Status == "skipped") skipped.Add(Json.Str(r.BlockName));
                else failed.Add(Json.Str(r.BlockName));
            }
            return Json.Obj(new Dictionary<string, string>
            {
                { "created", Json.Arr(created) },
                { "skipped", Json.Arr(skipped) },
                { "failed", Json.Arr(failed) },
                { "details", Json.Arr(list) }
            });
        }

        // ---------- args ----------

        internal static string RequireSwitch(string[] args, string name, string mode)
        {
            var v = ReadSwitch(args, name);
            if (string.IsNullOrEmpty(v))
                throw new TiaException("MISSING_ARG", "缺少参数 " + name + "（模式 " + mode + "）");
            return v;
        }

        internal static string ReadSwitch(string[] args, string name)
        {
            // 形式1: --name=value (单参数)
            var prefix = name + "=";
            foreach (var a in args)
            {
                if (a != null && a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return a.Substring(prefix.Length);
            }
            // 形式2: --name value (两个参数)
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        internal static bool HasFlag(string[] args, string name)
        {
            // 支持 --flag、--flag=true、--flag=false 三种写法;
            // --flag=false 返回 false(用户明确要关), 其余返回 true。
            var prefix = name + "=";
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == null) continue;
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var v = a.Substring(prefix.Length);
                    if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || v == "0")
                        return false;
                    return true;
                }
            }
            return false;
        }

        /// <summary>判断某开关后面是否紧跟着指定值（用于识别 "--dry-run false" 这种有害写法）。</summary>
        internal static bool HasFlagValue(string[] args, string name, string value)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(args[i + 1], value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ---------- assembly resolver ----------

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name);
            if (name.Name == null ||
                !name.Name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = Path.Combine(TiaApiDir, name.Name + ".dll");
            if (!File.Exists(path))
            {
                // 多目录查找: PublicAPI\net48 之外还有 Bin\PublicAPI / Bin\PublicAPI\Client
                string found = null;
                foreach (var dir in TiaSearchDirs)
                {
                    try
                    {
                        var cand = Path.Combine(dir, name.Name + ".dll");
                        if (File.Exists(cand)) { found = cand; break; }
                    }
                    catch { }
                }
                if (found == null)
                {
                    Console.Error.WriteLine("[engine] resolve miss: " + name.Name);
                    return null;
                }
                path = found;
            }
            var asm = Assembly.LoadFrom(path);
            if (!string.Equals(args.Name, asm.FullName, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("[engine] resolve version mismatch for " + name.Name);
            }
            return asm;
        }
    }
}