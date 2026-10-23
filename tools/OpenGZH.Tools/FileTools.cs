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

    /// <summary>texture-png &lt;path&gt; &lt;out.png&gt;: decodes a TGA/DDS texture; also writes the alpha channel as out_alpha.png.</summary>
    public static int TexturePng(VirtualFileSystem fileSystem, string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("texture-png <path> <out.png>");
            return 1;
        }
        var image = OpenGZH.Formats.Images.Image.Load(fileSystem.ReadAllBytes(args[0]), args[0]);
        OpenGZH.Formats.Images.PngWriter.Save(image, args[1]);
        var alpha = new OpenGZH.Formats.Images.Image(image.Width, image.Height);
        for (int i = 0; i < image.Width * image.Height; i++)
        {
            byte a = image.Pixels[i * 4 + 3];
            alpha.Pixels[i * 4] = alpha.Pixels[i * 4 + 1] = alpha.Pixels[i * 4 + 2] = a;
            alpha.Pixels[i * 4 + 3] = 255;
        }
        OpenGZH.Formats.Images.PngWriter.Save(alpha, Path.ChangeExtension(args[1], null) + "_alpha.png");
        Console.WriteLine($"{image.Width}x{image.Height}");
        return 0;
    }
}
