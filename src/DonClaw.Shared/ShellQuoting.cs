namespace DonClaw.Shared;

/// <summary>
/// Shell-aware argument quoting for cmd.exe and PowerShell.
/// </summary>
internal static class ShellQuoting
{
    internal static bool NeedsQuoting(string arg)
    {
        foreach (var c in arg)
        {
            if (IsShellMetachar(c))
                return true;
        }
        return false;
    }

    internal static string QuoteForShell(string arg, bool isCmd)
    {
        if (string.IsNullOrEmpty(arg))
            return isCmd ? "\"\"" : "''";

        if (!NeedsQuoting(arg))
            return arg;

        if (isCmd)
            return "\"" + arg.Replace("\"", "\"\"") + "\"";
        else
            return "'" + arg.Replace("'", "''") + "'";
    }

    internal static string FormatExecCommand(string[] argv)
    {
        return string.Join(" ", argv.Select(FormatSingleArg));

        static string FormatSingleArg(string arg)
        {
            if (arg.Length == 0) return "\"\"";
            if (!NeedsQuoting(arg)) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
    }

    private static bool IsShellMetachar(char c) => c switch
    {
        ' ' or '\t' or '"' or '\'' or
        '&' or '|' or ';' or '<' or '>' or
        '(' or ')' or '^' or '%' or '!' or
        '$' or '`' or '*' or '?' or '[' or
        ']' or '{' or '}' or '~' or
        '\n' or '\r' => true,
        _ => false
    };
}
