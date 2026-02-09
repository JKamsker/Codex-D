using System.Text;

namespace CodexD.Utils;

internal static class TextMojibakeRepair
{
    private static readonly Lazy<Encoding> Oem850 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(850);
    });

    private static readonly Lazy<Encoding> Oem437 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437);
    });

    private static readonly Lazy<Encoding> Cp1252 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    public static string Fix(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // Most common mojibake in our logs: UTF-8 bytes decoded as CP437 -> "ÔÇÖ", "ÔÇ£", etc.
        // Also common: UTF-8 bytes decoded as CP1252 -> "â€™", "â€œ", etc.
        if (!input.Contains('Ô') && !input.Contains('â'))
        {
            return input;
        }

        var repaired = FixTriples(input, leading1: 'Ô', leading2: 'Ç', Oem850.Value);
        repaired = FixTriples(repaired, leading1: 'Γ', leading2: 'Ç', Oem437.Value);
        repaired = FixTriples(repaired, leading1: 'â', leading2: '€', Cp1252.Value);
        return repaired;
    }

    private static string FixTriples(string input, char leading1, char leading2, Encoding sourceEncoding)
    {
        if (input.Length < 3 || !input.Contains(leading1))
        {
            return input;
        }

        StringBuilder? sb = null;

        var i = 0;
        while (i < input.Length)
        {
            if (i + 2 < input.Length &&
                input[i] == leading1 &&
                input[i + 1] == leading2)
            {
                var token = input.AsSpan(i, 3).ToString();
                if (TryDecodeUtf8From(token, sourceEncoding, out var decoded))
                {
                    if (sb is null)
                    {
                        sb = new StringBuilder(input.Length);
                        if (i > 0)
                        {
                            sb.Append(input.AsSpan(0, i));
                        }
                    }
                    sb.Append(decoded);
                    i += 3;
                    continue;
                }
            }

            sb?.Append(input[i]);
            i++;
        }

        return sb?.ToString() ?? input;
    }

    private static bool TryDecodeUtf8From(string token, Encoding sourceEncoding, out string decoded)
    {
        decoded = string.Empty;

        try
        {
            var bytes = sourceEncoding.GetBytes(token);
            decoded = Encoding.UTF8.GetString(bytes);
            if (decoded.Length == 0 || decoded.Contains('\uFFFD', StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
        catch
        {
            decoded = string.Empty;
            return false;
        }
    }
}
