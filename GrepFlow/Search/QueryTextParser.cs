using System.Text;

namespace GrepFlow.Search;

public sealed class QueryTextParser
{
    private const string ArgumentSeparator = " -- ";

    public ParsedQuery Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ParsedQuery(string.Empty, RipgrepUserOptions.Default);

        var separator = text.IndexOf(ArgumentSeparator, StringComparison.Ordinal);
        if (separator < 0) return new ParsedQuery(text.Trim(), RipgrepUserOptions.Default);

        var pattern = text[..separator].Trim();
        var tokenized = SplitArguments(text[(separator + ArgumentSeparator.Length)..]);
        if (tokenized.Error is not null)
            return new ParsedQuery(pattern, RipgrepUserOptions.Default, tokenized.Error);

        return ParseOptions(pattern, tokenized.Arguments);
    }

    private static ParsedQuery ParseOptions(string pattern, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return new ParsedQuery(pattern, RipgrepUserOptions.Default);

        var caseMode = SearchCaseMode.Smart;
        var fixedStrings = false;
        var wordRegexp = false;
        var lineRegexp = false;
        var includeHidden = false;
        var includeIgnored = false;
        var globs = new List<string>();
        var caseInsensitiveGlobs = new List<string>();
        var types = new List<string>();
        var excludedTypes = new List<string>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "-F":
                case "--fixed-strings":
                    fixedStrings = true;
                    continue;
                case "-i":
                case "--ignore-case":
                    caseMode = SearchCaseMode.Ignore;
                    continue;
                case "-s":
                case "--case-sensitive":
                    caseMode = SearchCaseMode.Sensitive;
                    continue;
                case "-S":
                case "--smart-case":
                    caseMode = SearchCaseMode.Smart;
                    continue;
                case "-w":
                case "--word-regexp":
                    wordRegexp = true;
                    continue;
                case "-x":
                case "--line-regexp":
                    lineRegexp = true;
                    continue;
                case "--hidden":
                    includeHidden = true;
                    continue;
                case "--no-ignore":
                    includeIgnored = true;
                    continue;
            }

            if (TryReadValue(arguments, ref index, argument, "-g", "--glob", out var glob, out var globError))
            {
                if (globError is not null) return Error(pattern, globError);
                globs.Add(glob!);
                continue;
            }

            if (TryReadValue(arguments, ref index, argument, null, "--iglob", out var iglob, out var iglobError))
            {
                if (iglobError is not null) return Error(pattern, iglobError);
                caseInsensitiveGlobs.Add(iglob!);
                continue;
            }

            if (TryReadValue(arguments, ref index, argument, "-t", "--type", out var type, out var typeError))
            {
                if (typeError is not null) return Error(pattern, typeError);
                types.Add(type!);
                continue;
            }

            if (TryReadValue(arguments, ref index, argument, "-T", "--type-not", out var excludedType, out var excludedTypeError))
            {
                if (excludedTypeError is not null) return Error(pattern, excludedTypeError);
                excludedTypes.Add(excludedType!);
                continue;
            }

            var kind = argument.Length > 0 && argument[0] == '-'
                ? QueryParseErrorKind.UnsupportedOption
                : QueryParseErrorKind.UnexpectedPositionalArgument;
            return Error(pattern, new QueryParseError(kind, argument));
        }

        return new ParsedQuery(
            pattern,
            new RipgrepUserOptions(
                caseMode,
                fixedStrings,
                wordRegexp,
                lineRegexp,
                includeHidden,
                includeIgnored,
                globs.ToArray(),
                caseInsensitiveGlobs.ToArray(),
                types.ToArray(),
                excludedTypes.ToArray()));
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string argument,
        string? shortName,
        string longName,
        out string? value,
        out QueryParseError? error)
    {
        value = null;
        error = null;

        if (argument == longName || (shortName is not null && argument == shortName))
        {
            if (index + 1 >= arguments.Count
                || string.IsNullOrEmpty(arguments[index + 1])
                || arguments[index + 1][0] == '-')
            {
                error = new QueryParseError(QueryParseErrorKind.MissingOptionValue, argument);
                return true;
            }

            value = arguments[++index];
            return true;
        }

        var longPrefix = longName + "=";
        if (argument.StartsWith(longPrefix, StringComparison.Ordinal))
        {
            value = argument[longPrefix.Length..];
            if (value.Length == 0)
                error = new QueryParseError(QueryParseErrorKind.MissingOptionValue, longName);
            return true;
        }

        if (shortName is not null
            && argument.Length > shortName.Length
            && argument.StartsWith(shortName, StringComparison.Ordinal))
        {
            value = argument[shortName.Length..];
            return true;
        }

        return false;
    }

    private static ParsedQuery Error(string pattern, QueryParseError error) =>
        new(pattern, RipgrepUserOptions.Default, error);

    private static TokenizeResult SplitArguments(string text)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var tokenStarted = false;

        foreach (var symbol in text)
        {
            if (quote != '\0')
            {
                if (symbol == quote) quote = '\0';
                else current.Append(symbol);
                tokenStarted = true;
                continue;
            }

            if (symbol is '"' or '\'')
            {
                quote = symbol;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(symbol))
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(symbol);
            tokenStarted = true;
        }

        if (quote != '\0')
            return new TokenizeResult(
                arguments,
                new QueryParseError(QueryParseErrorKind.UnterminatedQuote, quote.ToString()));

        if (tokenStarted) arguments.Add(current.ToString());
        return new TokenizeResult(arguments, null);
    }

    private sealed record TokenizeResult(
        IReadOnlyList<string> Arguments,
        QueryParseError? Error);
}
