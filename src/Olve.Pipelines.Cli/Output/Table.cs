namespace Olve.Pipelines.Cli.Output;

/// <summary>Renders a simple left-aligned, space-padded text table (no third-party dependency).</summary>
public static class Table
{
    public static IEnumerable<string> Render(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = new int[headers.Count];
        for (var c = 0; c < headers.Count; c++)
            widths[c] = headers[c].Length;

        foreach (var row in rows)
            for (var c = 0; c < headers.Count && c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c]?.Length ?? 0);

        yield return Format(headers, widths);
        foreach (var row in rows)
            yield return Format(row, widths);
    }

    private static string Format(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new string[widths.Length];
        for (var c = 0; c < widths.Length; c++)
        {
            var value = c < cells.Count ? cells[c] ?? string.Empty : string.Empty;
            // Don't pad the last column — avoids trailing whitespace.
            parts[c] = c == widths.Length - 1 ? value : value.PadRight(widths[c]);
        }

        return string.Join("  ", parts);
    }
}
