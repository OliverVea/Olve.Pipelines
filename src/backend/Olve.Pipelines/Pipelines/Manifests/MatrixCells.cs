using System.Text;
using System.Text.RegularExpressions;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// One cell of a matrix step: its values in axis order. <see cref="Name"/> (<c>build-cli[os=linux,arch=x64]</c>) is the
/// name its attempts, logs, files, reruns and addresses use.
/// </summary>
public sealed record MatrixCell(string Step, IReadOnlyList<KeyValuePair<string, string>> Values, string Name)
{
    /// <summary>The values joined with <c> · </c> (<c>linux · x64</c>).</summary>
    public string Label => string.Join(" · ", Values.Select(v => v.Value));

    /// <summary>Compact JSON in axis order (<c>{"os":"linux","arch":"x64"}</c>), as <c>OLVE_CELL</c> carries it.</summary>
    public string Json
    {
        get
        {
            // Axis names and values are restricted to characters JSON needs no escaping for.
            var sb = new StringBuilder("{");
            foreach (var (axis, value) in Values)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append('"').Append(axis).Append("\":\"").Append(value).Append('"');
            }
            return sb.Append('}').ToString();
        }
    }
}

/// <summary>The cell grammar and the cross product of a matrix (<c>docs/design/matrix-builds.md</c>).</summary>
public static partial class MatrixCells
{
    public const int MaxAxes = 4;
    public const int MaxCells = 64;

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,30}$")]
    public static partial Regex AxisNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    public static partial Regex ValueRegex();

    private const string Pair = "[a-z][a-zA-Z0-9]{0,30}=[A-Za-z0-9][A-Za-z0-9._-]{0,62}";

    [GeneratedRegex(@"^(?<step>[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)\[(?<pairs>" + Pair + "(?:," + Pair + "){0,3})\\]$")]
    private static partial Regex CellNameRegex();

    public static string Name(string step, IEnumerable<KeyValuePair<string, string>> values)
        => $"{step}[{string.Join(',', values.Select(v => $"{v.Key}={v.Value}"))}]";

    /// <summary>Every cell of <paramref name="matrix"/>: the cross product in axis order (first axis outermost) minus the excludes.</summary>
    public static IReadOnlyList<MatrixCell> Of(string step, ManifestMatrix matrix)
    {
        IEnumerable<List<KeyValuePair<string, string>>> product = [[]];
        foreach (var axis in matrix.Axes)
            product = product.SelectMany(c => axis.Values.Select(v => new List<KeyValuePair<string, string>>(c) { new(axis.Name, v) }));
        return product
            .Where(c => !Excluded(matrix, c))
            .Select(c => new MatrixCell(step, c, Name(step, c)))
            .ToList();
    }

    /// <summary>The number of cells, without materializing a product beyond <paramref name="bound"/> (then the product itself).</summary>
    public static long Count(ManifestMatrix matrix, long bound = 1_000_000)
    {
        long product = 1;
        foreach (var axis in matrix.Axes)
        {
            product *= axis.Values.Count;
            if (product > bound) return product;
        }
        return matrix.Exclude is null or { Count: 0 } ? product : Of("x", matrix).Count;
    }

    private static bool Excluded(ManifestMatrix matrix, IReadOnlyList<KeyValuePair<string, string>> cell) =>
        matrix.Exclude?.Any(entry => entry.Count > 0 && entry.All(e => cell.Any(c => c.Key == e.Key && c.Value == e.Value))) == true;

    /// <summary>True when <paramref name="name"/> is a cell name; <paramref name="step"/> is then its matrix step.</summary>
    public static bool TryParseName(string name, out string step)
    {
        var match = CellNameRegex().Match(name);
        step = match.Success ? match.Groups["step"].Value : name;
        return match.Success;
    }

    /// <summary>The step a name belongs to: a cell's matrix step, else the name itself.</summary>
    public static string StepOf(string name) => TryParseName(name, out var step) ? step : name;
}
