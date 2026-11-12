using System.Diagnostics;
using System.Text;
using UELib.Core;

namespace UELib.ExportCli;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFatal = 1;
    private const int ExitUsage = 2;
    private const int ExitPartialFailure = 3;

    /// <summary>
    /// Captured before UELib's <c>Console</c> redirection so our own diagnostics
    /// survive <c>--quiet</c>, which only silences the library.
    /// </summary>
    private static readonly TextWriter StdErr = Console.Error;

    private static int Main(string[] args)
    {
        // UELib itself writes diagnostics to Console.Out. Redirect that to stderr
        // and keep the real stdout for the list of emitted files, so the CLI can
        // be piped into other tools without library chatter polluting the stream.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        Console.SetOut(Console.Error);

        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (CommandLineException ex)
        {
            StdErr.WriteLine($"error: {ex.Message}");
            StdErr.WriteLine("Run 'uelib-export --help' for usage.");
            return ExitUsage;
        }

        if (options.ShowHelp)
        {
            PrintUsage(stdout);
            return ExitSuccess;
        }

        if (options.PackagePath == null)
        {
            PrintUsage(StdErr);
            return ExitUsage;
        }

        // --quiet must also silence UELib's own log output (both the Console.Out
        // chatter and the Console.Error warnings it emits for package quirks),
        // not just our progress lines. Our messages go through StdErr.
        if (options.Quiet)
        {
            Console.SetError(TextWriter.Null);
            Console.SetOut(TextWriter.Null);
        }

        try
        {
            return Run(options, stdout);
        }
        catch (Exception ex)
        {
            StdErr.WriteLine($"fatal: {ex}");
            return ExitFatal;
        }
    }

    private static int Run(CommandLineOptions options, TextWriter stdout)
    {
        string packagePath = Path.GetFullPath(options.PackagePath!);
        if (!File.Exists(packagePath))
        {
            StdErr.WriteLine($"error: package not found: {packagePath}");
            return ExitUsage;
        }

        UnrealPackage.GameBuild.BuildName buildTarget = UnrealPackage.GameBuild.BuildName.Unset;
        if (!string.IsNullOrEmpty(options.BuildTarget)
            && !Enum.TryParse(options.BuildTarget, ignoreCase: true, out buildTarget))
        {
            StdErr.WriteLine(
                $"error: unknown build target '{options.BuildTarget}'. " +
                "Use a UnrealPackage.GameBuild.BuildName value such as 'RocketLeague', 'UDK' or 'UT3'.");
            return ExitUsage;
        }

        var stopwatch = Stopwatch.StartNew();
        Info(options, $"Loading {packagePath} ...");

        UnrealPackage package;
        try
        {
            package = UnrealLoader.LoadPackage(packagePath, buildTarget);
        }
        catch (Exception ex)
        {
            StdErr.WriteLine($"error: failed to load '{packagePath}': {ex.Message}");
            return ExitFatal;
        }

        using (package)
        {
            if (options.NtlPath != null && !TryLoadNativeTable(package, options, out int ntlExitCode))
            {
                return ntlExitCode;
            }

            Info(options,
                $"Build: {package.Build?.Name.ToString() ?? "Unknown"} " +
                $"(version {package.Summary.Version}, licensee {package.Summary.LicenseeVersion})");

            Info(options, "Deserializing objects ...");
            try
            {
                package.InitializePackage();
            }
            catch (Exception ex)
            {
                StdErr.WriteLine($"error: failed to initialize package: {ex}");
                return ExitFatal;
            }

            // Index this package's natives so __NFUN_NNN__ placeholders can be
            // resolved against its own UFunction declarations (and, for Rocket
            // League, the binary-extracted GNatives fallback).
            UStruct.UByteCodeDecompiler.NativeFunctionToken.IndexPackageNatives(package);

            string outputDirectory = options.OutputDirectory != null
                ? Path.GetFullPath(options.OutputDirectory)
                : Path.Combine(Environment.CurrentDirectory, package.PackageName + "_decompiled");

            try
            {
                Directory.CreateDirectory(outputDirectory);
                if (options.Clean)
                {
                    DeleteExistingScripts(outputDirectory, options);
                }
            }
            catch (Exception ex)
            {
                StdErr.WriteLine($"error: cannot prepare output directory '{outputDirectory}': {ex.Message}");
                return ExitFatal;
            }

            List<UClass> classes = package.Objects
                .OfType<UClass>()
                .Where(c => c.ExportTable != null)
                .Where(c => options.Filter == null
                            || c.Name.ToString().Contains(options.Filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.GetPath(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (classes.Count == 0)
            {
                StdErr.WriteLine(options.Filter != null
                    ? $"error: no class matches the filter '{options.Filter}'"
                    : "error: package contains no classes");
                return options.Filter != null ? ExitUsage : ExitFatal;
            }

            var exporter = new ClassExporter(outputDirectory, options.TreeLayout, options.CreateEncoding());
            int exported = 0;
            int failed = 0;

            foreach (UClass cls in classes)
            {
                if (!exporter.TryExport(cls, out string filePath, out string error))
                {
                    ++failed;
                    StdErr.WriteLine($"  ! {cls.GetPath()}: {error}");
                    continue;
                }

                ++exported;
                stdout.WriteLine(filePath);
                Info(options, $"  + {cls.GetPath()}");
            }

            stopwatch.Stop();
            string elapsed = $"{stopwatch.Elapsed.TotalSeconds:0.0}s";
            if (failed > 0)
            {
                StdErr.WriteLine(
                    $"Exported {exported}/{classes.Count} class(es) to {outputDirectory} " +
                    $"({elapsed}, {failed} failed)");
                return ExitPartialFailure;
            }

            StdErr.WriteLine($"Exported {exported} class(es) to {outputDirectory} ({elapsed})");
            return ExitSuccess;
        }
    }

    private static bool TryLoadNativeTable(UnrealPackage package, CommandLineOptions options, out int exitCode)
    {
        exitCode = ExitSuccess;
        string ntlPath = Path.GetFullPath(options.NtlPath!);
        if (!File.Exists(ntlPath))
        {
            StdErr.WriteLine($"error: native table list not found: {ntlPath}");
            exitCode = ExitUsage;
            return false;
        }

        try
        {
            using var stream = File.OpenRead(ntlPath);
            package.NTLPackage = new NativesTablePackage();
            package.NTLPackage.Deserialize(stream);
        }
        catch (Exception ex)
        {
            StdErr.WriteLine($"error: failed to read native table list '{ntlPath}': {ex.Message}");
            exitCode = ExitFatal;
            return false;
        }

        Info(options, $"Native table: {ntlPath} ({package.NTLPackage.NativeTableList.Count} entries)");
        return true;
    }

    private static void DeleteExistingScripts(string directory, CommandLineOptions options)
    {
        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.uc", SearchOption.AllDirectories))
        {
            File.Delete(file);
            ++deleted;
        }

        Info(options, $"Removed {deleted} existing .uc file(s) from {directory}");
    }

    private static void Info(CommandLineOptions options, string message)
    {
        if (!options.Quiet)
        {
            StdErr.WriteLine(message);
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine(
            """
            uelib-export - decompile UnrealScript classes from an Unreal Engine package

            Usage:
              uelib-export <package> [options]

            Arguments:
              <package>              Path to a .upk/.u/.umap package. Rocket League packages
                                     may be AES-encrypted originals (no manual decrypt step).

            Options:
              -o, --out <dir>        Output directory (default: ./<PackageName>_decompiled)
              -b, --build <name>     Force the build target, e.g. RocketLeague, UDK, UT3, UT2004
              -n, --ntl <file>       Native table list (.NTL) used to name native functions
                  --filter <text>    Only export classes whose name contains <text>
                  --tree             Mirror group paths into subdirectories instead of one folder
                  --clean            Delete existing .uc files in the output directory first
                  --encoding <name>  utf8 | utf8-bom (default) | ansi
              -q, --quiet            Suppress progress output (errors are still reported)
              -h, --help             Show this help

            Output:
              One .uc file per class is written to the output directory. Every written
              path is printed on stdout (one per line) so the result can be piped;
              progress and errors go to stderr.

            Exit codes:
              0  success
              1  fatal error (package could not be loaded/initialized)
              2  usage error
              3  some classes failed to decompile (the rest were written)

            Examples:
              uelib-export TAGame.upk
              uelib-export TAGame.upk -o ./out --filter Car_TA
              uelib-export Engine.u -b UT3 -n "NativesTableList_UT3.NTL" --encoding ansi
            """);
    }
}
