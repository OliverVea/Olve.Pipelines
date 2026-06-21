using Net.Codecrete.QrCodeGenerator;

namespace Olve.Pipelines.Cli;

/// <summary>
/// Renders a QR code as text for the terminal. Two vertical modules are packed into one character
/// cell using the upper-half block (<c>▀</c>): the glyph's foreground paints the top module and its
/// background the bottom one. Colours are set explicitly (black modules on a white quiet zone) so the
/// code scans regardless of the terminal's theme — a default light-on-dark terminal would otherwise
/// invert it, which not every reader tolerates.
/// </summary>
public static class TerminalQr
{
    private const int Border = 2; // light quiet zone (modules) required around the symbol

    private static readonly char Esc = (char)0x1b;
    private static readonly string Reset = $"{Esc}[0m";
    private static readonly string DarkOnDark = $"{Esc}[30;40m"; // fg black, bg black
    private static readonly string DarkOnLight = $"{Esc}[30;107m"; // fg black, bg bright-white
    private static readonly string LightOnDark = $"{Esc}[97;40m"; // fg bright-white, bg black
    private static readonly string LightOnLight = $"{Esc}[97;107m"; // fg bright-white, bg bright-white

    /// <summary>Encodes <paramref name="text"/> and renders it; returns false if it can't be encoded.</summary>
    public static bool TryRender(string text, out string rendered)
    {
        try
        {
            var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
            rendered = Render(qr);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or DataTooLongException)
        {
            rendered = "";
            return false;
        }
    }

    private static string Render(QrCode qr)
    {
        var sb = new System.Text.StringBuilder();
        var min = -Border;
        var max = qr.Size + Border;

        for (var y = min; y < max; y += 2)
        {
            for (var x = min; x < max; x++)
            {
                var top = IsDark(qr, x, y);
                var bottom = IsDark(qr, x, y + 1);
                sb.Append((top, bottom) switch
                {
                    (true, true) => DarkOnDark,
                    (true, false) => DarkOnLight,
                    (false, true) => LightOnDark,
                    (false, false) => LightOnLight,
                });
                sb.Append('▀'); // ▀ upper half block
            }
            sb.Append(Reset).Append('\n');
        }

        return sb.ToString();
    }

    // Outside the symbol (the quiet zone) counts as light.
    private static bool IsDark(QrCode qr, int x, int y) =>
        x >= 0 && y >= 0 && x < qr.Size && y < qr.Size && qr.GetModule(x, y);
}
