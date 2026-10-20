using OpenGZH.Client.Graphics;
using OpenGZH.Formats.W3d;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.World;

/// <summary>GPU models by name, built once and shared by every object that shows them.</summary>
public sealed class ModelCache : IDisposable
{
    readonly GL gl;
    readonly W3dLibrary library;
    readonly Dictionary<string, RenderModel?> models = new(StringComparer.OrdinalIgnoreCase);

    public ModelCache(GL gl, W3dLibrary library)
    {
        this.gl = gl;
        this.library = library;
    }

    public W3dLibrary Library => library;

    public RenderModel? Get(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (!models.TryGetValue(name, out var model))
        {
            model = RenderModel.Build(gl, library, name);
            if (model == null)
                Logic.Log.Warning($"Model '{name}' was not found.");
            models[name] = model;
        }
        return model;
    }

    public void Dispose()
    {
        foreach (var model in models.Values)
            model?.Dispose();
        models.Clear();
    }
}
