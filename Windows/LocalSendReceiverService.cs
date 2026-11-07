using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Windows;

namespace AirSend.Windows;

internal sealed class LocalSendReceiverService : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, PreparedTransfer> _transfers = new();
    private readonly string _downloadDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AirSend");
    private CancellationTokenSource? _stop;

    public int Port => 53318;

    public Task StartAsync()
    {
        Directory.CreateDirectory(_downloadDirectory);
        _listener.Prefixes.Add($"http://+:{Port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"无法监听 TCP {Port}，请允许 AirSend 通过 Windows 防火墙或使用管理员权限运行。", ex);
        }
        _stop = new CancellationTokenSource();
        return Task.Run(() => ListenAsync(_stop.Token));
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(context), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            var path = context.Request.Url?.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path == "/api/localsend/v2/info")
            {
                await WriteJsonAsync(context, new { alias = "AirSend Windows", version = "2.0", deviceModel = "Windows", deviceType = "desktop", fingerprint = "airsend-windows", port = Port, protocol = "http", download = true });
                return;
            }
            if (context.Request.HttpMethod == "POST" && path == "/api/localsend/v2/prepare-upload")
            {
                var request = await JsonSerializer.DeserializeAsync<PrepareUploadRequest>(context.Request.InputStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException();
                var sessionId = Guid.NewGuid().ToString();
                var tokens = request.Files.Keys.ToDictionary(id => id, _ => Guid.NewGuid().ToString("N"));
                lock (_transfers) _transfers[sessionId] = new PreparedTransfer(request.Files, tokens);
                await WriteJsonAsync(context, new { sessionId, files = tokens });
                return;
            }
            if (context.Request.HttpMethod == "POST" && path == "/api/localsend/v2/upload")
            {
                await ReceiveFileAsync(context);
                return;
            }
            context.Response.StatusCode = 404;
        }
        catch { context.Response.StatusCode = 400; }
        finally { context.Response.Close(); }
    }

    private async Task ReceiveFileAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        var sessionId = query["sessionId"] ?? throw new InvalidDataException();
        var fileId = query["fileId"] ?? throw new InvalidDataException();
        var token = query["token"] ?? throw new InvalidDataException();
        PreparedTransfer transfer;
        lock (_transfers) if (!_transfers.TryGetValue(sessionId, out transfer!)) throw new InvalidDataException();
        if (!transfer.Tokens.TryGetValue(fileId, out var expected) || expected != token || !transfer.Files.TryGetValue(fileId, out var file)) throw new InvalidDataException();
        var isClipboard = string.Equals(file.FileName, "clipboard.txt", StringComparison.OrdinalIgnoreCase);
        var isClipboardImage = file.FileName.Contains("Clipboard", StringComparison.OrdinalIgnoreCase) &&
            new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase);
        var destination = UniquePath(Path.Combine(_downloadDirectory, Path.GetFileName(file.FileName)));
        await using var output = File.Create(destination);
        await context.Request.InputStream.CopyToAsync(output);
        if (isClipboard)
        {
            var text = await File.ReadAllTextAsync(destination);
            ClipboardSyncGuard.SuppressNextChange();
            Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
            File.Delete(destination);
        }
        else if (isClipboardImage)
        {
            ClipboardSyncGuard.SuppressNextChange();
            Application.Current.Dispatcher.Invoke(() =>
            {
                using var stream = File.OpenRead(destination);
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit();
                Clipboard.SetImage(image);
            });
        }
        lock (_transfers) _transfers.Remove(sessionId);
        context.Response.StatusCode = 200;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++) { var candidate = Path.Combine(directory, $"{name} ({i}){extension}"); if (!File.Exists(candidate)) return candidate; }
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object value)
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, value);
    }

    public void Dispose() { _stop?.Cancel(); _listener.Close(); _stop?.Dispose(); }
    private sealed record PreparedTransfer(Dictionary<string, FileMetadata> Files, Dictionary<string, string> Tokens);
    private sealed record PrepareUploadRequest([property: JsonPropertyName("info")] DeviceInfo Info, [property: JsonPropertyName("files")] Dictionary<string, FileMetadata> Files);
    private sealed record DeviceInfo([property: JsonPropertyName("alias")] string Alias, [property: JsonPropertyName("version")] string Version, [property: JsonPropertyName("deviceModel")] string DeviceModel, [property: JsonPropertyName("deviceType")] string DeviceType, [property: JsonPropertyName("fingerprint")] string Fingerprint);
    private sealed record FileMetadata([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("fileName")] string FileName, [property: JsonPropertyName("size")] long Size, [property: JsonPropertyName("fileType")] string FileType);
}
