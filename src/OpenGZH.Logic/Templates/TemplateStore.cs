using OpenGZH.Core.Rules;

namespace OpenGZH.Logic.Templates;

/// <summary>
/// Resolves object definitions from the rules, the way the original game built its ThingTemplates:
///   1. start from "DefaultThingTemplate" (rules/defaults/object.yaml) for every plain field;
///   2. if the object is a reskin ("ReskinOf: Parent"), start from the resolved parent instead;
///   3. apply the object's own fields on top. Whole fields replace; modules replace by tag.
/// Results are cached.
/// </summary>
public sealed class TemplateStore
{
    public const string DefaultTemplateName = "DefaultThingTemplate";

    readonly RuleSet rules;
    readonly Dictionary<string, ObjectTemplate?> cache = new(StringComparer.OrdinalIgnoreCase);

    public TemplateStore(RuleSet rules) => this.rules = rules;

    public RuleSet Rules => rules;

    /// <summary>Every object name defined in the rules.</summary>
    public IEnumerable<string> ObjectNames => rules.Section("Objects").Keys.Where(k => k != DefaultTemplateName);

    HashSet<string>? buildFacilities;

    /// <summary>
    /// The original's isBuildFacility: anything named in another object's Object prerequisites, and
    /// every command center (computer players count these to see who is still in the game).
    /// </summary>
    public bool IsBuildFacility(ObjectTemplate template)
    {
        if (buildFacilities == null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in ObjectNames)
            {
                if (Find(name) is not { } t || !t.Rules.TryGet("Prerequisites", out var pre) || !pre.IsMapping || !pre.TryGet("Object", out var lines))
                    continue;
                foreach (var line in lines.AsList())
                    foreach (string w in RuleValue.Words(line.Value))
                        set.Add(w);
            }
            buildFacilities = set;
        }
        return template.Is("COMMANDCENTER") || buildFacilities.Contains(template.Name);
    }

    public ObjectTemplate? Find(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (cache.TryGetValue(name, out var cached))
            return cached;

        cache[name] = null; // guards against reskin loops
        var node = Resolve(name, depth: 0);
        var template = node == null ? null : new ObjectTemplate(name, node);
        cache[name] = template;
        return template;
    }

    RuleNode? Resolve(string name, int depth)
    {
        var own = rules.Get("Objects", name);
        if (own == null || depth > 16)
            return null;

        RuleNode result;
        string? parent = own.GetString("ReskinOf") is { Length: > 0 } p ? p : null;
        if (parent != null && Resolve(parent, depth + 1) is { } parentNode)
        {
            result = parentNode;
        }
        else
        {
            result = rules.Get("Objects", DefaultTemplateName)?.DeepClone() ?? RuleNode.Mapping();
            result.Remove("Modules");
        }

        foreach (var (key, value) in own.Entries)
        {
            if (key.Equals("ReskinOf", StringComparison.OrdinalIgnoreCase))
                continue;

            if (key.Equals("Modules", StringComparison.OrdinalIgnoreCase) && value.IsMapping)
            {
                if (!result.TryGet("Modules", out var modules) || !modules.IsMapping)
                    modules = result.Set("Modules", RuleNode.Mapping());
                foreach (var (tag, module) in value.Entries)
                {
                    if (module.IsNull)
                        modules.Remove(tag);
                    else
                        modules.Set(tag, module.DeepClone());
                }
            }
            else if (value.IsNull)
            {
                result.Remove(key);
            }
            else
            {
                result.Set(key, value.DeepClone());
            }
        }

        return result;
    }
}
