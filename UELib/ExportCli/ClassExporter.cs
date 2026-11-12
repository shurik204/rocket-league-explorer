using System.Text;
using UELib.Core;

namespace UELib.ExportCli;

/// <summary>
/// Writes one <c>.uc</c> file per <see cref="UClass"/> below an output directory.
/// Handles name sanitizing, duplicate names and the optional group-path layout.
/// </summary>
internal sealed class ClassExporter
{
    private const string Extension = ".uc";

    private readonly string _rootDirectory;
    private readonly bool _treeLayout;
    private readonly Encoding _encoding;
    private readonly HashSet<string> _claimedPaths = new(StringComparer.OrdinalIgnoreCase);

    public ClassExporter(string rootDirectory, bool treeLayout, Encoding encoding)
    {
        _rootDirectory = rootDirectory;
        _treeLayout = treeLayout;
        _encoding = encoding;
    }

    /// <summary>
    /// Decompiles <paramref name="cls"/> and writes it to disk.
    /// </summary>
    /// <returns><c>true</c> when the class was exported; on failure <paramref name="error"/> holds the exception message.</returns>
    public bool TryExport(UClass cls, out string filePath, out string error)
    {
        filePath = string.Empty;
        error = string.Empty;

        string source;
        try
        {
            source = cls.Decompile() ?? string.Empty;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        try
        {
            filePath = AllocatePath(cls);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, source, _encoding);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private string AllocatePath(UClass cls)
    {
        string directory = _rootDirectory;
        if (_treeLayout)
        {
            string groupPath = GetGroupPath(cls);
            if (groupPath.Length != 0)
            {
                directory = Path.Combine(_rootDirectory, groupPath);
            }
        }

        string className = SanitizeFileName(cls.Name.ToString());
        string candidate = Path.Combine(directory, className + Extension);
        for (int suffix = 2; !_claimedPaths.Add(candidate); ++suffix)
        {
            candidate = Path.Combine(directory, $"{className}_{suffix}{Extension}");
        }

        return candidate;
    }

    /// <summary>
    /// Builds the group directory ("A.B") a top-level class lives in. Cooked packages
    /// almost always have a single top-level group, so this is normally empty.
    /// </summary>
    private static string GetGroupPath(UClass cls)
    {
        var groups = new List<string>();
        UPackage? rootPackage = cls.Package.RootPackage;
        for (UObject? outer = cls.Outer; outer != null; outer = outer.Outer)
        {
            // UObjects created from export-table entries point at the synthetic
            // root package object; that must not become part of the directory.
            if (ReferenceEquals(outer, rootPackage) || ReferenceEquals(outer.Name, null))
            {
                break;
            }

            groups.Add(SanitizeFileName(outer.Name.ToString()));
        }

        groups.Reverse();
        return string.Join(Path.DirectorySeparatorChar, groups);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unnamed";
        }

        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            // Path.GetInvalidFileNameChars() is platform-specific (e.g. ':' is
            // legal on Linux but not on Windows), so also reject the Windows set
            // explicitly to keep exports portable between machines.
            builder.Append(c switch
            {
                '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => '_',
                _ => char.IsControl(c) ? '_' : c,
            });
        }

        string sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            return "Unnamed";
        }

        return sanitized;
    }
}
