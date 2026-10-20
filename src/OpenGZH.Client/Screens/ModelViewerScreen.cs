using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Platform;
using Silk.NET.Input;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Screens;

/// <summary>
/// Shows a single W3D model, optionally animated. Used to check art imports and by modders
/// to preview models: drag with the mouse to orbit, scroll to zoom.
/// </summary>
public sealed class ModelViewerScreen : IScreen
{
    readonly GL gl;
    readonly TextureCache textures;
    readonly ModelRenderer renderer;
    readonly Camera camera = new();
    readonly SceneLighting lighting = new();
    readonly List<ModelInstance> instances = new();
    readonly RenderModel? model;

    Vector2 lastMouse;
    bool dragging;

    public ModelViewerScreen(GL gl, IInputContext input, GameContent content, string modelName, string? animationName, uint houseColor)
    {
        this.gl = gl;
        textures = new TextureCache(gl, content.FileSystem);
        renderer = new ModelRenderer(gl, textures);

        model = RenderModel.Build(gl, content.Models, modelName);
        if (model == null)
        {
            Console.Error.WriteLine($"Model '{modelName}' was not found in Art/W3D.");
            return;
        }

        var instance = new ModelInstance(model) { HouseColor = houseColor };
        if (animationName != null)
        {
            instance.Animation = content.Models.GetAnimation(animationName);
            if (instance.Animation == null)
                Console.Error.WriteLine($"Animation '{animationName}' was not found.");
        }
        instance.UpdatePose();
        instances.Add(instance);

        camera.Frame(model.BoundsMin, model.BoundsMax);
        camera.Pitch = 0.45f;
        camera.Yaw = MathF.PI * 1.25f;
        Console.WriteLine($"{modelName}: {model.Meshes.Count} meshes, {model.Hierarchy?.Pivots.Count ?? 0} bones, bounds {model.BoundsMin} - {model.BoundsMax}");

        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (_, button) => { if (button == MouseButton.Left) dragging = true; };
            mouse.MouseUp += (_, button) => { if (button == MouseButton.Left) dragging = false; };
            mouse.MouseMove += (_, position) =>
            {
                if (dragging)
                {
                    camera.Yaw -= (position.X - lastMouse.X) * 0.01f;
                    camera.Pitch = Math.Clamp(camera.Pitch + (position.Y - lastMouse.Y) * 0.01f, 0.05f, 1.5f);
                }
                lastMouse = position;
            };
            mouse.Scroll += (_, wheel) => camera.Distance *= wheel.Y > 0 ? 0.9f : 1.1f;
        }
    }

    public void Resize(int width, int height) => camera.AspectRatio = width / (float)Math.Max(1, height);

    public void Update(float seconds)
    {
        foreach (var instance in instances)
            instance.Advance(seconds);
    }

    public void Render(float seconds)
    {
        gl.ClearColor(0.18f, 0.2f, 0.24f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        renderer.Draw(instances, camera.ViewProjection, camera.Position, lighting);
    }

    public void Dispose()
    {
        model?.Dispose();
        renderer.Dispose();
        textures.Dispose();
    }
}
