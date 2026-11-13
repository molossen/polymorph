using System.Reflection;
using SharpDX.D3DCompiler;

namespace MoloMax.Rendering;

/// <summary>Compiles the embedded HLSL at start-up. A Shaders folder beside the exe overrides the embedded copy (handy while tuning).</summary>
public static class ShaderLoader
{
    public static string Source(string name)
    {
        var loose = Path.Combine(AppContext.BaseDirectory, "Shaders", name);
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new FileNotFoundException("Embedded shader missing: " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The source as plain ASCII bytes. Passed as a string, the source was turned into the system's ANSI code page:
    /// on Chinese / Japanese / Korean Windows, a non-ASCII character in a comment (an em dash, a degree sign) could swallow
    /// the line break after it, and the shader failed with "unexpected end of file". Comments don't need those characters.</summary>
    public static byte[] AsciiSource(string src)
    {
        var b = new byte[src.Length];
        for (int i = 0; i < src.Length; i++) b[i] = src[i] < 128 ? (byte)src[i] : (byte)' ';
        return b;
    }

    public static ShaderBytecode Compile(string name, string entry, string profile)
    {
        var src = AsciiSource(Source(name));
        var res = ShaderBytecode.Compile(src, entry, profile, ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor, EffectFlags.None, name);
        if (res.Bytecode == null || res.HasErrors)
            throw new InvalidOperationException($"Shader {name}:{entry} failed to compile:\n{res.Message}");
        return res.Bytecode;
    }
}
