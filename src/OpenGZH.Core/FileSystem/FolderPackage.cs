namespace OpenGZH.Core.FileSystem;

/// <summary>
/// A folder on disk treated as a package. Used for loose mod files and the original "Data" folders.
/// An optional mount prefix places the folder's contents under a virtual directory,
/// e.g. folder "content/zerohour/Data" with prefix "Data" exposes "Data/Cursors/...".
/// </summary>
public sealed class FolderPackage : IPackage
{
    readonly string root;
    readonly Dictionary<string, string> files = new(PathUtil.Comparer);

    public string Name { get; }

    public FolderPackage(string folder, string mountPrefix = "")
    {
        string prefix = PathUtil.Normalize(mountPrefix).TrimEnd('/');
        if (prefix.Length > 0)
            prefix += "/";

        root = Path.GetFullPath(folder);
        Name = root;

        if (!System.IO.Directory.Exists(root))
            return;

        foreach (string full in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = prefix + PathUtil.Normalize(Path.GetRelativePath(root, full));
            files.TryAdd(relative, full);
        }
    }

    public IEnumerable<string> FilePaths => files.Keys;

    public bool Contains(string path) => files.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!files.TryGetValue(PathUtil.Normalize(path), out string? full))
            throw new FileNotFoundException($"{path} is not in {Name}.");
        return File.OpenRead(full);
    }

    public void Dispose() { }
}
