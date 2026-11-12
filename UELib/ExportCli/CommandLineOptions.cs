using System.Text;

namespace UELib.ExportCli;

internal enum ScriptEncoding
{
    Utf8,
    Utf8Bom,
    Ansi,
}

internal sealed class CommandLineException(string message) : Exception(message);

/// <summary>
/// Parsed command line for the decompiler CLI. Kept deliberately dependency-free
/// so publishing stays a single self-contained binary.
/// </summary>
internal sealed class CommandLineOptions
{
    public string? PackagePath { get; private set; }
    public string? OutputDirectory { get; private set; }
    public string? BuildTarget { get; private set; }
    public string? NtlPath { get; private set; }
    public string? Filter { get; private set; }
    public bool TreeLayout { get; private set; }
    public bool Clean { get; private set; }
    public bool Quiet { get; private set; }
    public bool ShowHelp { get; private set; }
    public ScriptEncoding Encoding { get; private set; } = ScriptEncoding.Utf8Bom;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();

        for (var i = 0; i < args.Count; ++i)
        {
            string arg = args[i];
            if (string.IsNullOrEmpty(arg))
            {
                continue;
            }

            if (arg is "-h" or "--help")
            {
                options.ShowHelp = true;
                continue;
            }

            if (arg is "-q" or "--quiet")
            {
                options.Quiet = true;
                continue;
            }

            if (arg == "--tree")
            {
                options.TreeLayout = true;
                continue;
            }

            if (arg == "--clean")
            {
                options.Clean = true;
                continue;
            }

            if (arg is "-o" or "--out")
            {
                options.OutputDirectory = TakeValue(args, ref i, arg);
                continue;
            }

            if (arg is "-b" or "--build")
            {
                options.BuildTarget = TakeValue(args, ref i, arg);
                continue;
            }

            if (arg is "-n" or "--ntl")
            {
                options.NtlPath = TakeValue(args, ref i, arg);
                continue;
            }

            if (arg == "--filter")
            {
                options.Filter = TakeValue(args, ref i, arg);
                continue;
            }

            if (arg == "--encoding")
            {
                string value = TakeValue(args, ref i, arg);
                options.Encoding = ParseEncoding(value);
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Contains('='))
            {
                int separator = arg.IndexOf('=');
                string name = arg[..separator];
                string value = arg[(separator + 1)..];
                switch (name)
                {
                    case "--out":
                        options.OutputDirectory = RequireNonEmpty(value, name);
                        break;
                    case "--build":
                        options.BuildTarget = RequireNonEmpty(value, name);
                        break;
                    case "--ntl":
                        options.NtlPath = RequireNonEmpty(value, name);
                        break;
                    case "--filter":
                        options.Filter = RequireNonEmpty(value, name);
                        break;
                    case "--encoding":
                        options.Encoding = ParseEncoding(value);
                        break;
                    default:
                        throw new CommandLineException($"unknown option '{name}'");
                }

                continue;
            }

            if (arg.StartsWith('-'))
            {
                throw new CommandLineException($"unknown option '{arg}'");
            }

            if (options.PackagePath != null)
            {
                throw new CommandLineException(
                    $"only one package can be exported at a time (got '{options.PackagePath}' and '{arg}')");
            }

            options.PackagePath = arg;
        }

        return options;
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int index, string name)
    {
        if (index + 1 >= args.Count)
        {
            throw new CommandLineException($"option '{name}' requires a value");
        }

        return RequireNonEmpty(args[++index], name);
    }

    private static string RequireNonEmpty(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandLineException($"option '{name}' requires a non-empty value");
        }

        return value;
    }

    private static ScriptEncoding ParseEncoding(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => ScriptEncoding.Utf8,
            "utf8-bom" or "utf-8-bom" or "utf8bom" => ScriptEncoding.Utf8Bom,
            "ansi" or "windows-1252" or "1252" => ScriptEncoding.Ansi,
            _ => throw new CommandLineException(
                $"unknown encoding '{value}' (expected utf8, utf8-bom or ansi)"),
        };
    }

    public Encoding CreateEncoding()
    {
        return Encoding switch
        {
            ScriptEncoding.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            ScriptEncoding.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            _ => UnrealEncoding.ANSI,
        };
    }
}
