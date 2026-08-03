using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// 초경량 JSON 파서. JsonUtility가 못 읽는 딕셔너리(tags 등)를 다루기 위해 사용.
/// 반환 타입: Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / double / bool / null
/// </summary>
public static class MiniJson
{
    public static object Parse(string json)
    {
        int i = 0;
        return ParseValue(json, ref i);
    }

    static object ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObject(s, ref i);
            case '[': return ParseArray(s, ref i);
            case '"': return ParseString(s, ref i);
            case 't': i += 4; return true;    // true
            case 'f': i += 5; return false;   // false
            case 'n': i += 4; return null;    // null
            default:  return ParseNumber(s, ref i);
        }
    }

    static Dictionary<string, object> ParseObject(string s, ref int i)
    {
        var dict = new Dictionary<string, object>();
        i++; // '{'
        SkipWs(s, ref i);
        if (s[i] == '}') { i++; return dict; }
        while (true)
        {
            SkipWs(s, ref i);
            string key = ParseString(s, ref i);
            SkipWs(s, ref i);
            i++; // ':'
            dict[key] = ParseValue(s, ref i);
            SkipWs(s, ref i);
            if (s[i] == ',') { i++; continue; }
            i++; // '}'
            return dict;
        }
    }

    static List<object> ParseArray(string s, ref int i)
    {
        var list = new List<object>();
        i++; // '['
        SkipWs(s, ref i);
        if (s[i] == ']') { i++; return list; }
        while (true)
        {
            list.Add(ParseValue(s, ref i));
            SkipWs(s, ref i);
            if (s[i] == ',') { i++; continue; }
            i++; // ']'
            return list;
        }
    }

    static string ParseString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++; // '"'
        while (true)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }

            char e = s[i++];
            switch (e)
            {
                case '"':  sb.Append('"');  break;
                case '\\': sb.Append('\\'); break;
                case '/':  sb.Append('/');  break;
                case 'b':  sb.Append('\b'); break;
                case 'f':  sb.Append('\f'); break;
                case 'n':  sb.Append('\n'); break;
                case 'r':  sb.Append('\r'); break;
                case 't':  sb.Append('\t'); break;
                case 'u':
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                    i += 4;
                    break;
            }
        }
    }

    static object ParseNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                i++;
            else break;
        }
        return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }
}
