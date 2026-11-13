using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TiaEngine
{
    /// <summary>
    /// 内置自测 —— 不连接博途, 只测纯逻辑(块名扫描 / SCL 解析 / 编码校验 / 参数解析 / JSON)。
    /// 用于锁定已修过的 bug, 防止回归。
    /// 运行: TiaEngine.exe --mode self_test
    /// 信封: result = { passed, failed, failures:[...], allOk }
    /// </summary>
    public static class SelfTest
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> _failures = new List<string>();

        public static bool Run(out string summaryJson)
        {
            _passed = 0;
            _failed = 0;
            _failures.Clear();

            var dir = Path.Combine(Path.GetTempPath(), "tia_selftest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                TestScanXmlBlockNames(dir);
                TestScanSclDeclarations(dir);
                TestSclEncoding(dir);
                TestAddressOverlap();
                TestArgs();
                TestJson();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            summaryJson = Json.Obj(new Dictionary<string, string>
            {
                { "passed",  Json.Num(_passed) },
                { "failed",  Json.Num(_failed) },
                { "failures", Json.Arr(_failures.ConvertAll(Json.Str)) },
                { "allOk",   Json.Bool(_failed == 0) }
            });
            return _failed == 0;
        }

        private static void Check(string name, bool cond, string detail = "")
        {
            if (cond) _passed++;
            else { _failed++; _failures.Add(name + (detail.Length > 0 ? " — " + detail : "")); }
        }

        private static bool DoesNotThrow(Action a)
        {
            try { a(); return true; }
            catch { return false; }
        }

        private static void CheckThrows(string name, Action action, string expectCode)
        {
            try
            {
                action();
                _failed++; _failures.Add(name + " — 期望抛 " + expectCode + " 但没抛");
            }
            catch (TiaException ex)
            {
                if (ex.Code == expectCode) _passed++;
                else { _failed++; _failures.Add(name + " — 期望 " + expectCode + " 实抛 " + ex.Code); }
            }
            catch (Exception ex)
            {
                _failed++; _failures.Add(name + " — 期望 " + expectCode + " 实抛 " + ex.GetType().Name);
            }
        }

        // 锁定 BUG-2: ScanXmlBlockNames 必须能读 <AttributeList><Name> 子元素里的块名
        private static void TestScanXmlBlockNames(string dir)
        {
            var xml = Path.Combine(dir, "blk.xml");
            File.WriteAllText(xml,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Document>\n" +
                "  <SW.Blocks.OB ID=\"0\"><AttributeList><Name>Main</Name></AttributeList>\n" +
                "    <ObjectList><SW.Blocks.CompileUnit ID=\"3\" CompositionName=\"CompileUnits\">" +
                "<AttributeList><NetworkSource/><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>" +
                "</SW.Blocks.CompileUnit></ObjectList>\n" +
                "  </SW.Blocks.OB>\n" +
                "</Document>\n",
                new UTF8Encoding(false));

            var names = TiaSession.ScanXmlBlockNames(xml);
            Check("ScanXmlBlockNames 读子元素块名", names.Count == 1 && names[0] == "Main",
                "实际: [" + string.Join(",", names) + "]");
        }

        // 锁定 BUG-4: 多块 SCL 不能解析出 "FUNCTION_BLOCK" 幽灵块
        private static void TestScanSclDeclarations(string dir)
        {
            var scl = Path.Combine(dir, "two.scl");
            var content =
                "FUNCTION_BLOCK \"块A\"\nVAR_INPUT\n  启动 : Bool;\nEND_VAR\nBEGIN\n  启动 := TRUE;\nEND_FUNCTION_BLOCK\n\n" +
                "FUNCTION_BLOCK \"块B\"\nVAR_INPUT\n  停止 : Bool;\nEND_VAR\nBEGIN\n  停止 := FALSE;\nEND_FUNCTION_BLOCK\n";
            File.WriteAllText(scl, content, Encoding.GetEncoding(936));

            var names = TiaSession.ScanSclDeclarations(scl);
            bool ok = names.Count == 2 && names.Contains("块A") && names.Contains("块B");
            Check("ScanSclDeclarations 无幽灵块", ok, "实际: [" + string.Join(",", names) + "]");
        }

        // 锁定 BUG-1: 编码校验必须放行 GBK/ASCII、拒绝 UTF-8 中文与 BOM
        private static void TestSclEncoding(string dir)
        {
            var ascii = Path.Combine(dir, "ascii.scl");
            File.WriteAllText(ascii, "FUNCTION_BLOCK \"Ascii\"\nVAR_INPUT\n x : Bool;\nEND_VAR\nBEGIN\nEND_FUNCTION_BLOCK\n", Encoding.ASCII);
            Check("编码校验放行纯 ASCII", DoesNotThrow(() => TiaSession.AssertSclEncodingUsable(ascii)));

            var gbk = Path.Combine(dir, "gbk.scl");
            File.WriteAllText(gbk, "FUNCTION_BLOCK \"我的块\"\nVAR_INPUT\n 启动 : Bool;\nEND_VAR\nBEGIN\nEND_FUNCTION_BLOCK\n", Encoding.GetEncoding(936));
            Check("编码校验放行 GBK", DoesNotThrow(() => TiaSession.AssertSclEncodingUsable(gbk)));

            var utf8 = Path.Combine(dir, "utf8.scl");
            File.WriteAllText(utf8, "FUNCTION_BLOCK \"我的块\"\nVAR_INPUT\n 启动 : Bool;\nEND_VAR\nBEGIN\nEND_FUNCTION_BLOCK\n", new UTF8Encoding(false));
            CheckThrows("UTF-8 中文被拒", () => TiaSession.AssertSclEncodingUsable(utf8), "SCL_ENCODING_MISMATCH");

            var bom = Path.Combine(dir, "bom.scl");
            File.WriteAllText(bom, "FUNCTION_BLOCK \"我的块\"\nVAR_INPUT\n 启动 : Bool;\nEND_VAR\nBEGIN\nEND_FUNCTION_BLOCK\n", new UTF8Encoding(true));
            CheckThrows("UTF-8 BOM 被拒", () => TiaSession.AssertSclEncodingUsable(bom), "SCL_ENCODING_UTF8_BOM");

            var txt = TiaSession.ReadSclText(gbk);
            Check("ReadSclText 正确解码 GBK 中文", txt.Contains("我的块"), "实际: " + txt.Replace("\n", " "));
        }

        // 锁定地址重叠 bug: %MW10 与 %MB10/%MB11/%MD10 字节重叠, 精确字符串比较抓不到
        private static void TestAddressOverlap()
        {
            Check("TryParseAddress %MW10", TiaSession.TryParseAddress("%MW10", out var a1, out var s1, out var e1)
                && a1 == "M" && s1 == 80 && e1 == 95, "s=" + s1 + " e=" + e1);
            Check("TryParseAddress %MB10", TiaSession.TryParseAddress("%MB10", out var a2, out var s2, out var e2)
                && a2 == "M" && s2 == 80 && e2 == 87, "s=" + s2 + " e=" + e2);
            Check("TryParseAddress 位地址 %I0.0", TiaSession.TryParseAddress("%I0.0", out var a3, out var s3, out var e3)
                && a3 == "I" && s3 == 0 && e3 == 0, "s=" + s3 + " e=" + e3);

            Check("重叠 %MW10 vs %MB10", TiaSession.AddressesOverlap("%MW10", "%MB10"));
            Check("重叠 %MW10 vs %MB11", TiaSession.AddressesOverlap("%MW10", "%MB11"));
            Check("重叠 %MW10 vs %MD10", TiaSession.AddressesOverlap("%MW10", "%MD10"));
            Check("不重叠 %MW10 vs %MB12", !TiaSession.AddressesOverlap("%MW10", "%MB12"));
            Check("不重叠 %MW10 vs %MD14", !TiaSession.AddressesOverlap("%MW10", "%MD14"));
            Check("不重叠 位 %I0.0 vs %I0.1", !TiaSession.AddressesOverlap("%I0.0", "%I0.1"));
            Check("不重叠 跨区 %I0.0 vs %MB0", !TiaSession.AddressesOverlap("%I0.0", "%MB0"));
            Check("重叠 %M0.0 vs %MB0", TiaSession.AddressesOverlap("%M0.0", "%MB0"));
        }

        private static void TestArgs()
        {
            Check("ReadSwitch 命中", Program.ReadSwitch(new[] { "--mode", "x", "--plc", "P1" }, "--plc") == "P1");
            Check("ReadSwitch = 形式", Program.ReadSwitch(new[] { "--mode=x", "--plc=P1" }, "--plc") == "P1");
            Check("ReadSwitch 未命中返回 null", Program.ReadSwitch(new[] { "--mode", "x" }, "--plc") == null);
            Check("ReadSwitch 大小写不敏感", Program.ReadSwitch(new[] { "--PLC", "P1" }, "--plc") == "P1");
            Check("HasFlag 命中", Program.HasFlag(new[] { "--dry-run" }, "--dry-run"));
            Check("HasFlag =true 命中", Program.HasFlag(new[] { "--dry-run=true" }, "--dry-run"));
            Check("HasFlag =false 返回 false", !Program.HasFlag(new[] { "--dry-run=false" }, "--dry-run"));
            Check("HasFlag 未命中", !Program.HasFlag(new[] { "--xml", "a.xml" }, "--dry-run"));
            Check("HasFlagValue 识别 --dry-run false", Program.HasFlagValue(new[] { "--dry-run", "false" }, "--dry-run", "false"));
        }

        private static void TestJson()
        {
            Check("Json.Num(NaN)->null", Json.Num(double.NaN) == "null", "实际: " + Json.Num(double.NaN));
            Check("Json.Num(+Inf)->null", Json.Num(double.PositiveInfinity) == "null");
            Check("Json.Num(-Inf)->null", Json.Num(double.NegativeInfinity) == "null");
            Check("Json.Num(int) 正常", Json.Num(42) == "42");
            Check("Json.Num(long) 正常", Json.Num(1234567890123L) == "1234567890123");
            Check("Json.Str 转义", Json.Str("a\"b\\c\n") == "\"a\\\"b\\\\c\\n\"");
            Check("Json.Bool", Json.Bool(true) == "true");
            Check("Json.Null", Json.Null == "null");
            // 锁定 BUG: 裸字符串数组必须自己加引号, 否则产出非法 JSON([a,b] 而非 ["a","b"])
            Check("Json.StrArr 裸字符串数组", Json.StrArr(new[] { "a", "b" }) == "[\"a\",\"b\"]",
                "实际: " + Json.StrArr(new[] { "a", "b" }));
            Check("Json.StrArr 转义引号", Json.StrArr(new[] { "a\"b" }) == "[\"a\\\"b\"]",
                "实际: " + Json.StrArr(new[] { "a\"b" }));
            Check("Json.Arr 对象数组原样", Json.Arr(new[] { "{\"x\":1}" }) == "[{\"x\":1}]");
            Check("Json.StrArr 中文", Json.StrArr(new[] { "编译完成" }) == "[\"编译完成\"]");
            Check("Json.StrArr 空数组", Json.StrArr(new string[0]) == "[]");
        }
    }
}
