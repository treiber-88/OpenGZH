using System.Reflection;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.Modules;

/// <summary>
/// Creates modules by class name. Every non-abstract <see cref="Module"/> subclass in this assembly
/// is registered under its C# class name, which is the same as the original module name used in
/// the rules ("ActiveBody", "SlowDeathBehavior", ...).
///
/// Mods can add new module classes from their own assemblies with <see cref="Register"/>.
/// Draw and client-update modules are handled by the client, so the logic skips them.
/// </summary>
public static class ModuleRegistry
{
    static readonly Dictionary<string, Type> types = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> clientOnlyModuleTypes = new(StringComparer.OrdinalIgnoreCase) { "Draw", "ClientUpdate", "ClientBehavior" };

    /// <summary>Module classes the rules use that are not implemented yet (reported once each).</summary>
    public static SortedSet<string> MissingClasses { get; } = new(StringComparer.OrdinalIgnoreCase);

    static ModuleRegistry()
    {
        RegisterAssembly(typeof(ModuleRegistry).Assembly);
    }

    public static void RegisterAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
            if (typeof(Module).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                Register(type.Name, type);
    }

    public static void Register(string className, Type type) => types[className] = type;

    public static bool IsImplemented(string className) => types.ContainsKey(className);

    /// <summary>Creates the module for a definition, or null if it is client-only or not implemented.</summary>
    public static Module? Create(ModuleDefinition definition, GameObject owner)
    {
        if (clientOnlyModuleTypes.Contains(definition.ModuleType))
            return null;

        if (!types.TryGetValue(definition.ClassName, out var type))
        {
            if (MissingClasses.Add(definition.ClassName))
                Log.Warning($"Module class '{definition.ClassName}' is not implemented yet (used by {owner.Template.Name}).");
            return null;
        }

        var module = (Module)Activator.CreateInstance(type)!;
        module.Attach(owner, definition);
        return module;
    }
}
