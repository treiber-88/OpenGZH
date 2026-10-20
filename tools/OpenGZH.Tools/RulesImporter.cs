using System.Text.RegularExpressions;
using OpenGZH.Core.FileSystem;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Ini;

namespace OpenGZH.Tools;

/// <summary>
/// Converts the original Zero Hour INI data (Data/INI/**) into OpenGZH YAML rules files.
///
/// Every value from the original files is kept, using the original key names, so all balance
/// and mechanics carry over and existing Zero Hour modding knowledge still applies.
///
/// Output layout (inside mods/zh/rules):
///   defaults/*.yaml                  Data/INI/Default/*.ini (base values for every definition)
///   weapons.yaml, locomotors.yaml... one file per original INI file, named after its contents
///   &lt;faction&gt;/infantry.yaml         objects, split by faction and kind
///   &lt;faction&gt;/vehicles.yaml, aircraft.yaml, ships.yaml, structures.yaml, ...
///   ui/mapped-images/**.yaml         UI image atlases
///
/// Each file contains top-level sections such as "Objects:" or "Weapons:".
/// </summary>
static partial class RulesImporter
{
    /// <summary>Block types that hold a module (keyed by their module tag in the output).</summary>
    static readonly HashSet<string> ModuleBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draw", "Body", "Behavior", "ClientUpdate", "ClientBehavior",
    };

    /// <summary>Top-level block types whose nested blocks are an ordered list of "nuggets" (order matters).</summary>
    static readonly HashSet<string> NuggetListTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FXList", "ObjectCreationList",
    };

    /// <summary>Section names for top-level block types. Anything not listed uses the type name plus "s".</summary>
    static readonly Dictionary<string, string> SectionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Object"] = "Objects", ["ObjectReskin"] = "Objects",
        ["Armor"] = "Armor", ["Science"] = "Sciences", ["FXList"] = "FXLists",
        ["DamageFX"] = "DamageFX", ["CrateData"] = "Crates", ["Terrain"] = "TerrainTypes",
        ["MappedImage"] = "MappedImages", ["WaterSet"] = "WaterSets",
    };

    /// <summary>Output file names for the non-object INI files (by original file name, without extension).</summary>
    static readonly Dictionary<string, string> FileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Weapon"] = "weapons", ["Armor"] = "armor", ["Locomotor"] = "locomotors",
        ["Upgrade"] = "upgrades", ["Science"] = "sciences", ["SpecialPower"] = "special-powers",
        ["CommandButton"] = "command-buttons", ["CommandSet"] = "command-sets",
        ["FXList"] = "fx-lists", ["ParticleSystem"] = "particle-systems",
        ["ObjectCreationList"] = "object-creation-lists", ["PlayerTemplate"] = "factions",
        ["Crate"] = "crates", ["DamageFX"] = "damage-fx", ["Rank"] = "ranks",
        ["Campaign"] = "campaigns", ["ChallengeMode"] = "generals-challenge",
        ["Terrain"] = "terrain-types", ["Roads"] = "roads-and-bridges", ["Water"] = "water",
        ["Voice"] = "voices", ["SoundEffects"] = "sound-effects", ["Speech"] = "speech",
        ["Music"] = "music", ["Eva"] = "eva", ["AIData"] = "ai", ["GameData"] = "game-data",
        ["multiplayer"] = "multiplayer", ["CommandMap"] = "hotkeys",
        ["Animation2D"] = "animation-2d", ["GameLOD"] = "game-lod", ["GameLODPresets"] = "game-lod-presets",
    };

    /// <summary>Faction folders, by the object's "Side" value.</summary>
    static readonly Dictionary<string, string> FactionFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["America"] = "usa",
        ["AmericaAirForceGeneral"] = "usa-airforce",
        ["AmericaLaserGeneral"] = "usa-laser",
        ["AmericaSuperWeaponGeneral"] = "usa-superweapon",
        ["China"] = "china",
        ["ChinaTankGeneral"] = "china-tank",
        ["ChinaInfantryGeneral"] = "china-infantry",
        ["ChinaNukeGeneral"] = "china-nuke",
        ["GLA"] = "gla",
        ["GLADemolitionGeneral"] = "gla-demolition",
        ["GLAStealthGeneral"] = "gla-stealth",
        ["GLAToxinGeneral"] = "gla-toxin",
        ["Boss"] = "boss",
        ["Civilian"] = "civilian",
    };

    /// <summary>When an object has no Side, guess its faction from the file it came from.</summary>
    static readonly (string FilePrefix, string Side)[] FactionFromFile =
    [
        ("AirforceGeneral", "AmericaAirForceGeneral"), ("LaserGeneral", "AmericaLaserGeneral"),
        ("SuperWeaponGeneral", "AmericaSuperWeaponGeneral"), ("TankGeneral", "ChinaTankGeneral"),
        ("InfantryGeneral", "ChinaInfantryGeneral"), ("NukeGeneral", "ChinaNukeGeneral"),
        ("DemoGeneral", "GLADemolitionGeneral"), ("StealthGeneral", "GLAStealthGeneral"),
        ("ChemicalGeneral", "GLAToxinGeneral"), ("BossGeneral", "Boss"),
        ("GC_Chem", "GLAToxinGeneral"), ("GC_Slth", "GLAStealthGeneral"),
        ("America", "America"), ("China", "China"), ("GLA", "GLA"),
    ];

    sealed class ObjectEntry
    {
        public required string Name;
        public required IniBlock Block;
        public required string SourceFile;
        public string? ReskinOf;
        public string Folder = "";
        public string Category = "";
    }

    public static int Run(VirtualFileSystem fileSystem, string outputDirectory, bool force)
    {
        if (!force && Directory.Exists(outputDirectory) && Directory.EnumerateFiles(outputDirectory, "*.yaml", SearchOption.AllDirectories).Any())
        {
            Console.Error.WriteLine($"{outputDirectory} already contains rules. The YAML files are the source of truth once imported;");
            Console.Error.WriteLine("re-run with --force to overwrite them with a fresh conversion of the original INI data.");
            return 1;
        }

        var outputs = new SortedDictionary<string, RuleNode>(StringComparer.OrdinalIgnoreCase);
        var objects = new List<ObjectEntry>();
        int blockCount = 0;

        foreach (string path in fileSystem.List("Data/INI", ".ini", recursive: true))
        {
            var document = IniParser.Parse(fileSystem.ReadAllText(path), path);
            string relative = path["Data/INI/".Length..];
            bool isObjectFile = relative.StartsWith("Object/", StringComparison.OrdinalIgnoreCase);

            foreach (var block in document.Blocks)
            {
                blockCount++;
                if (isObjectFile && block.Type.Equals("Object", StringComparison.OrdinalIgnoreCase))
                {
                    objects.Add(new ObjectEntry { Name = block.Name, Block = block, SourceFile = relative });
                }
                else if (isObjectFile && block.Type.Equals("ObjectReskin", StringComparison.OrdinalIgnoreCase))
                {
                    string[] names = SplitWords(block.Name);
                    objects.Add(new ObjectEntry { Name = names[0], ReskinOf = names.ElementAtOrDefault(1), Block = block, SourceFile = relative });
                }
                else
                {
                    AddTopLevelBlock(OutputFile(outputs, OutputPathFor(relative)), block);
                }
            }
        }

        PlaceObjects(objects);
        foreach (var entry in objects)
        {
            var body = ConvertBody(entry.Block, entry.Block.Type);
            if (entry.ReskinOf != null)
                body = Prepend(body, "ReskinOf", entry.ReskinOf);
            body.Set("SourceFile", "Data/INI/" + entry.SourceFile);
            var sections = OutputFile(outputs, $"{entry.Folder}/{entry.Category}.yaml");
            AddNamed(Section(sections, "Objects"), entry.Name, body);
        }

        WriteOutputs(outputs, outputDirectory);
        Console.WriteLine($"Imported {blockCount} INI blocks ({objects.Count} objects) into {outputs.Count} YAML files in {outputDirectory}");
        return 0;
    }

    // ---- Where things go ----

    static string OutputPathFor(string relativeIniPath)
    {
        string name = PathUtil.WithoutExtension(relativeIniPath);
        string directory = PathUtil.Directory(relativeIniPath);

        if (directory.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return $"defaults/{FileNames.GetValueOrDefault(name, Kebab(name))}.yaml";

        if (directory.StartsWith("MappedImages", StringComparison.OrdinalIgnoreCase))
        {
            string sub = string.Join('/', directory.Split('/').Skip(1).Select(Kebab));
            return $"ui/mapped-images/{(sub.Length > 0 ? sub + "/" : "")}{Kebab(name)}.yaml";
        }

        if (directory.Length > 0)
            return $"{Kebab(directory)}/{FileNames.GetValueOrDefault(name, Kebab(name))}.yaml";

        return FileNames.GetValueOrDefault(name, Kebab(name)) + ".yaml";
    }

    static void PlaceObjects(List<ObjectEntry> objects)
    {
        var byName = new Dictionary<string, ObjectEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in objects)
            byName[entry.Name] = entry;

        foreach (var entry in objects)
        {
            // A reskin lives next to the object it reskins.
            var source = entry;
            for (int depth = 0; source.ReskinOf != null && depth < 8 && byName.TryGetValue(source.ReskinOf, out var parent); depth++)
                source = parent;

            string side = source.Block.Get("Side") ?? GuessSide(source.SourceFile);
            entry.Folder = FactionFolders.GetValueOrDefault(side, Kebab(side));
            entry.Category = Categorize(source, entry);
        }
    }

    static string GuessSide(string sourceFile)
    {
        string file = PathUtil.FileName(sourceFile);
        foreach (var (prefix, side) in FactionFromFile)
            if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return side;
        return "Civilian";
    }

    static string Categorize(ObjectEntry source, ObjectEntry entry)
    {
        string file = PathUtil.FileName(source.SourceFile);
        var kinds = new HashSet<string>(SplitWords(source.Block.Get("KindOf") ?? entry.Block.Get("KindOf") ?? ""), StringComparer.OrdinalIgnoreCase);

        if (file.Contains("CINE", StringComparison.OrdinalIgnoreCase)) return "cinematics";
        if (kinds.Contains("BOAT")) return "ships";
        if (kinds.Contains("STRUCTURE")) return "structures";
        if (kinds.Contains("INFANTRY")) return "infantry";
        if (kinds.Contains("AIRCRAFT")) return "aircraft";
        if (kinds.Contains("VEHICLE")) return "vehicles";
        if (kinds.Contains("PROJECTILE") || file.StartsWith("WeaponObjects", StringComparison.OrdinalIgnoreCase)) return "projectiles";
        if (file.StartsWith("Hulk", StringComparison.OrdinalIgnoreCase)) return "hulks";
        if (file.Contains("Prop", StringComparison.OrdinalIgnoreCase)) return "props";
        if (file.StartsWith("System", StringComparison.OrdinalIgnoreCase) || file.StartsWith("SpecialPowerObjects", StringComparison.OrdinalIgnoreCase)) return "system";
        return "misc";
    }

    // ---- Converting blocks ----

    static void AddTopLevelBlock(RuleNode file, IniBlock block)
    {
        string section = SectionNames.GetValueOrDefault(block.Type, block.Type + "s");

        // Unnamed blocks (GameData, InGameUI, AIData, ...) are singletons: merge them into one section.
        if (block.Name.Length == 0)
        {
            var body = ConvertBody(block, block.Type);
            if (file.TryGet(block.Type, out var existing) && existing.IsMapping)
                existing.MergeFrom(body);
            else
                file.Set(block.Type, body);
            return;
        }

        // Single-line top-level entries (e.g. "BenchProfile ...") become a list of strings.
        if (block.Children.Count == 0 && block.Type is "BenchProfile" or "LODPreset" or "ReallyLowMHz")
        {
            if (!file.TryGet(section, out var list))
                list = file.Set(section, RuleNode.Sequence());
            list.Add(RuleNode.Scalar(CleanValue(block.Name)));
            return;
        }

        AddNamed(Section(file, section), CleanValue(block.Name), ConvertBody(block, block.Type));
    }

    /// <summary>
    /// Converts a block's contents into a mapping:
    ///   properties          → "Key: value" (a list when the key repeats)
    ///   modules             → "Modules: { Tag: { Module: Behavior, Class: X, ... } }"
    ///   FX / OCL nuggets    → "Nuggets: [ { Type: ParticleSystem, ... }, ... ]" (order kept)
    ///   other nested blocks → "Type: { Header: {...} }" when headers are unique, else a list
    /// </summary>
    static RuleNode ConvertBody(IniBlock block, string blockType)
    {
        var result = RuleNode.Mapping();
        var propertyValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var propertyOrder = new List<string>();
        var nestedByType = new Dictionary<string, List<IniBlock>>(StringComparer.OrdinalIgnoreCase);
        var nestedOrder = new List<string>();
        var modules = new List<IniBlock>();
        var nuggets = new List<IniBlock>();
        var aliases = new Dictionary<IniBlock, List<string>>();
        IniBlock? lastConditionState = null;

        foreach (object child in block.Children)
        {
            if (child is IniProperty property)
            {
                // "AliasConditionState X" applies to the condition state right before it.
                if (property.Key.Equals("AliasConditionState", StringComparison.OrdinalIgnoreCase) && lastConditionState != null)
                {
                    if (!aliases.TryGetValue(lastConditionState, out var list))
                        aliases[lastConditionState] = list = new();
                    list.Add(CleanValue(property.Value));
                    continue;
                }

                if (!propertyValues.TryGetValue(property.Key, out var values))
                {
                    propertyValues[property.Key] = values = new();
                    propertyOrder.Add(property.Key);
                }
                values.Add(CleanValue(property.Value));
            }
            else if (child is IniBlock nested)
            {
                if (ModuleBlockTypes.Contains(nested.Type))
                {
                    modules.Add(nested);
                }
                else if (NuggetListTypes.Contains(blockType))
                {
                    nuggets.Add(nested);
                }
                else
                {
                    if (nested.Type.Equals("ConditionState", StringComparison.OrdinalIgnoreCase) ||
                        nested.Type.Equals("DefaultConditionState", StringComparison.OrdinalIgnoreCase))
                        lastConditionState = nested;

                    if (!nestedByType.TryGetValue(nested.Type, out var list))
                    {
                        nestedByType[nested.Type] = list = new();
                        nestedOrder.Add(nested.Type);
                    }
                    list.Add(nested);
                }
            }
        }

        foreach (string key in propertyOrder)
        {
            var values = propertyValues[key];
            if (values.Count == 1)
                result.Set(key, values[0]);
            else
            {
                var sequence = RuleNode.Sequence();
                foreach (string value in values)
                    sequence.Add(RuleNode.Scalar(value));
                result.Set(key, sequence);
            }
        }

        foreach (string type in nestedOrder)
            result.Set(type, ConvertNestedGroup(nestedByType[type], aliases));

        if (nuggets.Count > 0)
        {
            var list = RuleNode.Sequence();
            foreach (var nugget in nuggets)
                list.Add(Prepend(ConvertBody(nugget, nugget.Type), "Type", nugget.Type));
            result.Set("Nuggets", list);
        }

        if (modules.Count > 0)
        {
            var moduleMap = RuleNode.Mapping();
            int untagged = 0;
            foreach (var module in modules)
            {
                string[] words = SplitWords(module.Name);
                string moduleClass = words.ElementAtOrDefault(0) ?? "";
                string tag = words.Length > 1 ? words[^1] : $"{module.Type}_{++untagged}";

                var body = ConvertBody(module, module.Type);
                body = Prepend(body, "Class", moduleClass);
                body = Prepend(body, "Module", module.Type);
                AddNamed(moduleMap, tag, body);
            }
            result.Set("Modules", moduleMap);
        }

        return result;
    }

    static RuleNode ConvertNestedGroup(List<IniBlock> blocks, Dictionary<IniBlock, List<string>> aliases)
    {
        RuleNode Body(IniBlock b)
        {
            var body = ConvertBody(b, b.Type);
            if (aliases.TryGetValue(b, out var list))
            {
                var sequence = RuleNode.Sequence();
                foreach (string alias in list)
                    sequence.Add(RuleNode.Scalar(alias));
                body.Set("Aliases", sequence);
            }
            return body;
        }

        var headers = blocks.Select(b => CleanValue(b.Name)).ToList();
        bool headersUsable = headers.All(h => h.Length > 0) && headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() == headers.Count;

        if (headersUsable)
        {
            var map = RuleNode.Mapping();
            for (int i = 0; i < blocks.Count; i++)
                map.Set(headers[i], Body(blocks[i]));
            return map;
        }

        if (blocks.Count == 1 && headers[0].Length == 0)
            return Body(blocks[0]);

        var sequence = RuleNode.Sequence();
        for (int i = 0; i < blocks.Count; i++)
        {
            var body = Body(blocks[i]);
            sequence.Add(headers[i].Length > 0 ? Prepend(body, "Header", headers[i]) : body);
        }
        return sequence;
    }

    // ---- Small helpers ----

    static RuleNode OutputFile(SortedDictionary<string, RuleNode> outputs, string path)
    {
        if (!outputs.TryGetValue(path, out var node))
            outputs[path] = node = RuleNode.Mapping();
        return node;
    }

    static RuleNode Section(RuleNode file, string name) =>
        file.TryGet(name, out var section) ? section : file.Set(name, RuleNode.Mapping());

    /// <summary>Adds a named definition; a repeated name merges into the earlier one (later values win).</summary>
    static void AddNamed(RuleNode section, string name, RuleNode body)
    {
        if (section.TryGet(name, out var existing) && existing.IsMapping)
            existing.MergeFrom(body);
        else
            section.Set(name, body);
    }

    static RuleNode Prepend(RuleNode mapping, string key, string value)
    {
        var result = RuleNode.Mapping();
        result.Set(key, value);
        foreach (var (k, v) in mapping.Entries)
            result.Set(k, v);
        return result;
    }

    static string[] SplitWords(string text) => text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Collapses runs of whitespace (outside quotes) to single spaces.</summary>
    static string CleanValue(string value)
    {
        value = value.Trim();
        return value.Contains('"') ? value : Whitespace().Replace(value, " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    static string Kebab(string name)
    {
        string spaced = KebabBoundary().Replace(name.Replace('_', '-'), "$1-$2");
        return spaced.ToLowerInvariant().Replace("--", "-");
    }

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex KebabBoundary();

    static void WriteOutputs(SortedDictionary<string, RuleNode> outputs, string outputDirectory)
    {
        // Remove previously generated files so renamed outputs don't linger. Hand-written files
        // (those without the generated header) are left alone.
        if (Directory.Exists(outputDirectory))
            foreach (string old in Directory.EnumerateFiles(outputDirectory, "*.yaml", SearchOption.AllDirectories))
                if (File.ReadLines(old).FirstOrDefault()?.StartsWith("# GENERATED", StringComparison.Ordinal) == true)
                    File.Delete(old);

        foreach (var (path, node) in outputs)
        {
            string full = Path.Combine(outputDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string header = "GENERATED from the original Zero Hour INI data by 'OpenGZH.Tools import-rules'.\n" +
                            "This file is now the source of truth for these rules: edit it freely.\n" +
                            "To make a separate mod instead, override definitions from your own mod's rules files.";
            File.WriteAllText(full, RuleYaml.Write(node, header));
        }
    }
}
