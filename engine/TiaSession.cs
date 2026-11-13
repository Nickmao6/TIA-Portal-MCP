using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaEngine
{
    /// <summary>
    /// Wraps a TIA Portal Openness session. Every method attaches to the running TIA Portal
    /// (UI-visible, project already open), performs the operation, and detaches.
    /// Siemens assemblies are resolved at runtime from the D-drive PublicAPI folder
    /// via the AssemblyResolve hook registered in Program.Main.
    /// API surface verified against the real V21 assemblies:
    ///   PlcBlock/PlcBlockGroup/ProgrammingLanguage (V21 renamed Block/BlockGroup/SimaticProgrammingLanguage)
    ///   CompilerResultState: Success|Information|Warning|Error
    ///   PlcSoftware implements IEngineeringServiceProvider -> GetService&lt;ICompilable&gt;().Compile()
    /// </summary>
    public sealed class TiaSession : IDisposable
    {
        private TiaPortal _portal;
        private Project _project;
        private bool _disposed;

        public static TiaSession Connect(string projectHint = null)
        {
            var processes = TiaPortal.GetProcesses();
            if (processes.Count == 0)
            {
                throw new TiaException("NO_TIA_INSTANCE",
                    "未找到运行中的 TIA Portal V21 实例，请先打开博途并加载项目");
            }

            TiaPortalProcess selected = processes[0];
            string selectedProjectPath = null;

            if (!string.IsNullOrEmpty(projectHint))
            {
                var wanted = Path.GetFullPath(projectHint);
                foreach (var p in processes)
                {
                    bool match = false;
                    try
                    {
                        using (var probe = p.Attach())
                        {
                            foreach (var prj in probe.Projects)
                            {
                                var path = ReadProjectPath(prj);
                                if (path != null && string.Equals(Path.GetFullPath(path), wanted,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    selected = p;
                                    selectedProjectPath = path;
                                    match = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                    if (match) break;
                }
            }

            var portal = selected.Attach();
            var session = new TiaSession();
            try
            {
                session._portal = portal;
                session.AutoAnswerConfirmations();
                session.SelectProject(projectHint ?? selectedProjectPath);
                return session;
            }
            catch
            {
                portal.Dispose();
                throw;
            }
        }

        // ---------- project lifecycle (static, works even when no project is open) ----------

        private static TiaPortal AttachPortal()
        {
            var processes = TiaPortal.GetProcesses();
            if (processes.Count == 0)
                throw new TiaException("NO_TIA_INSTANCE", "未找到运行中的 TIA Portal V21 实例，请先启动博途");
            var portal = processes[0].Attach();
            try
            {
                if (Environment.GetEnvironmentVariable("TIA_MCP_AUTO_CONFIRM") != "no")
                {
                    portal.Confirmation += (s, e) =>
                    {
                        try
                        {
                            var a = e as ConfirmationEventArgs;
                            if (a == null) return;
                            a.Result = a.Choices.HasFlag(ConfirmationChoices.YesToAll)
                                ? ConfirmationResult.YesToAll : ConfirmationResult.Yes;
                            a.IsHandled = true;
                        }
                        catch { }
                    };
                }
            }
            catch { }
            return portal;
        }

        public static string CreateProject(string directory, string projectName)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new TiaException("BAD_ARGS", "缺少 --dir");
            if (string.IsNullOrWhiteSpace(projectName)) throw new TiaException("BAD_ARGS", "缺少 --name");
            var dir = Path.GetFullPath(directory);
            var expected = Path.Combine(dir, projectName + ".ap21");
            if (!IsProjectInScope(expected))
                throw new TiaException("PROJECT_NOT_ALLOWED",
                    "新项目路径不在 tia-scope.txt 授权范围内: " + expected);

            Directory.CreateDirectory(dir);
            var portal = AttachPortal();
            try
            {
                var project = portal.Projects.Create(new DirectoryInfo(dir), projectName);
                project.Save();
                return Json.Obj(new Dictionary<string, string>
                {
                    { "project", Json.Str(project.Name) },
                    { "path", Json.Str(project.Path == null ? expected : project.Path.FullName) },
                    { "saved", Json.Bool(true) }
                });
            }
            finally { try { portal.Dispose(); } catch { } }
        }

        public static string OpenProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new TiaException("BAD_ARGS", "缺少 --path");
            var full = Path.GetFullPath(path);
            if (!IsProjectInScope(full))
                throw new TiaException("PROJECT_NOT_ALLOWED", "项目不在 tia-scope.txt 授权范围内: " + full);
            var portal = AttachPortal();
            try
            {
                var project = portal.Projects.Open(new FileInfo(full));
                return Json.Obj(new Dictionary<string, string>
                {
                    { "project", Json.Str(project.Name) },
                    { "path", Json.Str(project.Path == null ? full : project.Path.FullName) }
                });
            }
            finally { try { portal.Dispose(); } catch { } }
        }

        public static string CloseProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new TiaException("BAD_ARGS", "缺少 --path");
            var full = Path.GetFullPath(path);
            var portal = AttachPortal();
            try
            {
                var project = portal.Projects.FirstOrDefault(p =>
                {
                    try { return p.Path != null && string.Equals(Path.GetFullPath(p.Path.FullName), full, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                });
                if (project == null) throw new TiaException("PROJECT_NOT_FOUND", "没有打开这个项目: " + full);
                var name = project.Name;
                project.Close();
                return Json.Obj(new Dictionary<string, string>
                {
                    { "project", Json.Str(name) },
                    { "closed", Json.Bool(true) }
                });
            }
            finally { try { portal.Dispose(); } catch { } }
        }

        public string ListDevices()
        {
            var items = new List<string>();
            foreach (var d in _project.Devices)
            {
                items.Add(Json.Obj(new Dictionary<string, string>
                {
                    { "name", Json.Str(SafeString(() => d.Name)) },
                    { "type", Json.Str(SafeString(() => d.TypeIdentifier)) }
                }));
            }
            return Json.Arr(items);
        }

        public string AddDevice(string typeIdentifier, string name, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(typeIdentifier)) throw new TiaException("BAD_ARGS", "缺少 --type");
            if (string.IsNullOrWhiteSpace(name)) throw new TiaException("BAD_ARGS", "缺少 --name");
            if (string.IsNullOrWhiteSpace(deviceName)) deviceName = name;
            var device = _project.Devices.CreateWithItem(typeIdentifier, name, deviceName);
            return Json.Obj(new Dictionary<string, string>
            {
                { "name", Json.Str(SafeString(() => device.Name)) },
                { "type", Json.Str(SafeString(() => device.TypeIdentifier)) },
                { "created", Json.Bool(true) }
            });
        }

        private void SelectProject(string requestedPath)
        {
            List<Project> projects;
            try { projects = _portal.Projects.ToList(); }
            catch (Exception ex) { throw new TiaException("PROJECT_READ_FAILED", "无法读取项目列表: " + ex.Message); }

            if (projects.Count == 0)
            {
                throw new TiaException("NO_PROJECT", "TIA Portal 已连接但没有打开任何项目，请先在博途里打开项目");
            }

            if (!string.IsNullOrEmpty(requestedPath))
            {
                var wanted = Path.GetFullPath(requestedPath);
                var match = projects.FirstOrDefault(p =>
                {
                    var path = ReadProjectPath(p);
                    return path != null && string.Equals(Path.GetFullPath(path), wanted, StringComparison.OrdinalIgnoreCase);
                });
                if (match != null) { _project = match; AssertProjectInScope(match); return; }
            }

            // No explicit request: still only ever fall back to an in-scope project.
            var inScope = projects.FirstOrDefault(p => IsProjectInScope(ReadProjectPath(p)));
            if (inScope == null)
            {
                var openPaths = projects.Select(ReadProjectPath).Where(x => x != null).ToList();
                throw new TiaException("PROJECT_NOT_ALLOWED",
                    "博途里打开的项目不在授权范围内，已拒绝操作（以免误改你其他项目）。" +
                    "授权范围: " + string.Join(" | ", AllowedProjectPaths()) +
                    "。当前博途打开的项目: " + (openPaths.Count == 0 ? "(读不到路径)" : string.Join(" | ", openPaths)));
            }
            _project = inScope;
        }

        // ---------- confirmation auto-answer ----------
        // TIA Portal raises a modal "Openness access" prompt every time an Openness process
        // attaches and wants to modify something. That blocks unattended MCP operation.
        // Subscribing to TiaPortal.Confirmation lets the worker answer it itself.
        // We answer YesToAll, which is also what the dialog's "全部确定" button does —
        // it tells TIA to remember the authorization so it stops asking.
        // Set TIA_MCP_AUTO_CONFIRM=no to opt out and see the prompts yourself.

        private void AutoAnswerConfirmations()
        {
            if (Environment.GetEnvironmentVariable("TIA_MCP_AUTO_CONFIRM") == "no")
                return;
            try
            {
                _portal.Confirmation += OnConfirmation;
            }
            catch { }
        }

        private void OnConfirmation(object sender, ConfirmationEventArgs e)
        {
            try
            {
                var args = e as ConfirmationEventArgs;
                if (args == null) return;
                // Prefer "yes to all" so TIA persists the grant; fall back to plain yes.
                var choices = args.Choices;
                bool hasYesToAll = choices.HasFlag(ConfirmationChoices.YesToAll);
                args.Result = hasYesToAll ? ConfirmationResult.YesToAll : ConfirmationResult.Yes;
                args.IsHandled = true;
            }
            catch { }
        }

        // ---------- project scope guard ----------
        // Hard boundary: this server may only ever touch projects inside the allowed roots.
        // Anything else (the user's other TIA projects) is refused, so a stray call can never
        // modify an unrelated project. Configurable via tia-scope.txt next to the workspace.

        private static string ScopeFilePath()
        {
            // engine\bin\Debug\ -> walk up to the workspace root that holds tia-scope.txt
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var probe = Path.Combine(dir, "tia-scope.txt");
                if (File.Exists(probe)) return probe;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
            return null;
        }

        public static List<string> AllowedProjectPaths()
        {
            var f = ScopeFilePath();
            var result = new List<string>();
            if (f == null) return result;
            foreach (var line in System.IO.File.ReadAllLines(f, System.Text.Encoding.UTF8))
            {
                // 去掉 UTF-8 BOM: char.IsWhiteSpace('\uFEFF') == false, 所以 Trim() 不会去掉它。
                // 若用户在记事本里存成"UTF-8 带 BOM", 第一行路径会带 \uFEFF,
                // Path.GetFullPath 得到怪路径 -> 白名单全部失配 -> 所有操作被拒。
                var t = line.Trim().TrimStart('\uFEFF').Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                result.Add(t);
            }
            return result;
        }

        public static bool IsProjectInScope(string projectFilePath)
        {
            if (string.IsNullOrEmpty(projectFilePath)) return false;
            var allowed = AllowedProjectPaths();
            if (allowed.Count == 0) return false; // fail closed
            string target;
            try { target = Path.GetFullPath(projectFilePath); } catch { return false; }
            foreach (var a in allowed)
            {
                try
                {
                    var root = Path.GetFullPath(a);
                    if (target.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static void AssertProjectInScope(Project p)
        {
            var path = ReadProjectPath(p);
            if (!IsProjectInScope(path))
            {
                throw new TiaException("PROJECT_NOT_ALLOWED",
                    "拒绝操作授权范围之外的项目: " + (path ?? "(路径未知)") +
                    "。授权范围: " + string.Join(" | ", AllowedProjectPaths()));
            }
        }

        private static string ReadProjectPath(Project p)
        {
            try { return p.Path?.FullName ?? SafeName(() => p.Name); }
            catch { return SafeName(() => p.Name); }
        }

        public string ProjectName => SafeName(() => _project.Name);
        public string ProjectPath => ReadProjectPath(_project);

        /// <summary>All PlcSoftware instances under the project (recursive device items).</summary>
        public List<PlcSoftware> FindAllPlc(string plcName)
        {
            var result = new List<PlcSoftware>();
            foreach (var device in _project.Devices)
            {
                try { CollectPlcSoftware(device.DeviceItems, result); }
                catch { }
            }

            if (result.Count == 0)
                throw new TiaException("PLC_NOT_FOUND", "项目中未找到 PLC 软件节点");

            if (!string.IsNullOrEmpty(plcName))
            {
                var filtered = result.Where(s => SafeName(() => s.Name)
                    .Equals(plcName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count == 0)
                {
                    var names = string.Join(", ", result.Select(s => SafeName(() => s.Name)));
                    throw new TiaException("PLC_NOT_FOUND",
                        $"未找到名为 '{plcName}' 的 PLC。项目中的 PLC 有: {names}");
                }
                return filtered;
            }
            return result;
        }

        private static void CollectPlcSoftware(DeviceItemComposition items, List<PlcSoftware> into)
        {
            foreach (DeviceItem item in items)
            {
                try
                {
                    var container = item.GetService<SoftwareContainer>();
                    if (container?.Software is PlcSoftware sw) into.Add(sw);
                }
                catch { }
                try
                {
                    if (item.DeviceItems != null && item.DeviceItems.Count > 0)
                        CollectPlcSoftware(item.DeviceItems, into);
                }
                catch { }
            }
        }

        // ============ 1. list_projects ============
        // Static / connection-free: enumerates running portal processes directly, so it can report
        // the "no portal running" state instead of failing to connect.
        public static string ListProjectsDetached()
        {
            var instanceJson = new List<string>();
            var processes = TiaPortal.GetProcesses();
            foreach (var p in processes)
            {
                int pid = 0;
                var projs = new List<string>();
                try
                {
                    pid = p.Id;
                    using (var portal = p.Attach())
                    {
                        foreach (var prj in portal.Projects)
                        {
                            projs.Add(Json.Obj(new Dictionary<string, string>
                            {
                                { "name", Json.Str(SafeName(() => prj.Name)) },
                                { "path", Json.Str(ReadProjectPath(prj) ?? "") }
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    projs.Add(Json.Obj(new Dictionary<string, string>
                    {
                        { "name", Json.Str("(读取失败)") },
                        { "path", Json.Str(ex.Message) }
                    }));
                }
                instanceJson.Add(Json.Obj(new Dictionary<string, string>
                {
                    { "pid", pid.ToString() },
                    { "projects", Json.Arr(projs) }
                }));
            }
            return Json.Obj(new Dictionary<string, string> { { "instances", Json.Arr(instanceJson) } });
        }

        // ============ 2. browse_tree ============
        public string BrowseTree(string plcName)
        {
            var plc = FindAllPlc(plcName)[0];
            return Json.Obj(new Dictionary<string, string>
            {
                { "plc", Json.Str(SafeName(() => plc.Name)) },
                { "blocks", Json.Arr(CollectBlocks(plc)) },
                { "tags", BuildTagSummary(plc) },
                { "udts", Json.Arr(CollectUdts(plc)) }
            });
        }

        private static string BlockTypeName(PlcBlock b)
        {
            if (b is OB) return "OB";
            if (b is FB) return "FB";
            if (b is FC) return "FC";
            if (b is GlobalDB) return "DB";
            if (b is InstanceDB) return "IDB";
            return b.GetType().Name;
        }

        private List<string> CollectBlocks(PlcSoftware plc)
        {
            var list = new List<string>();
            try { WalkBlockGroup(plc.BlockGroup, "/", list); } catch { }
            return list;
        }

        private HashSet<string> CollectBlockNames(PlcSoftware plc)
        {
            var names = new HashSet<string>();
            try { CollectBlockNamesInto(plc.BlockGroup, names); } catch { }
            return names;
        }

        private static void CollectBlockNamesInto(PlcBlockSystemGroup group, HashSet<string> into)
        {
            if (group == null) return;
            foreach (PlcBlock b in group.Blocks)
            {
                try { into.Add(SafeName(() => b.Name)); } catch { }
            }
            foreach (var g in group.Groups)
            {
                try { CollectBlockNamesIntoUserGroup(g, into); } catch { }
            }
            foreach (var sg in group.SystemBlockGroups)
            {
                try { CollectBlockNamesIntoSystemGroup(sg, into); } catch { }
            }
        }

        private static void CollectBlockNamesIntoUserGroup(PlcBlockUserGroup group, HashSet<string> into)
        {
            if (group == null) return;
            foreach (PlcBlock b in group.Blocks)
            {
                try { into.Add(SafeName(() => b.Name)); } catch { }
            }
            foreach (var g in group.Groups)
            {
                try { CollectBlockNamesIntoUserGroup(g, into); } catch { }
            }
        }

        private static void CollectBlockNamesIntoSystemGroup(PlcSystemBlockGroup group, HashSet<string> into)
        {
            if (group == null) return;
            foreach (PlcBlock b in group.Blocks)
            {
                try { into.Add(SafeName(() => b.Name)); } catch { }
            }
            foreach (var g in group.Groups)
            {
                try { CollectBlockNamesIntoSystemGroup(g, into); } catch { }
            }
        }

        private void WalkBlockGroup(PlcBlockSystemGroup group, string pathPrefix, List<string> into)
        {
            if (group == null) return;
            string path = pathPrefix;
            foreach (PlcBlock b in group.Blocks)
            {
                try
                {
                    into.Add(Json.Obj(new Dictionary<string, string>
                    {
                        { "name", Json.Str(SafeName(() => b.Name)) },
                        { "type", Json.Str(BlockTypeName(b)) },
                        { "number", Json.Num(SafeNumber(b)) },
                        { "lang", Json.Str(SafeLang(b)) },
                        { "path", Json.Str(path) }
                    }));
                }
                catch { }
            }
            foreach (var g in group.Groups)
            {
                try { WalkUserGroup(g, path + "/" + SafeName(() => g.Name), into); } catch { }
            }
            foreach (var sg in group.SystemBlockGroups)
            {
                try { WalkSystemGroup(sg, path + "/" + SafeName(() => sg.Name), into); } catch { }
            }
        }

        private void WalkSystemGroup(PlcSystemBlockGroup group, string pathPrefix, List<string> into)
        {
            if (group == null) return;
            string path = pathPrefix;
            foreach (PlcBlock b in group.Blocks)
            {
                try
                {
                    into.Add(Json.Obj(new Dictionary<string, string>
                    {
                        { "name", Json.Str(SafeName(() => b.Name)) },
                        { "type", Json.Str(BlockTypeName(b)) },
                        { "number", Json.Num(SafeNumber(b)) },
                        { "lang", Json.Str(SafeLang(b)) },
                        { "path", Json.Str(path) }
                    }));
                }
                catch { }
            }
            foreach (var g in group.Groups)
            {
                try { WalkSystemGroup(g, path + "/" + SafeName(() => g.Name), into); } catch { }
            }
        }

        private void WalkUserGroup(PlcBlockUserGroup group, string pathPrefix, List<string> into)
        {
            if (group == null) return;
            string path = pathPrefix;
            foreach (PlcBlock b in group.Blocks)
            {
                try
                {
                    into.Add(Json.Obj(new Dictionary<string, string>
                    {
                        { "name", Json.Str(SafeName(() => b.Name)) },
                        { "type", Json.Str(BlockTypeName(b)) },
                        { "number", Json.Num(SafeNumber(b)) },
                        { "lang", Json.Str(SafeLang(b)) },
                        { "path", Json.Str(path) }
                    }));
                }
                catch { }
            }
            foreach (var g in group.Groups)
            {
                try { WalkUserGroup(g, path + "/" + SafeName(() => g.Name), into); } catch { }
            }
        }

        private static int SafeNumber(PlcBlock b)
        {
            try { return b.Number; } catch { return 0; }
        }

        private static string SafeLang(PlcBlock b)
        {
            try { return b.ProgrammingLanguage.ToString(); } catch { return ""; }
        }

        private string BuildTagSummary(PlcSoftware plc)
        {
            var inputs = new List<string>();
            var outputs = new List<string>();
            var memory = new List<string>();
            var other = new List<string>();

            var tables = new List<PlcTagTable>();
            try { CollectTagTables(plc.TagTableGroup, tables); } catch { }
            foreach (var tbl in tables)
            {
                foreach (PlcTag t in tbl.Tags)
                {
                    var addr = SafeString(() => t.LogicalAddress);
                    string entry = Json.Obj(new Dictionary<string, string>
                    {
                        { "name", Json.Str(SafeName(() => t.Name)) },
                        { "address", Json.Str(addr) },
                        { "type", Json.Str(SafeString(() => t.DataTypeName)) },
                        { "table", Json.Str(SafeName(() => tbl.Name)) }
                    });
                    if (addr.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) inputs.Add(entry);
                    else if (addr.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) outputs.Add(entry);
                    else if (addr.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) memory.Add(entry);
                    else other.Add(entry);
                }
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "inputs", Json.Arr(inputs) },
                { "outputs", Json.Arr(outputs) },
                { "memory", Json.Arr(memory) },
                { "other", Json.Arr(other) }
            });
        }

        // Deletes PLC program blocks (OB/FB/FC/DB) by name. Used to clean up a project
        // before starting a new task. NOT exposed as an MCP tool -- deliberately kept as an
        // explicit worker mode so accidental deletion requires a deliberate CLI invocation.
        public string DeleteBlocks(string namesCsv, string plcName, bool dryRun)
        {
            var plc = FindAllPlc(plcName)[0];
            var wanted = namesCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim()).Where(x => x.Length > 0)
                                .ToList();
            var deleted = new List<string>();
            var missing = new List<string>();

            // collect from main groups and system blocks (IDBs live under system block groups)
            var all = new List<PlcBlock>();
            try { all.AddRange(EnumerateAllBlocks(plc.BlockGroup)); } catch { }
            try
            {
                foreach (var sg in plc.BlockGroup.SystemBlockGroups)
                    foreach (PlcBlock b in sg.Blocks) all.Add(b);
            }
            catch { }

            foreach (var name in wanted)
            {
                PlcBlock target = null;
                foreach (var b in all)
                {
                    try { if (SafeName(() => b.Name) == name) { target = b; break; } } catch { }
                }
                if (target == null) { missing.Add(name); continue; }
                if (dryRun) { deleted.Add(name + "(dry_run)"); continue; }
                try { target.Delete(); deleted.Add(name); }
                catch (Exception ex) { missing.Add(name + ": " + ex.Message); }
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "plc", Json.Str(SafeName(() => plc.Name)) },
                { "deleted", Json.StrArr(deleted) },
                { "not_deleted", Json.StrArr(missing) }
            });
        }

        // Deletes PLC tags by name. Used to repair tags that were created with the
        // Create(name, address, type) argument-order bug and cannot be fixed in place
        // (SetAttribute raises EngineeringSecurityException).
        public string DeleteTags(string namesCsv, string plcName, bool dryRun)
        {
            var plc = FindAllPlc(plcName)[0];
            var tables = new List<PlcTagTable>();
            try { CollectTagTables(plc.TagTableGroup, tables); } catch { }
            if (tables.Count == 0)
                throw new TiaException("NO_TAG_TABLE", "PLC 中找不到变量表");

            var wanted = new HashSet<string>(
                namesCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            var deleted = new List<string>();
            var missing = new List<string>();
            foreach (var name in wanted)
            {
                PlcTag target = null;
                foreach (var t in tables[0].Tags)
                {
                    try { if (string.Equals(SafeName(() => t.Name), name, StringComparison.OrdinalIgnoreCase)) { target = t; break; } } catch { }
                }
                if (target == null) { missing.Add(name); continue; }
                if (dryRun) { deleted.Add(name + "(dry_run)"); continue; }
                try { target.Delete(); deleted.Add(name); }
                catch (Exception ex) { missing.Add(name + ": 删除失败 " + ex.Message); }
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "table", Json.Str(SafeName(() => tables[0].Name)) },
                { "deleted", Json.StrArr(deleted) },
                { "not_deleted", Json.StrArr(missing) }
            });
        }

        // ============ 2.5 ensure_tags (needed before importing symbolic LAD) ============
        // Ladder networks reference operands by symbolic tag name (Scope="GlobalVariable" ->
        // <Component Name="..."/>). Those tags must exist first. Creates them idempotently:
        // existing names are skipped unless overwrite is set.
        public string EnsureTags(string specsCsv, string plcName, bool dryRun)
        {
            var plc = FindAllPlc(plcName)[0];
            var tables = new List<PlcTagTable>();
            try { CollectTagTables(plc.TagTableGroup, tables); } catch { }
            if (tables.Count == 0)
                throw new TiaException("NO_TAG_TABLE", "PLC 中找不到变量表，无法创建变量");

            var defaultTable = tables[0];
            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingTags = new Dictionary<string, PlcTag>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in defaultTable.Tags)
            {
                try
                {
                    var n = SafeName(() => t.Name);
                    existingNames.Add(n);
                    existingTags[n] = t;
                }
                catch { }
            }

            var repaired = new List<string>();

            var created = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();

            // spec format: Name@Address[@Type]  separated by ',' or ';'
            var specs = specsCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var spec in specs)
            {
                var parts = spec.Split('@');
                if (parts.Length < 2) { failed.Add(spec + " (格式应为 Name@Address[@Type])"); continue; }
                var name = parts[0].Trim();
                var addr = parts[1].Trim();
                var type = parts.Length >= 3 && parts[2].Trim().Length > 0 ? parts[2].Trim() : "Bool";

                if (existingNames.Contains(name))
                {
                    // Self-heal: an earlier run may have created this tag with Create(name, addr, type)
                    // instead of Create(name, type, addr), which TIA silently accepts but swaps the
                    // values (DataTypeName becomes "%I0.0"). Repair in place, non-destructively.
                    var existing = existingTags[name];
                    if (existing != null && !dryRun)
                    {
                        try
                        {
                            var curType = SafeString(() => existing.DataTypeName);
                            var curAddr = SafeString(() => existing.LogicalAddress);
                            bool swapped = !string.IsNullOrEmpty(curType) && curType.StartsWith("%")
                                        && !string.IsNullOrEmpty(curAddr) && !curAddr.StartsWith("%");
                            if (swapped)
                            {
                                existing.SetAttribute("DataTypeName", type);
                                existing.SetAttribute("LogicalAddress", addr);
                                repaired.Add(name + ": " + curType + "/" + curAddr + " -> " + type + "/" + addr);
                            }
                        }
                        catch { }
                    }
                    skipped.Add(name + "(已存在)");
                    continue;
                }
                if (dryRun) { created.Add(name + " -> " + addr); continue; }

                try
                {
                    // 地址冲突检查: 精确相等还不够 —— %MW10 与 %MB10/%MB11/%MD10 字节重叠
                    // 也会互相踩踏。按"区域 + 位区间"判断重叠, 而非字符串相等。
                    foreach (var t in defaultTable.Tags)
                    {
                        string curAddr, curName;
                        try { curAddr = SafeString(() => t.LogicalAddress); curName = SafeString(() => t.Name); } catch { continue; }
                        if (!string.Equals(curName, name, StringComparison.OrdinalIgnoreCase)
                            && AddressesOverlap(curAddr, addr))
                        {
                            throw new TiaException("TAG_ADDRESS_CONFLICT",
                                "地址冲突: " + addr + " 与变量 \"" + curName + "\"(" + curAddr + ") 的地址重叠。" +
                                "请换一个空闲地址(可先用 browse_tree 查看已占用地址)。");
                        }
                    }
                    // 参数顺序(V21 官方文档): Create(name, dataTypeName, logicalAddress)
                    // 传反成 (name, address, type) 时 TIA 不报错, 但会把类型和地址对调,
                    // 导致梯形图引用时报 "Invalid data type"。切勿调换。
                    defaultTable.Tags.Create(name, type, addr);
                    existingNames.Add(name);
                    created.Add(name + " -> " + addr);
                }
                catch (Exception ex)
                {
                    failed.Add(name + ": " + ex.Message);
                }
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "table", Json.Str(SafeName(() => defaultTable.Name)) },
                { "created", Json.StrArr(created) },
                { "skipped", Json.StrArr(skipped) },
                { "repaired", Json.StrArr(repaired) },
                { "failed", Json.StrArr(failed) }
            });
        }

        // ---------- 地址解析 / 重叠判断 ----------
        // 精确字符串比较抓不到字节重叠: %MW10(占 M10~M11) 与 %MB11、%MD10 都会互相踩踏。
        // 把地址归一成 (区域, 起始位, 结束位), 用区间重叠判断。
        // 支持: %I0.0 / %Q0.7(位)、%MB10 / %MW10 / %MD10 / %ML10(字节/字/双字/长字)。
        // DB 寻址(%DB1.DBX0.0)暂不解析, 视为"无法判断"返回 false(不误报)。

        internal static bool TryParseAddress(string addr, out string area, out int bitStart, out int bitEnd)
        {
            area = null; bitStart = 0; bitEnd = -1;
            if (string.IsNullOrEmpty(addr)) return false;
            var s = addr.Trim();
            if (!s.StartsWith("%", StringComparison.Ordinal)) return false;
            s = s.Substring(1);
            if (s.Length == 0 || !char.IsLetter(s[0])) return false;
            area = s[0].ToString().ToUpperInvariant();

            int idx = 1;
            int sizeBytes = 1;
            if (idx < s.Length && (s[idx] == 'B' || s[idx] == 'W' || s[idx] == 'D' || s[idx] == 'L'))
            {
                sizeBytes = s[idx] == 'B' ? 1 : s[idx] == 'W' ? 2 : s[idx] == 'D' ? 4 : 8;
                idx++;
            }

            int offStart = idx;
            while (idx < s.Length && char.IsDigit(s[idx])) idx++;
            if (offStart == idx) return false;
            if (!int.TryParse(s.Substring(offStart, idx - offStart), out int byteOffset)) return false;

            // 位地址 %I0.0: 数字后跟 .bit
            if (idx < s.Length && s[idx] == '.')
            {
                idx++;
                int bStart = idx;
                while (idx < s.Length && char.IsDigit(s[idx])) idx++;
                if (bStart == idx) return false;
                if (!int.TryParse(s.Substring(bStart, idx - bStart), out int bitIndex)) return false;
                if (bitIndex < 0 || bitIndex > 7) return false;
                bitStart = byteOffset * 8 + bitIndex;
                bitEnd = bitStart;
                return true;
            }

            bitStart = byteOffset * 8;
            bitEnd = bitStart + sizeBytes * 8 - 1;
            return true;
        }

        internal static bool AddressesOverlap(string a, string b)
        {
            if (!TryParseAddress(a, out string areaA, out int a0, out int a1)) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            if (!TryParseAddress(b, out string areaB, out int b0, out int b1)) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(areaA, areaB, StringComparison.OrdinalIgnoreCase)) return false;
            return a0 <= b1 && b0 <= a1;
        }

        private static void CollectTagTables(PlcTagTableSystemGroup group, List<PlcTagTable> into)
        {
            if (group == null) return;
            foreach (PlcTagTable t in group.TagTables) into.Add(t);
            foreach (var g in group.Groups)
            {
                try { CollectTagTablesUserGroup(g, into); } catch { }
            }
        }

        private static void CollectTagTablesUserGroup(PlcTagTableUserGroup group, List<PlcTagTable> into)
        {
            if (group == null) return;
            foreach (PlcTagTable t in group.TagTables) into.Add(t);
            foreach (var g in group.Groups)
            {
                try { CollectTagTablesUserGroup(g, into); } catch { }
            }
        }

        private List<string> CollectUdts(PlcSoftware plc)
        {
            var list = new List<string>();
            try
            {
                foreach (PlcType t in plc.TypeGroup.Types)
                {
                    list.Add(Json.Obj(new Dictionary<string, string>
                    {
                        { "name", Json.Str(SafeName(() => t.Name)) },
                        { "type", Json.Str(t.GetType().Name) }
                    }));
                }
            }
            catch { }
            return list;
        }

        // ============ 3. import_scl ============

        /// <summary>
        /// Reads an SCL file with correct encoding. TIA Portal external sources on a Chinese
        /// system are read as the system ANSI codepage (GBK/CP936), not UTF-8. Reading a GBK
        /// file as UTF-8 corrupts multi-byte Chinese into mojibake and makes the SCL parser
        /// mis-read the block structure (VAR sections disappear -> "tag not defined" errors).
        /// So: prefer UTF-8 when the bytes are valid UTF-8; otherwise fall back to GBK.
        /// </summary>
        public static string ReadSclText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                var strictUtf8 = new System.Text.UTF8Encoding(false, true); // throwOnInvalidBytes
                return strictUtf8.GetString(bytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return System.Text.Encoding.GetEncoding(936).GetString(bytes); // GBK
            }
        }

        public static List<string> ScanSclDeclarations(string sclPath)
        {
            if (!File.Exists(sclPath))
                throw new TiaException("FILE_NOT_FOUND", "SCL 文件不存在: " + sclPath);

            var text = ReadSclText(sclPath);
            var result = new List<string>();
            // (?<![\w]) 防止匹配到 END_FUNCTION_BLOCK 里的子串 ——
            // 之前会把 END_FUNCTION_BLOCK 当成一条声明, 并在后面捕获出幽灵块名
            // (实测: 两块文件导入成功却报 failed:["FUNCTION_BLOCK"])。
            const string wordStart = @"(?<![\w])";
            var patterns = new[]
            {
                wordStart + @"FUNCTION_BLOCK\s+""([^""]+)""",
                wordStart + @"FUNCTION_BLOCK\s+([A-Za-z_]\w*)",
                wordStart + @"FUNCTION\s+""([^""]+)""",
                wordStart + @"FUNCTION\s+([A-Za-z_]\w*)",
                wordStart + @"DATA_BLOCK\s+""([^""]+)""",
                wordStart + @"DATA_BLOCK\s+([A-Za-z_]\w*)",
                wordStart + @"ORGANIZATION_BLOCK\s+""([^""]+)""",
                wordStart + @"ORGANIZATION_BLOCK\s+([A-Za-z_]\w*)",
                wordStart + @"TYPE\s+""([^""]+)"""
            };
            foreach (var pat in patterns)
            {
                var re = new System.Text.RegularExpressions.Regex(pat,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in re.Matches(text))
                {
                    var v = m.Groups[1].Value;
                    // 兜底: 关键字本身绝不该被当成块名
                    if (v.EndsWith("_BLOCK", StringComparison.OrdinalIgnoreCase)) continue;
                    if (v.StartsWith("END_", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(v);
                }
            }
            return result.Distinct().ToList();
        }

        /// <summary>
        /// TIA 的 SCL 外部源按 GBK/ANSI 解码, 但本工具预览用 UTF-8。
        /// 若文件是 UTF-8 且含非 ASCII 字符, TIA 会把中文读成乱码 ——
        /// 实测结果是"返回 failed 但块已用乱码名字建进工程", 属于静默数据污染。
        /// 这里提前拒绝并给出可执行的指引。
        /// </summary>
        public static void AssertSclEncodingUsable(string sclPath)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(sclPath); }
            catch { return; }
            if (bytes.Length == 0) return;

            bool hasNonAscii = bytes.Any(b => b > 0x7F);
            if (!hasNonAscii) return;   // 纯 ASCII 任何编码都一样, 安全

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                throw new TiaException("SCL_ENCODING_UTF8_BOM",
                    "SCL 文件是 UTF-8 with BOM。TIA 的 SCL 外部源按 GBK/ANSI 解码, " +
                    "直接导入会把中文变成乱码, 并且**块仍然会被创建**(名字乱码), 污染工程。" +
                    "请把文件另存为 ANSI/GBK 编码(记事本: 另存为 -> 编码选 ANSI)后重试。");

            bool validUtf8;
            try
            {
                var strict = new System.Text.UTF8Encoding(false, true);
                strict.GetString(bytes);
                validUtf8 = true;
            }
            catch (System.Text.DecoderFallbackException) { validUtf8 = false; }

            if (validUtf8)
                throw new TiaException("SCL_ENCODING_MISMATCH",
                    "SCL 文件含非 ASCII 字符(中文)且是合法 UTF-8, 但 TIA 的 SCL 外部源按 GBK/ANSI 解码。" +
                    "直接导入会产生乱码块名并污染工程(实测: 返回 failed 但块已建入)。" +
                    "请把文件另存为 ANSI/GBK 编码后重试, 或把中文标识符改成 ASCII。");
        }

        /// <summary>
        /// Import an SCL file: create an external source in the PLC's ExternalSourceGroup,
        /// then GenerateBlocksFromSource to materialize the declared blocks. dry_run only
        /// parses and reports.
        /// V21: PlcExternalSourceComposition.CreateFromFile(name, path); PlcExternalSource.GenerateBlocksFromSource(GenerateBlockOption).
        /// </summary>
        public List<ImportResult> ImportScl(string sclPath, string plcName, bool dryRun, bool overwrite)
        {
            if (!File.Exists(sclPath))
                throw new TiaException("FILE_NOT_FOUND", "SCL 文件不存在: " + sclPath);

            var declarations = ScanSclDeclarations(sclPath);
            if (declarations.Count == 0)
                throw new TiaException("SCL_PARSE_EMPTY", "SCL 文件中没有解析到任何块声明");

            // 编码必须能通过 TIA 的 GBK 解码, 否则会写出乱码块名污染工程。
            AssertSclEncodingUsable(sclPath);

            var plc = FindAllPlc(plcName)[0];
            var existingNames = CollectBlockNames(plc);

            var results = new List<ImportResult>();
            foreach (var name in declarations)
            {
                if (existingNames.Contains(name) && !overwrite)
                {
                    results.Add(new ImportResult { BlockName = name, Status = "skipped", Reason = "块已存在且未指定 overwrite" });
                }
                else
                {
                    results.Add(new ImportResult { BlockName = name, Status = "created", Reason = dryRun ? "dry_run 预演" : null });
                }
            }

            if (dryRun)
            {
                return results;
            }

            // ---- real import ----
            PlcExternalSource externalSource = null;
            try
            {
                var sourceGroup = plc.ExternalSourceGroup;
                externalSource = sourceGroup.ExternalSources.CreateFromFile(Path.GetFileName(sclPath), sclPath);
                var generated = externalSource.GenerateBlocksFromSource(GenerateBlockOption.None);

                var finalNames = new HashSet<string>();
                foreach (var o in generated)
                {
                    try
                    {
                        var nameProp = o.GetType().GetProperty("Name");
                        if (nameProp != null)
                        {
                            var n = nameProp.GetValue(o, null)?.ToString();
                            if (!string.IsNullOrEmpty(n)) finalNames.Add(n);
                        }
                    }
                    catch { }
                }

                var final = new List<ImportResult>();
                foreach (var r in results)
                {
                    if (r.Status == "skipped")
                    {
                        final.Add(r);
                    }
                    else
                    {
                        final.Add(new ImportResult
                        {
                            BlockName = r.BlockName,
                            Status = finalNames.Contains(r.BlockName) ? "created" : "failed",
                            Reason = finalNames.Contains(r.BlockName) ? null : "未在生成结果中找到该块"
                        });
                    }
                }
                return final;
            }
            catch (TiaException) { throw; }
            catch (Exception ex)
            {
                throw new TiaException("SCL_IMPORT_FAILED", "SCL 导入失败: " + ex.Message);
            }
            finally
            {
                try { if (externalSource != null) externalSource.Delete(); } catch { }
            }
        }

        // ============ 4. import_lad_xml ============
        public List<ImportResult> ImportLadXml(string xmlPath, string plcName, bool dryRun, bool overwrite = true)
        {
            if (!File.Exists(xmlPath))
                throw new TiaException("FILE_NOT_FOUND", "XML 文件不存在: " + xmlPath);

            var plc = FindAllPlc(plcName)[0];
            var names = ScanXmlBlockNames(xmlPath);

            // 同名块检查: 之前硬编码 ImportOptions.Override, 会**静默覆盖**已有块,
            // 与 tia_import_scl 的 overwrite 语义不一致, 也违反了"OB1 只追加不重写"的承诺。
            var existing = CollectBlockNames(plc);
            var clashing = names.Where(n => existing.Contains(n)).ToList();
            if (clashing.Count > 0 && !overwrite)
            {
                var results0 = new List<ImportResult>();
                foreach (var n in names)
                {
                    results0.Add(new ImportResult
                    {
                        BlockName = n,
                        Status = existing.Contains(n) ? "skipped" : "created",
                        Reason = existing.Contains(n) ? "块已存在且未指定 overwrite" : (dryRun ? "dry_run 预演" : null)
                    });
                }
                if (dryRun) return results0;
                // 真实导入时也不能覆盖: 只报冲突, 不动工程
                throw new TiaException("XML_BLOCK_EXISTS",
                    "XML 中的块已存在于工程中，且未指定 overwrite: " + string.Join(", ", clashing) +
                    "。传 overwrite=true 才会覆盖。");
            }

            if (dryRun)
            {
                return names.Select(n => new ImportResult
                {
                    BlockName = n,
                    Status = "created",
                    Reason = "dry_run 预演"
                }).ToList();
            }

            try
            {
                plc.BlockGroup.Blocks.Import(new FileInfo(xmlPath),
                    overwrite ? ImportOptions.Override : ImportOptions.None);

                // 坑: OB 的 <CyclicTime>/<PhaseOffset> 导入时被**忽略**
                //(新建出来的 CyclicTime=0, 编译报 "循环时间: The value must be between 1 and 60000")。
                // 块上有 SetAttribute, 所以按 XML 里声明的值补设, 并在结果里如实报告。
                var postFix = new Dictionary<string, string>();
                int cyc = ReadDeclaredInt(xmlPath, "CyclicTime");
                int phase = ReadDeclaredInt(xmlPath, "PhaseOffset");
                if (cyc > 0 || phase >= 0)
                {
                    foreach (var n in names)
                    {
                        PlcBlock blk = FindBlockByName(plc, n);
                        if (blk == null) continue;
                        if (!(blk is OB)) continue;   // 只有 OB 有循环时间
                        var notes = new List<string>();
                        if (cyc > 0 && !TrySetAttr(blk, "CyclicTime", cyc, notes))
                            notes.Add("CyclicTime 未能设为 " + cyc);
                        if (phase >= 0) TrySetAttr(blk, "PhaseOffset", phase, notes);
                        if (notes.Count > 0) postFix[n] = string.Join("; ", notes);
                    }
                }

                return names.Select(n => new ImportResult
                {
                    BlockName = n,
                    Status = "created",
                    Reason = postFix.ContainsKey(n) ? postFix[n] : null
                }).ToList();
            }
            catch (TiaException) { throw; }
            catch (Exception ex)
            {
                throw new TiaException("XML_IMPORT_FAILED", "XML 导入失败: " + ex.Message);
            }
        }

        /// <summary>读 XML 里某个元素的整数值; 不存在/解析失败返回 -1。</summary>
        private static int ReadDeclaredInt(string xmlPath, string elementName)
        {
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(xmlPath);
                var n = doc.SelectSingleNode("//*[local-name()='" + elementName + "']");
                if (n == null) return -1;
                int v;
                return int.TryParse(n.InnerText.Trim(), out v) ? v : -1;
            }
            catch { return -1; }
        }

        private static PlcBlock FindBlockByName(PlcSoftware plc, string name)
        {
            try
            {
                foreach (var b in EnumerateAllBlocks(plc.BlockGroup))
                {
                    string bn;
                    try { bn = b.Name; } catch { continue; }
                    if (bn == name) return b;
                }
            }
            catch { }
            return null;
        }

        /// <summary>用反射调 SetAttribute 并读回校验; 成功返回 true。</summary>
        private static bool TrySetAttr(PlcBlock blk, string attr, object value, List<string> notes)
        {
            try
            {
                var t = blk.GetType();
                var setM = t.GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                var getM = t.GetMethod("GetAttribute", new[] { typeof(string) });
                if (setM == null || getM == null) { notes.Add("该块类型不支持 SetAttribute"); return false; }
                setM.Invoke(blk, new object[] { attr, value });
                var after = getM.Invoke(blk, new object[] { attr });
                if (after != null && Convert.ToInt32(after) == Convert.ToInt32(value)) return true;
                notes.Add(attr + " 读回不一致(=" + (after ?? "null") + ")");
                return false;
            }
            catch (Exception ex)
            {
                notes.Add(attr + " 设置失败: " + ex.Message);
                return false;
            }
        }

        public static List<string> ScanXmlBlockNames(string xmlPath)
        {
            // SimaticML 里块名**不是属性**, 而是 <AttributeList><Name> 子元素:
            //   <SW.Blocks.OB ID="0"><AttributeList>...<Name>Main</Name>...
            // 之前查 //*[local-name()='Block'] 永远匹配不到, 于是 dry_run 预览
            // 和导入结果都是空数组 —— 安全预览形同虚设。
            var doc = new System.Xml.XmlDocument();
            doc.Load(xmlPath);
            var result = new List<string>();

            var nodes = doc.SelectNodes("//*[starts-with(local-name(),'SW.Blocks.')]");
            if (nodes != null)
            {
                foreach (System.Xml.XmlNode node in nodes)
                {
                    string v = null;
                    // 少数变体会把名字写成属性, 一并支持
                    var attr = node.Attributes?["Name"];
                    if (attr != null) v = attr.Value;
                    if (string.IsNullOrEmpty(v))
                    {
                        // 子元素形式: AttributeList/Name
                        var nameNode = node.SelectSingleNode(
                            "*[local-name()='AttributeList']/*[local-name()='Name']");
                        if (nameNode != null) v = nameNode.InnerText;
                    }
                    // SW.Blocks.CompileUnit 等没有名字, 自然被跳过
                    if (!string.IsNullOrEmpty(v) && !result.Contains(v)) result.Add(v);
                }
            }
            return result;
        }

        // ============ 3.5 export_block (read-only helper for LAD sampling) ============
        // Exports an existing block to a SimaticML XML file. This does NOT modify the project;
        // it only reads a block and writes its XML to disk. Used to obtain a real LAD structure
        // reference (e.g. the existing OB1) instead of inventing XML. Not exposed as an MCP tool.
        // 只读探测: 列出 PlcSoftware 上所有组合属性, 以及每个组合里的组和块名。
        // 用途: 实例 DB(如 IEC_Timer_0_DB)挂在哪个组合下, 公开文档没写清楚, 靠这个摸出来。
        public string ProbeBlockLocations(string plcName)
        {
            var plc = FindAllPlc(plcName)[0];
            var lines = new List<string>();

            foreach (var prop in plc.GetType().GetProperties())
            {
                object val;
                try { val = prop.GetValue(plc, null); } catch { continue; }
                if (val == null) { continue; }

                var en = val as System.Collections.IEnumerable;
                if (en == null) continue;

                var names = new List<string>();
                foreach (var item in en)
                {
                    if (item == null) continue;
                    var bp = item.GetType().GetProperty("Blocks");
                    if (bp == null) { continue; }
                    var bs = bp.GetValue(item, null) as System.Collections.IEnumerable;
                    if (bs == null) continue;
                    foreach (var b in bs)
                    {
                        try
                        {
                            var n = b.GetType().GetProperty("Name");
                            names.Add(n == null ? "?" : (n.GetValue(b, null) ?? "?").ToString());
                        }
                        catch { }
                    }
                }
                if (names.Count > 0)
                    lines.Add(prop.Name + ": " + string.Join(", ", names));
            }

            if (lines.Count == 0)
                lines.Add("(PlcSoftware 上没有可枚举且含 Blocks 的组合)");

            return Json.Obj(new Dictionary<string, string>
            {
                { "plc", Json.Str(SafeName(() => plc.Name)) },
                { "locations", Json.StrArr(lines) }
            });
        }

        // 探测设备(CPU)的硬件组态属性 —— 用于找"时钟存储器字节"这类 CPU 属性的真实名字。
        // Openness 把 CPU 属性放在 DeviceItem 的 Attributes 集合里, 用反射枚举出来,
        // 就能看到属性名 + 当前值, 不必猜。
        public string ProbeDeviceAttributes(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                lines.Add("### 设备: " + SafeName(() => device.Name));
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var nm = SafeName(() => item.Name);
                    lines.Add("  DeviceItem: " + nm);
                    try
                    {
                        var infos = item.GetAttributeInfos();
                        foreach (var info in infos)
                        {
                            string an = "?";
                            try { an = info.GetType().GetProperty("Name")?.GetValue(info, null)?.ToString() ?? "?"; } catch { }
                            object av = null;
                            try { av = item.GetAttribute(an); } catch (Exception ex) { av = "(读失败:" + ex.Message + ")"; }
                            lines.Add("    " + an + " = " + (av ?? "(null)"));
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add("    GetAttributeInfos 失败: " + ex.Message);
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(未枚举到任何设备属性)");
            return Json.Obj(new Dictionary<string, string> { { "attrs", Json.StrArr(lines) } });
        }

        // 探测时钟存储器字节相关属性 —— 之前 GetAttributeInfos 没列出 Clock/Memory 关键字,
        // 但有些属性是"隐藏"的(不列在 Info 里, 但 Get/SetAttribute 仍可用)。逐个读候选名,
        // 看哪些真实存在。
        public string ProbeClockMemory(string plcName)
        {
            var lines = new List<string>();
            var candidates = new[]
            {
                "SystemMemoryClockMemoryEnable", "ClockMemoryEnable", "EnableClockMemoryByte",
                "ClockMemoryByte", "ClockMemoryAddress", "ClockMemoryByteAddress",
                "SystemMemoryClockMemoryAddress", "ClockMemory", "SystemMemoryEnable",
                "SystemMemoryByte", "ClockMemoryByteEnable", "EnableSystemMemoryClockMemory",
                "ClockMemoryAddressByte"
            };
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "PLC_1") continue;
                    foreach (var c in candidates)
                    {
                        try
                        {
                            var v = item.GetAttribute(c);
                            lines.Add(c + " = " + (v ?? "(null)"));
                        }
                        catch (Exception ex)
                        {
                            lines.Add(c + " => 读失败: " + ex.Message);
                        }
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(没找到 PLC_1)");
            return Json.Obj(new Dictionary<string, string> { { "probe", Json.StrArr(lines) } });
        }

        // 探测 HMI(WinCC)设备 —— 看 HMI 的 Software(HmiTarget) 暴露了哪些成员(画面/变量/连接)。
        public string ProbeHmi(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                Console.Error.WriteLine("[probe_hmi] device=" + SafeName(() => device.Name));
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var nm = SafeName(() => item.Name);
                    if (nm.IndexOf("HMI", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Console.Error.WriteLine("[probe_hmi]   item=" + nm);
                    lines.Add("DeviceItem: " + nm);
                    try
                    {
                        var sc = item.GetService<SoftwareContainer>();
                        Console.Error.WriteLine("[probe_hmi]   GetService 返回 " + (sc == null ? "null" : "非null"));
                        if (sc == null || sc.Software == null)
                        {
                            lines.Add("  (无 SoftwareContainer / Software)");
                            continue;
                        }
                        var sw = sc.Software;
                        Console.Error.WriteLine("[probe_hmi]   sw=" + (sw == null ? "null" : "非null"));
                        lines.Add("  Software 类型: " + sw.GetType().FullName);
                        foreach (var p in sw.GetType().GetProperties())
                        {
                            lines.Add("    " + p.Name + " : " + p.PropertyType.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add("  GetService 失败: " + ex.Message);
                    }
                    Console.Error.WriteLine("[probe_hmi]   完成 " + nm);
                }
            }
            if (lines.Count == 0) lines.Add("(没找到 HMI 设备)");
            return Json.Obj(new Dictionary<string, string> { { "hmi", Json.StrArr(lines) } });
        }

        // 深入探测 HMI 的画面文件夹 / 变量文件夹 / 连接 —— 用反射只列**属性名**,
        // 不调 getter(避免触发耗时操作)。确认画面(Screen)集合怎么访问。
        public string ProbeHmiDetail(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    try
                    {
                        var sc = item.GetService<SoftwareContainer>();
                        if (sc == null || sc.Software == null) { lines.Add("(无 Software)"); break; }
                        var hmi = sc.Software;
                        lines.Add("HmiTarget 类型: " + hmi.GetType().FullName);
                        // 逐个探测关键成员
                        foreach (var key in new[] { "ScreenFolder", "TagFolder", "Connections", "ScreenOverview" })
                        {
                            var prop = hmi.GetType().GetProperty(key);
                            if (prop == null) { lines.Add(key + ": (无此属性)"); continue; }
                            object val = null;
                            try { val = prop.GetValue(hmi, null); }
                            catch (Exception ex) { lines.Add(key + ": 取值失败 " + ex.Message); continue; }
                            if (val == null) { lines.Add(key + ": (null)"); continue; }
                            var t = val.GetType();
                            lines.Add("### " + key + " : " + t.FullName);
                            foreach (var p in t.GetProperties())
                            {
                                lines.Add("    " + p.Name + " : " + p.PropertyType.Name);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add("探测失败: " + ex.Message);
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(没找到 HMI_RT_1)");
            return Json.Obj(new Dictionary<string, string> { { "detail", Json.StrArr(lines) } });
        }

        // 列出 HMI 实际的画面名 / HMI 变量名 / 连接数。
        public string ProbeHmiScreens(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    try
                    {
                        var hmi = item.GetService<SoftwareContainer>().Software;

                        // 画面
                        var sf = hmi.GetType().GetProperty("ScreenFolder").GetValue(hmi, null);
                        var screens = sf.GetType().GetProperty("Screens").GetValue(sf, null) as System.Collections.IEnumerable;
                        lines.Add("=== 画面 ===");
                        if (screens != null)
                        {
                            foreach (var s in screens)
                            {
                                string nm = "?";
                                try { nm = s.GetType().GetProperty("Name")?.GetValue(s, null)?.ToString() ?? "?"; } catch { }
                                lines.Add("  " + nm);
                            }
                        }

                        // HMI 变量
                        var tf = hmi.GetType().GetProperty("TagFolder").GetValue(hmi, null);
                        var tt = tf.GetType().GetProperty("DefaultTagTable").GetValue(tf, null);
                        var tags = tt.GetType().GetProperty("Tags").GetValue(tt, null) as System.Collections.IEnumerable;
                        lines.Add("=== HMI 变量 ===");
                        if (tags != null)
                        {
                            foreach (var t in tags)
                            {
                                string nm = "?";
                                try { nm = t.GetType().GetProperty("Name")?.GetValue(t, null)?.ToString() ?? "?"; } catch { }
                                lines.Add("  " + nm);
                            }
                        }

                        // 连接
                        var conn = hmi.GetType().GetProperty("Connections").GetValue(hmi, null);
                        var cnt = conn.GetType().GetProperty("Count").GetValue(conn, null);
                        lines.Add("=== 连接数: " + (cnt ?? "?") + " ===");
                    }
                    catch (Exception ex)
                    {
                        lines.Add("探测失败: " + ex.Message);
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(没找到 HMI_RT_1)");
            return Json.Obj(new Dictionary<string, string> { { "screens", Json.StrArr(lines) } });
        }

        // 探测单个画面(Screen)对象的成员 —— 找 Export/Import 方法(能不能像块一样导出 XML)。
        public string ProbeHmiScreenDetail(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    try
                    {
                        var hmi = item.GetService<SoftwareContainer>().Software;
                        var sf = hmi.GetType().GetProperty("ScreenFolder").GetValue(hmi, null);
                        var screens = sf.GetType().GetProperty("Screens").GetValue(sf, null) as System.Collections.IEnumerable;
                        foreach (var s in screens)
                        {
                            string nm = "?";
                            try { nm = s.GetType().GetProperty("Name")?.GetValue(s, null)?.ToString() ?? "?"; } catch { }
                            lines.Add("=== 画面: " + nm + "  (类型 " + s.GetType().FullName + ") ===");
                            foreach (var m in s.GetType().GetMethods())
                            {
                                if (m.IsSpecialName) continue;
                                var pns = string.Join(",", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name));
                                lines.Add("  方法 " + m.Name + "(" + pns + ") -> " + m.ReturnType.Name);
                            }
                            foreach (var p in s.GetType().GetProperties())
                            {
                                lines.Add("  属性 " + p.Name + " : " + p.PropertyType.Name);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add("探测失败: " + ex.Message);
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(没找到画面)");
            return Json.Obj(new Dictionary<string, string> { { "screen", Json.StrArr(lines) } });
        }

        // 导出 HMI 画面到 XML 文件(学习画面结构用)。画面有 Export(FileInfo, ExportOptions)。
        /// <summary>按名字找 HMI 画面对象。</summary>
        private object FindHmiScreen(string screenName)
        {
            object screen = null;
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    var hmi = item.GetService<SoftwareContainer>()?.Software;
                    if (hmi == null) continue;
                    var sf = hmi.GetType().GetProperty("ScreenFolder")?.GetValue(hmi, null);
                    if (sf == null) continue;
                    var screens = sf.GetType().GetProperty("Screens")?.GetValue(sf, null) as System.Collections.IEnumerable;
                    if (screens == null) continue;
                    foreach (var s in screens)
                    {
                        string nm = "?";
                        try { nm = s.GetType().GetProperty("Name")?.GetValue(s, null)?.ToString() ?? "?"; } catch { }
                        if (nm == screenName) { screen = s; break; }
                    }
                }
            }
            return screen;
        }

        /// <summary>列出 HMI 画面的全部属性(名/值/类型)。</summary>
        public string HmiScreenAttrs(string screenName, string plcName)
        {
            var screen = FindHmiScreen(screenName);
            if (screen == null) throw new TiaException("HMI_SCREEN_NOT_FOUND", "找不到 HMI 画面: " + screenName);
            var t = screen.GetType();
            var lines = new List<string>();
            lines.Add("类型: " + t.FullName);
            var infosM = t.GetMethod("GetAttributeInfos");
            var getM = t.GetMethod("GetAttribute", new[] { typeof(string) });
            if (infosM != null && getM != null)
            {
                foreach (var info in infosM.Invoke(screen, null) as System.Collections.IEnumerable)
                {
                    string an = "?";
                    try { an = info.GetType().GetProperty("Name")?.GetValue(info, null)?.ToString() ?? "?"; } catch { }
                    object av = null; string ty = "";
                    try { av = getM.Invoke(screen, new object[] { an }); ty = av?.GetType().FullName ?? "null"; }
                    catch (Exception ex) { av = "(读失败:" + ex.Message + ")"; }
                    lines.Add("  " + an + " = " + (av ?? "(null)") + "   [" + ty + "]");
                }
            }
            else lines.Add("(无 GetAttributeInfos)");
            return Json.Obj(new Dictionary<string, string> { { "attrs", Json.StrArr(lines) } });
        }

        /// <summary>设置 HMI 画面属性。颜色类属性接受 "r,g,b"。</summary>
        public string SetHmiScreenAttr(string screenName, string attr, string valueText, string plcName)
        {
            var screen = FindHmiScreen(screenName);
            if (screen == null) throw new TiaException("HMI_SCREEN_NOT_FOUND", "找不到 HMI 画面: " + screenName);
            var t = screen.GetType();
            var getM = t.GetMethod("GetAttribute", new[] { typeof(string) });
            var setM = t.GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
            if (getM == null || setM == null)
                throw new TiaException("ATTR_UNSUPPORTED", "该画面类型不支持 Get/SetAttribute");

            object oldVal;
            try { oldVal = getM.Invoke(screen, new object[] { attr }); }
            catch (Exception ex) { throw new TiaException("ATTR_NOT_FOUND", "属性不存在: " + attr + " (" + ex.Message + ")"); }

            object newVal = ParseAttrValue(oldVal, valueText);
            try { setM.Invoke(screen, new object[] { attr, newVal }); }
            catch (Exception ex)
            {
                var m = ex.Message;
                var ie = ex.InnerException;
                int g = 0;
                while (ie != null && g++ < 4) { m += " <- " + ie.Message; ie = ie.InnerException; }
                throw new TiaException("SET_FAILED", "写属性失败: " + m);
            }

            object back = null;
            try { back = getM.Invoke(screen, new object[] { attr }); } catch { }
            return Json.Obj(new Dictionary<string, string>
            {
                { "screen", Json.Str(screenName) },
                { "attr", Json.Str(attr) },
                { "oldValue", Json.Str(oldVal?.ToString() ?? "(null)") },
                { "setTo", Json.Str(newVal?.ToString() ?? "(null)") },
                { "readBack", Json.Str(back?.ToString() ?? "(null)") },
                { "ok", Json.Bool(back != null && back.ToString() == (newVal?.ToString() ?? "")) }
            });
        }

        /// <summary>按原值类型解析输入: 颜色 "r,g,b"; 数值; 布尔; 字符串。</summary>
        private static object ParseAttrValue(object oldVal, string text)
        {
            string tn = oldVal?.GetType().FullName ?? "";
            if (tn == "System.Drawing.Color")
            {
                var parts = text.Split(',');
                if (parts.Length != 3) throw new TiaException("BAD_COLOR", "颜色要写成 \"r,g,b\": " + text);
                int r = int.Parse(parts[0].Trim()), gg = int.Parse(parts[1].Trim()), b = int.Parse(parts[2].Trim());
                var ct = oldVal.GetType();
                var fromArgb = ct.GetMethod("FromArgb", new[] { typeof(int), typeof(int), typeof(int) });
                return fromArgb.Invoke(null, new object[] { r, gg, b });
            }
            return ConvertLike(oldVal, text);
        }

        // 探测 HMI 画面的导入能力: ScreenFolder / Screens 集合 / Screen 上的 Import/Export/Create 方法。
        public string ProbeHmiImport(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    var hmi = item.GetService<SoftwareContainer>()?.Software;
                    if (hmi == null) continue;
                    var sf = hmi.GetType().GetProperty("ScreenFolder")?.GetValue(hmi, null);
                    if (sf == null) continue;

                    foreach (var obj in new object[] { hmi, sf })
                    {
                        if (obj == null) continue;
                        lines.Add("### " + obj.GetType().FullName);
                        foreach (var m in obj.GetType().GetMethods())
                        {
                            if (m.IsSpecialName) continue;
                            if (m.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) < 0 &&
                                m.Name.IndexOf("Export", StringComparison.OrdinalIgnoreCase) < 0 &&
                                m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                            lines.Add("   " + m.Name + "(" + ps + ") => " + m.ReturnType.Name);
                        }
                    }

                    var screens = sf.GetType().GetProperty("Screens")?.GetValue(sf, null);
                    if (screens != null)
                    {
                        lines.Add("### Screens 集合: " + screens.GetType().FullName);
                        foreach (var m in screens.GetType().GetMethods())
                        {
                            if (m.IsSpecialName) continue;
                            if (m.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) < 0 &&
                                m.Name.IndexOf("Export", StringComparison.OrdinalIgnoreCase) < 0 &&
                                m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                            lines.Add("   " + m.Name + "(" + ps + ") => " + m.ReturnType.Name);
                        }
                        if (screens is System.Collections.IEnumerable sen)
                        {
                            foreach (var s in sen)
                            {
                                if (s == null) continue;
                                lines.Add("### 画面对象: " + s.GetType().FullName);
                                foreach (var m in s.GetType().GetMethods())
                                {
                                    if (m.IsSpecialName) continue;
                                    if (m.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        m.Name.IndexOf("Export", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                                    lines.Add("   " + m.Name + "(" + ps + ") => " + m.ReturnType.Name);
                                }
                                break;
                            }
                        }
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(没找到 HMI)");
            return Json.Obj(new Dictionary<string, string> { { "probe", Json.StrArr(lines) } });
        }

        // 导入 HMI 画面 XML(走 ScreenComposition.Import)。
        public string ImportHmiScreen(string xmlPath, string plcName, bool overwrite)
        {
            if (!File.Exists(xmlPath)) throw new TiaException("FILE_NOT_FOUND", "文件不存在: " + xmlPath);
            object screens = null;
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    var hmi = item.GetService<SoftwareContainer>()?.Software;
                    if (hmi == null) continue;
                    var sf = hmi.GetType().GetProperty("ScreenFolder")?.GetValue(hmi, null);
                    if (sf == null) continue;
                    screens = sf.GetType().GetProperty("Screens")?.GetValue(sf, null);
                }
            }
            if (screens == null) throw new TiaException("HMI_NOT_FOUND", "找不到 HMI 画面集合");

            var importM = screens.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
            if (importM == null) throw new TiaException("HMI_IMPORT_UNSUPPORTED", "ScreenComposition 上没有 Import(FileInfo, ImportOptions)");

            object result = null;
            try { result = importM.Invoke(screens, new object[] { new FileInfo(xmlPath), overwrite ? ImportOptions.Override : ImportOptions.None }); }
            catch (Exception ex)
            {
                var m = ex.Message; var ie = ex.InnerException; int g = 0;
                while (ie != null && g++ < 4) { m += " <- " + ie.Message; ie = ie.InnerException; }
                throw new TiaException("HMI_IMPORT_FAILED", "画面导入失败: " + m);
            }

            var names = new List<string>();
            if (result is System.Collections.IEnumerable en)
                foreach (var s in en)
                {
                    string nm = "?";
                    try { nm = s.GetType().GetProperty("Name")?.GetValue(s, null)?.ToString() ?? "?"; } catch { }
                    names.Add(nm);
                }
            return Json.Obj(new Dictionary<string, string>
            {
                { "imported", Json.StrArr(names) },
                { "path", Json.Str(xmlPath) }
            });
        }

        // 探测 HMI 图形资源库(GraphicLists): 有哪些图形、能否导入新图片。
        public string ProbeHmiGraphics(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    var hmi = item.GetService<SoftwareContainer>()?.Software;
                    if (hmi == null) continue;
                    var gls = hmi.GetType().GetProperty("GraphicLists")?.GetValue(hmi, null);
                    if (gls == null) { lines.Add("(没有 GraphicLists)"); continue; }
                    lines.Add("GraphicLists 类型: " + gls.GetType().FullName);

                    foreach (var m in gls.GetType().GetMethods())
                    {
                        if (m.IsSpecialName) continue;
                        if (m.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) < 0 &&
                            m.Name.IndexOf("Export", StringComparison.OrdinalIgnoreCase) < 0 &&
                            m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                        lines.Add("   方法 " + m.Name + "(" + ps + ") => " + m.ReturnType.Name);
                    }

                    if (gls is System.Collections.IEnumerable en)
                    {
                        foreach (var gl in en)
                        {
                            if (gl == null) continue;
                            string nm = "?";
                            try { nm = gl.GetType().GetProperty("Name")?.GetValue(gl, null)?.ToString() ?? "?"; } catch { }
                            lines.Add("=== 图形列表: " + nm + "  (" + gl.GetType().FullName + ")");
                            foreach (var m in gl.GetType().GetMethods())
                            {
                                if (m.IsSpecialName) continue;
                                if (m.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    m.Name.IndexOf("Export", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                                lines.Add("     方法 " + m.Name + "(" + ps + ") => " + m.ReturnType.Name);
                            }
                            // 列出里面的图形
                            object items = null;
                            foreach (var key in new[] { "Graphics", "Items", "GraphicItems" })
                            {
                                var p = gl.GetType().GetProperty(key);
                                if (p == null) continue;
                                try { items = p.GetValue(gl, null); } catch { }
                                if (items != null) { lines.Add("     [取自 ." + key + "]"); break; }
                            }
                            if (items is System.Collections.IEnumerable ien)
                            {
                                int n = 0;
                                foreach (var g in ien)
                                {
                                    if (g == null) continue;
                                    n++;
                                    if (n > 30) { lines.Add("     ...还有更多"); break; }
                                    string gn = "?";
                                    try { gn = g.GetType().GetProperty("Name")?.GetValue(g, null)?.ToString() ?? "?"; } catch { }
                                    lines.Add("     - " + gn);
                                }
                                if (n == 0) lines.Add("     (空)");
                            }
                            else lines.Add("     (无可枚举的图形集合)");
                        }
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(没找到 HMI)");
            return Json.Obj(new Dictionary<string, string> { { "graphics", Json.StrArr(lines) } });
        }

        // 导出 HMI 图形列表(看图片资源怎么存)。
        // 递归导出图形资源树(文件夹→列表→图片项), 用来确认画面引用的"图形_N"是否还在。
        public string ProbeGraphicsTree(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    var hmi = item.GetService<SoftwareContainer>()?.Software;
                    if (hmi == null) continue;
                    var gls = hmi.GetType().GetProperty("GraphicLists")?.GetValue(hmi, null);
                    if (gls == null) { lines.Add("(没有 GraphicLists)"); continue; }
                    lines.Add("根容器: " + gls.GetType().FullName);
                    WalkGraphics(gls, 0, lines, 5);
                }
            }
            if (lines.Count == 0) lines.Add("(没找到 HMI)");
            return Json.Obj(new Dictionary<string, string> { { "tree", Json.StrArr(lines) } });
        }

        private void WalkGraphics(object node, int depth, List<string> lines, int maxDepth)
        {
            if (node == null || depth > maxDepth) return;
            string ind = new string(' ', depth * 2);

            object lists = null;
            foreach (var key in new[] { "GraphicLists", "Lists", "Items" })
            {
                var p = node.GetType().GetProperty(key);
                if (p == null) continue;
                try { lists = p.GetValue(node, null); } catch { }
                if (lists != null) break;
            }
            // 组合对象本身常常就是可枚举的(属性集合为空)
            if (lists == null && node is System.Collections.IEnumerable self && !(node is string)) lists = node;
            if (lists is System.Collections.IEnumerable len && !(lists is string))
            {
                foreach (var gl in len)
                {
                    if (gl == null) continue;
                    string nm = "?";
                    try { nm = gl.GetType().GetProperty("Name")?.GetValue(gl, null)?.ToString() ?? "?"; } catch { }
                    lines.Add(ind + "[列表] " + nm);
                    // 任意可枚举属性都当作"图形项"试一次
                    foreach (var p in gl.GetType().GetProperties())
                    {
                        if (!p.CanRead) continue;
                        object v;
                        try { v = p.GetValue(gl, null); } catch { continue; }
                        if (v == null || v is string || !(v is System.Collections.IEnumerable ve)) continue;
                        var kids = new List<string>();
                        foreach (var it in ve)
                        {
                            if (it == null) continue;
                            string iname = "?";
                            try { iname = it.GetType().GetProperty("Name")?.GetValue(it, null)?.ToString() ?? "?"; } catch { }
                            kids.Add(iname);
                            if (kids.Count >= 25) break;
                        }
                        if (kids.Count > 0) lines.Add(ind + "   ." + p.Name + " → " + string.Join(", ", kids));
                    }
                }
            }

            object folders = null;
            foreach (var key in new[] { "Folders", "Groups" })
            {
                var p = node.GetType().GetProperty(key);
                if (p == null) continue;
                try { folders = p.GetValue(node, null); } catch { }
                if (folders != null) break;
            }
            if (folders is System.Collections.IEnumerable fen && !(folders is string))
                foreach (var f in fen)
                {
                    if (f == null) continue;
                    string fn = "?";
                    try { fn = f.GetType().GetProperty("Name")?.GetValue(f, null)?.ToString() ?? "?"; } catch { }
                    lines.Add(ind + "▸ 文件夹: " + fn);
                    WalkGraphics(f, depth + 1, lines, maxDepth);
                }
        }

        public string ExportHmiGraphicList(string listName, string targetPath, string plcName)
        {
            // ⚠ 已禁用。2026-10-22 21:13:45 实测: 该调用在 TIA 内部抛异常并把整个博途带崩
            // (Diagnostics/ErrorReports.xml: Reason="Openness"), 丢失约 30 分钟未保存改动。
            // 在找到安全替代方案前不再触碰 GraphicList.Export。
            throw new TiaException("UNSAFE_DISABLED",
                "导出图形列表已禁用: 该调用曾导致博途崩溃(Reason=Openness)。");
        }

        // 保存工程(落盘到 .ap21)。
        public string SaveProject(string plcName)
        {
            try { _project.Save(); }
            catch (Exception ex)
            {
                var m = ex.Message; var ie = ex.InnerException; int g = 0;
                while (ie != null && g++ < 4) { m += " <- " + ie.Message; ie = ie.InnerException; }
                throw new TiaException("SAVE_FAILED", "保存失败: " + m);
            }
            return Json.Obj(new Dictionary<string, string>
            {
                { "project", Json.Str(SafeName(() => _project.Name)) },
                { "saved", Json.Bool(true) }
            });
        }

        public string ExportHmiScreen(string screenName, string targetPath, string plcName)
        {
            object screen = null;
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    if (SafeName(() => item.Name) != "HMI_RT_1") continue;
                    var hmi = item.GetService<SoftwareContainer>().Software;
                    var sf = hmi.GetType().GetProperty("ScreenFolder").GetValue(hmi, null);
                    var screens = sf.GetType().GetProperty("Screens").GetValue(sf, null) as System.Collections.IEnumerable;
                    foreach (var s in screens)
                    {
                        string nm = "?";
                        try { nm = s.GetType().GetProperty("Name")?.GetValue(s, null)?.ToString() ?? "?"; } catch { }
                        if (nm == screenName) { screen = s; break; }
                    }
                }
            }
            if (screen == null)
                throw new TiaException("HMI_SCREEN_NOT_FOUND", "找不到 HMI 画面: " + screenName);

            var exportMethod = screen.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
            if (exportMethod == null)
                throw new TiaException("HMI_EXPORT_UNSUPPORTED", "画面对象上没有 Export(FileInfo, ExportOptions) 方法");

            exportMethod.Invoke(screen, new object[] { new FileInfo(targetPath), ExportOptions.WithDefaults });

            long bytes = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;
            return Json.Obj(new Dictionary<string, string>
            {
                { "screen", Json.Str(screenName) },
                { "path", Json.Str(targetPath) },
                { "bytes", Json.Num(bytes) }
            });
        }

        // 读一个块的全部属性(GetAttributeInfos + GetAttribute), 用于诊断导入后属性是否生效。
        public string ProbeBlockAttrs(string blockName, string plcName)
        {
            var plc = FindAllPlc(plcName)[0];
            PlcBlock found = null;
            foreach (var b in EnumerateAllBlocks(plc.BlockGroup))
            {
                try { if (SafeName(() => b.Name) == blockName) { found = b; break; } } catch { }
            }
            if (found == null)
                throw new TiaException("BLOCK_NOT_FOUND", "找不到块: " + blockName);

            var lines = new List<string>();
            lines.Add("类型: " + found.GetType().FullName);
            var t = found.GetType();
            var infosM = t.GetMethod("GetAttributeInfos");
            var getM = t.GetMethod("GetAttribute", new[] { typeof(string) });
            if (infosM != null && getM != null)
            {
                var infos = infosM.Invoke(found, null) as System.Collections.IEnumerable;
                if (infos != null)
                {
                    foreach (var info in infos)
                    {
                        string nm = "?";
                        try { nm = info.GetType().GetProperty("Name")?.GetValue(info, null)?.ToString() ?? "?"; } catch { }
                        string val;
                        try { val = getM.Invoke(found, new object[] { nm })?.ToString() ?? "(null)"; }
                        catch (Exception ex) { val = "(读失败: " + ex.Message + ")"; }
                        lines.Add("  " + nm + " = " + val);
                    }
                }
            }
            else lines.Add("(该类型没有 GetAttributeInfos/GetAttribute)");

            foreach (var m in t.GetMethods())
            {
                if (m.Name.IndexOf("Attribute", StringComparison.OrdinalIgnoreCase) >= 0)
                    lines.Add("方法: " + m.Name);
            }
            return Json.Obj(new Dictionary<string, string> { { "attrs", Json.StrArr(lines) } });
        }

        // 深入探测工艺对象组: TechnologicalObjectGroup -> 实例DB -> Members(参数树)
        public string ProbeTechnologyObjects(string plcName)
        {
            var plc = FindAllPlc(plcName)[0];
            var lines = new List<string>();
            var t = plc.GetType();

            var grpProp = t.GetProperty("TechnologicalObjectGroup");
            if (grpProp == null) { lines.Add("(无 TechnologicalObjectGroup)"); return Json.Obj(new Dictionary<string, string> { { "to", Json.StrArr(lines) } }); }
            object grp;
            try { grp = grpProp.GetValue(plc, null); } catch (Exception ex) { lines.Add("取值失败: " + ex.Message); return Json.Obj(new Dictionary<string, string> { { "to", Json.StrArr(lines) } }); }
            lines.Add("容器类型: " + grp.GetType().FullName);
            foreach (var p in grp.GetType().GetProperties())
                lines.Add("  容器属性 " + p.Name + " : " + p.PropertyType.Name);

            var en = grp as System.Collections.IEnumerable;
            if (en == null)
            {
                lines.Add("(容器不是 IEnumerable, 尝试成员集合)");
                foreach (var key in new[] { "TechnologicalObjects", "TechnologicalInstanceDBs", "InstanceDBs", "Items", "Groups", "Objects" })
                {
                    var p = grp.GetType().GetProperty(key);
                    if (p == null) continue;
                    lines.Add("  找到集合属性: " + key);
                    try { en = p.GetValue(grp, null) as System.Collections.IEnumerable; } catch { }
                    if (en != null) break;
                }
            }
            if (en == null) { lines.Add("(无法枚举工艺对象)"); return Json.Obj(new Dictionary<string, string> { { "to", Json.StrArr(lines) } }); }

            foreach (var item in en)
            {
                if (item == null) continue;
                var it = item.GetType();
                string nm = "?";
                try { nm = it.GetProperty("Name")?.GetValue(item, null)?.ToString() ?? "?"; } catch { }
                lines.Add("");
                lines.Add("=== 工艺对象: " + nm + " (" + it.FullName + ") ===");
                foreach (var p in it.GetProperties())
                    lines.Add("  属性 " + p.Name + " : " + p.PropertyType.Name);

                // 大量参数通常在 Members 里(像 DB 结构), 递归导出 Name/Datatype/StartValue
                object members = null;
                foreach (var key in new[] { "Members", "Parameters", "Items" })
                {
                    var p = it.GetProperty(key);
                    if (p == null) continue;
                    try { members = p.GetValue(item, null); } catch { }
                    if (members != null) { lines.Add("  [参数树取自 ." + key + "]"); break; }
                }
                if (members != null)
                {
                    int budget = 220;
                    var firstFew = new List<object>();
                    if (members is System.Collections.IEnumerable men)
                    {
                        int n0 = 0;
                        foreach (var mm in men) { if (mm != null && n0 < 3) { firstFew.Add(mm); n0++; } }
                        DumpMembers(men, "    ", 0, lines, ref budget);
                    }
                    // 参数对象的属性名不在 Name/DatatypeName/StartValue 上, 前几个各列一遍
                    foreach (var mm in firstFew)
                    {
                        var mt2 = mm.GetType();
                        string nm2 = "?";
                        try { nm2 = mt2.GetProperty("Name")?.GetValue(mm, null)?.ToString() ?? "?"; } catch { }
                        lines.Add("  --- 参数对象 " + nm2 + " (" + mt2.FullName + ") 的属性 ---");
                        foreach (var pp in mt2.GetProperties())
                        {
                            string v2;
                            try { v2 = pp.GetValue(mm, null)?.ToString() ?? "(null)"; }
                            catch (Exception ex) { v2 = "(读失败: " + ex.Message + ")"; }
                            lines.Add("      " + pp.Name + " : " + pp.PropertyType.Name + " = " + v2);
                        }
                    }
                    lines.Add("  (参数树输出结束, 剩余预算 " + budget + ")");
                }
                else lines.Add("  (没有 Members/Parameters 集合)");
            }
            return Json.Obj(new Dictionary<string, string> { { "to", Json.StrArr(lines) } });
        }

        private static void DumpMembers(System.Collections.IEnumerable list, string indent, int depth, List<string> lines, ref int budget)
        {
            if (budget <= 0 || depth > 3) return;
            foreach (var m in list)
            {
                if (budget <= 0) return;
                if (m == null) continue;
                budget--;
                var mt = m.GetType();
                string name = "?", dtype = "?", val = "";
                try { name = mt.GetProperty("Name")?.GetValue(m, null)?.ToString() ?? "?"; } catch { }
                try { dtype = mt.GetProperty("DatatypeName")?.GetValue(m, null)?.ToString() ?? "?"; } catch { }
                // 工艺对象参数(TechnologicalParameter)的值在 .Value 上, 普通 DB 成员在 .StartValue 上
                try
                {
                    var vp = mt.GetProperty("Value");
                    var sv = vp != null ? vp.GetValue(m, null) : mt.GetProperty("StartValue")?.GetValue(m, null);
                    if (sv != null) val = " = " + sv;
                }
                catch { }
                lines.Add(indent + name + " : " + dtype + val);

                object sub = null;
                foreach (var key in new[] { "Members", "Items" })
                {
                    var p = mt.GetProperty(key);
                    if (p == null) continue;
                    try { sub = p.GetValue(m, null); } catch { }
                    if (sub != null) break;
                }
                if (sub is System.Collections.IEnumerable sen && sen != null)
                    DumpMembers(sen, indent + "  ", depth + 1, lines, ref budget);
            }
        }

        /// <summary>找到工艺对象(如 PID_Compact_2)。</summary>
        private static object FindTechObject(PlcSoftware plc, string toName)
        {
            var grp = plc.GetType().GetProperty("TechnologicalObjectGroup")?.GetValue(plc, null);
            if (grp == null) return null;
            var en = grp.GetType().GetProperty("TechnologicalObjects")?.GetValue(grp, null)
                     as System.Collections.IEnumerable;
            if (en == null) return null;
            foreach (var to in en)
            {
                if (to == null) continue;
                string nm = "?";
                try { nm = to.GetType().GetProperty("Name")?.GetValue(to, null)?.ToString() ?? "?"; } catch { }
                if (nm == toName) return to;
            }
            return null;
        }

        private static System.Collections.IEnumerable TechParams(object to)
        {
            return to.GetType().GetProperty("Parameters")?.GetValue(to, null) as System.Collections.IEnumerable;
        }

        /// <summary>读工艺对象的全部参数(name = value)。</summary>
        public string GetTechParams(string plcName, string toName)
        {
            var plc = FindAllPlc(plcName)[0];
            var to = FindTechObject(plc, toName);
            if (to == null) throw new TiaException("TO_NOT_FOUND", "找不到工艺对象: " + toName);
            var lines = new List<string>();
            var en = TechParams(to);
            if (en != null)
            {
                foreach (var p in en)
                {
                    if (p == null) continue;
                    var pt = p.GetType();
                    string n = "?", v = "?";
                    try { n = pt.GetProperty("Name")?.GetValue(p, null)?.ToString() ?? "?"; } catch { }
                    try { v = pt.GetProperty("Value")?.GetValue(p, null)?.ToString() ?? "(null)"; } catch (Exception ex) { v = "(读失败: " + ex.Message + ")"; }
                    lines.Add(n + " = " + v);
                }
            }
            return Json.Obj(new Dictionary<string, string>
            {
                { "to", Json.Str(toName) },
                { "count", Json.Num(lines.Count) },
                { "params", Json.StrArr(lines) }
            });
        }

        /// <summary>改工艺对象的单个参数: 记录原值 -> 设置 -> 读回校验。dryRun 只读不改。</summary>
        public string SetTechParam(string plcName, string toName, string paramName, string valueText, bool dryRun)
        {
            var plc = FindAllPlc(plcName)[0];
            var to = FindTechObject(plc, toName);
            if (to == null) throw new TiaException("TO_NOT_FOUND", "找不到工艺对象: " + toName);
            var en = TechParams(to);
            if (en == null) throw new TiaException("TO_NO_PARAMS", "工艺对象没有 Parameters 集合");

            object target = null;
            foreach (var p in en)
            {
                if (p == null) continue;
                string n = "?";
                try { n = p.GetType().GetProperty("Name")?.GetValue(p, null)?.ToString() ?? "?"; } catch { }
                if (n == paramName) { target = p; break; }
            }
            if (target == null) throw new TiaException("TO_PARAM_NOT_FOUND", "工艺对象 " + toName + " 没有参数: " + paramName);

            var tt = target.GetType();
            var valProp = tt.GetProperty("Value");
            if (valProp == null) throw new TiaException("TO_PARAM_READONLY", "参数 " + paramName + " 没有 Value 属性");
            object oldVal = valProp.GetValue(target, null);

            object newVal = ConvertLike(oldVal, valueText);
            if (dryRun)
            {
                return Json.Obj(new Dictionary<string, string>
                {
                    { "to", Json.Str(toName) },
                    { "param", Json.Str(paramName) },
                    { "oldValue", Json.Str(oldVal?.ToString() ?? "(null)") },
                    { "wouldSet", Json.Str(newVal?.ToString() ?? "(null)") },
                    { "dryRun", Json.Bool(true) }
                });
            }

            if (!valProp.CanWrite)
                throw new TiaException("TO_PARAM_READONLY", "参数 " + paramName + " 的 Value 不可写");

            valProp.SetValue(target, newVal, null);
            object readBack = valProp.GetValue(target, null);
            return Json.Obj(new Dictionary<string, string>
            {
                { "to", Json.Str(toName) },
                { "param", Json.Str(paramName) },
                { "oldValue", Json.Str(oldVal?.ToString() ?? "(null)") },
                { "setTo", Json.Str(newVal?.ToString() ?? "(null)") },
                { "readBack", Json.Str(readBack?.ToString() ?? "(null)") },
                { "ok", Json.Bool(readBack != null && readBack.ToString() == newVal?.ToString()) },
                { "dryRun", Json.Bool(false) }
            });
        }

        /// <summary>按原值的类型把文本转成目标值(Real/Bool/Int/String)。</summary>
        private static object ConvertLike(object oldVal, string text)
        {
            if (oldVal is bool) return bool.Parse(text.Trim());
            if (oldVal is double) return double.Parse(text.Trim(), CultureInfo.InvariantCulture);
            if (oldVal is float) return float.Parse(text.Trim(), CultureInfo.InvariantCulture);
            if (oldVal is decimal) return decimal.Parse(text.Trim(), CultureInfo.InvariantCulture);
            if (oldVal is int) return int.Parse(text.Trim(), CultureInfo.InvariantCulture);
            if (oldVal is long) return long.Parse(text.Trim(), CultureInfo.InvariantCulture);
            if (oldVal is short) return short.Parse(text.Trim(), CultureInfo.InvariantCulture);
            if (oldVal is byte) return byte.Parse(text.Trim(), CultureInfo.InvariantCulture);
            if (oldVal is uint) return uint.Parse(text.Trim(), CultureInfo.InvariantCulture);
            return text;
        }

        // 探测 1) 工艺对象能否用 API 创建(组合的 Create 方法) 2) PhysicalQuantity 的枚举值(找"照明")
        public string ProbeTechObjectCreate(string plcName)
        {
            var plc = FindAllPlc(plcName)[0];
            var lines = new List<string>();

            var grp = plc.GetType().GetProperty("TechnologicalObjectGroup")?.GetValue(plc, null);
            var comp = grp?.GetType().GetProperty("TechnologicalObjects")?.GetValue(grp, null);
            if (comp == null) { lines.Add("(拿不到 TechnologicalObjects)"); return Json.Obj(new Dictionary<string, string> { { "probe", Json.StrArr(lines) } }); }

            lines.Add("组合类型: " + comp.GetType().FullName);
            // 先列出已有 TO 的 OfSystemLibElement / 版本 —— Create 需要它们
            var en0 = comp as System.Collections.IEnumerable;
            if (en0 != null)
            {
                foreach (var x in en0)
                {
                    if (x == null) continue;
                    var xt = x.GetType();
                    string nm = "?", ele = "?", ver = "?";
                    try { nm = xt.GetProperty("Name")?.GetValue(x, null)?.ToString() ?? "?"; } catch { }
                    try { ele = xt.GetProperty("OfSystemLibElement")?.GetValue(x, null)?.ToString() ?? "?"; } catch { }
                    try { ver = xt.GetProperty("OfSystemLibVersion")?.GetValue(x, null)?.ToString() ?? "?"; } catch { }
                    lines.Add("已有TO: " + nm + "  OfSystemLibElement=" + ele + "  OfSystemLibVersion=" + ver);
                    // 是否有 Delete
                    var del = xt.GetMethod("Delete");
                    lines.Add("    Delete 方法: " + (del == null ? "无" : "有"));
                }
            }
            lines.Add("=== 组合的公开方法(找 Create/Delete) ===");
            foreach (var m in comp.GetType().GetMethods())
            {
                if (m.IsSpecialName) continue;
                if (m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) < 0 &&
                    m.Name.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) < 0 &&
                    m.Name.IndexOf("Find", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var ps = m.GetParameters();
                var pdesc = string.Join(", ", Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name));
                lines.Add("  " + m.Name + "(" + pdesc + ")");
                if (pdesc.Length < 200)
                {
                    foreach (var p in ps)
                    {
                        if (!p.ParameterType.IsEnum) continue;
                        lines.Add("      枚举 " + p.ParameterType.Name + ": " + string.Join(" | ", Enum.GetNames(p.ParameterType)));
                    }
                }
            }

            // PhysicalQuantity 枚举
            var to = FindTechObject(plc, "PID_Compact_2");
            if (to != null)
            {
                var en = TechParams(to);
                if (en != null)
                {
                    foreach (var p in en)
                    {
                        string n = "?";
                        try { n = p.GetType().GetProperty("Name")?.GetValue(p, null)?.ToString() ?? "?"; } catch { }
                        if (n != "PhysicalQuantity" && n != "PhysicalUnit" && n != "_Config.OutputSelect" && n != "Mode") continue;
                        var v = p.GetType().GetProperty("Value")?.GetValue(p, null);
                        lines.Add("=== 参数 " + n + " ===");
                        if (v == null) { lines.Add("  值为 null"); continue; }
                        var vt = v.GetType();
                        lines.Add("  值类型: " + vt.FullName + "  值=" + v);
                        if (vt.IsEnum)
                        {
                            foreach (var nm in Enum.GetNames(vt))
                            {
                                object ev = Enum.Parse(vt, nm);
                                lines.Add("    " + ((int)Convert.ToInt32(ev)) + " = " + nm);
                            }
                        }
                        else
                        {
                            // 可能是 int 但底层有枚举: 尝试找同名字段
                            lines.Add("  (不是枚举, 是 " + vt.Name + "; 取值靠试)");
                        }
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(无输出)");
            return Json.Obj(new Dictionary<string, string> { { "probe", Json.StrArr(lines) } });
        }

        /// <summary>创建工艺对象(如 PID_Compact)。element 见 OfSystemLibElement, 版本见 OfSystemLibVersion。</summary>
        public string CreateTechObject(string plcName, string name, string element, string version)
        {
            var plc = FindAllPlc(plcName)[0];
            var grp = plc.GetType().GetProperty("TechnologicalObjectGroup")?.GetValue(plc, null);
            var comp = grp?.GetType().GetProperty("TechnologicalObjects")?.GetValue(grp, null);
            if (comp == null) throw new TiaException("TO_NO_CONTAINER", "拿不到工艺对象容器");

            if (FindTechObject(plc, name) != null)
                throw new TiaException("TO_EXISTS", "工艺对象已存在: " + name);

            var create = comp.GetType().GetMethod("Create", new[] { typeof(string), typeof(string), typeof(Version) });
            if (create == null)
                throw new TiaException("TO_CREATE_UNSUPPORTED", "该组合上没有 Create(string,string,Version)");

            try
            {
                create.Invoke(comp, new object[] { name, element, Version.Parse(version) });
            }
            catch (TiaException) { throw; }
            catch (Exception ex)
            {
                throw new TiaException("TO_CREATE_FAILED", "创建工艺对象失败: " + ex.Message);
            }

            var to = FindTechObject(plc, name);
            if (to == null) throw new TiaException("TO_CREATE_FAILED", "创建后找不到工艺对象: " + name);
            var tt = to.GetType();
            return Json.Obj(new Dictionary<string, string>
            {
                { "name", Json.Str(name) },
                { "element", Json.Str(SafeString(() => tt.GetProperty("OfSystemLibElement")?.GetValue(to, null)?.ToString())) },
                { "version", Json.Str(SafeString(() => tt.GetProperty("OfSystemLibVersion")?.GetValue(to, null)?.ToString())) },
                { "created", Json.Bool(true) }
            });
        }

        /// <summary>批量设工艺对象参数, spec 形如 "A=1;B=2"。逐个读原值 -> 设 -> 读回。</summary>
        public string SetTechParams(string plcName, string toName, string spec, bool dryRun)
        {
            var plc = FindAllPlc(plcName)[0];
            var to = FindTechObject(plc, toName);
            if (to == null) throw new TiaException("TO_NOT_FOUND", "找不到工艺对象: " + toName);

            var report = new List<string>();
            var failed = new List<string>();
            int changed = 0;

            foreach (var pair in spec.Split(';'))
            {
                var s = pair.Trim();
                if (s.Length == 0) continue;
                int eq = s.IndexOf('=');
                if (eq <= 0) { failed.Add("格式错误: " + s); continue; }
                string pname = s.Substring(0, eq).Trim();
                string pval = s.Substring(eq + 1).Trim();

                object target = null;
                var en = TechParams(to);
                if (en != null)
                {
                    foreach (var p in en)
                    {
                        if (p == null) continue;
                        string n = "?";
                        try { n = p.GetType().GetProperty("Name")?.GetValue(p, null)?.ToString() ?? "?"; } catch { }
                        if (n == pname) { target = p; break; }
                    }
                }
                if (target == null) { failed.Add(pname + ": 参数不存在"); continue; }

                try
                {
                    var t = target.GetType();
                    var valProp = t.GetProperty("Value");
                    if (valProp == null) { failed.Add(pname + ": 无 Value 属性"); continue; }
                    object oldVal = valProp.GetValue(target, null);
                    object newVal = ConvertLike(oldVal, pval);
                    if (dryRun)
                    {
                        report.Add(pname + ": " + oldVal + " -> " + newVal + " (dry_run)");
                        continue;
                    }
                    if (!valProp.CanWrite) { failed.Add(pname + ": Value 不可写"); continue; }
                    valProp.SetValue(target, newVal, null);
                    object back = valProp.GetValue(target, null);
                    bool ok = back != null && back.ToString() == newVal?.ToString();
                    report.Add(pname + ": " + oldVal + " -> " + aOrB(newVal, back) + (ok ? "  ✓" : "  ✗读回不一致"));
                    if (ok) changed++; else failed.Add(pname + ": 读回不一致(=" + back + ")");
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    var ie = ex.InnerException;
                    int guard = 0;
                    while (ie != null && guard++ < 5) { msg += " <- " + ie.Message; ie = ie.InnerException; }
                    failed.Add(pname + ": " + msg);
                }
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "to", Json.Str(toName) },
                { "changed", Json.Num(changed) },
                { "report", Json.StrArr(report) },
                { "failed", Json.StrArr(failed) },
                { "dryRun", Json.Bool(dryRun) }
            });
        }

        /// <summary>把"设成的新值"和"读回值"合成一个简洁展示。</summary>
        private static string aOrB(object setV, object readV)
        {
            string a = setV?.ToString() ?? "null";
            string b = readV?.ToString() ?? "null";
            return a == b ? a : a + "(读回 " + b + ")";
        }

        /// <summary>删除工艺对象(可回滚; 若被块引用会失败)。</summary>
        public string DeleteTechObject(string plcName, string toName)
        {
            var plc = FindAllPlc(plcName)[0];
            var to = FindTechObject(plc, toName);
            if (to == null) throw new TiaException("TO_NOT_FOUND", "找不到工艺对象: " + toName);
            var del = to.GetType().GetMethod("Delete");
            if (del == null) throw new TiaException("TO_DELETE_UNSUPPORTED", "该工艺对象没有 Delete()");
            try { del.Invoke(to, null); }
            catch (Exception ex) { throw new TiaException("TO_DELETE_FAILED", "删除工艺对象失败: " + ex.Message); }
            if (FindTechObject(plc, toName) != null)
                throw new TiaException("TO_DELETE_FAILED", "删除后仍能查到: " + toName);
            return Json.Obj(new Dictionary<string, string>
            {
                { "name", Json.Str(toName) },
                { "deleted", Json.Bool(true) }
            });
        }

        // 探测硬件: 扩展模块插槽(GetPlugLocations) + CPU 启动模式相关属性。
        public string ProbeHardware(string plcName)
        {
            var lines = new List<string>();
            foreach (var device in _project.Devices)
            {
                lines.Add("### 设备 " + SafeName(() => device.Name));
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var nm = SafeName(() => item.Name);
                    string tid = "", pos = "";
                    try { tid = item.TypeIdentifier ?? ""; } catch { }
                    try { pos = item.PositionNumber.ToString(); } catch { }
                    lines.Add("  DeviceItem: " + nm + " | 类型=" + tid + " | 位号=" + pos);

                    // 可用插槽
                    try
                    {
                        var locs = item.GetPlugLocations();
                        int n = 0;
                        foreach (var l in locs)
                        {
                            n++;
                            if (n > 10) { lines.Add("      ...更多插槽省略"); break; }
                            var parts = new List<string>();
                            foreach (var p in l.GetType().GetProperties())
                            {
                                string v;
                                try { v = p.GetValue(l, null)?.ToString() ?? "(null)"; } catch { continue; }
                                parts.Add(p.Name + "=" + v);
                            }
                            lines.Add("      插槽" + n + ": " + string.Join(", ", parts));
                        }
                        if (n == 0) lines.Add("      (没有可用插槽)");
                    }
                    catch (Exception ex) { lines.Add("      GetPlugLocations 失败: " + ex.Message); }

                    // CPU 启动模式等候选属性
                    if (nm == "PLC_1")
                    {
                        lines.Add("      --- 启动/循环相关属性探测 ---");
                        foreach (var cand in new[]
                        {
                            "StartupMode", "StartupType", "WarmStart", "ColdRestart", "ColdStart",
                            "RetentiveMemory", "StartupObjectNumbers", "MinCycleTime", "CycleTime",
                            "CycleMonitoringTime", "OperatingMode", "RunStopMode", "StartupBehavior",
                            "RestartMode", "KeepRetentiveMemory"
                        })
                        {
                            try
                            {
                                var v = item.GetAttribute(cand);
                                lines.Add("        " + cand + " = " + (v ?? "(null)"));
                            }
                            catch (Exception ex)
                            {
                                string msg = ex.Message;
                                if (msg.Length > 90) msg = msg.Substring(0, 90) + "...";
                                lines.Add("        " + cand + " => " + msg);
                            }
                        }
                    }
                }
            }
            if (lines.Count == 0) lines.Add("(无设备)");
            return Json.Obj(new Dictionary<string, string> { { "hw", Json.StrArr(lines) } });
        }

        // 搜 Siemens.Engineering 程序集里名字含 Startup/WarmStart/ColdStart 的类型,
        // 以及 Device 上的 Export/Import/CAx 方法 —— 判断启动参数是否可达。
        public string ProbeStartupApi(string plcName)
        {
            var lines = new List<string>();
            var seen = new HashSet<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an;
                try { an = asm.GetName().Name ?? ""; } catch { continue; }
                if (!an.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var ty in types)
                {
                    string fn;
                    try { fn = ty.FullName ?? ""; } catch { continue; }
                    if (fn.Length == 0) continue;
                    if (fn.IndexOf("Startup", StringComparison.OrdinalIgnoreCase) < 0 &&
                        fn.IndexOf("WarmStart", StringComparison.OrdinalIgnoreCase) < 0 &&
                        fn.IndexOf("ColdStart", StringComparison.OrdinalIgnoreCase) < 0 &&
                        fn.IndexOf("Restart", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!seen.Add(fn)) continue;
                    lines.Add("类型: " + fn);
                }
            }
            if (lines.Count == 0) lines.Add("(Siemens 程序集里没有 Startup/WarmStart/Restart 类型)");

            // Device 上的 Export/Import/CAx
            foreach (var device in _project.Devices)
            {
                lines.Add("### Device: " + SafeName(() => device.Name));
                foreach (var m in device.GetType().GetMethods())
                {
                    if (m.IsSpecialName) continue;
                    bool hit = m.Name.IndexOf("Export", StringComparison.OrdinalIgnoreCase) >= 0
                            || m.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0
                            || m.Name.IndexOf("Cax", StringComparison.OrdinalIgnoreCase) >= 0
                            || m.Name.IndexOf("Aml", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                    var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                    lines.Add("  方法 " + m.Name + "(" + ps + ")");
                }
            }
            return Json.Obj(new Dictionary<string, string> { { "probe", Json.StrArr(lines) } });
        }

        // 从 Siemens 程序集的类型名里挖"隐藏属性名": GetAttributeInfos 没列出来,
        // 但类型存在说明属性可能存在。用类型名当属性名逐个试 GetAttribute。
        public string ProbeHiddenAttrs(string plcName, string keywordsCsv)
        {
            var lines = new List<string>();
            var kws = (keywordsCsv ?? "Startup,ClockMemory,SystemMemory,Retentive,Cycle")
                      .Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

            var candidates = new List<string>();
            var seen = new HashSet<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an;
                try { an = asm.GetName().Name ?? ""; } catch { continue; }
                if (!an.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var ty in types)
                {
                    string nm;
                    try { nm = ty.Name ?? ""; } catch { continue; }
                    if (nm.Length == 0) continue;
                    bool hit = false;
                    foreach (var k in kws)
                        if (nm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    if (!hit) continue;
                    if (seen.Add(nm)) candidates.Add(nm);
                }
            }

            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var nm = SafeName(() => item.Name);
                    if (nm != "PLC_1") continue;
                    lines.Add("### 在 PLC_1 上试 " + candidates.Count + " 个候选属性名");
                    foreach (var c in candidates)
                    {
                        try
                        {
                            var v = item.GetAttribute(c);
                            lines.Add("  ✓ " + c + " = " + (v ?? "(null)") + "   [" + (v?.GetType().Name ?? "null") + "]");
                        }
                        catch (Exception ex)
                        {
                            // "is not supported by type" = 名字不对; 其它错误 = 属性存在但读不了(更有价值)
                            var m = ex.Message ?? "";
                            if (m.IndexOf("is not supported by type", StringComparison.OrdinalIgnoreCase) < 0)
                                lines.Add("  ? " + c + " => " + (m.Length > 120 ? m.Substring(0, 120) : m));
                        }
                    }
                }
            }
            lines.Add("(候选名: " + string.Join(", ", candidates) + ")");
            return Json.Obj(new Dictionary<string, string> { { "hidden", Json.StrArr(lines) } });
        }

        // 反查: 找出哪些类的属性/字段用了名字含 keyword 的类型 —— 直接定位"属性宿主"。
        public string ProbeTypeUsage(string keywordsCsv)
        {
            var kws = (keywordsCsv ?? "Startup,ClockMemory,SystemMemory")
                      .Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            var lines = new List<string>();
            var asms = new List<Assembly>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an;
                try { an = asm.GetName().Name ?? ""; } catch { continue; }
                if (an.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)) asms.Add(asm);
            }

            // 1) 收集目标类型全名
            var targets = new HashSet<string>();
            foreach (var asm in asms)
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var ty in ts)
                {
                    string nm;
                    try { nm = ty.Name ?? ""; } catch { continue; }
                    bool hit = false;
                    foreach (var k in kws)
                        if (nm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    if (hit) targets.Add(ty.FullName ?? nm);
                }
            }

            // 2) 反查谁用了这些类型
            int found = 0;
            foreach (var asm in asms)
            {
                Type[] ts;
                try { ts = asm.GetTypes(); } catch { continue; }
                foreach (var ty in ts)
                {
                    if (ty.FullName == null || targets.Contains(ty.FullName)) continue;
                    foreach (var p in ty.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                    {
                        string ptn;
                        try { ptn = p.PropertyType.FullName ?? ""; } catch { continue; }
                        if (!targets.Contains(ptn)) continue;
                        lines.Add(ty.FullName + " . " + p.Name + " : " + p.PropertyType.Name);
                        found++;
                    }
                }
            }
            if (found == 0) lines.Add("(没有任何公开属性使用这些类型 —— 说明它们不是可访问的属性)");
            return Json.Obj(new Dictionary<string, string>
            {
                { "targets", Json.StrArr(targets.OrderBy(x => x).Take(40)) },
                { "usages", Json.StrArr(lines) }
            });
        }

        // 探测模块能否插入: 用 CanPlugNew 试各种 TypeIdentifier 写法和插槽位置(不真插)。
        public string ProbeCanPlug(string plcName, string orderNumber)
        {
            var lines = new List<string>();
            DeviceItem rack = null;
            DeviceItem cpu = null;
            foreach (var device in _project.Devices)
            {
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var nm = SafeName(() => item.Name);
                    if (nm.StartsWith("Rack", StringComparison.OrdinalIgnoreCase)) rack = item;
                    if (nm == "PLC_1") cpu = item;
                }
            }
            if (rack == null) throw new TiaException("NO_RACK", "找不到机架");

            // 方法签名
            foreach (var host in new[] { rack, cpu })
            {
                if (host == null) continue;
                lines.Add("### " + SafeName(() => host.Name));
                foreach (var m in host.GetType().GetMethods())
                {
                    if (m.IsSpecialName) continue;
                    if (m.Name.IndexOf("Plug", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var ps = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                    lines.Add("   " + m.Name + "(" + ps + ") => " + m.ReturnType.Name);
                }
            }

            // 候选 TypeIdentifier
            var bases = new List<string>();
            var clean = orderNumber.Replace(" ", "");
            bases.Add(orderNumber); bases.Add(clean);
            var vers = new[] { "", "/V1.0", "/V2.0", "/V3.0", "/V4.0", "/V4.1", "/V4.2" };
            var variants = new List<string>();
            foreach (var b in bases)
                foreach (var v in vers)
                    variants.Add("OrderNumber:" + b + v);

            var positions = new[] { 2, 3, 4, 5, 101, 102, 103 };
            lines.Add("### 试插 " + variants.Count + " 种写法 x " + positions.Length + " 个位置");
            foreach (var host in new[] { rack, cpu })
            {
                if (host == null) continue;
                var hostName = SafeName(() => host.Name);
                foreach (var v in variants)
                {
                    foreach (var pos in positions)
                    {
                        try
                        {
                            if (host.CanPlugNew(v, "MCP探测", pos))
                                lines.Add("  ✓ " + hostName + " 可插: " + v + " @ " + pos);
                        }
                        catch { }
                    }
                }
            }
            if (lines.Count <= 3) lines.Add("  (没有可用的写法/位置组合)");
            return Json.Obj(new Dictionary<string, string> { { "probe", Json.StrArr(lines) } });
        }

        // 真正插入一个模块到机架。typeIdentifier 形如 OrderNumber:6ES7 234-4HE32-0XB0/V2.0
        public string PlugModule(string plcName, string typeIdentifier, string name, int position)
        {
            DeviceItem rack = null;
            foreach (var device in _project.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                    if (SafeName(() => item.Name).StartsWith("Rack", StringComparison.OrdinalIgnoreCase)) rack = item;
            if (rack == null) throw new TiaException("NO_RACK", "找不到机架");

            if (!rack.CanPlugNew(typeIdentifier, name, position))
            {
                var posList = new List<string>();
                foreach (var loc in rack.GetPlugLocations())
                    posList.Add(loc.PositionNumber.ToString());
                throw new TiaException("PLUG_NOT_ALLOWED",
                    "该位置不允许插入: " + typeIdentifier + " @ " + position +
                    "。当前可用位置: " + string.Join(", ", posList));
            }

            DeviceItem plugged;
            try { plugged = rack.PlugNew(typeIdentifier, name, position); }
            catch (Exception ex) { throw new TiaException("PLUG_FAILED", "插入失败: " + ex.Message); }

            return Json.Obj(new Dictionary<string, string>
            {
                { "name", Json.Str(SafeName(() => plugged.Name)) },
                { "typeIdentifier", Json.Str(SafeString(() => plugged.TypeIdentifier)) },
                { "position", Json.Num(position) }
            });
        }

        // 递归遍历某个 DeviceItem 的子树, 打印每层的全部属性(找通道级的硬件参数)。
        public string ProbeItemTree(string plcName, string itemName)
        {
            var lines = new List<string>();
            DeviceItem root = null;
            foreach (var device in _project.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                    if (SafeName(() => item.Name) == itemName) root = item;
            if (root == null) throw new TiaException("ITEM_NOT_FOUND", "找不到 DeviceItem: " + itemName);

            WalkItem(root, 0, lines, 6);
            if (lines.Count == 0) lines.Add("(无输出)");
            return Json.Obj(new Dictionary<string, string> { { "tree", Json.StrArr(lines) } });
        }

        private void WalkItem(DeviceItem item, int depth, List<string> lines, int maxDepth)
        {
            if (depth > maxDepth) return;
            string indent = new string(' ', depth * 2);
            var nm = SafeName(() => item.Name);
            string tid = SafeString(() => item.TypeIdentifier);
            string pos = "";
            try { pos = item.PositionNumber.ToString(); } catch { }
            lines.Add(indent + "▸ " + nm + "  [" + tid + "] 位号=" + pos);

            try
            {
                var infos = item.GetAttributeInfos();
                foreach (var info in infos)
                {
                    string an = "?";
                    try { an = info.GetType().GetProperty("Name")?.GetValue(info, null)?.ToString() ?? "?"; } catch { }
                    // 只打印非通用属性(过滤掉每个模块都有的那批)
                    if (an == "Author" || an == "Comment" || an == "CommentML" || an == "Container"
                        || an == "IsBuiltIn" || an == "IsPlugged" || an == "Items" || an == "Name"
                        || an == "PositionNumber" || an == "TypeIdentifier" || an == "Classification") continue;
                    object av = null;
                    try { av = item.GetAttribute(an); } catch (Exception ex) { av = "(读失败:" + ex.Message + ")"; }
                    lines.Add(indent + "    " + an + " = " + (av ?? "(null)"));
                }
            }
            catch (Exception ex) { lines.Add(indent + "    GetAttributeInfos 失败: " + ex.Message); }

            try
            {
                foreach (DeviceItem child in item.DeviceItems)
                    WalkItem(child, depth + 1, lines, maxDepth);
            }
            catch { }
        }

        // 拔掉一个模块(删除 DeviceItem)。
        public string UnplugModule(string plcName, string name)
        {
            DeviceItem target = null;
            foreach (var device in _project.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                    if (SafeName(() => item.Name) == name) target = item;
            if (target == null) throw new TiaException("ITEM_NOT_FOUND", "找不到模块: " + name);

            var del = target.GetType().GetMethod("Delete");
            if (del == null) throw new TiaException("DELETE_UNSUPPORTED", "该 DeviceItem 没有 Delete()");
            try { del.Invoke(target, null); }
            catch (Exception ex) { throw new TiaException("DELETE_FAILED", "删除模块失败: " + ex.Message); }

            // 读回确认
            bool still = false;
            foreach (var device in _project.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                    if (SafeName(() => item.Name) == name) still = true;
            if (still) throw new TiaException("DELETE_FAILED", "删除后仍存在: " + name);

            return Json.Obj(new Dictionary<string, string>
            {
                { "name", Json.Str(name) },
                { "deleted", Json.Bool(true) }
            });
        }

        // 读写设备/模块的通用属性(DeviceItem 属性层)。dryRun 只读。
        public string SetDeviceAttr(string plcName, string itemName, string attr, string valueText, bool dryRun)
        {
            DeviceItem target = null;
            foreach (var device in _project.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                    if (SafeName(() => item.Name) == itemName) target = item;
            if (target == null) throw new TiaException("ITEM_NOT_FOUND", "找不到设备项: " + itemName);

            object oldVal = null;
            try { oldVal = target.GetAttribute(attr); }
            catch (Exception ex) { throw new TiaException("ATTR_NOT_FOUND", "属性不存在: " + attr + " (" + ex.Message + ")"); }

            object newVal = ConvertLike(oldVal, valueText);
            if (dryRun)
            {
                return Json.Obj(new Dictionary<string, string>
                {
                    { "item", Json.Str(itemName) },
                    { "attr", Json.Str(attr) },
                    { "oldValue", Json.Str(oldVal?.ToString() ?? "(null)") },
                    { "wouldSet", Json.Str(newVal?.ToString() ?? "(null)") },
                    { "dryRun", Json.Bool(true) }
                });
            }

            try { target.SetAttribute(attr, newVal); }
            catch (Exception ex) { throw new TiaException("SET_FAILED", "写属性失败: " + ex.Message); }

            object back = null;
            try { back = target.GetAttribute(attr); } catch { }
            return Json.Obj(new Dictionary<string, string>
            {
                { "item", Json.Str(itemName) },
                { "attr", Json.Str(attr) },
                { "oldValue", Json.Str(oldVal?.ToString() ?? "(null)") },
                { "setTo", Json.Str(newVal?.ToString() ?? "(null)") },
                { "readBack", Json.Str(back?.ToString() ?? "(null)") },
                { "ok", Json.Bool(back != null && back.ToString() == (newVal?.ToString() ?? "")) }
            });
        }

        public string ExportBlock(string blockName, string targetPath, string plcName)
        {
            var plc = FindAllPlc(plcName)[0];
            PlcBlock found = null;
            foreach (var b in EnumerateAllBlocks(plc.BlockGroup))
            {
                try { if (SafeName(() => b.Name) == blockName) { found = b; break; } } catch { }
            }
            // 实例 DB / 系统块(如 IEC_Timer_0_DB)不在主 BlockGroup 下, 在别的组合里。
            // 强类型路径不好猜, 这里遍历 PlcSoftware 的所有组合, 逐个找 Blocks 集合。
            if (found == null)
            {
                foreach (var prop in plc.GetType().GetProperties())
                {
                    object grp = null;
                    try { grp = prop.GetValue(plc, null); } catch { continue; }
                    if (grp == null) continue;
                    try
                    {
                        var en = grp as System.Collections.IEnumerable;
                        if (en == null) continue;
                        foreach (var sg in en)
                        {
                            if (sg == null) continue;
                            var blocksProp = sg.GetType().GetProperty("Blocks");
                            if (blocksProp == null) continue;
                            var bs = blocksProp.GetValue(sg, null) as System.Collections.IEnumerable;
                            if (bs == null) continue;
                            foreach (PlcBlock b in bs)
                            {
                                try { if (SafeName(() => b.Name) == blockName) { found = b; break; } } catch { }
                            }
                            if (found != null) break;
                        }
                    }
                    catch { }
                    if (found != null) break;
                }
            }
            if (found == null)
                throw new TiaException("BLOCK_NOT_FOUND", "在 PLC '" + SafeName(() => plc.Name) + "' 中找不到块: " + blockName);

            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            try
            {
                if (File.Exists(targetPath))
                    throw new TiaException("EXPORT_FILE_EXISTS",
                        "目标文件已存在，博途不会覆盖。请先删除或换一个输出路径: " + targetPath);
                found.Export(new FileInfo(targetPath), ExportOptions.WithDefaults);
            }
            catch (TiaException)
            {
                // 关键: 必须放在通用 catch 之前。否则上面抛的 EXPORT_FILE_EXISTS 会被下面的
                // catch(Exception) 重新包成 EXPORT_FAILED, 具体错误码就丢了。
                throw;
            }
            catch (Exception ex)
            {
                // Common real-world cause: the block is mid-edit in the TIA editor and therefore
                // not in a consistent state, which TIA refuses to export.
                if (ex.Message.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new TiaException("EXPORT_INCONSISTENT_BLOCK",
                        "块当前处于不一致状态，无法导出。通常原因：正在博途编辑器里修改该块且尚未定稿保存。" +
                        "请先在博途中完成修改并保存，若仍失败请先执行一次'编译全部块'让状态回到一致，然后重试。原始错误: " + ex.Message);
                throw new TiaException("EXPORT_FAILED", "导出块失败: " + ex.Message);
            }

            var info = new FileInfo(targetPath);
            if (!info.Exists)
                throw new TiaException("EXPORT_FAILED", "导出后未生成文件: " + targetPath);

            return Json.Obj(new Dictionary<string, string>
            {
                { "block", Json.Str(SafeName(() => found.Name)) },
                { "type", Json.Str(BlockTypeName(found)) },
                { "lang", Json.Str(SafeLang(found)) },
                { "path", Json.Str(info.FullName) },
                { "bytes", Json.Num(info.Length) }
            });
        }

        // Flatten all blocks under a block system group (used by export lookup).
        private static IEnumerable<PlcBlock> EnumerateAllBlocks(PlcBlockSystemGroup group)
        {
            if (group == null) yield break;
            foreach (PlcBlock b in group.Blocks) yield return b;
            foreach (var g in group.Groups)
            {
                if (g == null) continue;
                foreach (var b in EnumerateAllBlocksUser(g)) yield return b;
            }
        }

        private static IEnumerable<PlcBlock> EnumerateAllBlocksUser(PlcBlockUserGroup group)
        {
            if (group == null) yield break;
            foreach (PlcBlock b in group.Blocks) yield return b;
            foreach (var g in group.Groups)
            {
                if (g == null) continue;
                foreach (var b in EnumerateAllBlocksUser(g)) yield return b;
            }
        }

        // ============ 5. compile_plc ============
        public string CompilePlc(string plcName)
        {
            var plc = FindAllPlc(plcName)[0];

            CompilerResult result = null;
            long ms;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var provider = plc as IEngineeringServiceProvider;
                if (provider == null)
                    throw new TiaException("COMPILE_NOT_COMPILABLE", "PlcSoftware 不支持 IEngineeringServiceProvider");
                var compilable = provider.GetService<ICompilable>();
                if (compilable == null)
                    throw new TiaException("COMPILE_NOT_COMPILABLE", "无法从 PlcSoftware 获取 ICompilable 服务");
                result = compilable.Compile();
                sw.Stop();
                ms = sw.ElapsedMilliseconds;
            }
            catch (TiaException) { throw; }
            catch (Exception ex)
            {
                throw new TiaException("COMPILE_FAILED", "编译调用失败: " + ex.Message);
            }

            if (result == null)
                throw new TiaException("COMPILE_FAILED", "编译返回了空结果");

            int errorCount = 0, warningCount = 0;
            try { errorCount = result.ErrorCount; } catch { }
            try { warningCount = result.WarningCount; } catch { }
            string state = "Unknown";
            try { state = MapState(result.State); } catch { }

            var errors = new List<string>();
            var warnings = new List<string>();
            var infos = new List<string>();
            try
            {
                FlattenMessages(result.Messages, errors, warnings, infos);
            }
            catch (Exception ex)
            {
                infos.Add("(诊断读取失败: " + ex.Message + ")");
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "state", Json.Str(state) },
                { "errorCount", Json.Num(errorCount) },
                { "warningCount", Json.Num(warningCount) },
                { "durationMs", Json.Num(ms) },
                { "errors", Json.StrArr(errors) },
                { "warnings", Json.StrArr(warnings) },
                { "infos", Json.StrArr(infos) }
            });
        }

        private static string MapState(CompilerResultState s)
        {
            switch (s)
            {
                case CompilerResultState.Success: return "Successful";
                case CompilerResultState.Warning: return "WithWarnings";
                case CompilerResultState.Error: return "WithErrors";
                default: return s.ToString();
            }
        }

        private void FlattenMessages(
            IEnumerable<CompilerResultMessage> messages,
            List<string> errors, List<string> warnings, List<string> infos)
        {
            foreach (var msg in messages)
            {
                try
                {
                    string desc = SafeString(() => msg.Description);
                    string severity = ReadMessageSeverity(msg);
                    string path = ReadMessagePath(msg);

                    string line = (string.IsNullOrEmpty(path) ? "" : path + " | ") + desc;
                    if (severity == "Error") errors.Add(line);
                    else if (severity == "Warning") warnings.Add(line);
                    else infos.Add(line);

                    var children = SafeChildren(msg);
                    if (children != null)
                    {
                        FlattenMessages(children, errors, warnings, infos);
                    }
                }
                catch { }
            }
        }

        private static string ReadMessageSeverity(CompilerResultMessage msg)
        {
            try
            {
                var prop = msg.GetType().GetProperty("State");
                if (prop == null) return "Information";
                var v = prop.GetValue(msg, null)?.ToString();
                if (v == null) return "Information";
                if (v.Contains("Error")) return "Error";
                if (v.Contains("Warning")) return "Warning";
                return "Information";
            }
            catch { return "Information"; }
        }

        private static string ReadMessagePath(CompilerResultMessage msg)
        {
            try
            {
                var prop = msg.GetType().GetProperty("Path");
                if (prop == null) return "";
                var v = prop.GetValue(msg, null);
                return v?.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static IEnumerable<CompilerResultMessage> SafeChildren(CompilerResultMessage msg)
        {
            try
            {
                var prop = msg.GetType().GetProperty("Messages");
                if (prop == null) return null;
                return prop.GetValue(msg, null) as IEnumerable<CompilerResultMessage>;
            }
            catch { return null; }
        }

        // ---------- instance DB (needed before a CALL can bind to an FB) ----------
        // TIA's UI creates the instance DB automatically when you drop a CALL, but an XML
        // import cannot: the <Instance> reference must already resolve, otherwise the
        // compiler reports "Missing instance DB" / "block call was invalid".
        public string EnsureInstanceDb(string dbName, string fbName, string plcName, bool dryRun)
        {
            var plc = FindAllPlc(plcName)[0];

            // already there?
            foreach (var b in EnumerateAllBlocks(plc.BlockGroup))
            {
                try { if (SafeName(() => b.Name) == dbName) return Pack("已存在", null); } catch { }
            }

            // verify the FB exists -- but system FBs (IEC_TIMER / IEC_COUNTER / ...) do NOT live in
            // the project's BlockGroup, so a miss here is only a warning, not a hard error.
            bool fbFound = false;
            foreach (var b in EnumerateAllBlocks(plc.BlockGroup))
            {
                try { if (SafeName(() => b.Name) == fbName) { fbFound = true; break; } } catch { }
            }
            bool looksSystem = fbName.StartsWith("IEC_", StringComparison.OrdinalIgnoreCase)
                            || fbName.StartsWith("IEC", StringComparison.OrdinalIgnoreCase);
            if (!fbFound && !looksSystem)
                throw new TiaException("FB_NOT_FOUND", "找不到功能块: " + fbName
                    + "。若要建定时器/计数器的实例 DB, 请把 --fb 传成系统 FB 名(如 IEC_TIMER / IEC_COUNTER)。");

            if (dryRun) return Pack("(dry_run 将创建)", null);

            // TIA requires an explicit number when auto-numbering is off; pick the next free one.
            int next = 1;
            try
            {
                var used = new HashSet<int>();
                foreach (var b in EnumerateAllBlocks(plc.BlockGroup))
                {
                    try { used.Add(SafeNumber(b)); } catch { }
                }
                while (used.Contains(next)) next++;
            }
            catch { }

            try
            {
                plc.BlockGroup.Blocks.CreateInstanceDB(dbName, false, next, fbName);
            }
            catch (Exception ex)
            {
                throw new TiaException("IDB_CREATE_FAILED",
                    "创建实例 DB 失败: " + ex.Message
                  + "。提示: 实例 DB 通常在「系统块 > 程序资源」下, 名称如 <FB名>_1_DB。");
            }
            return Pack("已创建", "块号=" + next);
        }

        // (removed) EnsureTypedDb -- CreateDataBlock does not exist on PlcBlockComposition in V21.
        // Timer/counter instance DBs are created with EnsureInstanceDb using the SYSTEM FB name
        // (IEC_TIMER / IEC_COUNTER / IEC_TON / ...), which is what TIA itself does when you drag
        // a timer into the editor.

        private string Pack(string status, string extra)
        {
            var d = new Dictionary<string, string> { { "status", Json.Str(status) } };
            if (extra != null) d.Add("detail", Json.Str(extra));
            return Json.Obj(d);
        }

        // ---------- watch table helper (for PLCSIM verification) ----------
        // Creates a watch table and opens it in the editor, pre-filled with the given
        // tag names. Lets the user verify simulation behaviour without manual setup.
        public string EnsureWatchTable(string tableName, string tagsCsv, string plcName, bool showInEditor)
        {
            var plc = FindAllPlc(plcName)[0];

            var watchTables = plc.WatchAndForceTableGroup.WatchTables;
            Siemens.Engineering.SW.WatchAndForceTables.PlcWatchTable table = null;
            try { table = watchTables.Find(tableName); } catch { }
            bool created = false;
            if (table == null)
            {
                table = watchTables.Create(tableName);
                created = true;
            }

            var wanted = tagsCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

            var added = new List<string>();

            // The public Openness docs do not declare the element type of PlcWatchTable.Entries
            // nor its Create() signature, so resolve both at runtime instead of binding at
            // compile time. Entries also contain comment rows that have no Name property.
            object entriesObj = null;
            try { entriesObj = table.GetType().GetProperty("Entries").GetValue(table, null); }
            catch { }

            if (entriesObj == null)
            {
                if (showInEditor) { try { table.ShowInEditor(); } catch { } }
                return Json.Obj(new Dictionary<string, string>
                {
                    { "plc", Json.Str(SafeName(() => plc.Name)) },
                    { "table", Json.Str(tableName) },
                    { "tableCreated", Json.Bool(created) },
                    { "entries", Json.StrArr(new List<string> { "(Entries 集合不可访问)" }) },
                    { "openedInEditor", Json.Bool(showInEditor) },
                    { "note", Json.Str("监控表已建好并在编辑器中打开, 但该集合的 API 未在公开文档中声明, 无法自动添加变量。请把变量手动拖进监控表。") }
                });
            }

            // already present?
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var en = entriesObj as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (var e in en)
                    {
                        if (e == null) continue;
                        var pi = e.GetType().GetProperty("Name");
                        if (pi == null) continue;
                        var nm = pi.GetValue(e, null) as string;
                        if (!string.IsNullOrEmpty(nm)) present.Add(nm);
                    }
                }
            }
            catch { }

            // find a Create(string) on the collection
            var createM = entriesObj.GetType().GetMethods()
                               .FirstOrDefault(m => m.Name == "Create"
                                   && m.GetParameters().Length == 1
                                   && m.GetParameters()[0].ParameterType == typeof(string));

            foreach (var t in wanted)
            {
                if (present.Contains(t)) { added.Add(t + " (已存在)"); continue; }
                if (createM == null) { added.Add(t + " (无可用 Create 签名)"); continue; }
                try
                {
                    createM.Invoke(entriesObj, new object[] { t });
                    added.Add(t);
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    added.Add(t + " (失败: " + inner.Message + ")");
                }
            }

            if (showInEditor)
            {
                try { table.ShowInEditor(); } catch { }
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "plc", Json.Str(SafeName(() => plc.Name)) },
                { "table", Json.Str(tableName) },
                { "tableCreated", Json.Bool(created) },
                { "entries", Json.StrArr(added) },
                { "openedInEditor", Json.Bool(showInEditor) }
            });
        }

        // ---------- download API discovery (read-only) ----------
        // TIA V21 exposes DownloadProvider.Download(IConfiguration, ...), but the docs
        // declare IConfiguration only as an interface with no public factory. Probe the runtime
        // to see what is actually reachable, so we can tell the user definitively whether
        // "download to PLCSIM" can be automated or must be done in the TIA UI.
        public string ProbeDownloadApi(string plcName)
        {
            var plc = FindAllPlc(plcName)[0];
            var report = new List<string>();

            // 1) Can we get a DownloadProvider service from the software container?
            object provider = null;
            try
            {
                var svc = plc.GetService<Siemens.Engineering.Download.DownloadProvider>();
                if (svc != null) { provider = svc; report.Add("DownloadProvider: 可获取"); }
                else report.Add("DownloadProvider: GetService 返回 null");
            }
            catch (Exception ex) { report.Add("DownloadProvider: 不可用 - " + ex.GetType().Name + ": " + ex.Message); }

            if (provider != null)
            {
                // 2) What public members does it expose?
                var names = new List<string>();
                foreach (var m in provider.GetType().GetMethods())
                {
                    if (!m.IsPublic || m.DeclaringType == typeof(object)) continue;
                    var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                    names.Add(m.Name + "(" + ps + ")");
                }
                report.Add("公开方法: " + (names.Count == 0 ? "(无)" : string.Join("; ", names.Distinct().Take(12))));

                // 3) Where could an IConfiguration come from? List public members mentioning
                //    Configuration on the portal/project level.
                var cfgMembers = new List<string>();
                foreach (object t in new object[] { _portal, _project })
                {
                    if (t == null) continue;
                    foreach (var p in t.GetType().GetProperties())
                        if (p.Name.IndexOf("Configuration", StringComparison.OrdinalIgnoreCase) >= 0)
                            cfgMembers.Add(p.Name);
                    foreach (var m in t.GetType().GetMethods())
                        if (m.IsPublic && m.Name.IndexOf("Configuration", StringComparison.OrdinalIgnoreCase) >= 0)
                            cfgMembers.Add(m.Name + "()");
                }
                report.Add("Configuration 相关成员: " + (cfgMembers.Distinct().Count() == 0
                    ? "(未找到公开入口, IConfiguration 无法由外部构造)"
                    : string.Join(", ", cfgMembers.Distinct().Take(15))));
            }

            return Json.Obj(new Dictionary<string, string>
            {
                { "plc", Json.Str(SafeName(() => plc.Name)) },
                { "report", Json.StrArr(report) },
                { "conclusion", Json.Str(
                    "PLCSIM 下载需要 DownloadProvider + 一个 IConfiguration(目标设备配置)。"
                  + "Openness 公开文档只把 IConfiguration 声明为接口, 没有工厂方法, "
                  + "因此目标设备配置通常只能由博途 UI 的\"选择目标设备\"对话框产生。"
                  + "结论: 下载到 PLCSIM 这一步需在博途界面手动完成(右键 PLC -> 下载到设备 -> PLCSIM)。") }
            });
        }

        // ============ 6. plcsim (V21 standard = Electron app without process-side API) ============
        public string PlcSim(string action, string tagName, string value, string plcName)
        {
            var exe = FindPlcSimExe();
            var running = IsPlcSimRunning();

            switch ((action ?? "").ToLowerInvariant())
            {
                case "start":
                    return Json.Obj(new Dictionary<string, string>
                    {
                        { "tag", Json.Null }, { "value", Json.Null },
                        { "note", Json.Str(BuildPlcSimGuide("启动", exe, running)) },
                        { "plcsimExe", exe == null ? Json.Null : Json.Str(exe) },
                        { "plcsimRunning", Json.Bool(running) }
                    });
                case "stop":
                    return Json.Obj(new Dictionary<string, string>
                    {
                        { "tag", Json.Null }, { "value", Json.Null },
                        { "note", Json.Str(BuildPlcSimGuide("停止", exe, running)) },
                        { "plcsimExe", exe == null ? Json.Null : Json.Str(exe) },
                        { "plcsimRunning", Json.Bool(running) }
                    });
                case "read":
                case "write":
                    throw new TiaException("PLCSIM_NOT_SUPPORTED",
                        "PLCSIM V21 标准版没有公开的进程外变量读写 API(它是独立的 Electron 桌面应用)。" +
                        "本机检测: PLCSIM " + (running ? "运行中" : "未运行") +
                        (exe == null ? "，且未找到 S7PLCSIMV21.exe" : "，程序位于 " + exe) + "。" +
                        "可行替代: 1) 在博途里加一张\"监控表\"(Watch Table), 在线强制/监控 %I0.0~%Q0.1;" +
                        " 2) 安装 PLCSIM Advanced, 它提供 REST API + S7 协议, 可被程序直接读写变量。");
                default:
                    throw new TiaException("PLCSIM_BAD_ACTION", "不支持的 action: " + action);
            }
        }

        // 定位 PLCSIM V21 的可执行文件(博途装在 D 盘)
        private static string FindPlcSimExe()
        {
            var candidates = new[]
            {
                @"D:\Program Files\Siemens\Automation\PLCSIM_V21\S7PLCSIMV21.exe",
                @"C:\Program Files\Siemens\Automation\PLCSIM_V21\S7PLCSIMV21.exe"
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        private static string BuildPlcSimGuide(string verb, string exe, bool running)
        {
            var status = running ? "运行中" : "未运行";
            if (verb == "启动")
            {
                return "PLCSIM V21 标准版是独立 Electron 应用, 没有官方进程外启动 API, 所以本工具不能替你启动它。"
                     + "当前状态: " + status + "。"
                     + (exe == null
                        ? "未在常见路径找到 S7PLCSIMV21.exe。"
                        : "请手动运行: " + exe)
                     + " 启动后回到博途: 右键 PLC_1 -> \"下载到设备\"(或工具栏的\"下载\"图标) -> "
                     + "选择 PLCSIM 虚拟接口 -> 搜索到设备 -> 加载。下载完成后即可在线监控变量。";
            }
            return "PLCSIM V21 标准版没有进程外停止 API。当前状态: " + status + "。"
                 + "如需停止: 在 PLCSIM 应用界面点击 Stop, 或在博途中断开/停止仿真。";
        }

        private static bool IsPlcSimRunning()
        {
            try
            {
                return System.Diagnostics.Process.GetProcessesByName("S7PLCSIMV21").Length > 0;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var p = _portal;
            _portal = null;
            try { p?.Dispose(); } catch { }
        }

        // ---------- safe helpers ----------
        private static string SafeName<T>(Func<T> getter)
        {
            try { var v = getter(); return v?.ToString() ?? ""; }
            catch { return ""; }
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter() ?? ""; }
            catch { return ""; }
        }
    }
}