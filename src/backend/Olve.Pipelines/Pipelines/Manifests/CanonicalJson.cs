using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// Byte-exact port of the SDK's canonical JSON (<c>src/sdk/src/util/canonical.ts</c>):
/// <c>JSON.stringify(canonicalize(v), null, 2) + "\n"</c> — object keys sorted, arrays in order,
/// 2-space indent, ES2019 string escaping and JS number formatting. Since describe prints exactly this,
/// the hash of a describe output is the hash of its bytes.
/// </summary>
/// <remarks>
/// Input must be a clean document (no duplicate keys, finite numbers, decodable strings) — ManifestParser
/// guarantees that for anything taken from a parsed manifest.
/// </remarks>
public static class CanonicalJson
{
    /// <summary>Canonical text of <paramref name="element"/>, with a trailing newline.</summary>
    public static string Serialize(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(sb, element, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary><c>sha256:&lt;hex&gt;</c> of the canonical text's UTF-8 bytes.</summary>
    public static string Hash(JsonElement element) => HashText(Serialize(element));

    /// <summary><c>sha256:&lt;hex&gt;</c> of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string HashText(string text) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Write(StringBuilder sb, JsonElement e, int indent)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var props = e.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
                if (props.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                props.Sort((a, b) => CompareKeys(a.Name, b.Name));
                sb.Append('{');
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, indent + 1);
                    WriteString(sb, props[i].Name);
                    sb.Append(": ");
                    Write(sb, props[i].Value, indent + 1);
                }
                NewLine(sb, indent);
                sb.Append('}');
                return;
            case JsonValueKind.Array:
                if (e.GetArrayLength() == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                var first = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent + 1);
                    Write(sb, item, indent + 1);
                }
                NewLine(sb, indent);
                sb.Append(']');
                return;
            case JsonValueKind.String:
                WriteString(sb, e.GetString()!);
                return;
            case JsonValueKind.Number:
                sb.Append(FormatNumber(e.GetDouble()));
                return;
            case JsonValueKind.True:
                sb.Append("true");
                return;
            case JsonValueKind.False:
                sb.Append("false");
                return;
            default:
                sb.Append("null");
                return;
        }
    }

    private static void NewLine(StringBuilder sb, int indent) => sb.Append('\n').Append(' ', indent * 2);

    /// <summary>
    /// JS own-property order after <c>Object.keys(v).sort()</c>: array-index keys ("0".."4294967294",
    /// canonical form) come first in numeric order, then the others by UTF-16 code units.
    /// </summary>
    internal static int CompareKeys(string a, string b)
    {
        var ai = ArrayIndex(a);
        var bi = ArrayIndex(b);
        if (ai is not null && bi is not null) return ai.Value.CompareTo(bi.Value);
        if (ai is not null) return -1;
        if (bi is not null) return 1;
        return string.CompareOrdinal(a, b);
    }

    private static uint? ArrayIndex(string key)
    {
        if (key.Length == 0 || key.Length > 10) return null;
        if (key.Length > 1 && key[0] == '0') return null;
        foreach (var c in key)
            if (c is < '0' or > '9') return null;
        var v = ulong.Parse(key, CultureInfo.InvariantCulture);
        return v <= 4294967294UL ? (uint)v : null;
    }

    /// <summary>ES2019 well-formed JSON.stringify string escaping.</summary>
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
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
        sb.Append('"');
    }

    /// <summary>ECMAScript Number::toString(x) for finite doubles.</summary>
    internal static string FormatNumber(double x)
    {
        if (x == 0) return "0"; // also -0
        if (x < 0) return "-" + FormatNumber(-x);

        // Shortest round-trip digits from .NET, re-laid out per the JS algorithm.
        var r = x.ToString("R", CultureInfo.InvariantCulture);
        var exp = 0;
        var ePos = r.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            exp = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ePos];
        }
        var dot = r.IndexOf('.');
        var intPart = dot >= 0 ? r[..dot] : r;
        var fracPart = dot >= 0 ? r[(dot + 1)..] : "";
        var digits = intPart + fracPart;
        var n = intPart.Length + exp; // value = 0.d1d2… × 10^n
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        n -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";
        var k = digits.Length;

        if (k <= n && n <= 21) return digits + new string('0', n - k);
        if (0 < n && n <= 21) return digits[..n] + "." + digits[n..];
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
        var e = n - 1;
        var sign = e < 0 ? "-" : "+";
        var mantissa = k == 1 ? digits : digits[..1] + "." + digits[1..];
        return mantissa + "e" + sign + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
    }
}
