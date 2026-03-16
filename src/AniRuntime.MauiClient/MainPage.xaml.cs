using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AniRuntime.MauiClient;

public partial class MainPage : ContentPage
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _sessionCts;
    private bool _isConnected;

    // Platform audio services — injected via DI
    private readonly IAudioCaptureService _audioCapture;
    private readonly IAudioPlaybackService _audioPlayback;

    public MainPage(IAudioCaptureService audioCapture, IAudioPlaybackService audioPlayback)
    {
        InitializeComponent();
        _audioCapture = audioCapture;
        _audioPlayback = audioPlayback;

        // Wire audio capture → WebSocket send
        _audioCapture.AudioDataAvailable += OnAudioCaptured;

        // Load saved server URL
        var savedUrl = Preferences.Get("server_url", "ws://192.168.1.100:5100/voice/stream");
        ServerUrlEntry.Text = savedUrl;
    }

    private async void OnTalkButtonClicked(object? sender, EventArgs e)
    {
        if (_isConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            UpdateUI("Connecting...", "#cccc44", "#3a3a6a");

            // Save URL for next launch
            var url = ServerUrlEntry.Text?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                UpdateUI("Enter server URL", "#cc4444", "#4a4a8a");
                return;
            }
            Preferences.Set("server_url", url);

            // Request microphone permission
            var micStatus = await Permissions.RequestAsync<Permissions.Microphone>();
            if (micStatus != PermissionStatus.Granted)
            {
                UpdateUI("Microphone permission required", "#cc4444", "#4a4a8a");
                return;
            }

            _sessionCts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            await _ws.ConnectAsync(new Uri(url), _sessionCts.Token);

            // Send start message
            await SendJsonAsync(new { type = "start" });

            _isConnected = true;
            UpdateUI("Connected — listening", "#44cc44", "#2a6a2a");
            TalkButton.Text = "End Call";

            // Start audio capture (PCM 16kHz, 16-bit, mono)
            _audioCapture.Start();

            // Start audio playback stream
            _audioPlayback.Start();

            // Start receive loop
            _ = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token));
        }
        catch (Exception ex)
        {
            UpdateUI($"Failed: {ex.Message}", "#cc4444", "#4a4a8a");
            TalkButton.Text = "Talk to Ani";
            _isConnected = false;
        }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            _audioCapture.Stop();
            _audioPlayback.Stop();

            if (_ws?.State == WebSocketState.Open)
            {
                await SendJsonAsync(new { type = "end" });
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch { /* best effort cleanup */ }
        finally
        {
            _sessionCts?.Cancel();
            _ws?.Dispose();
            _ws = null;
            _isConnected = false;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                TalkButton.Text = "Talk to Ani";
                UpdateUI("Ready", "#8888aa", "#4a4a8a");
                TranscriptLabel.Text = "";
            });
        }
    }

    private async void OnAudioCaptured(byte[] pcmData)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            await _ws.SendAsync(pcmData, WebSocketMessageType.Binary, true, _sessionCts?.Token ?? default);
        }
        catch { /* connection may have dropped */ }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Audio from server → play through speaker
                    var audioChunk = new byte[result.Count];
                    Buffer.BlockCopy(buffer, 0, audioChunk, 0, result.Count);
                    _audioPlayback.Write(audioChunk);
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessServerMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            await DisconnectAsync();
        }
    }

    private void ProcessServerMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                switch (type)
                {
                    case "session_started":
                        StatusLabel.Text = "Connected";
                        break;

                    case "listening":
                        StatusLabel.Text = "Listening...";
                        StatusLabel.TextColor = Color.FromArgb("#44cc44");
                        TalkButton.BackgroundColor = Color.FromArgb("#2a6a2a");
                        break;

                    case "transcript":
                        var text = doc.RootElement.GetProperty("text").GetString() ?? "";
                        var isFinal = doc.RootElement.TryGetProperty("isFinal", out var f) && f.GetBoolean();
                        TranscriptLabel.Text = text;
                        if (isFinal)
                            TranscriptLabel.TextColor = Color.FromArgb("#ffffff");
                        else
                            TranscriptLabel.TextColor = Color.FromArgb("#aaaacc");
                        break;

                    case "reply_start":
                        StatusLabel.Text = "Ani is speaking...";
                        StatusLabel.TextColor = Color.FromArgb("#aa66cc");
                        TalkButton.BackgroundColor = Color.FromArgb("#4a2a6a");
                        break;

                    case "reply_end":
                        TranscriptLabel.Text = "";
                        break;

                    case "error":
                        var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                        StatusLabel.Text = $"Error: {msg}";
                        StatusLabel.TextColor = Color.FromArgb("#cc4444");
                        break;
                }
            });
        }
        catch { /* best effort UI update */ }
    }

    private async Task SendJsonAsync(object payload)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _sessionCts?.Token ?? default);
    }

    private void UpdateUI(string status, string statusColor, string buttonColor)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            StatusLabel.TextColor = Color.FromArgb(statusColor);
            TalkButton.BackgroundColor = Color.FromArgb(buttonColor);
        });
    }
}
