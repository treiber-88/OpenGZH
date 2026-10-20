using OpenGZH.Core.FileSystem;

namespace OpenGZH.Tools;

/// <summary>Locates the OpenGZH root folder (the one containing "mods/").</summary>
static class GameRoot
{
    public static string Find()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "mods")) && Directory.Exists(Path.Combine(dir, "src")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }
}

static class FileTools
{
    public static int List(VirtualFileSystem fileSystem, string[] args)
    {
        string directory = args.Length > 0 ? args[0] : "";
        string extension = args.Length > 1 ? args[1] : "";
        foreach (string path in fileSystem.List(directory, extension, recursive: true))
            Console.WriteLine($"{path}    [{fileSystem.Provider(path)?.Name}]");
        return 0;
    }

    public static int Extract(VirtualFileSystem fileSystem, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: extract <path> <output file>");
            return 1;
        }
        File.WriteAllBytes(args[1], fileSystem.ReadAllBytes(args[0]));
        return 0;
    }
}
