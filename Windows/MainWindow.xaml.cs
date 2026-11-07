using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.IO;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace AirSend.Windows;

public partial class MainWindow : Window
{
    private const int DiscoveryPort = 53317;
    private static readonly IPAddress DiscoveryMulticast = IPAddress.Parse("224.0.0.167");
    private const string DiscoveryMessage = "\"announce\":true";
    private readonly LocalSendTransferService _transferService = new();
    private readonly LocalSendReceiverService _receiverService = new();
    private readonly TaskbarIcon _trayIcon;
    private bool _allowClose;
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private uint _clipboardSequence;
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    public ObservableCollection<DiscoveredDevice> Devices { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        _trayIcon = new TaskbarIcon { ToolTipText = "AirSend" };
        var menu = new System.Windows.Controls.ContextMenu();
        var show = new System.Windows.Controls.MenuItem { Header = "打开 AirSend" };
        show.Click += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        var refresh = new System.Windows.Controls.MenuItem { Header = "刷新设备" };
        refresh.Click += async (_, _) => await DiscoverAsync();
        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => { _allowClose = true; Close(); };
        menu.Items.Add(show); menu.Items.Add(refresh); menu.Items.Add(new System.Windows.Controls.Separator()); menu.Items.Add(exit);
        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        RegisterStartup();
        DataContext = this;
        _ = StartReceiverAsync();
        _clipboardTimer.Tick += ClipboardTimer_Tick;
        _clipboardTimer.Start();
        Loaded += async (_, _) => await DiscoverAsync();
    }

    private async Task StartReceiverAsync()
    {
        try
        {
            await _receiverService.StartAsync();
            DetailText.Text = "接收服务已启动（TCP 53318）；发现使用 UDP 53317";
        }
        catch (Exception ex)
        {
            DetailText.Text = ex.Message;
            StatusText.Text = "接收服务启动失败";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _clipboardTimer.Stop();
        _receiverService.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        DetailText.Text = "AirSend 已在系统托盘运行";
    }

    private static void RegisterStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("AirSend", $"\"{Environment.ProcessPath}\"");
        }
        catch { }
    }

    private async void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        if (Devices.Count == 0 || ClipboardSyncGuard.ConsumeSuppression()) return;
        try
        {
            var sequence = GetClipboardSequenceNumber();
            if (sequence == _clipboardSequence) return;
            _clipboardSequence = sequence;
            var data = Clipboard.GetDataObject();
            if (data is null) return;
            var temp = Path.Combine(Path.GetTempPath(), $"airsend-clipboard-{Guid.NewGuid():N}");
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                temp = Path.Combine(Path.GetTempPath(), $"AirSend-Clipboard-{Guid.NewGuid():N}.png");
                using var output = File.Create(temp);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
                await _transferService.SendAsync(DeviceList.SelectedItem as DiscoveredDevice ?? Devices[0], temp, CancellationToken.None);
                DetailText.Text = "图片剪贴板已发送";
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                temp += ".txt";
                await File.WriteAllTextAsync(temp, data.GetData(DataFormats.UnicodeText)?.ToString() ?? "");
                await _transferService.SendAsync(DeviceList.SelectedItem as DiscoveredDevice ?? Devices[0], temp, CancellationToken.None);
                DetailText.Text = "文字剪贴板已发送";
            }
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception ex) { DetailText.Text = $"剪贴板同步失败：{ex.Message}"; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DiscoveredDevice target) { StatusText.Text = "请先刷新并选择在线设备"; return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择要发送的文件" };
        if (picker.ShowDialog() != true) return;
        try
        {
            StatusText.Text = $"正在发送到 {target.Name}...";
            await _transferService.SendAsync(target, picker.FileName, CancellationToken.None);
            StatusText.Text = "发送完成";
        }
        catch (Exception ex) { StatusText.Text = $"发送失败：{ex.Message}"; }
    }

    private async Task DiscoverAsync()
    {
        StatusText.Text = "正在搜索局域网设备...";
        Devices.Clear();
        using var client = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { client.JoinMulticastGroup(DiscoveryMulticast); } catch (SocketException) { }
        var payload = Encoding.UTF8.GetBytes("{\"alias\":\"AirSend Windows\",\"version\":\"2.0\",\"deviceModel\":\"Windows\",\"deviceType\":\"desktop\",\"fingerprint\":\"airsend-windows\",\"port\":53318,\"protocol\":\"http\",\"download\":true," + DiscoveryMessage + "}");
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        await client.SendAsync(payload, payload.Length, new IPEndPoint(DiscoveryMulticast, DiscoveryPort));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var receive = client.ReceiveAsync();
            var completed = await Task.WhenAny(receive, Task.Delay(250));
            if (completed != receive) continue;
            try
            {
                var result = await receive;
                var json = JsonDocument.Parse(result.Buffer).RootElement;
                if (!json.TryGetProperty("alias", out var alias)) continue;
                var port = json.TryGetProperty("port", out var portValue) ? portValue.GetInt32() : 53318;
                var protocol = json.TryGetProperty("protocol", out var protocolValue) && protocolValue.GetString() is { Length: > 0 } advertisedProtocol
                    ? advertisedProtocol.ToLowerInvariant() : "https";
                if (protocol is not ("http" or "https")) protocol = "https";
                var device = new DiscoveredDevice(alias.GetString() ?? "Unknown", result.RemoteEndPoint.Address.ToString(), port, protocol);
                if (!Devices.Any(existing => existing.Address == device.Address)) Devices.Add(device);
            }
            catch (JsonException) { }
        }
        client.Close();
        StatusText.Text = $"找到 {Devices.Count} 台设备";
    }
}

public sealed record DiscoveredDevice(string Name, string Address, int Port, string Protocol);

internal static class ClipboardSyncGuard
{
    private static int _suppress;
    public static void SuppressNextChange() => Interlocked.Exchange(ref _suppress, 1);
    public static bool ConsumeSuppression() => Interlocked.Exchange(ref _suppress, 0) == 1;
}
