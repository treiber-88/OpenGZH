using OpenGZH.Formats.Images;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Graphics;

/// <summary>An OpenGL 2D texture created from a decoded <see cref="Image"/>.</summary>
public sealed class Texture : IDisposable
{
    readonly GL gl;

    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>True if any pixel is not fully opaque (useful for choosing a blend mode).</summary>
    public bool HasAlpha { get; }

    public unsafe Texture(GL gl, Image image, bool repeat = true, bool mipmaps = true, bool nearest = false)
    {
        this.gl = gl;
        Width = image.Width;
        Height = image.Height;

        for (int i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] != 255)
            {
                HasAlpha = true;
                break;
            }
        }

        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        fixed (byte* pixels = image.Pixels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)Width, (uint)Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        var wrap = repeat ? GLEnum.Repeat : GLEnum.ClampToEdge;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);

        if (nearest)
        {
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        }
        else if (mipmaps)
        {
            gl.GenerateMipmap(TextureTarget.Texture2D);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        }
        else
        {
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        }
    }

    public void Bind(int unit = 0)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    public void Dispose() => gl.DeleteTexture(Handle);
}
