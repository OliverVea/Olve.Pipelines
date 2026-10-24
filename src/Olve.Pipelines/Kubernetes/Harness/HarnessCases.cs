using System.Text.Json;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>The <c>OLVE_CASES</c> value: a JSON array of select case conditions (<c>["target.prod.kind=ssh"]</c>).</summary>
public static class HarnessCases
{
    public static string ToJson(IReadOnlyList<string> cases)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var c in cases) w.WriteStringValue(c);
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
