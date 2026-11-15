using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CSBridge
{
    /// <summary>Minimal JSON writer (no external libs). Usage: new JsonWriter().Obj().Prop("a", 1).End().ToString()</summary>
    public class JsonWriter
    {
        private readonly StringBuilder sb = new StringBuilder();
        private readonly Stack<bool> first = new Stack<bool>();

        private void Sep()
        {
            if (first.Count > 0)
            {
                if (first.Peek()) { first.Pop(); first.Push(false); }
                else sb.Append(',');
            }
        }

        public JsonWriter Obj() { Sep(); sb.Append('{'); first.Push(true); return this; }
        public JsonWriter Arr() { Sep(); sb.Append('['); first.Push(true); return this; }
        public JsonWriter ObjIn(string key) { Key(key); sb.Append('{'); first.Push(true); return this; }
        public JsonWriter ArrIn(string key) { Key(key); sb.Append('['); first.Push(true); return this; }
        public JsonWriter EndObj() { sb.Append('}'); first.Pop(); return this; }
        public JsonWriter EndArr() { sb.Append(']'); first.Pop(); return this; }

        private void Key(string key)
        {
            Sep();
            sb.Append(Quote(key)).Append(':');
        }

        public JsonWriter Prop(string key, string v) { Sep(); sb.Append(Quote(key)).Append(':').Append(v == null ? "null" : Quote(v)); return this; }
        public JsonWriter Prop(string key, long v) { Sep(); sb.Append(Quote(key)).Append(':').Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Prop(string key, double v) { Sep(); sb.Append(Quote(key)).Append(':').Append(v.ToString("R", CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Prop(string key, bool v) { Sep(); sb.Append(Quote(key)).Append(':').Append(v ? "true" : "false"); return this; }

        /// <summary>Append an already-serialized JSON value (e.g. as array element).</summary>
        public JsonWriter Raw(string json) { Sep(); sb.Append(json); return this; }

        public override string ToString() { return sb.ToString(); }

        public static string Quote(string s)
        {
            StringBuilder o = new StringBuilder(s.Length + 2);
            o.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': o.Append("\\\""); break;
                    case '\\': o.Append("\\\\"); break;
                    case '\n': o.Append("\\n"); break;
                    case '\r': o.Append("\\r"); break;
                    case '\t': o.Append("\\t"); break;
                    default:
                        if (c < 0x20) o.Append("\\u").Append(((int)c).ToString("x4"));
                        else o.Append(c);
                        break;
                }
            }
            o.Append('"');
            return o.ToString();
        }

        public static string Error(string message)
        {
            return new JsonWriter().Obj().Prop("error", message).EndObj().ToString();
        }
    }
}
