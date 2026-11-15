using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CSBridge
{
    /// <summary>Thrown to produce a specific HTTP error status from a handler.</summary>
    public class ApiException : Exception
    {
        public readonly int Status;
        public ApiException(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>Parses a flat JSON object ({"a":1,"b":"x","c":true}) into strings. No nesting; enough for action requests.</summary>
    public static class JsonReader
    {
        public static Dictionary<string, string> ParseFlat(string json)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            int i = 0;
            Ws(json, ref i);
            if (i >= json.Length || json[i] != '{') throw new ApiException(400, "Body must be a JSON object");
            i++;
            Ws(json, ref i);
            if (i < json.Length && json[i] == '}') return result;
            while (true)
            {
                Ws(json, ref i);
                string key = Str(json, ref i);
                Ws(json, ref i);
                if (i >= json.Length || json[i] != ':') throw new ApiException(400, "Invalid JSON: expected ':'");
                i++;
                Ws(json, ref i);
                result[key] = Value(json, ref i);
                Ws(json, ref i);
                if (i >= json.Length) throw new ApiException(400, "Invalid JSON: unexpected end");
                if (json[i] == ',') { i++; continue; }
                if (json[i] == '}') return result;
                throw new ApiException(400, "Invalid JSON: expected ',' or '}'");
            }
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static string Value(string s, ref int i)
        {
            if (i >= s.Length) throw new ApiException(400, "Invalid JSON: missing value");
            if (s[i] == '"') return Str(s, ref i);
            if (s[i] == '{' || s[i] == '[') throw new ApiException(400, "Nested JSON values are not supported");
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && !char.IsWhiteSpace(s[i])) i++;
            return s.Substring(start, i - start);
        }

        private static string Str(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new ApiException(400, "Invalid JSON: expected string");
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    switch (s[i])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        default: sb.Append(s[i]); break;
                    }
                }
                else sb.Append(s[i]);
                i++;
            }
            if (i >= s.Length) throw new ApiException(400, "Invalid JSON: unterminated string");
            i++;
            return sb.ToString();
        }

        public static bool GetBool(Dictionary<string, string> d, string key)
        {
            string v;
            if (!d.TryGetValue(key, out v)) throw new ApiException(400, "Missing field: " + key);
            if (v == "true") return true;
            if (v == "false") return false;
            throw new ApiException(400, "Field '" + key + "' must be true or false");
        }

        public static int GetInt(Dictionary<string, string> d, string key, int min, int max)
        {
            string v;
            if (!d.TryGetValue(key, out v)) throw new ApiException(400, "Missing field: " + key);
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                throw new ApiException(400, "Field '" + key + "' must be an integer");
            if (n < min || n > max) throw new ApiException(400, "Field '" + key + "' must be between " + min + " and " + max);
            return n;
        }
    }
}
