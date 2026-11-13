// ildump — 读取 .NET 程序集里方法的 IL，并按顺序还原其中的字符串字面量(ldstr)。
// 目的: 找出 TIA 写 SimaticML 时 "Openbranch" / "OpenCon" / "NameCon" 的实际写入顺序,
//       从而确定并联分支的确切 XML 结构(而不是靠猜)。
//
// 用法: ildump <dll路径> [方法名关键字]
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 1)
{
    Console.Error.WriteLine("用法: ildump <dll> [方法名关键字]");
    return 1;
}

string dll = args[0];
string keyword = args.Length > 1 ? args[1] : null;

using var fs = File.OpenRead(dll);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

Console.WriteLine($"程序集: {Path.GetFileName(dll)}");
Console.WriteLine(new string('=', 70));

int hit = 0;
foreach (var th in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(th);
    var typeName = md.GetString(td.Name);
    if (keyword != null &&
        !typeName.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
        !td.GetMethods().Any(m => md.GetString(md.GetMethodDefinition(m).Name)
                                    .Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        continue;

    foreach (var mh in td.GetMethods())
    {
        var mdf = md.GetMethodDefinition(mh);
        string mname = md.GetString(mdf.Name);

        if (mdf.RelativeVirtualAddress == 0) continue;
        MethodBodyBlock body;
        try { body = pe.GetMethodBody(mdf.RelativeVirtualAddress); }
        catch { continue; }

        var il = body.GetILBytes();
        if (il == null) continue;

        // 扫 IL: 0x72 = ldstr, 后面 4 字节是 #US 堆的 token
        var strings = new List<string>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x72) continue;
            int token = BitConverter.ToInt32(il, i + 1);
            if ((token >> 24) != 0x70) continue;           // #US 堆标记 0x70
            try
            {
                var s = md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
                if (!string.IsNullOrEmpty(s)) strings.Add(s);
            }
            catch { }
            i += 4;
        }

        bool relevant = keyword == null ||
                        strings.Any(s => s.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (!relevant) continue;

        hit++;
        Console.WriteLine($"\n--- {typeName}::{mname}  ({strings.Count} 个字符串) ---");
        foreach (var s in strings)
        {
            var one = s.Replace("\r", " ").Replace("\n", " ");
            if (one.Length > 120) one = one.Substring(0, 120) + "…";
            Console.WriteLine("    \"" + one + "\"");
        }
        if (hit > 60) { Console.WriteLine("\n(过多, 截断)"); return 0; }
    }
}

Console.WriteLine($"\n命中方法数: {hit}");
return 0;