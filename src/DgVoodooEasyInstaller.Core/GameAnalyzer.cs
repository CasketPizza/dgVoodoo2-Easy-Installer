using System.Buffers.Binary;
using System.Text;

namespace DgVoodooEasyInstaller.Core;

public static class GameAnalyzer
{
    private static readonly Dictionary<string, GraphicsApi> ApiNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ddraw.dll"] = GraphicsApi.DirectDraw,
        ["d3dim.dll"] = GraphicsApi.DirectDraw,
        ["d3drm.dll"] = GraphicsApi.Direct3DRetained,
        ["d3d8.dll"] = GraphicsApi.Direct3D8,
        ["d3d9.dll"] = GraphicsApi.Direct3D9,
        ["glide.dll"] = GraphicsApi.Glide,
        ["glide2x.dll"] = GraphicsApi.Glide2,
        ["glide3x.dll"] = GraphicsApi.Glide3,
        ["opengl32.dll"] = GraphicsApi.OpenGl
    };

    public static GameAnalysis Analyze(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var bytes = File.ReadAllBytes(executablePath);
        var architecture = ReadArchitecture(bytes);
        var text = Encoding.Latin1.GetString(bytes);
        var apis = ApiNames
            .Where(pair => text.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .ToHashSet();

        return new GameAnalysis(Path.GetFullPath(executablePath), architecture, apis);
    }

    public static GameArchitecture ReadArchitecture(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            return GameArchitecture.Unknown;

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(0x3c, 4));
        if (peOffset < 0 || peOffset > bytes.Length - 6 ||
            bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
            return GameArchitecture.Unknown;

        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(peOffset + 4, 2)) switch
        {
            0x014c => GameArchitecture.X86,
            0x8664 => GameArchitecture.X64,
            0xaa64 => GameArchitecture.Arm64,
            _ => GameArchitecture.Unknown
        };
    }
}
