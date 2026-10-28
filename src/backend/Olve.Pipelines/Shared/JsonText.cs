using System.Text.Json;

namespace Olve.Pipelines.Shared;

internal static class JsonText
{
    /// <summary>True when <paramref name="text"/> parses as a single JSON value.</summary>
    public static bool IsWellFormed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
