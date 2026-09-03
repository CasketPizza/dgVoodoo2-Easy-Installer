namespace DgVoodooEasyInstaller.Core;

public enum GameArchitecture
{
    X86,
    X64,
    Arm64,
    Unknown
}

public enum GraphicsApi
{
    DirectDraw,
    Direct3DRetained,
    Direct3D8,
    Direct3D9,
    Glide,
    Glide2,
    Glide3,
    OpenGl
}

public sealed record GameAnalysis(
    string ExecutablePath,
    GameArchitecture Architecture,
    IReadOnlySet<GraphicsApi> Apis)
{
    public bool HasSupportedApi => Apis.Any(api => api != GraphicsApi.OpenGl);
}

public sealed record DgVoodooRelease(string Version, Uri DownloadUri);

public sealed record MesaRelease(string Version, Uri DownloadUri, string? Sha256);

public sealed record InstalledFile(string Name, string? BackupName);

public sealed record InstallManifest(
    int FormatVersion,
    string GameExecutable,
    string? DgVoodooVersion,
    string? MesaVersion,
    DateTimeOffset InstalledAt,
    List<string> GraphicsApis,
    List<InstalledFile> Files);

public enum InstallState
{
    NotInstalled,
    Managed,
    Unmanaged
}
