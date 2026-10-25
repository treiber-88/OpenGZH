using System.Text;
using System.Text.RegularExpressions;
using OpenGZH.Core.Rules;

namespace OpenGZH.Tools;

/// <summary>
/// Converts Tiberian Dawn's built-in type tables (the C++ "...DATA.CPP" files of EA's released
/// source: static XTypeClass const objects and the Weapons/Warheads arrays) into YAML rules files.
/// Each constructor argument is named after the class member it sets (following the constructor
/// and its base-class constructors), so the YAML reads like the original's own data.
/// </summary>
public static class TdRulesImporter
{
    // What the build defined (DEFINES.H: PATCH and the remaster's USE_RA_AI on; ADVANCED, SCENARIO_EDITOR... off).
    static readonly HashSet<string> Defines = ["PATCH", "WIN32", "TRUE_FALSE_DEFINED", "USE_RA_AI"];

    static readonly (string File, string Class, string Output)[] Tables =
    [
        ("CDATA.CPP", "TemplateTypeClass", "templates.yaml"),
        ("TDATA.CPP", "TerrainTypeClass", "terrain-objects.yaml"),
        ("ODATA.CPP", "OverlayTypeClass", "overlays.yaml"),
        ("SDATA.CPP", "SmudgeTypeClass", "smudges.yaml"),
        ("ADATA.CPP", "AnimTypeClass", "animations.yaml"),
        ("BBDATA.CPP", "BulletTypeClass", "projectiles.yaml"),
        ("IDATA.CPP", "InfantryTypeClass", "infantry.yaml"),
        ("UDATA.CPP", "UnitTypeClass", "vehicles.yaml"),
        ("AADATA.CPP", "AircraftTypeClass", "aircraft.yaml"),
        ("BDATA.CPP", "BuildingTypeClass", "buildings.yaml"),
        ("HDATA.CPP", "HouseTypeClass", "houses.yaml"),
    ];

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("import-td-rules <TIBERIANDAWN source folder> <output rules folder>");
            return 1;
        }
        string source = args[0], output = args[1];
        var paths = Directory.EnumerateFiles(source, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".CPP", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".H", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string path in paths)
            foreach (Match d in Regex.Matches(File.ReadAllText(path, Encoding.Latin1), @"^[ \t]*#[ \t]*define[ \t]+(\w+)[ \t]+([^\r\n]+)", RegexOptions.Multiline))
            {
                // Drop a trailing // or /* comment (a '/' may also be division).
                string value = Regex.Replace(d.Groups[2].Value, @"//.*$|/\*.*?(?:\*/|$)", "").Trim();
                if (value.Length > 0)
                    Constants.TryAdd(d.Groups[1].Value, value);
            }
        var files = paths.ToDictionary(f => Path.GetFileName(f).ToUpperInvariant(), f => Preprocess(File.ReadAllText(f, Encoding.Latin1)));

        // WIN32LIB's headers only lend names to the first enum pass (the colours in WWSTD.H).
        string libDir = Path.Combine(source, "WIN32LIB");
        var libTexts = Directory.Exists(libDir)
            ? Directory.EnumerateFiles(libDir, "*.*").Where(f => f.EndsWith(".H", StringComparison.OrdinalIgnoreCase))
                .Select(f => Preprocess(File.ReadAllText(f, Encoding.Latin1))).ToList()
            : [];
        var enums = ParseEnums(files.Values, libTexts);
        var constructors = ParseConstructors(files.Values);
        Directory.CreateDirectory(output);
        int total = 0;
        foreach (var (file, cls, outFile) in Tables)
        {
            if (!files.TryGetValue(file, out var text))
                continue;
            var arrays = ParseStaticArrays(text);
            var root = RuleNode.Mapping();
            var section = RuleNode.Mapping();
            root.Set(SectionName(cls), section);
            // As_Reference indexes the class's Pointers[] table, so a type's number is its slot
            // there (BBDATA's ClassSniper says BULLET_BULLET but sits at BULLET_SNIPER).
            var slots = new Dictionary<string, int>(StringComparer.Ordinal);
            var pointers = Regex.Match(text, cls + @"\s*::\s*Pointers\s*\[[^\]]*\]\s*=\s*\{([^}]*)\}");
            if (pointers.Success)
            {
                int slot = 0;
                foreach (Match p in Regex.Matches(Regex.Replace(pointers.Groups[1].Value, @"//[^\n]*", ""), @"&\s*(\w+)|\bNULL\b|\b0\b"))
                {
                    if (p.Groups[1].Success)
                        slots.TryAdd(p.Groups[1].Value, slot);
                    slot++;
                }
            }
            foreach (var (variable, arguments) in ParseInstances(text, cls))
            {
                var members = ResolveMembers(cls, arguments, constructors);
                var node = RuleNode.Mapping();
                string? key = null;
                foreach (var (member, value) in members)
                {
                    var converted = Convert(value, arrays);
                    if (converted == null)
                        continue;
                    node.Set(member, converted);
                    if (member.Equals("IniName", StringComparison.OrdinalIgnoreCase) && converted.IsScalar)
                        key = converted.Value;
                    if (member == "Type" && converted.IsScalar && EnumValue(enums, converted.Value!) is int id)
                        node.Set("Id", id.ToString());
                }
                if (slots.TryGetValue(variable, out int slotId))
                    node.Set("Id", slotId.ToString());
                node.Set("SourceName", variable);
                // Several types can share an INI name (the bullets' "50cal", "DRAGON"...): later ones
                // keep their C++ variable name as the key so none is lost.
                if (key == null || section.Entries.Any(e => e.Key == key))
                    key = variable;
                section.Set(key, node);
                total++;
            }
            if (cls == "BuildingTypeClass")
                AddBuildingAnimations(text, section);
            File.WriteAllText(Path.Combine(output, outFile), RuleYaml.Write(root, Header(file)));
            Console.WriteLine($"{outFile}: {section.Entries.Count()} {cls}");
        }

        // CONST.CPP: the weapon and warhead arrays, one row per enum value.
        if (files.TryGetValue("CONST.CPP", out var constText) && files.TryGetValue("TYPE.H", out var typeH))
        {
            WriteStructArray(constText, typeH, enums, "WeaponTypeClass", "Weapons", "WeaponType", "WEAPON_", Path.Combine(output, "weapons.yaml"), "Weapons");
            WriteStructArray(constText, typeH, enums, "WarheadTypeClass", "Warheads", "WarheadType", "WARHEAD_", Path.Combine(output, "warheads.yaml"), "Warheads");

            // Ground: per land type, the radar colour, the movement cost for each speed type
            // (SPEED_FOOT, TRACK, HARVESTER, WHEEL, WINGED, HOVER, FLOAT) and whether it can be built on.
            WriteStructArray(constText, typeH, enums, "GroundType", "Ground", "LandType", "LAND_", Path.Combine(output, "ground.yaml"), "Ground",
                ["Color", "Cost", "Build"]);

            // The house colour remap tables (RemapGold, RemapRed...): palette index -> replacement index.
            var remaps = RuleNode.Mapping();
            var remapRoot = RuleNode.Mapping();
            remapRoot.Set("RemapTables", remaps);
            foreach (Match m in Regex.Matches(constText, @"unsigned\s+char\s+const\s+(Remap\w+)\s*\[\s*256\s*\]\s*=\s*\{"))
            {
                int open = constText.IndexOf('{', m.Index + m.Length - 1);
                int close = MatchBrace(constText, open);
                if (Convert(constText[open..(close + 1)], new Dictionary<string, string>()) is { } table)
                    remaps.Set(m.Groups[1].Value, table);
            }
            File.WriteAllText(Path.Combine(output, "remaps.yaml"), RuleYaml.Write(remapRoot, Header("CONST.CPP")));
            Console.WriteLine($"remaps.yaml: {remaps.Entries.Count()} remap tables");
        }
        Console.WriteLine($"{total} definitions");

        // --enums <file.cs>: the source's enums and numeric #defines as C#, with their original values.
        int enumsAt = Array.IndexOf(args, "--enums");
        if (enumsAt >= 0 && enumsAt + 1 < args.Length)
            WriteEnums(args[enumsAt + 1], enums);
        return 0;
    }

    static readonly HashSet<string> CSharpKeywords = ["true", "false", "null", "base", "fixed", "char", "int", "long", "short", "object", "string", "event", "operator", "checked", "params", "lock", "out", "ref"];

    static void WriteEnums(string path, Dictionary<string, int> values)
    {
        var code = new StringBuilder();
        code.AppendLine("// GENERATED by 'OpenGZH.Tools import-td-rules --enums' from Tiberian Dawn's headers (EA's GPL source).");
        code.AppendLine("// The original enum names and values, so the port reads like the source. Do not edit by hand.");
        code.AppendLine("#pragma warning disable CA1069 // several names can share a value, as in the original");
        foreach (var (alias, target) in EnumTypedefs.OrderBy(e => e.Key, StringComparer.Ordinal))
            if (!EnumMembers.ContainsKey(alias) && Regex.IsMatch(target, @"^[A-Z]\w*$") && EnumMembers.TryGetValue(target, out var targetMembers) && targetMembers.Count > 0)
                code.AppendLine($"global using {alias} = OpenGZH.TiberianDawn.Logic.{target};");
        code.AppendLine("namespace OpenGZH.TiberianDawn.Logic;");
        code.AppendLine();
        foreach (var (typeName, members) in EnumMembers.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(typeName, @"^[A-Z]\w*$") || members.Count == 0)
                continue;
            code.AppendLine($"public enum {typeName}");
            code.AppendLine("{");
            var own = EnumLocalValues.GetValueOrDefault(typeName) ?? values;
            foreach (string member in members.Distinct())
                if (own.TryGetValue(member, out int value))
                    code.AppendLine($"    {member} = {value},");
            code.AppendLine("}");
            code.AppendLine();
        }
        // Numeric #define constants (flag sets such as HOUSEF_GOOD, STRUCTF_RADAR, sizes, timings).
        code.AppendLine("public static class TdDefines");
        code.AppendLine("{");
        var enumNames = new HashSet<string>(EnumMembers.Values.SelectMany(v => v));
        foreach (var name in Constants.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_]\w*$") || CSharpKeywords.Contains(name) || enumNames.Contains(name) || name.StartsWith('_'))
                continue;
            string definition = Constants[name];
            long? v = Evaluate(definition) ?? (TryInt(definition, out int simple) ? simple : (long?)null);
            // Enum names inside the definition (HOUSEF_GOOD is 1 << HOUSE_GOOD).
            if (v == null)
            {
                string substituted = System.Text.RegularExpressions.Regex.Replace(definition, @"\b[A-Za-z_]\w*\b", m => values.TryGetValue(m.Value, out int e) ? e.ToString() : m.Value);
                v = Evaluate(substituted + " + 0");
            }
            // int when it fits, as the C source uses them; wider values (0xC000C000L) stay long.
            if (v is long number)
                code.AppendLine(number is >= int.MinValue and <= int.MaxValue
                    ? $"    public const int {name} = {number};"
                    : $"    public const long {name} = {number};");
        }
        code.AppendLine("}");
        File.WriteAllText(path, code.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {EnumMembers.Count} enums");
    }

    static string SectionName(string cls) => cls switch
    {
        "TemplateTypeClass" => "Templates",
        "TerrainTypeClass" => "TerrainObjects",
        "OverlayTypeClass" => "Overlays",
        "SmudgeTypeClass" => "Smudges",
        "AnimTypeClass" => "Animations",
        "BulletTypeClass" => "Projectiles",
        "InfantryTypeClass" => "Infantry",
        "UnitTypeClass" => "Vehicles",
        "AircraftTypeClass" => "Aircraft",
        "BuildingTypeClass" => "Buildings",
        "HouseTypeClass" => "Houses",
        _ => cls,
    };

    static string Header(string file) =>
        $"GENERATED from Tiberian Dawn's {file} (EA's GPL source release) by 'OpenGZH.Tools import-td-rules'.\n" +
        "Each key is the class member the original constructor argument sets.\n" +
        "This file is now the source of truth for these rules: edit it freely.";

    // ---- Preprocessing ----

    /// <summary>Removes comments and resolves #if/#ifdef blocks with the original build's defines.</summary>
    static string Preprocess(string text)
    {
        var noComments = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                char quote = c;
                noComments.Append(c);
                for (i++; i < text.Length && text[i] != quote; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        noComments.Append(text[i++]);
                    noComments.Append(text[i]);
                }
                if (i < text.Length)
                    noComments.Append(text[i]);
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                noComments.Append('\n');
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                        noComments.Append('\n');
                    i++;
                }
                i++;
            }
            else
                noComments.Append(c);
        }

        var result = new StringBuilder();
        var stack = new Stack<(bool Active, bool Taken)>();
        bool active = true;
        foreach (string raw in noComments.ToString().Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('#'))
            {
                string directive = Regex.Match(line, @"^#\s*(\w+)").Groups[1].Value;
                string arg = Regex.Replace(line, @"^#\s*\w+\s*", "").Trim();
                switch (directive)
                {
                    case "ifdef":
                    case "ifndef":
                    case "if":
                    {
                        bool cond = directive switch
                        {
                            "ifdef" => Defines.Contains(arg),
                            "ifndef" => !Defines.Contains(arg),
                            _ => EvaluateIf(arg),
                        };
                        stack.Push((active, cond));
                        active = active && cond;
                        break;
                    }
                    case "elif":
                    {
                        var (outer, taken) = stack.Pop();
                        bool cond = !taken && EvaluateIf(arg);
                        stack.Push((outer, taken || cond));
                        active = outer && cond;
                        break;
                    }
                    case "else":
                    {
                        var (outer, taken) = stack.Pop();
                        stack.Push((outer, true));
                        active = outer && !taken;
                        break;
                    }
                    case "endif":
                        if (stack.Count > 0)
                            active = stack.Pop().Active;
                        break;
                }
                result.Append('\n');
                continue;
            }
            result.Append(active ? raw : "").Append('\n');
        }
        return result.ToString();
    }

    static bool EvaluateIf(string expression)
    {
        string e = Regex.Replace(expression, @"defined\s*\(?\s*(\w+)\s*\)?", m => Defines.Contains(m.Groups[1].Value) ? "1" : "0");
        e = e.Replace("(", "").Replace(")", "").Trim();
        if (e.StartsWith('!'))
            return !EvaluateIf(e[1..]);
        if (e.Contains("&&"))
            return e.Split("&&").All(EvaluateIf);
        if (e.Contains("||"))
            return e.Split("||").Any(EvaluateIf);
        if (int.TryParse(e, out int n))
            return n != 0;
        return Defines.Contains(e);
    }

    // ---- Enums ----

    static Dictionary<string, int> ParseEnums(IEnumerable<string> texts, List<string> libraryTexts)
    {
        // Two passes: an enum may use names from a header read later (CC_GDI_COLOR = YELLOW).
        var list = texts.ToList();
        var firstPass = ParseEnumsPass([.. libraryTexts, .. list], new Dictionary<string, int>(StringComparer.Ordinal), warn: false);
        EnumMembers.Clear();
        EnumLocalValues.Clear();
        NestedEnums.Clear();
        return ParseEnumsPass(list, firstPass, warn: true);
    }

    static Dictionary<string, int> ParseEnumsPass(List<string> texts, Dictionary<string, int> earlier, bool warn)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string text in texts)
            foreach (Match m in Regex.Matches(text, @"\benum\s+(\w*)\s*(?::\s*\w+(?:\s+\w+)?)?\s*\{([^}]*)\}\s*(\w*)"))
            {
                int next = 0;
                // The enum's type name: "enum LandType {...}" or "typedef enum ... {...} LandType;".
                string typeName = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[3].Value;
                var members = new List<string>();
                // Names are scoped to their enum in C++ classes (UnitTypeClass::STEP_COUNT and
                // StepCountEnum's STEP_COUNT differ), so each enum keeps its own values too.
                var local = new Dictionary<string, int>(StringComparer.Ordinal);
                // An indented enum sits inside a class (EventClass::EventType); a top-level enum of the
                // same name (TRIGGER.H's EventType) is the one the global name means.
                int lineStart = text.LastIndexOf('\n', m.Index) + 1;
                bool nested = lineStart < text.Length && (text[lineStart] == '\t' || text[lineStart] == ' ');
                if (typeName.Length > 0)
                {
                    if (EnumMembers.TryAdd(typeName, members))
                    {
                        EnumLocalValues[typeName] = local;
                        if (nested)
                            NestedEnums.Add(typeName);
                    }
                    else if (!nested && NestedEnums.Remove(typeName))
                    {
                        EnumMembers[typeName] = members;
                        EnumLocalValues[typeName] = local;
                    }
                }
                // "typedef enum GameEnum {...} GameType;": the source mostly uses the typedef name.
                string typedefName = m.Groups[3].Value;
                if (m.Groups[1].Value.Length > 0 && typedefName.Length > 0 && typedefName != typeName)
                    EnumTypedefs.TryAdd(typedefName, typeName);
                foreach (string part in SplitTopLevel(m.Groups[2].Value))
                {
                    string item = part.Trim();
                    if (item.Length == 0)
                        continue;
                    int eq = item.IndexOf('=');
                    string name = (eq >= 0 ? item[..eq] : item).Trim();
                    if (eq >= 0)
                    {
                        string expr = item[(eq + 1)..].Trim();
                        if (TryInt(expr, out int v))
                            next = v;
                        else if (local.TryGetValue(expr, out int own) || values.TryGetValue(expr, out own))
                            next = own;
                        else
                        {
                            // Any constant expression (1<<5, (5<<5)-8, HOUSE_GOOD+1...), with the enum
                            // values known so far and type casts substituted.
                            string substituted = Regex.Replace(Regex.Replace(expr, @"\(\s*\w+Type\s*\)", ""), @"\b[A-Za-z_]\w*\b",
                                w => local.TryGetValue(w.Value, out int known) || values.TryGetValue(w.Value, out known) || earlier.TryGetValue(w.Value, out known) ? known.ToString() : w.Value);
                            if (TryInt(substituted, out int plain))
                                next = plain;
                            else if (Evaluate(substituted) is long evaluated)
                                next = (int)evaluated;
                            else if (warn)
                                Console.Error.WriteLine($"warning: enum {typeName}.{name} = '{expr}' not evaluated; counting on from {next}");
                        }
                    }
                    if (Regex.IsMatch(name, @"^\w+$"))
                    {
                        values.TryAdd(name, next);
                        local.TryAdd(name, next);
                        members.Add(name);
                    }
                    next++;
                }
            }
        AllEnumValues = values;
        return values;
    }

    static Dictionary<string, int> AllEnumValues = new();

    /// <summary>Each enum type's own member values.</summary>
    static readonly Dictionary<string, Dictionary<string, int>> EnumLocalValues = new();

    /// <summary>Enum names so far known only from a class-nested declaration.</summary>
    static readonly HashSet<string> NestedEnums = new();

    /// <summary>typedef names that differ from the enum's tag (GameType -> GameEnum).</summary>
    static readonly Dictionary<string, string> EnumTypedefs = new();

    /// <summary>Each enum type's member names, in declaration order.</summary>
    static readonly Dictionary<string, List<string>> EnumMembers = new();

    static int? EnumValue(Dictionary<string, int> enums, string name) => enums.TryGetValue(name, out int v) ? v : null;

    static bool TryInt(string text, out int value)
    {
        text = text.Trim();
        // Character literals: 'A', '\3', ' '.
        if (text.Length >= 3 && text[0] == '\'' && text[^1] == '\'')
        {
            string body = text[1..^1];
            if (body.Length == 1)
            {
                value = body[0];
                return true;
            }
            if (body.Length > 1 && body[0] == '\\')
            {
                string esc = body[1..];
                if (esc.All(char.IsAsciiDigit))
                {
                    value = System.Convert.ToInt32(esc, 8);
                    return true;
                }
                value = esc switch { "n" => 10, "r" => 13, "t" => 9, "\\" => 92, "'" => 39, _ => -1 };
                if (value >= 0)
                    return true;
            }
        }
        text = text.TrimEnd('L', 'l', 'U', 'u');
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase) && text.Length > 2 && text[2..].All(c => c is '0' or '1'))
        {
            value = System.Convert.ToInt32(text[2..], 2);
            return true;
        }
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return int.TryParse(text, out value);
    }

    // ---- Constructors: which member each argument sets ----

    sealed record Constructor(string Class, List<string> Params, string? BaseClass, List<string> BaseArgs, Dictionary<string, string> ParamToMember, Dictionary<string, string> Constants);

    static Dictionary<string, Constructor> ParseConstructors(IEnumerable<string> texts)
    {
        var result = new Dictionary<string, Constructor>();
        foreach (string text in texts)
            foreach (Match m in Regex.Matches(text, @"\b(\w+TypeClass)::\1\s*\("))
            {
                string cls = m.Groups[1].Value;
                int open = m.Index + m.Length - 1;
                int close = MatchParen(text, open);
                if (close < 0)
                    continue;
                var paramList = SplitTopLevel(text[(open + 1)..close]).Select(p => Regex.Match(p.Trim(), @"(\w+)\s*(?:\[\s*\])?\s*$").Groups[1].Value).ToList();
                int i = close + 1;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                string? baseClass = null;
                var baseArgs = new List<string>();
                var paramToMember = new Dictionary<string, string>();
                var constants = new Dictionary<string, string>();
                if (i < text.Length && text[i] == ':')
                {
                    int braceAt = text.IndexOf('{', i);
                    string init = text[(i + 1)..braceAt];
                    foreach (Match call in Regex.Matches(init, @"(\w+)\s*\("))
                    {
                        int o = init.IndexOf('(', call.Index);
                        int c = MatchParen(init, o);
                        if (c < 0)
                            continue;
                        var callArgs = SplitTopLevel(init[(o + 1)..c]).Select(a => a.Trim()).ToList();
                        string name = call.Groups[1].Value;
                        if (name.EndsWith("Class"))
                        {
                            baseClass = name;
                            baseArgs = callArgs;
                        }
                        else if (callArgs.Count == 1)
                        {
                            if (paramList.Contains(callArgs[0]))
                                paramToMember[callArgs[0]] = name;
                            else
                                constants[name] = callArgs[0];
                        }
                    }
                    i = braceAt;
                }
                if (i >= text.Length || text[i] != '{')
                    continue;
                int end = MatchBrace(text, i);
                if (end < 0)
                    continue;
                string body = text[(i + 1)..end];
                // The name copied into IniName (strncpy((char *)IniName, ininame, ...)).
                foreach (Match copy in Regex.Matches(body, @"strn?cpy\s*\(\s*(?:\([^)]*\)\s*)?(\w+)\s*,\s*(\w+)"))
                    if (paramList.Contains(copy.Groups[2].Value))
                        paramToMember[copy.Groups[2].Value] = copy.Groups[1].Value;
                // Assignments in order; a later one wins (e.g. "risk = risk; Risk = 0;" leaves Risk at 0).
                foreach (Match assign in Regex.Matches(body, @"(?<![\w.>])(\w+)\s*=\s*([^;=][^;]*);"))
                {
                    string member = assign.Groups[1].Value, value = assign.Groups[2].Value.Trim();
                    // A cast on the parameter ("Delay = (unsigned char)delaytime;").
                    value = Regex.Replace(value, @"^\(\s*(?:unsigned\s+|signed\s+)?\w+\s*\*?\s*\)\s*", "");
                    if (member == value)
                        continue; // a parameter assigned to itself does nothing
                    if (paramList.Contains(value))
                    {
                        paramToMember[value] = member;
                        constants.Remove(member);
                    }
                    else if (char.IsUpper(member[0]) && Regex.IsMatch(value, @"^(true|false|-?\d+|NULL|\w+_NONE)$"))
                    {
                        constants[member] = value;
                        foreach (var p in paramToMember.Where(kv => kv.Value == member).Select(kv => kv.Key).ToList())
                            paramToMember.Remove(p);
                    }
                }
                result.TryAdd(cls, new Constructor(cls, paramList, baseClass, baseArgs, paramToMember, constants));
            }
        return result;
    }

    /// <summary>The members (in constructor order) an instance's arguments set, following base constructors.</summary>
    static List<(string Member, string Value)> ResolveMembers(string cls, List<string> args, Dictionary<string, Constructor> constructors)
    {
        var result = new List<(string, string)>();
        if (!constructors.TryGetValue(cls, out var ctor))
        {
            for (int i = 0; i < args.Count; i++)
                result.Add(($"Arg{i}", args[i]));
            return result;
        }
        var byParam = new Dictionary<string, string>();
        for (int i = 0; i < ctor.Params.Count && i < args.Count; i++)
            byParam[ctor.Params[i]] = args[i];

        // Arguments passed on to the base class constructor (substituting our parameters).
        if (ctor.BaseClass != null)
        {
            var baseArgs = ctor.BaseArgs.Select(a => byParam.TryGetValue(a, out var v) ? v
                : Regex.Replace(a, @"\b\w+\b", t => byParam.TryGetValue(t.Value, out var pv) ? $"({pv})" : t.Value)).ToList();
            result.AddRange(ResolveMembers(ctor.BaseClass, baseArgs, constructors));
        }
        for (int i = 0; i < ctor.Params.Count && i < args.Count; i++)
        {
            string param = ctor.Params[i];
            if (ctor.BaseArgs.Contains(param) && !ctor.ParamToMember.ContainsKey(param))
                continue; // already named by the base class
            if (ctor.ParamToMember.GetValueOrDefault(param) is not { } member)
            {
                // Not stored in a member (or overwritten by a constant): keep it under the parameter's name.
                member = Pascal(param);
                if (ctor.Constants.ContainsKey(member) || result.Any(r => r.Item1.Equals(member, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            result.RemoveAll(r => r.Item1.Equals(member, StringComparison.OrdinalIgnoreCase));
            result.Add((member, args[i]));
        }
        foreach (var (member, value) in ctor.Constants)
        {
            result.RemoveAll(r => r.Item1.Equals(member, StringComparison.OrdinalIgnoreCase));
            result.Add((member, value));
        }
        return result;
    }

    static string Pascal(string name) =>
        string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    // ---- Instances and arrays ----

    static IEnumerable<(string Variable, List<string> Args)> ParseInstances(string text, string cls)
    {
        foreach (Match m in Regex.Matches(text, $@"(?:static\s+)?{cls}\s+const\s+(\w+)\s*\("))
        {
            int open = m.Index + m.Length - 1;
            int close = MatchParen(text, open);
            if (close < 0)
                continue;
            yield return (m.Groups[1].Value, SplitTopLevel(text[(open + 1)..close]).Select(a => a.Trim()).ToList());
        }
    }

    static Dictionary<string, string> ParseStaticArrays(string text)
    {
        var arrays = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(text, @"(?:static\s+)?[\w\s]+?\b(\w+)\s*((?:\[\s*\w*\s*\]\s*)+)=\s*\{"))
        {
            int open = text.IndexOf('{', m.Index + m.Length - 1);
            int close = MatchBrace(text, open);
            if (close <= open)
                continue;
            string body = text[open..(close + 1)];
            // A table indexed by an enum ([DO_COUNT][3]) written as a flat list: one named row per enum value.
            var dims = Regex.Matches(m.Groups[2].Value, @"\[\s*(\w*)\s*\]").Select(d => d.Groups[1].Value).ToList();
            if (dims.Count == 2 && int.TryParse(dims[1], out int rowLength) && rowLength > 0
                && EnumMembers.Values.FirstOrDefault(list => list.Contains(dims[0])) is { } rowNames)
            {
                var values = SplitTopLevel(body[1..^1]).Select(v => v.Trim()).Where(v => v.Length > 0 && !v.StartsWith('{')).ToList();
                if (values.Count > 0)
                {
                    var rows = new StringBuilder("{");
                    var names = rowNames.Where(n => n != dims[0] && AllEnumValues.GetValueOrDefault(n, -1) >= 0).ToList();
                    for (int r = 0; r * rowLength < values.Count && r < names.Count; r++)
                        rows.Append($"{names[r]}=[{string.Join(",", values.Skip(r * rowLength).Take(rowLength))}];");
                    body = rows.Append('}').ToString();
                    NamedRowTables[m.Groups[1].Value] = true;
                }
            }
            arrays[m.Groups[1].Value] = body;
        }
        return arrays;
    }

    /// <summary>Tables turned into named rows (written "{NAME=[a,b,c];...}" internally).</summary>
    static readonly Dictionary<string, bool> NamedRowTables = new();

    /// <summary>A C++ argument as YAML: numbers, booleans, strings, flag sets, and arrays it names.</summary>
    static RuleNode? Convert(string value, Dictionary<string, string> arrays, int depth = 0)
    {
        value = value.Trim();
        if (Regex.IsMatch(value, @"^(\(\s*[\w\s\*]+\)\s*)?NULL$"))
            return null;
        if (value.Length == 0)
            return RuleNode.Scalar(value);
        if (value.StartsWith('"') && value.EndsWith('"'))
            return RuleNode.Scalar(value[1..^1]);
        if (value.StartsWith('{') && value.Contains("=[") && value.EndsWith(";}"))
        {
            var map = RuleNode.Mapping();
            foreach (Match row in Regex.Matches(value, @"(\w+)=\[([^\]]*)\];"))
            {
                var list = RuleNode.Sequence();
                foreach (string item in row.Groups[2].Value.Split(','))
                    if (Convert(item, arrays, depth + 1) is { } converted)
                        list.Add(converted);
                map.Set(row.Groups[1].Value, list);
            }
            return map;
        }
        if (value.StartsWith('{') && value.EndsWith('}'))
        {
            var list = RuleNode.Sequence();
            foreach (string item in SplitTopLevel(value[1..^1]))
                if (item.Trim().Length > 0 && Convert(item, arrays, depth + 1) is { } converted)
                    list.Add(converted);
            return list;
        }
        string cleaned = value.TrimStart('&');
        cleaned = Regex.Replace(cleaned, @"^\(\s*[\w\s\*]+\)\s*", ""); // casts like (short const *)
        cleaned = Regex.Replace(cleaned, @"(\[\s*\d+\s*\])+$", ""); // &Table[0][0] is the table itself
        if (depth < 3 && arrays.TryGetValue(cleaned, out var array))
            return Convert(array, arrays, depth + 1);
        // XYP_COORD(x, y): a pixel offset, kept as "x,y" pixels (24 pixels to a cell).
        if (Regex.Match(value, @"^XYP_COORD\s*\((.*)\)$") is { Success: true } xyp && SplitTopLevel(xyp.Groups[1].Value) is [var px, var py])
        {
            long? ex = Evaluate(px) ?? (TryInt(px, out int ix) ? ix : null), ey = Evaluate(py) ?? (TryInt(py, out int iy) ? iy : null);
            if (ex != null && ey != null)
                return RuleNode.Scalar($"{ex},{ey}");
        }
        // A cast enum value such as (DirType)160.
        if (Regex.Match(value, @"^\(\s*\w+\s*\)\s*(-?(?:0x[0-9a-fA-F]+|\d+))$") is { Success: true } cast && TryInt(cast.Groups[1].Value, out int castValue))
            return RuleNode.Scalar(castValue.ToString());
        if (Evaluate(value) is long computed)
            return RuleNode.Scalar(computed.ToString());
        if (value.Contains('|'))
            return RuleNode.Scalar(string.Join(" ", value.Split('|').Select(v => v.Trim()).Where(v => v.Length > 0)));
        if (TryInt(value, out int number) && (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || char.IsDigit(value.TrimStart('-')[0])))
            return RuleNode.Scalar(number.ToString());
        return RuleNode.Scalar(Regex.Replace(value, @"\s+", " "));
    }

    static void WriteStructArray(string constText, string typeH, Dictionary<string, int> enums, string cls, string arrayName,
        string enumName, string prefix, string outputFile, string section, List<string>? memberNames = null)
    {
        // Member names from the class declaration, in order.
        var decl = Regex.Match(typeH, $@"class\s+{cls}\s*\{{(.*?)\}};", RegexOptions.Singleline);
        var members = memberNames ?? Regex.Matches(decl.Groups[1].Value, @"^\s*(?:unsigned\s+)?(?:\w+)\s+(\w+)(\s*\[[^\]]*\])?\s*;", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        var array = Regex.Match(constText, $@"{cls}\s+const\s+{arrayName}\s*\[[^\]]*\]\s*=\s*\{{");
        int open = constText.IndexOf('{', array.Index + array.Length - 1);
        int close = MatchBrace(constText, open);
        var rows = SplitTopLevel(constText[(open + 1)..close]).Select(r => r.Trim()).Where(r => r.StartsWith('{')).ToList();
        // Row names from the enum the array is indexed by (e.g. LandType), in value order.
        var names = (EnumMembers.GetValueOrDefault(enumName) ?? [])
            .Where(n => n.StartsWith(prefix) && enums.GetValueOrDefault(n, -1) >= 0 && !n.EndsWith("_COUNT") && !n.EndsWith("_FIRST"))
            .OrderBy(n => enums[n]).GroupBy(n => enums[n]).Select(g => g.First()).ToList();
        var root = RuleNode.Mapping();
        var node = RuleNode.Mapping();
        root.Set(section, node);
        for (int r = 0; r < rows.Count; r++)
        {
            var fields = SplitTopLevel(rows[r][1..^1]).Select(f => f.Trim()).ToList();
            var row = RuleNode.Mapping();
            row.Set("Id", r.ToString());
            for (int f = 0; f < fields.Count; f++)
                if (Convert(fields[f], new Dictionary<string, string>()) is { } v)
                    row.Set(f < members.Count ? members[f] : $"Field{f}", v);
            string key = r < names.Count ? names[r][prefix.Length..] : $"{prefix}{r}";
            node.Set(key, row);
        }
        File.WriteAllText(outputFile, RuleYaml.Write(root, Header("CONST.CPP")));
        Console.WriteLine($"{Path.GetFileName(outputFile)}: {rows.Count} {cls}");
    }

    /// <summary>
    /// BuildingTypeClass::One_Time's _anims[] table ({type, state, start frame, frame count, rate})
    /// becomes each building's "Anims" (state: [start, count, rate]).
    /// </summary>
    static void AddBuildingAnimations(string text, RuleNode buildings)
    {
        var table = Regex.Match(text, @"_anims\s*\[\s*\]\s*=\s*\{");
        if (!table.Success)
            return;
        int open = text.IndexOf('{', table.Index + table.Length - 1);
        int close = MatchBrace(text, open);
        foreach (string row in SplitTopLevel(text[(open + 1)..close]).Select(r => r.Trim()).Where(r => r.StartsWith('{')))
        {
            var f = SplitTopLevel(row[1..^1]).Select(x => x.Trim()).ToList();
            if (f.Count < 5)
                continue;
            var building = buildings.Entries.Select(e => e.Value).FirstOrDefault(b => b.GetString("Type") == f[0]);
            if (building == null)
                continue;
            if (!building.TryGet("Anims", out var anims))
                building.Set("Anims", anims = RuleNode.Mapping());
            var entry = RuleNode.Sequence();
            foreach (string value in f.Skip(2).Take(3))
                entry.Add(Convert(value, new Dictionary<string, string>()) ?? RuleNode.Scalar(value));
            anims.Set(f[1], entry);
        }
    }

    // ---- Constant expressions ----

    /// <summary>#define NAME value from the whole source (object-like macros only).</summary>
    static readonly Dictionary<string, string> Constants = new();

    /// <summary>
    /// The value of a constant expression made of numbers, #define constants, ( ) and + - * / % << >> | &amp;,
    /// or null if it has anything else (enum names, function calls...) or no arithmetic or constant at all.
    /// </summary>
    static long? Evaluate(string expression, int depth = 0)
    {
        if (depth > 12)
            return null;
        string text = Regex.Replace(expression, @"\((?:unsigned\s+)?(?:long|short|int|char|signed)\)", "");
        var tokens = Regex.Matches(text, @"0[xX][0-9a-fA-F]+[lLuU]*|\d+[lLuU]*|\w+|<<|>>|[-+*/%|&()~]|\S").Select(m => m.Value).ToList();
        if (tokens.Count == 0)
            return null;
        bool interesting = tokens.Any(t => t is "+" or "-" or "*" or "/" or "%" or "<<" or ">>" or "|" or "&" or "~") || tokens.Any(t => Constants.ContainsKey(t));
        if (!interesting)
            return null;
        var values = new List<string>();
        foreach (string t in tokens)
        {
            if (Regex.IsMatch(t, @"^[A-Za-z_]\w*$"))
            {
                if (!Constants.TryGetValue(t, out var definition) || Evaluate(definition, depth + 1) is not long v)
                {
                    if (Constants.TryGetValue(t, out var raw) && TryInt(raw, out int simple))
                        v = simple;
                    else
                        return null;
                }
                values.Add(v.ToString());
            }
            else
                values.Add(t);
        }
        int pos = 0;
        try
        {
            long result = ParseOr(values, ref pos);
            return pos == values.Count ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static long ParseOr(List<string> t, ref int p)
    {
        long v = ParseAnd(t, ref p);
        while (p < t.Count && t[p] == "|") { p++; v |= ParseAnd(t, ref p); }
        return v;
    }

    static long ParseAnd(List<string> t, ref int p)
    {
        long v = ParseShift(t, ref p);
        while (p < t.Count && t[p] == "&") { p++; v &= ParseShift(t, ref p); }
        return v;
    }

    static long ParseShift(List<string> t, ref int p)
    {
        long v = ParseSum(t, ref p);
        while (p < t.Count && t[p] is "<<" or ">>")
        {
            string op = t[p++];
            long r = ParseSum(t, ref p);
            v = op == "<<" ? v << (int)r : v >> (int)r;
        }
        return v;
    }

    static long ParseSum(List<string> t, ref int p)
    {
        long v = ParseProduct(t, ref p);
        while (p < t.Count && t[p] is "+" or "-")
        {
            string op = t[p++];
            long r = ParseProduct(t, ref p);
            v = op == "+" ? v + r : v - r;
        }
        return v;
    }

    static long ParseProduct(List<string> t, ref int p)
    {
        long v = ParseUnary(t, ref p);
        while (p < t.Count && t[p] is "*" or "/" or "%")
        {
            string op = t[p++];
            long r = ParseUnary(t, ref p);
            v = op == "*" ? v * r : op == "/" ? v / r : v % r;
        }
        return v;
    }

    static long ParseUnary(List<string> t, ref int p)
    {
        if (t[p] == "-") { p++; return -ParseUnary(t, ref p); }
        if (t[p] == "~") { p++; return ~ParseUnary(t, ref p); }
        if (t[p] == "(")
        {
            p++;
            long v = ParseOr(t, ref p);
            if (t[p++] != ")") throw new FormatException();
            return v;
        }
        string n = t[p++].TrimEnd('l', 'L', 'u', 'U');
        return n.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? System.Convert.ToInt64(n[2..], 16) : long.Parse(n);
    }

    // ---- Text helpers ----

    static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                inString = !inString;
            if (inString)
                continue;
            if (c is '(' or '{' or '[')
                depth++;
            else if (c is ')' or '}' or ']')
                depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    static int MatchParen(string text, int open) => MatchPair(text, open, '(', ')');
    static int MatchBrace(string text, int open) => MatchPair(text, open, '{', '}');

    static int MatchPair(string text, int open, char a, char b)
    {
        int depth = 0;
        bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && text[i - 1] != '\\')
                inString = !inString;
            if (inString)
                continue;
            if (c == a)
                depth++;
            else if (c == b && --depth == 0)
                return i;
        }
        return -1;
    }
}
