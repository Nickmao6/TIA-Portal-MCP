using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TiaEngine
{
    /// <summary>
    /// Minimal JSON serialization support that avoids external library dependencies.
    /// We emit our own JSON text so we keep full control over the envelope shape sent to stdout.
    /// </summary>
    public static class Json
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Str(string s) => "\"" + Escape(s) + "\"";

        public static string Bool(bool b) => b ? "true" : "false";

        public static string Num(object n)
        {
            if (n is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (n is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (n is double d)
            {
                // NaN / ±Infinity 不是合法 JSON 数字, "R" 格式会原样输出它们;
                // 统一映射成 null, 否则会产出坏 JSON 让下游解析失败。
                if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
                return d.ToString("R", CultureInfo.InvariantCulture);
            }
            if (n is float f)
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) return "null";
                return f.ToString("R", CultureInfo.InvariantCulture);
            }
            if (n is decimal m) return m.ToString(CultureInfo.InvariantCulture);
            return n?.ToString() ?? "null";
        }

        public static string Null => "null";

        /// <summary>Build a JSON object from key/value string pairs, where value already JSON-encoded.</summary>
        public static string Obj(IEnumerable<KeyValuePair<string, string>> pairs, bool pretty = false)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            bool first = true;
            foreach (var kv in pairs)
            {
                if (!first) sb.Append(",");
                if (pretty) { sb.Append("\n  "); }
                sb.Append(Str(kv.Key)).Append(":").Append(kv.Value);
                first = false;
            }
            if (pretty && !first) sb.Append("\n");
            sb.Append("}");
            return sb.ToString();
        }

        public static string Arr(IEnumerable<string> items)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            foreach (var it in items)
            {
                if (!first) sb.Append(",");
                sb.Append(it);
                first = false;
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// 由**裸字符串**拼 JSON 数组(每个元素自动转义)。
        /// 与 Arr 的区别: Arr 假定元素已经是 JSON 文本(对象/数组), StrArr 自己负责加引号。
        /// 混用会产出非法 JSON(如 [abc,def] 而不是 ["abc","def"])。
        /// </summary>
        public static string StrArr(IEnumerable<string> items)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            foreach (var it in items)
            {
                if (!first) sb.Append(",");
                sb.Append(Str(it));
                first = false;
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}
