using System.IO.Compression;
using DgVoodooEasyInstaller.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("PE and API detection", TestAnalysis),
    ("Release page parsing", TestReleaseParsing),
    ("Install, backup, and uninstall", TestInstallRoundTrip)
};
var mesaTestArchive = Environment.GetEnvironmentVariable("MESA_TEST_ARCHIVE");
if (File.Exists(mesaTestArchive))
    tests.Add(("Official Mesa3D archive", () => TestMesaArchive(mesaTestArchive!)));
if (Environment.GetEnvironmentVariable("ONLINE_TESTS") == "1")
    tests.Add(("Official dgVoodoo2 downloads", TestOfficialDownloads));

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static Task TestAnalysis()
{
    var bytes = new byte[256];
    bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[0x3c] = 0x80;
    bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x84] = 0x4c; bytes[0x85] = 0x01;
    "d3d8.dll\0Glide2x.dll\0d3drm.dll\0opengl32.dll\0"u8.CopyTo(bytes.AsSpan(0xa0));
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, bytes);
    try
    {
        var result = GameAnalyzer.Analyze(path);
        Equal(GameArchitecture.X86, result.Architecture);
        True(result.Apis.SetEquals([
            GraphicsApi.Direct3D8, GraphicsApi.Glide2, GraphicsApi.Direct3DRetained, GraphicsApi.OpenGl
        ]));
    }
    finally { File.Delete(path); }
    return Task.CompletedTask;
}

static Task TestReleaseParsing()
{
    const string html = "<h3>Latest stable version</h3><a href='..\\bin\\dgVoodoo2_87_4.zip'>dgVoodoo</a>";
    var release = ReleaseClient.ParseLatest(html);
    Equal("2.87.4", release.Version);
    Equal("dgVoodoo2_87_4.zip", Path.GetFileName(release.DownloadUri.LocalPath));
    return Task.CompletedTask;
}

static async Task TestInstallRoundTrip()
{
    var root = Path.Combine(Path.GetTempPath(), $"dgvoodoo-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var game = Path.Combine(root, "game.exe");
    var archivePath = Path.Combine(root, "package.zip");
    var mesaPath = Path.Combine(root, "mesa.zip");
    var d3drmPath = Path.Combine(root, "d3drm.zip");
    File.WriteAllText(game, "game");
    File.WriteAllText(Path.Combine(root, "D3D8.dll"), "original");
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
        Add(archive, "dgVoodoo.conf", "config");
        Add(archive, "dgVoodooCpl.exe", "cpl");
        Add(archive, "MS/x86/D3D8.dll", "wrapper");
    }
    using (var archive = ZipFile.Open(mesaPath, ZipArchiveMode.Create))
    {
        Add(archive, "x86/opengl32.dll", "mesa-opengl");
        Add(archive, "x86/libgallium_wgl.dll", "mesa-gallium");
        Add(archive, "x86/dxil.dll", "dxil");
    }
    using (var archive = ZipFile.Open(d3drmPath, ZipArchiveMode.Create))
        Add(archive, "d3drm.dll", "retained-mode");

    try
    {
        var manager = new InstallManager();
        var analysis = new GameAnalysis(game, GameArchitecture.X86, new HashSet<GraphicsApi> { GraphicsApi.Direct3D8 });
        await manager.InstallAsync(analysis,
            [GraphicsApi.Direct3D8, GraphicsApi.Direct3DRetained, GraphicsApi.OpenGl], "test", archivePath,
            d3drmPath, "test-mesa", mesaPath);
        Equal("wrapper", File.ReadAllText(Path.Combine(root, "D3D8.dll")));
        Equal("retained-mode", File.ReadAllText(Path.Combine(root, "D3DRM.dll")));
        Equal("mesa-opengl", File.ReadAllText(Path.Combine(root, "opengl32.dll")));
        Equal(InstallState.Managed, manager.GetInstallState(root));
        await manager.UninstallAsync(root);
        Equal("original", File.ReadAllText(Path.Combine(root, "D3D8.dll")));
        True(!Directory.Exists(Path.Combine(root, InstallManager.MetadataDirectoryName)));
    }
    finally { Directory.Delete(root, true); }
}

static async Task TestMesaArchive(string archivePath)
{
    var root = Path.Combine(Path.GetTempPath(), $"mesa-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var game = Path.Combine(root, "game.exe");
    File.WriteAllText(game, "game");
    try
    {
        var manager = new InstallManager();
        var analysis = new GameAnalysis(game, GameArchitecture.X64, new HashSet<GraphicsApi> { GraphicsApi.OpenGl });
        await manager.InstallAsync(analysis, [GraphicsApi.OpenGl], null, null,
            mesaVersion: "integration-test", mesaArchivePath: archivePath);
        True(new[] { "opengl32.dll", "libgallium_wgl.dll", "dxil.dll" }
            .All(name => File.Exists(Path.Combine(root, name))));
        await manager.UninstallAsync(root);
    }
    finally { Directory.Delete(root, true); }
}

static async Task TestOfficialDownloads()
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    var client = new ReleaseClient(httpClient);
    var package = Path.Combine(Path.GetTempPath(), $"dgvoodoo-online-{Guid.NewGuid():N}.zip");
    var d3drm = Path.Combine(Path.GetTempPath(), $"d3drm-online-{Guid.NewGuid():N}.zip");
    try
    {
        var release = await client.GetLatestAsync();
        await client.DownloadAsync(release, package);
        using (var archive = ZipFile.OpenRead(package))
            True(archive.GetEntry("dgVoodoo.conf") is not null);

        await client.DownloadFileAsync(await client.GetD3DrmDownloadAsync(), d3drm);
        using var d3drmArchive = ZipFile.OpenRead(d3drm);
        True(d3drmArchive.GetEntry("d3drm.dll") is not null);

        var mesa = await new MesaClient(httpClient).GetLatestAsync();
        True(mesa.DownloadUri.AbsolutePath.EndsWith("-release-msvc.7z", StringComparison.OrdinalIgnoreCase));
        True(mesa.Sha256 is { Length: 64 });
    }
    finally
    {
        if (File.Exists(package)) File.Delete(package);
        if (File.Exists(d3drm)) File.Delete(d3drm);
    }
}

static void Add(ZipArchive archive, string name, string value)
{
    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
    writer.Write(value);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}

static void True(bool value)
{
    if (!value) throw new Exception("Expected true");
}
