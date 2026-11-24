using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Alondra1.Core.Notes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Alondra1.App.Notes;

/// <summary>
/// Hosts the Alondra1 Notes editor (Assets\NotesEditor, built from alondra1-backend contract/notes-editor) in WebView2. The page is
/// served from the app folder under https://editor.alondra1.invalid and pictures from the local notes-assets folder
/// under https://assets.alondra1.invalid; nothing is loaded from the internet and no navigation is allowed. Messages
/// in both directions are JSON (see alondra1-backend contract/notes-editor/docs/HOST-BRIDGE.md).
/// </summary>
public sealed class NoteEditorHost : ContentControl
{
    public const string EditorHost = "editor.alondra1.invalid";
    public const string AssetPrefix = "https://assets.alondra1.invalid/";
    private const string EditorUrl = "https://" + EditorHost + "/editor.html";

    private WebView2? _web;
    private bool _ready;
    private Task? _starting;
    private readonly List<string> _queued = [];
    private TaskCompletionSource _readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NoteEditorHost()
    {
        Focusable = false;
        Content = new TextBlock { Text = "Loading the editor…", Margin = new Thickness(24), Foreground = (Brush)Application.Current.FindResource("MutedBrush") };
        Loaded += (_, _) => _starting ??= StartAsync();
    }

    /// <summary>Supplies pictures to the page (local files only).</summary>
    public NoteAssets? Assets { get; set; }

    /// <summary>A message from the editor page: change, saveNow, newNote, pickImage, storeImage, fetchImage, openLink, checklistToTask, error.</summary>
    public event Action<JsonObject>? MessageReceived;

    /// <summary>A note referenced a picture that is not on this PC yet (Cloud Sync may still be downloading it).</summary>
    public event Action<string>? MissingAsset;

    public bool Available { get; private set; } = true;

    private async Task StartAsync()
    {
        var environment = await Controls.MailBodyView.Environment();
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "NotesEditor");
        if (environment is null || !File.Exists(Path.Combine(folder, "editor.html")))
        {
            Available = false;
            Content = new TextBlock
            {
                Text = environment is null ? "The note editor needs the Microsoft Edge WebView2 Runtime, which is missing on this PC." : "The note editor files are missing. Reinstall Alondra1.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24),
            };
            return;
        }

        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        Content = web;
        await web.EnsureCoreWebView2Async(environment);
        var core = web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = Debugger.IsAttached;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+F, Ctrl+N, Ctrl+S belong to the editor
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        core.SetVirtualHostNameToFolderMapping(EditorHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.AddWebResourceRequestedFilter(AssetPrefix + "*", CoreWebView2WebResourceContext.Image);
        core.WebResourceRequested += ServeAsset;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(EditorUrl, StringComparison.Ordinal))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnMessage;
        _web = web;
        core.Navigate(EditorUrl);
    }

    private void ServeAsset(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var sha = e.Request.Uri[AssetPrefix.Length..].Split('?', '#')[0];
        var deferral = e.GetDeferral();
        _ = ServeAsync();

        async Task ServeAsync()
        {
            try
            {
                var found = Assets is null ? null : await Assets.ReadAsync(sha);
                var environment = _web!.CoreWebView2.Environment;
                if (found is { } f)
                {
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(f.Data), 200, "OK",
                        $"Content-Type: {f.Asset.MimeType}\r\nCache-Control: max-age=31536000, immutable\r\nX-Content-Type-Options: nosniff");
                }
                else
                {
                    e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    MissingAsset?.Invoke(sha);
                }
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(e.TryGetWebMessageAsString()) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return;
        }

        if (message is null)
            return;
        if (message["type"]?.GetValue<string>() == "ready")
        {
            _ready = true;
            _readySignal.TrySetResult();
            foreach (var script in _queued)
                _ = _web!.CoreWebView2.ExecuteScriptAsync(script);
            _queued.Clear();
            return;
        }

        MessageReceived?.Invoke(message);
    }

    private Task<string> RunAsync(string script)
    {
        if (!_ready || _web?.CoreWebView2 is null)
        {
            _queued.Add(script);
            return Task.FromResult("null");
        }

        return _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>Calls alondra.&lt;method&gt;(argument) in the page. The argument is passed as JSON, never as script text.</summary>
    public Task<string> CallAsync(string method, JsonNode? argument = null)
    {
        // Text arguments arrive as a JS string; objects as their JSON text (the page parses it).
        var literal = argument switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var text) => JsonSerializer.Serialize(text),
            _ => JsonSerializer.Serialize(argument.ToJsonString()),
        };
        return RunAsync($"window.alondra && window.alondra.{method}({literal})");
    }

    public Task LoadAsync(Note? note, bool focusTitle = false) =>
        CallAsync("load", new JsonObject
        {
            ["title"] = note?.Title ?? "",
            ["doc"] = note?.Doc?.DeepClone() ?? (note is null ? null : PlainDocument(note.Body)),
            ["readOnly"] = note is null || note.TrashedUtc is not null,
            ["mode"] = "desktop",
            ["theme"] = Theme(),
            ["limits"] = new JsonObject { ["imageBytes"] = Assets?.MaxBytes ?? NoteAssets.DefaultMaxBytes },
            ["focus"] = focusTitle ? "title" : null,
        });

    /// <summary>The document now (unsaved edits included), or null when the editor is not running.</summary>
    public async Task<JsonObject?> FlushAsync()
    {
        if (!_ready || _web?.CoreWebView2 is null)
            return null;
        var result = await _web.CoreWebView2.ExecuteScriptAsync("window.alondra ? window.alondra.flush() : null");
        return result == "null" ? null : JsonNode.Parse(JsonSerializer.Deserialize<string>(result)!) as JsonObject;
    }

    public async Task<string?> ExportAsync(string format)
    {
        if (!_ready || _web?.CoreWebView2 is null)
            return null;
        var result = await _web.CoreWebView2.ExecuteScriptAsync($"window.alondra.exportAs({JsonSerializer.Serialize(format)})");
        return JsonSerializer.Deserialize<string>(result);
    }

    public Task ApplyThemeAsync() => CallAsync("setTheme", Theme());

    public void FocusEditor()
    {
        _web?.Focus();
        _ = CallAsync("focus");
    }

    /// <summary>A plain-text note (from an older client) shown as paragraphs; saving turns it into a rich note.</summary>
    public static JsonObject PlainDocument(string text)
    {
        var content = new JsonArray();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var p = new JsonObject { ["type"] = "paragraph" };
            if (line.Length > 0)
                p["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line });
            content.Add(p);
        }

        return new JsonObject { ["schemaVersion"] = 1, ["type"] = "doc", ["content"] = content };
    }

    /// <summary>The app's current colors, so the editor matches light and dark themes.</summary>
    private static JsonObject Theme()
    {
        string Hex(string key) => Application.Current.TryFindResource(key) is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "";
        var surface = Application.Current.TryFindResource("SurfaceBrush") as SolidColorBrush;
        var dark = surface is not null && (surface.Color.R * 299 + surface.Color.G * 587 + surface.Color.B * 114) / 1000 < 128;
        var vars = new JsonObject();
        foreach (var (css, key) in new[] { ("--a1-bg", "SurfaceBrush"), ("--a1-subtle", "SubtleBrush"), ("--a1-text", "TextBrush"), ("--a1-muted", "MutedBrush"),
            ("--a1-line", "LineBrush"), ("--a1-hover", "HoverBrush"), ("--a1-selected", "SelectedBrush"), ("--a1-accent", "BlueBrush"), ("--a1-brand", "BrandBrush"), ("--a1-danger", "DangerBrush") })
        {
            if (Hex(key) is { Length: > 0 } value)
                vars[css] = value;
        }

        return new JsonObject { ["dark"] = dark, ["vars"] = vars };
    }

    /// <summary>The WebView element (snapshots draw it from the screen).</summary>
    public FrameworkElement? Surface => _web;

    /// <summary>A picture of the editor as rendered (diagnostic snapshots).</summary>
    public async Task<System.Windows.Media.Imaging.BitmapSource?> CaptureAsync()
    {
        if (_web?.CoreWebView2 is null)
            return null;
        using var stream = new MemoryStream();
        await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        stream.Position = 0;
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Waits until the page is ready (for tests and the first load).</summary>
    public Task WhenReadyAsync() => _readySignal.Task;
}
