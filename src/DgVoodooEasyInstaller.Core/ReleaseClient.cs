using System.Net;
using System.Text.RegularExpressions;

namespace DgVoodooEasyInstaller.Core;

public sealed partial class ReleaseClient(HttpClient httpClient)
{
    public const string DownloadsPage = "https://dege.fw.hu/dgVoodoo2/dgVoodoo2/index.html";

    public async Task<DgVoodooRelease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var html = await httpClient.GetStringAsync(DownloadsPage, cancellationToken);
        return ParseLatest(html);
    }

    public async Task DownloadAsync(DgVoodooRelease release, string destination, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
        => await DownloadFileAsync(release.DownloadUri, destination, progress, cancellationToken);

    public async Task<Uri> GetD3DrmDownloadAsync(CancellationToken cancellationToken = default)
    {
        var html = await httpClient.GetStringAsync(DownloadsPage, cancellationToken);
        var match = D3DrmLinkRegex().Match(WebUtility.HtmlDecode(html));
        if (!match.Success)
            throw new InvalidOperationException("The D3DRM download could not be found on the official page.");
        return new Uri("https://dege.fw.hu/dgVoodoo2/bin/D3DRM.zip");
    }

    public async Task DownloadFileAsync(Uri uri, string destination, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendFromOfficialHosts(uri, cancellationToken);
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
    }

    public static DgVoodooRelease ParseLatest(string html)
    {
        var match = ReleaseLinkRegex().Match(WebUtility.HtmlDecode(html));
        if (!match.Success)
            throw new InvalidOperationException("The latest dgVoodoo2 download could not be found on the official page.");

        var fileName = match.Groups["file"].Value;
        var version = $"2.{match.Groups["version"].Value.Replace('_', '.')}";
        return new DgVoodooRelease(version, new Uri($"https://dege.fw.hu/dgVoodoo2/bin/{fileName}"));
    }

    private async Task<HttpResponseMessage> SendFromOfficialHosts(Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage? lastResponse = null;
        var hosts = new[] { "dege.fw.hu", "dege.freeweb.hu" };
        var schemes = new[] { Uri.UriSchemeHttps, Uri.UriSchemeHttp };
        foreach (var host in hosts)
        foreach (var scheme in schemes)
        {
            lastResponse?.Dispose();
            var candidate = new UriBuilder(uri) { Host = host, Scheme = scheme, Port = -1 }.Uri;
            lastResponse = await httpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (lastResponse.IsSuccessStatusCode)
                return lastResponse;
        }

        return lastResponse!;
    }

    [GeneratedRegex("href=[\\\"'][^\\\"']*(?<file>dgVoodoo2_(?<version>[0-9_]+)\\.zip)[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseLinkRegex();

    [GeneratedRegex("href=[\\\"'][^\\\"']*D3DRM\\.zip[\\\"']", RegexOptions.IgnoreCase)]
    private static partial Regex D3DrmLinkRegex();
}
