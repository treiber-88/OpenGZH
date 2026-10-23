using OpenGZH.Logic;
using OpenGZH.Logic.Vision;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.World;

/// <summary>
/// The local player's fog of war as a one-texel-per-cell texture, like the original's W3DShroud:
/// ClearAlpha, FogAlpha and ShroudAlpha (GameData) for clear, fogged and shrouded cells, filtered so
/// the edges are soft. Terrain and models multiply their colour by it.
/// </summary>
public sealed class ShroudTexture : IDisposable
{
    readonly GL gl;
    readonly ShroudManager shroud;
    readonly byte[] texels;
    readonly byte clearAlpha, fogAlpha, shroudAlpha;
    bool dirty = true;

    public uint Handle { get; }
    public int PlayerIndex { get; set; }

    /// <summary>World units covered by the texture (cells x cell size).</summary>
    public float WorldWidth => shroud.CellsX * shroud.CellSize;
    public float WorldHeight => shroud.CellsY * shroud.CellSize;

    public unsafe ShroudTexture(GL gl, GameLogic game, int playerIndex)
    {
        this.gl = gl;
        shroud = game.Shroud;
        PlayerIndex = playerIndex;
        var data = game.Catalog.GameData;
        clearAlpha = (byte)data.ClearAlpha;
        fogAlpha = (byte)data.FogAlpha;
        shroudAlpha = (byte)data.ShroudAlpha;
        texels = new byte[shroud.CellsX * shroud.CellsY];
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = texels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)shroud.CellsX, (uint)shroud.CellsY, 0, PixelFormat.Red, PixelType.UnsignedByte, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        shroud.CellChanged += (player, _, _, _) => { if (player == PlayerIndex) dirty = true; };
    }

    /// <summary>Re-uploads the texture if anything changed for the local player.</summary>
    public unsafe void Refresh()
    {
        if (!dirty)
            return;
        dirty = false;
        for (int y = 0; y < shroud.CellsY; y++)
            for (int x = 0; x < shroud.CellsX; x++)
                texels[y * shroud.CellsX + x] = shroud.CellStatus(PlayerIndex, x, y) switch
                {
                    CellShroud.Clear => clearAlpha,
                    CellShroud.Fogged => fogAlpha,
                    _ => shroudAlpha,
                };
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = texels)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)shroud.CellsX, (uint)shroud.CellsY, PixelFormat.Red, PixelType.UnsignedByte, p);
    }

    public void Bind(int unit)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose() => gl.DeleteTexture(Handle);
}
