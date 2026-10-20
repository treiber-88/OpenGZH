namespace OpenGZH.Core.FileSystem;

/// <summary>
/// Merges many packages into one file tree.
///
/// Override rule (same as the original game): the package mounted FIRST wins.
/// Mount the mod's own folders first, then Zero Hour archives, then base Generals archives.
/// </summary>
public sealed class VirtualFileSystem : IDisposable
{
    readonly List<IPackage> packages = new();

    // Maps every known path to the package that provides it.
    readonly Dictionary<string, IPackage> index = new(PathUtil.Comparer);

    public IReadOnlyList<IPackage> Packages => packages;

    public void Mount(IPackage package)
    {
        packages.Add(package);
        foreach (string path in package.FilePaths)
            index.TryAdd(path, package);
    }

    public bool Exists(string path) => index.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (TryOpen(path, out var stream))
            return stream;
        throw new FileNotFoundException($"File not found in any mounted package: {path}");
    }

    public bool TryOpen(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Stream? stream)
    {
        if (index.TryGetValue(PathUtil.Normalize(path), out var package))
        {
            stream = package.Open(path);
            return true;
        }
        stream = null;
        return false;
    }

    public byte[] ReadAllBytes(string path)
    {
        using var stream = Open(path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public string ReadAllText(string path)
    {
        // The original data files are Windows-1252 / Latin-1 text.
        return System.Text.Encoding.Latin1.GetString(ReadAllBytes(path));
    }

    /// <summary>Which package provides a path (null if none). Useful for debugging overrides.</summary>
    public IPackage? Provider(string path) => index.GetValueOrDefault(PathUtil.Normalize(path));

    /// <summary>
    /// Lists files inside a directory, optionally recursively, whose name ends with the given extension.
    /// Results are sorted case-insensitively so load order is the same on every platform.
    /// </summary>
    public List<string> List(string directory, string extension = "", bool recursive = false)
    {
        string prefix = PathUtil.Normalize(directory).TrimEnd('/');
        if (prefix.Length > 0)
            prefix += "/";

        var results = new List<string>();
        foreach (string path in index.Keys)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!recursive && path.IndexOf('/', prefix.Length) >= 0)
                continue;
            if (extension.Length > 0 && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;
            results.Add(path);
        }

        results.Sort(PathUtil.Comparer);
        return results;
    }

    public void Dispose()
    {
        foreach (var package in packages)
            package.Dispose();
        packages.Clear();
        index.Clear();
    }
}
