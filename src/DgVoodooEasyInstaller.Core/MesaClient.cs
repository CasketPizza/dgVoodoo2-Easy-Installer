using System.Security.Cryptography;
using System.Text.Json;

namespace DgVoodooEasyInstaller.Core;

public sealed class MesaClient(HttpClient httpClient)
{
    public const string LatestReleaseApi = "https://api.github.com/repos/pal1000/mesa-dist-win/releases/latest";

    public async Task<MesaRelease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("dgVoodoo2-Easy-Installer/1.0");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var root = json.RootElement;
        var version = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("The Mesa3D release has no version.");

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith("-release-msvc.7z", StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            return new MesaRelease(version, new Uri(url!), digest?.Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        throw new InvalidOperationException("The latest Mesa3D MSVC release package could not be found.");
    }

    public async Task DownloadAsync(MesaRelease release, string destination, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (total > 0)
                progress?.Report((int)(readTotal * 100 / total.Value));
        }
        await output.FlushAsync(cancellationToken);

        if (release.Sha256 is not null)
        {
            output.Close();
            await using var downloaded = File.OpenRead(destination);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Mesa3D package failed its published SHA-256 integrity check.");
        }
    }
}
