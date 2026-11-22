using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CurseTorrent.App;

/// <summary>Bounds untrusted text shown in native dialogs without scrolling.</summary>
internal static class DialogText
{
    internal static string SingleLine(string text, int maximum = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 2);
        var result = new StringBuilder(Math.Min(maximum, text.Length));
        bool pendingSpace = false;
        bool shortened = false;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character)) { pendingSpace = result.Length > 0; continue; }
            if (pendingSpace)
            {
                if (result.Length == maximum) { shortened = true; break; }
                result.Append(' ');
                pendingSpace = false;
            }
            if (result.Length == maximum) { shortened = true; break; }
            result.Append(character);
        }
        return Limit(result.ToString(), maximum, shortened);
    }

    internal static string Summary(string text)
    {
        const int maximumLines = 6;
        var lines = new List<string>(maximumLines);
        using var reader = new StringReader(text);
        string? line;
        while (lines.Count < maximumLines && (line = reader.ReadLine()) is not null)
            lines.Add(SingleLine(line));
        if (reader.ReadLine() is not null && lines.Count > 0)
            lines[^1] = Limit(lines[^1], 100, true);
        return Limit(string.Join("\n", lines), 600, false);
    }

    internal static string StartupSummary(IReadOnlyList<string> warnings)
    {
        var result = new StringBuilder($"Сообщений при запуске: {warnings.Count}.\n\n");
        for (int i = 0; i < Math.Min(3, warnings.Count); i++)
            result.AppendLine($"{i + 1}. {SingleLine(warnings[i], 140)}");
        if (warnings.Count > 3) result.AppendLine($"И ещё {warnings.Count - 3}.");
        result.Append("Полный список — в журнале приложения app.log.");
        return result.ToString();
    }

    private static string Limit(string value, int maximum, bool shortened)
    {
        if (!shortened && value.Length <= maximum) return value;
        string prefix = value[..Math.Min(maximum - 1, value.Length)].TrimEnd();
        if (prefix.Length > 0 && char.IsHighSurrogate(prefix[^1])) prefix = prefix[..^1];
        return prefix + "…";
    }
}
