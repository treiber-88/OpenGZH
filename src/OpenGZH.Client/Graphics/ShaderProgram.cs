using System.Numerics;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.Graphics;

/// <summary>A compiled and linked GLSL program with cached uniform locations.</summary>
public sealed class ShaderProgram : IDisposable
{
    readonly GL gl;
    readonly Dictionary<string, int> uniforms = new();

    public uint Handle { get; }

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        this.gl = gl;
        uint vertex = Compile(ShaderType.VertexShader, vertexSource);
        uint fragment = Compile(ShaderType.FragmentShader, fragmentSource);

        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vertex);
        gl.AttachShader(Handle, fragment);
        gl.LinkProgram(Handle);
        gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException("Shader link failed: " + gl.GetProgramInfoLog(Handle));

        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
    }

    uint Compile(ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
            throw new InvalidOperationException($"{type} compile failed: {gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Use() => gl.UseProgram(Handle);

    int Location(string name)
    {
        if (!uniforms.TryGetValue(name, out int location))
            uniforms[name] = location = gl.GetUniformLocation(Handle, name);
        return location;
    }

    public void Set(string name, int value) => gl.Uniform1(Location(name), value);
    public void Set(string name, float value) => gl.Uniform1(Location(name), value);
    public void Set(string name, Vector2 value) => gl.Uniform2(Location(name), value.X, value.Y);
    public void Set(string name, Vector3 value) => gl.Uniform3(Location(name), value.X, value.Y, value.Z);
    public void Set(string name, Vector4 value) => gl.Uniform4(Location(name), value.X, value.Y, value.Z, value.W);

    public unsafe void Set(string name, Matrix4x4 value)
    {
        // System.Numerics matrices are row-major with row vectors; GLSL is column-major with
        // column vectors. Uploading without transposing makes the two conventions line up.
        gl.UniformMatrix4(Location(name), 1, false, (float*)&value);
    }

    /// <summary>Uploads an array of matrices to a uniform array (e.g. "uBones") in one call.</summary>
    public unsafe void Set(string arrayName, ReadOnlySpan<Matrix4x4> values)
    {
        fixed (Matrix4x4* data = values)
            gl.UniformMatrix4(Location(arrayName + "[0]"), (uint)values.Length, false, (float*)data);
    }

    public void Dispose() => gl.DeleteProgram(Handle);
}
