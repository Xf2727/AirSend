using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json.Serialization;

namespace AirSend.Windows;

internal sealed class LocalSendTransferService
{
    private readonly HttpClient _http;

    public LocalSendTransferService()
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AirSend/5.0 Windows");
    }

    public async Task SendAsync(DiscoveredDevice device, string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var fileId = Guid.NewGuid().ToString();
        var prepare = new PrepareUploadRequest(
            new DeviceInfo("AirSend Windows", "2.0", "Windows", "desktop", "airsend-windows", 53318, "http", true),
            new Dictionary<string, FileMetadata> { [fileId] = new(fileId, file.Name, file.Length, "application/octet-stream") });
        using var prepareResponse = await _http.PostAsJsonAsync(Endpoint(device, "prepare-upload"), prepare, cancellationToken);
        prepareResponse.EnsureSuccessStatusCode();
        var session = await prepareResponse.Content.ReadFromJsonAsync<PrepareUploadResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Receiver returned an empty prepare response.");
        if (!session.Files.TryGetValue(fileId, out var token)) throw new InvalidOperationException("Receiver did not return an upload token.");
        var upload = Endpoint(device, "upload") + $"?sessionId={Uri.EscapeDataString(session.SessionId)}&fileId={Uri.EscapeDataString(fileId)}&token={Uri.EscapeDataString(token)}";
        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = file.Length;
        using var response = await _http.PostAsync(upload, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string Endpoint(DiscoveredDevice device, string path) => $"{device.Protocol}://{device.Address}:{device.Port}/api/localsend/v2/{path}";
    private sealed record DeviceInfo([property: JsonPropertyName("alias")] string Alias, [property: JsonPropertyName("version")] string Version, [property: JsonPropertyName("deviceModel")] string DeviceModel, [property: JsonPropertyName("deviceType")] string DeviceType, [property: JsonPropertyName("fingerprint")] string Fingerprint, [property: JsonPropertyName("port")] int Port, [property: JsonPropertyName("protocol")] string Protocol, [property: JsonPropertyName("download")] bool Download);
    private sealed record FileMetadata([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("fileName")] string FileName, [property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("fileType")] string FileType);
    private sealed record PrepareUploadRequest([property: JsonPropertyName("info")] DeviceInfo Info, [property: JsonPropertyName("files")] Dictionary<string, FileMetadata> Files);
    private sealed class PrepareUploadResponse
    {
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
        [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = new();
    }
}
