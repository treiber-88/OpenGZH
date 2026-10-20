namespace OpenGZH.Core.FileSystem;

public static class PathUtil
{
    /// <summary>
    /// Converts any path into the engine's canonical form: forward slashes, no leading slash.
    /// Case is preserved; lookups use <see cref="Comparer"/> to ignore it.
    /// </summary>
    public static string Normalize(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>The original game treats file names case-insensitively on every platform, so we do too.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Directory(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public static string FileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public static string WithoutExtension(string path)
    {
        string name = FileName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    public static string Extension(string path)
    {
        string name = FileName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..];
    }
}
