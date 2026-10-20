namespace OpenGZH.Formats.Ini;

/// <summary>
/// Parses the original Generals / Zero Hour INI data files into a tree of <see cref="IniBlock"/>s.
///
/// The format:
///   ; comment            (also "//")
///   Object SomeName      opens a block (keyword + name, no '=')
///     Key = Value        property
///     Draw = W3DModelDraw ModuleTag_01    some keys with '=' also open a nested block
///       ...
///     End
///   End                  closes the innermost block (case-insensitive)
/// </summary>
public static class IniParser
{
    /// <summary>
    /// Keys that open a nested block even though they are written "Key = Value".
    /// (The original tokenizer treats '=' as optional, so "Key Value" and "Key = Value" mean the same.)
    /// </summary>
    static readonly HashSet<string> BlockKeysWithEquals = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draw", "Body", "Behavior", "ClientUpdate", "ClientBehavior",
        "ConditionState", "DefaultConditionState", "TransitionState",
    };

    /// <summary>
    /// Keys that open a nested block when written without '='. Any other key is a property,
    /// e.g. "AliasConditionState DAMAGED" or "ExtraPublicBone Splash04".
    /// </summary>
    static readonly HashSet<string> NestedBlockKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Object
        "ArmorSet", "WeaponSet", "UnitSpecificSounds", "UnitSpecificFX", "Prerequisites",
        "InheritableModule", "OverrideableByLikeKind", "ReplaceModule", "AddModule",
        // Modules
        "Turret", "AltTurret", "AttackAreaDecal", "TargetingReticleDecal", "GridDecalTemplate",
        // FXList nuggets
        "ParticleSystem", "Sound", "ViewShake", "TerrainScorch", "LightPulse", "Tracer",
        "FXListAtBonePos", "BuffNugget", "RayEffect", "CameraShakerVolume",
        // ObjectCreationList nuggets
        "CreateDebris", "CreateObject", "DeliverPayload", "DeliveryDecal", "FireWeapon",
        "ApplyRandomForce", "Attack",
        // Other systems
        "SideSounds", "Window", "SideInfo", "SkirmishBuildList", "Structure",
        "SkillSet1", "SkillSet2", "SkillSet3", "SkillSet4", "SkillSet5",
        "Mission", "ImagePart", "AnimationPart",
        "GeneralPersona0", "GeneralPersona1", "GeneralPersona2", "GeneralPersona3",
        "GeneralPersona4", "GeneralPersona5", "GeneralPersona6", "GeneralPersona7",
        "GeneralPersona8", "GeneralPersona9", "GeneralPersona10", "GeneralPersona11",
    };

    /// <summary>Top-level entries that are a single line with no "End".</summary>
    static readonly HashSet<string> SingleLineTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "BenchProfile", "LODPreset", "ReallyLowMHz",
    };

    static bool IsRadiusCursor(string key) => key.EndsWith("RadiusCursor", StringComparison.OrdinalIgnoreCase);

    public static IniDocument Parse(string text, string sourcePath)
    {
        var document = new IniDocument(sourcePath);
        var stack = new Stack<IniBlock>();

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
                continue;

            if (line.Equals("End", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count == 0)
                    throw new IniException(sourcePath, lineNumber, "'End' without an open block.");
                stack.Pop();
                continue;
            }

            int equals = line.IndexOf('=');
            string key = (equals >= 0 ? line[..equals] : FirstToken(line)).Trim();
            string value = equals >= 0 ? line[(equals + 1)..].Trim() : line[key.Length..].Trim();

            if (stack.Count == 0 && SingleLineTopLevelKeys.Contains(key))
            {
                var single = new IniBlock(key, value, lineNumber);
                document.Blocks.Add(single);
                continue;
            }

            bool opensBlock = stack.Count == 0
                || BlockKeysWithEquals.Contains(key)
                || equals < 0 && (NestedBlockKeys.Contains(key) || IsRadiusCursor(key));
            if (opensBlock)
            {
                var block = new IniBlock(key, value, lineNumber);
                if (stack.Count == 0)
                    document.Blocks.Add(block);
                else
                    stack.Peek().Children.Add(block);
                stack.Push(block);
            }
            else
            {
                stack.Peek().Children.Add(new IniProperty(key, value, lineNumber));
            }
        }

        if (stack.Count > 0)
            throw new IniException(sourcePath, stack.Peek().Line, $"Block '{stack.Peek()}' is never closed with 'End'.");

        return document;
    }

    static string StripComment(string line)
    {
        int semicolon = line.IndexOf(';');
        int slashes = line.IndexOf("//", StringComparison.Ordinal);
        int cut = semicolon >= 0 && slashes >= 0 ? Math.Min(semicolon, slashes) : Math.Max(semicolon, slashes);
        return cut >= 0 ? line[..cut] : line;
    }

    static string FirstToken(string line)
    {
        int space = line.IndexOfAny([' ', '\t']);
        return space < 0 ? line : line[..space];
    }
}

public sealed class IniException(string file, int line, string message)
    : Exception($"{file}({line}): {message}");
