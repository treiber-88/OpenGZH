namespace OpenGZH.Core.FileSystem;

/// <summary>
/// A source of files: a .big archive, or a folder on disk.
/// Paths are always relative, use '/' separators and are compared case-insensitively.
/// </summary>
public interface IPackage : IDisposable
{
    /// <summary>Human readable name, used in logs and error messages.</summary>
    string Name { get; }

    /// <summary>Every file path this package contains.</summary>
    IEnumerable<string> FilePaths { get; }

    bool Contains(string path);

    /// <summary>Opens a file for reading. Throws <see cref="FileNotFoundException"/> if missing.</summary>
    Stream Open(string path);
}
