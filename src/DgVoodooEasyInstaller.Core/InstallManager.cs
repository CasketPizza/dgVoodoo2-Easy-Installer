using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using SharpCompress.Archives;

namespace DgVoodooEasyInstaller.Core;

public sealed class InstallManager
{
    public const string MetadataDirectoryName = ".dgvoodoo-easy-installer";
    public const string ManifestFileName = "manifest.json";

    private static readonly string[] KnownCompatibilityFiles =
    [
        "DDraw.dll", "D3DImm.dll", "D3DRM.dll", "D3D8.dll", "D3D9.dll",
        "Glide.dll", "Glide2x.dll", "Glide3x.dll", "dgVoodoo.conf", "dgVoodooCpl.exe",
        "opengl32.dll", "libgallium_wgl.dll", "dxil.dll"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public InstallState GetInstallState(string gameDirectory)
    {
        if (File.Exists(GetManifestPath(gameDirectory)))
            return InstallState.Managed;

        foreach (var name in KnownCompatibilityFiles.Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(gameDirectory, name);
            if (!File.Exists(path))
                continue;

            var info = FileVersionInfo.GetVersionInfo(path);
            if ((info.ProductName?.Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (info.LegalCopyright?.Contains("Dege", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (info.ProductName?.Contains("Mesa", StringComparison.OrdinalIgnoreCase) ?? false))
                return InstallState.Unmanaged;
        }

        return InstallState.NotInstalled;
    }

    public async Task<InstallManifest> InstallAsync(
        GameAnalysis game,
        IReadOnlyCollection<GraphicsApi> apis,
        string? dgVoodooVersion,
        string? archivePath,
        string? d3drmArchivePath = null,
        string? mesaVersion = null,
        string? mesaArchivePath = null,
        CancellationToken cancellationToken = default)
    {
        if (game.Architecture == GameArchitecture.Unknown)
            throw new InvalidOperationException("The executable architecture is not supported or could not be detected.");
        if (apis.Count == 0)
            throw new InvalidOperationException("Select at least one graphics API.");

        var gameDirectory = Path.GetDirectoryName(game.ExecutablePath)!;
        var metadataDirectory = Path.Combine(gameDirectory, MetadataDirectoryName);
        if (Directory.Exists(metadataDirectory))
            throw new InvalidOperationException("Installer metadata already exists. Uninstall the current managed installation first.");

        Directory.CreateDirectory(metadataDirectory);
        var installed = new List<InstalledFile>();
        try
        {
            var mappings = BuildMappings(game.Architecture, apis);
            if (mappings.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(archivePath))
                    throw new InvalidOperationException("A dgVoodoo2 archive is required for the selected APIs.");
                using var archive = ZipFile.OpenRead(archivePath);
                ValidateArchive(archive, mappings.Select(mapping => mapping.Source));
                foreach (var mapping in mappings)
                {
                    await InstallZipEntryAsync(archive, mapping.Source, mapping.Target, gameDirectory,
                        metadataDirectory, installed, cancellationToken);
                }
            }

            if (apis.Contains(GraphicsApi.Direct3DRetained))
            {
                if (game.Architecture != GameArchitecture.X86)
                    throw new InvalidOperationException("The official D3DRM package is only available for 32-bit games.");
                if (string.IsNullOrWhiteSpace(d3drmArchivePath))
                    throw new InvalidOperationException("The D3DRM archive is required for this game.");
                using var d3drmArchive = ZipFile.OpenRead(d3drmArchivePath);
                ValidateArchive(d3drmArchive, ["d3drm.dll"]);
                await InstallZipEntryAsync(d3drmArchive, "d3drm.dll", "D3DRM.dll", gameDirectory,
                    metadataDirectory, installed, cancellationToken);
            }

            if (apis.Contains(GraphicsApi.OpenGl))
            {
                if (game.Architecture == GameArchitecture.Arm64)
                    throw new InvalidOperationException("This Mesa3D distribution does not provide ARM64 binaries.");
                if (string.IsNullOrWhiteSpace(mesaArchivePath))
                    throw new InvalidOperationException("A Mesa3D archive is required for OpenGL.");
                await InstallMesaAsync(mesaArchivePath, game.Architecture, gameDirectory, metadataDirectory,
                    installed, cancellationToken);
            }

            var manifest = new InstallManifest(2, Path.GetFileName(game.ExecutablePath), dgVoodooVersion,
                mesaVersion,
                DateTimeOffset.UtcNow, apis.Select(api => api.ToString()).ToList(), installed);
            await File.WriteAllTextAsync(GetManifestPath(gameDirectory), JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
            return manifest;
        }
        catch
        {
            RollBack(gameDirectory, metadataDirectory, installed);
            throw;
        }
    }

    public async Task<InstallManifest> ReadManifestAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(GetManifestPath(gameDirectory));
        return await JsonSerializer.DeserializeAsync<InstallManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The installation manifest is invalid.");
    }

    public async Task UninstallAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(gameDirectory, cancellationToken);
        var metadataDirectory = Path.Combine(gameDirectory, MetadataDirectoryName);

        foreach (var file in manifest.Files)
        {
            ValidateManifestName(file.Name);
            if (file.BackupName is null)
                continue;
            ValidateManifestName(file.BackupName);
            if (!File.Exists(Path.Combine(metadataDirectory, file.BackupName)))
                throw new InvalidDataException($"Required backup is missing: {file.BackupName}");
        }

        foreach (var file in manifest.Files.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(gameDirectory, file.Name);
            if (File.Exists(target))
                File.Delete(target);
            if (file.BackupName is not null)
            {
                var backup = Path.Combine(metadataDirectory, file.BackupName);
                File.Move(backup, target);
            }
        }

        Directory.Delete(metadataDirectory, true);
    }

    public void RemoveUnmanaged(string gameDirectory)
    {
        var mesaDetected = IsMesaFile(Path.Combine(gameDirectory, "opengl32.dll"));
        foreach (var name in KnownCompatibilityFiles)
        {
            var path = Path.Combine(gameDirectory, name);
            if (File.Exists(path) && (IsDgVoodooFile(path, name) ||
                (mesaDetected && name is "opengl32.dll" or "libgallium_wgl.dll" or "dxil.dll")))
                File.Delete(path);
        }
    }

    private static bool IsDgVoodooFile(string path, string name)
    {
        if (name.StartsWith("dgVoodoo", StringComparison.OrdinalIgnoreCase))
            return true;
        var info = FileVersionInfo.GetVersionInfo(path);
        return (info.ProductName?.Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (info.LegalCopyright?.Contains("Dege", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool IsMesaFile(string path)
    {
        if (!File.Exists(path)) return false;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName?.Contains("Mesa", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static List<(string Source, string Target)> BuildMappings(GameArchitecture architecture,
        IReadOnlyCollection<GraphicsApi> apis)
    {
        if (architecture != GameArchitecture.X86 &&
            (apis.Contains(GraphicsApi.DirectDraw) || apis.Contains(GraphicsApi.Direct3D8)))
            throw new InvalidOperationException("dgVoodoo2 only provides DirectX 1-8 wrappers for 32-bit games.");

        var glideArchitectureFolder = architecture switch
        {
            GameArchitecture.X86 => "x86",
            GameArchitecture.X64 => "x64",
            GameArchitecture.Arm64 => "arm64",
            _ => throw new InvalidOperationException("Unsupported executable architecture.")
        };
        var hasDgVoodooApi = apis.Any(api => api is GraphicsApi.DirectDraw or GraphicsApi.Direct3D8 or
            GraphicsApi.Direct3D9 or GraphicsApi.Glide or GraphicsApi.Glide2 or GraphicsApi.Glide3);
        var result = hasDgVoodooApi
            ? new List<(string, string)> { ("dgVoodoo.conf", "dgVoodoo.conf"), ("dgVoodooCpl.exe", "dgVoodooCpl.exe") }
            : [];

        void AddMs(string name)
        {
            var folder = architecture == GameArchitecture.Arm64 ? "arm64x" : glideArchitectureFolder;
            result.Add(($"MS/{folder}/{name}", name));
        }
        void AddGlide(string name) => result.Add(($"3Dfx/{glideArchitectureFolder}/{name}", name));
        if (apis.Contains(GraphicsApi.DirectDraw))
        {
            AddMs("DDraw.dll");
            AddMs("D3DImm.dll");
        }
        if (apis.Contains(GraphicsApi.Direct3D8)) AddMs("D3D8.dll");
        if (apis.Contains(GraphicsApi.Direct3D9)) AddMs("D3D9.dll");
        if (apis.Contains(GraphicsApi.Glide)) AddGlide("Glide.dll");
        if (apis.Contains(GraphicsApi.Glide2)) AddGlide("Glide2x.dll");
        if (apis.Contains(GraphicsApi.Glide3)) AddGlide("Glide3x.dll");
        return result.DistinctBy(mapping => mapping.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ValidateArchive(ZipArchive archive, IEnumerable<string> requiredEntries)
    {
        foreach (var entry in requiredEntries)
        {
            var archiveEntry = archive.GetEntry(entry);
            if (archiveEntry is null)
                throw new InvalidDataException($"The official archive does not contain the required file: {entry}");
            if (archiveEntry.Length > 256 * 1024 * 1024)
                throw new InvalidDataException($"The archive entry is unexpectedly large: {entry}");
        }
    }

    private static async Task InstallZipEntryAsync(ZipArchive archive, string sourceName, string targetName,
        string gameDirectory, string metadataDirectory, List<InstalledFile> installed,
        CancellationToken cancellationToken)
    {
        await using var source = archive.GetEntry(sourceName)!.Open();
        await InstallStreamAsync(source, targetName, gameDirectory, metadataDirectory, installed, cancellationToken);
    }

    private static async Task InstallMesaAsync(string archivePath, GameArchitecture architecture,
        string gameDirectory, string metadataDirectory, List<InstalledFile> installed,
        CancellationToken cancellationToken)
    {
        var folder = architecture == GameArchitecture.X86 ? "x86/" : "x64/";
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{folder}opengl32.dll"] = "opengl32.dll",
            [$"{folder}libgallium_wgl.dll"] = "libgallium_wgl.dll",
            [$"{folder}dxil.dll"] = "dxil.dll"
        };
        using var archive = ArchiveFactory.Open(archivePath);
        var entries = archive.Entries
            .Where(entry => !entry.IsDirectory && entry.Key is not null)
            .ToDictionary(entry => entry.Key!.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        foreach (var sourceName in required.Keys)
        {
            if (!entries.ContainsKey(sourceName))
                throw new InvalidDataException($"The Mesa3D archive does not contain the required file: {sourceName}");
            if (entries[sourceName].Size > 256 * 1024 * 1024)
                throw new InvalidDataException($"The Mesa3D archive entry is unexpectedly large: {sourceName}");
        }

        foreach (var mapping in required)
        {
            await using var source = entries[mapping.Key].OpenEntryStream();
            await InstallStreamAsync(source, mapping.Value, gameDirectory, metadataDirectory, installed, cancellationToken);
        }
    }

    private static async Task InstallStreamAsync(Stream source, string targetName, string gameDirectory,
        string metadataDirectory, List<InstalledFile> installed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.Combine(gameDirectory, targetName);
        string? backupName = null;
        if (File.Exists(target))
        {
            backupName = $"{installed.Count:D2}-{targetName}.bak";
            File.Move(target, Path.Combine(metadataDirectory, backupName));
        }

        installed.Add(new InstalledFile(targetName, backupName));
        await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static string GetManifestPath(string gameDirectory) =>
        Path.Combine(gameDirectory, MetadataDirectoryName, ManifestFileName);

    private static void ValidateManifestName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            throw new InvalidDataException("The installation manifest contains an unsafe file name.");
    }

    private static void RollBack(string gameDirectory, string metadataDirectory, IEnumerable<InstalledFile> installed)
    {
        foreach (var file in installed.Reverse())
        {
            var target = Path.Combine(gameDirectory, file.Name);
            if (File.Exists(target)) File.Delete(target);
            if (file.BackupName is not null)
            {
                var backup = Path.Combine(metadataDirectory, file.BackupName);
                if (File.Exists(backup)) File.Move(backup, target);
            }
        }
        if (Directory.Exists(metadataDirectory)) Directory.Delete(metadataDirectory, true);
    }
}
