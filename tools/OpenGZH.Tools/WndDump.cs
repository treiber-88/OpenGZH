using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Wnd;

namespace OpenGZH.Tools;

/// <summary>Prints a .wnd layout as an indented tree: type, name, rectangle and first image.</summary>
static class WndDump
{
    public static int Run(VirtualFileSystem fileSystem, string[] args)
    {
        foreach (var root in WndFile.Parse(fileSystem.ReadAllText(args[0])))
            Print(root, 0);
        return 0;
    }

    static void Print(WndWindow w, int depth)
    {
        string image = w.EnabledData(0) is { HasImage: true } d ? d.Image : "";
        string hidden = w.Status.Contains("HIDDEN") ? " HIDDEN" : "";
        string color = w.EnabledData(0) is { } c ? $"rgba({c.Color.X * 255:0},{c.Color.Y * 255:0},{c.Color.Z * 255:0},{c.Color.W * 255:0})" : "";
        Console.WriteLine($"{new string(' ', depth * 2)}{w.Type} {w.Name.Split(':').Last()} [{w.X},{w.Y} {w.Width}x{w.Height}] {image}{hidden} {w.Text} draw={w.DrawCallback} status={string.Join('+', w.Status)} {color}");
        foreach (var child in w.Children)
            Print(child, depth + 1);
    }
}
