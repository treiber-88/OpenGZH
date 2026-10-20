using OpenGZH.Core.FileSystem;
using OpenGZH.Formats.Ini;

namespace OpenGZH.Tools;

/// <summary>Parses every INI file in Data/INI and prints a summary of block types, so parser problems show up quickly.</summary>
static class IniCheck
{
    public static int Run(VirtualFileSystem fileSystem)
    {
        int failures = 0;
        var blockCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in fileSystem.List("Data/INI", ".ini", recursive: true))
        {
            try
            {
                var document = IniParser.Parse(fileSystem.ReadAllText(path), path);
                foreach (var block in document.Blocks)
                    blockCounts[block.Type] = blockCounts.GetValueOrDefault(block.Type) + 1;
            }
            catch (IniException e)
            {
                failures++;
                Console.WriteLine("FAIL " + e.Message);
            }
        }

        foreach (var (type, count) in blockCounts)
            Console.WriteLine($"{count,6}  {type}");
        Console.WriteLine(failures == 0 ? "All INI files parsed." : $"{failures} file(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
