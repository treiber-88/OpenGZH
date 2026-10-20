using OpenGZH.Core.FileSystem;

namespace OpenGZH.Formats.W3d;

/// <summary>
/// Finds and caches W3D assets by name, the way the original asset manager did:
///   model "AVLeopard"            → Art/W3D/AVLeopard.w3d (HLOD + meshes)
///   hierarchy "AIRngr_SKL"        → Art/W3D/AIRngr_SKL.w3d
///   animation "AIRngr_SKL.AIRngr_IDA" → the animation named AIRngr_IDA, in Art/W3D/AIRngr_IDA.w3d
/// Names are case-insensitive.
/// </summary>
public sealed class W3dLibrary
{
    readonly VirtualFileSystem fileSystem;
    readonly Dictionary<string, W3dFile?> files = new(StringComparer.OrdinalIgnoreCase);

    public W3dLibrary(VirtualFileSystem fileSystem) => this.fileSystem = fileSystem;

    /// <summary>Loads Art/W3D/&lt;name&gt;.w3d, or returns null if it does not exist.</summary>
    public W3dFile? GetFile(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (files.TryGetValue(name, out var cached))
            return cached;

        W3dFile? file = null;
        string path = $"Art/W3D/{name}.w3d";
        if (fileSystem.Exists(path))
        {
            try
            {
                file = W3dReader.Read(fileSystem.ReadAllBytes(path));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Could not read {path}: {e.Message}");
            }
        }

        files[name] = file;
        return file;
    }

    public W3dHierarchy? GetHierarchy(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        return GetFile(name)?.Hierarchy;
    }

    /// <summary>
    /// Finds an animation. Animation names in the data look like "Hierarchy.AnimName";
    /// the animation lives in the file named after the part after the dot.
    /// </summary>
    public W3dAnimation? GetAnimation(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        int dot = name.IndexOf('.');
        string animationName = dot >= 0 ? name[(dot + 1)..] : name;
        var file = GetFile(animationName) ?? (dot >= 0 ? GetFile(name[..dot]) : null);
        return file?.Animations.FirstOrDefault(a => a.Name.Equals(animationName, StringComparison.OrdinalIgnoreCase))
            ?? file?.Animations.FirstOrDefault();
    }

    /// <summary>Finds a mesh by its full name ("Container.Mesh"), looking in the container's file first.</summary>
    public W3dMesh? GetMesh(string fullName, W3dFile? preferredFile = null)
    {
        var mesh = preferredFile?.Meshes.FirstOrDefault(m => m.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase));
        if (mesh != null)
            return mesh;

        int dot = fullName.IndexOf('.');
        var file = GetFile(dot >= 0 ? fullName[..dot] : fullName);
        return file?.Meshes.FirstOrDefault(m => m.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase))
            ?? (dot < 0 ? file?.Meshes.FirstOrDefault() : null);
    }
}
