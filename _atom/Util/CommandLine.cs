using System.Text;

namespace Atom.Util;

/// <summary>
///     Builds the argument string of a <see cref="Invex.Process.ProcessRunOptions" /> from a list of arguments, so
///     that the child process receives each argument whole.
/// </summary>
/// <remarks>
///     <para>
///         The <c>string[]</c> constructor of <see cref="Invex.Process.ProcessRunOptions" /> only joins its arguments
///         with spaces (dropping empty ones), and .NET splits that string again with the Windows rules on every
///         operating system, so an argument with a space in it reaches the child as several. Passing
///         <c>CommandLine.Join([...])</c> to the <c>string</c> constructor instead quotes each argument for those
///         rules (those of <c>CommandLineToArgvW</c>), which also leaves the logged invocation ready to paste into a
///         shell for the common cases.
///     </para>
///     <para>
///         Unlike the <c>string[]</c> constructor, an empty argument is kept and passed as an empty argument.
///     </para>
/// </remarks>
internal static class CommandLine
{
    /// <summary>
    ///     Joins <paramref name="arguments" /> into one argument string, each one quoted as needed.
    /// </summary>
    /// <param name="arguments">The arguments, one per item, unquoted.</param>
    /// <returns>An argument string that .NET splits back into exactly <paramref name="arguments" />.</returns>
    public static string Join(params IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(Quote));

    /// <summary>
    ///     Quotes one argument for the Windows command-line rules, or returns it as is when it needs no quoting.
    /// </summary>
    /// <param name="argument">The argument, unquoted.</param>
    /// <returns>
    ///     <paramref name="argument" /> unchanged when it is non-empty with no whitespace or double quote in it;
    ///     otherwise wrapped in double quotes, with each embedded double quote and the backslashes before it, and the
    ///     backslashes before the closing quote, escaped.
    /// </returns>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"'))
            return argument;

        var quoted = new StringBuilder(argument.Length + 2);
        quoted.Append('"');

        var backslashes = 0;

        foreach (var c in argument)
            switch (c)
            {
                case '\\':
                    backslashes++;

                    break;

                case '"':
                    // Backslashes are only special before a double quote: each one doubles, then one escapes the quote
                    quoted.Append('\\', (backslashes * 2) + 1);
                    quoted.Append('"');
                    backslashes = 0;

                    break;

                default:
                    quoted.Append('\\', backslashes);
                    quoted.Append(c);
                    backslashes = 0;

                    break;
            }

        // The backslashes before the closing quote double, so that it still closes the argument
        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');

        return quoted.ToString();
    }
}
