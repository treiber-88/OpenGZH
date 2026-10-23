using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.UI.Gui;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Strings;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.UI;

/// <summary>Everything the interface draws with, created once and shared by every screen.</summary>
public sealed class UiResources : IDisposable
{
    public GameContent Content { get; }
    public TextureCache Textures { get; }
    public SpriteBatch Batch { get; }
    public ImageCatalog Images { get; }
    public FontLibrary Fonts { get; }
    public StringTable Strings { get; } = new();
    public GuiContext Gui { get; }

    /// <summary>Sound, speech and music for the whole session.</summary>
    public Audio.AudioManager Audio { get; }

    public UiResources(GL gl, GameContent content)
    {
        Content = content;
        Textures = new TextureCache(gl, content.FileSystem);
        Batch = new SpriteBatch(gl);
        Images = new ImageCatalog(content.Rules, Textures);
        Fonts = new FontLibrary(gl, content.RootFolder);

        if (content.FileSystem.Exists("Data/English/generals.csf"))
            Strings.LoadCsf(content.FileSystem.ReadAllBytes("Data/English/generals.csf"));
        // Mods (and OpenGZH's own additions) add or override text in their rules: "Strings: { LABEL: text }".
        foreach (var (label, text) in content.Rules.Section("Strings").Entries)
            if (text.Value != null)
                Strings.Set(label, text.Value);

        Gui = new GuiContext { Batch = Batch, Images = Images, Fonts = Fonts, Strings = Strings };
        Audio = new Audio.AudioManager(content.FileSystem, content.Rules);
    }

    /// <summary>The interface is laid out for 800x600 and stretched to the screen, like the original.</summary>
    public void Resize(int width, int height)
    {
        var scale = new Vector2(width / 800f, height / 600f);
        Gui.Scale = scale;
        float fontScale = Math.Min(scale.X, scale.Y);
        if (MathF.Abs(fontScale - Fonts.Scale) > 0.01f)
        {
            Fonts.Clear();
            Fonts.Scale = fontScale;
        }
    }

    public void Dispose()
    {
        Audio.Dispose();
        Fonts.Dispose();
        Batch.Dispose();
        Textures.Dispose();
    }
}
