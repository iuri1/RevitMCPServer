using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RevitMCPAddin.Panel;

/// <summary>
/// WPF content of the AutoAudit dockable pane: a WebView2 pointed at the
/// AutoAudit UI URL, with a small toolbar (reload / open-in-browser) and a
/// fallback overlay used whenever WebView2 cannot run (runtime missing,
/// environment init failure, renderer crash). Built code-only — no XAML.
///
/// Known WebView2-in-Revit gotchas handled here:
///  * user-data folder MUST be explicit — the default (next to Revit.exe)
///    is not writable and init fails;
///  * Revit's model-upgrade dialog can wedge the WebView2 interop queue —
///    App.cs calls <see cref="Suspend"/>/<see cref="Resume"/> around
///    DocumentClosing/DocumentOpened to dispose/recreate the browser;
///  * renderer crashes (CEF/WebView2 clashes on some machines) surface via
///    CoreWebView2.ProcessFailed → we tear down and show the fallback with
///    a Retry button. The panel must NEVER take Revit down with it.
/// </summary>
public sealed class AutoAuditPanelView : UserControl
{
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly Grid _root;
    private readonly StackPanel _fallback;
    private readonly TextBlock _fallbackMessage;
    private WebView2? _webView;
    private bool _suspended;
    private readonly string _label;
    // jr fork: the pane waits for (and may start) its local service instead of showing the
    // browser's "can't connect" page — see Connect()/OnNavigationCompleted.
    private readonly LocalServiceLauncher? _launcher;
    private readonly string _waiting;
    private System.Windows.Threading.DispatcherTimer? _poll;
    private int _pollTicks;
    private bool _navigatingToService;

    public AutoAuditPanelView(string revitVersion)
        : this(PanelConfig.ResolveUrl(revitVersion),
               Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                   "RevitMCPAddin", "WebView2", revitVersion),
               "AutoAudit")
    {
    }

    /// <summary>
    /// URL + user-data-folder overload so a SECOND dockable pane (the config-driven extra pane) reuses this WebView2
    /// host — and every gotcha fix in it — instead of duplicating ~250 lines. Each pane MUST pass a
    /// DISTINCT userDataFolder: two WebView2 profiles sharing one folder can lock each other out.
    /// </summary>
    /// <param name="label">
    /// Pane name used in every message the user reads. Required because BOTH
    /// dockable panes are instances of this class: with "AutoAudit" hardcoded,
    /// the second pane told users "AutoAudit panel is paused" and pointed
    /// them at AutoAudit while showing a different service entirely.
    /// </param>
    public AutoAuditPanelView(string url, string userDataFolder, string label)
        : this(url, userDataFolder, label, null, null)
    {
    }

    /// <param name="launcher">
    /// When given, a pane whose URL does not answer starts the service through it and shows a
    /// waiting page until the URL answers (jr fork). Null = plain browser, as upstream.
    /// </param>
    /// <param name="waiting">Text of the waiting page; null = "Starting {label}…".</param>
    internal AutoAuditPanelView(string url, string userDataFolder, string label,
                                LocalServiceLauncher? launcher, string? waiting)
    {
        _url = url;
        _userDataFolder = userDataFolder;
        _label = label;
        _launcher = launcher;
        _waiting = waiting ?? $"Starting {label}…";

        // Explicit surface colors: the pane host gives WPF a transparent
        // backdrop that renders BLACK under Revit's dark theme — the first
        // live run showed an unreadable black-on-black fallback.
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x27));

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _root.Children.Add(BuildToolbar());

        _fallbackMessage = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xEB, 0xF0)),
        };
        _fallback = BuildFallback();
        Grid.SetRow(_fallback, 1);
        _root.Children.Add(_fallback);

        Content = _root;
        Loaded += (_, _) => EnsureWebView();
    }

    // ── lifecycle hooks (App.cs wires these to Revit document events) ──────

    /// <summary>Dispose the browser before a document transition.</summary>
    public void Suspend()
    {
        _suspended = true;
        TearDownWebView();
        ShowFallback($"{_label} panel is paused while Revit switches documents.");
    }

    /// <summary>Recreate the browser after the document transition ends.</summary>
    public void Resume()
    {
        _suspended = false;
        // Back on the UI thread queue — recreate lazily so a burst of
        // document events collapses into one rebuild.
        Dispatcher.BeginInvoke(new Action(EnsureWebView));
    }

    // ── UI construction ─────────────────────────────────────────────────────

    private UIElement BuildToolbar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4),
        };
        var reload = new Button { Content = "Reload", Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 8, 2) };
        reload.Click += (_, _) =>
        {
            // Connect() probes first: with the service down, a plain Reload only re-fails.
            if (_webView?.CoreWebView2 is not null) Connect("reload button");
            else ForceRebuild();
        };
        var external = new Button { Content = "Open in browser", Padding = new Thickness(8, 2, 8, 2) };
        external.Click += (_, _) => OpenExternal();
        bar.Children.Add(reload);
        bar.Children.Add(external);
        return bar;
    }

    private StackPanel BuildFallback()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_fallbackMessage);

        var open = new Button
        {
            Content = $"Open {_label} in browser",
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4),
        };
        open.Click += (_, _) => OpenExternal();
        panel.Children.Add(open);

        var retry = new Button
        {
            Content = "Retry embedded view",
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        retry.Click += (_, _) => ForceRebuild();
        panel.Children.Add(retry);
        return panel;
    }

    // ── WebView2 management ────────────────────────────────────────────────

    /// <summary>
    /// JIT-isolation shim: this method touches NO WebView2 types, so it
    /// always JITs. The body lives in <see cref="EnsureWebViewCore"/> —
    /// jitting THAT can itself throw FileNotFound/TypeLoad when the
    /// WebView2 assemblies fail to resolve inside Revit's addin load
    /// context, and that throw happens at the CALL SITE. Caught here, it
    /// lands in the log + fallback instead of being swallowed by Revit's
    /// dispatcher (the exact silent-black-panel failure seen live
    /// 2026-07-12).
    /// </summary>
    /// <summary>
    /// A user pressing "Retry embedded view" or "Reload" IS the statement that
    /// the document transition is over, so it clears the suspend flag before
    /// rebuilding. <see cref="EnsureWebViewCore"/> returns early while
    /// <c>_suspended</c> is set, so routing these buttons straight there left
    /// the only affordance offered for the paused state unable to leave it —
    /// an inert button with no message, for the rest of the Revit session.
    /// </summary>
    private void ForceRebuild()
    {
        _suspended = false;
        EnsureWebView();
    }

    private void EnsureWebView()
    {
        try
        {
            EnsureWebViewCore();
        }
        catch (Exception ex)
        {
            LogPanel("call-site failure (assembly load?): " + ex);
            ShowFallback(
                "The embedded browser component could not load: "
                + ex.GetType().Name + " — " + ex.Message
                + $"\n(full details: {LogPath})");
        }
    }

    private async void EnsureWebViewCore()
    {
        if (_suspended || _webView is not null) return;
        try
        {
            // Throws when the Evergreen WebView2 Runtime is not installed —
            // cheapest possible probe, before we touch any control state.
            CoreWebView2Environment.GetAvailableBrowserVersionString();

            var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
            Grid.SetRow(view, 1);
            _webView = view;
            // The control MUST be in the visual tree BEFORE awaiting
            // EnsureCoreWebView2Async: the WPF WebView2 only completes
            // initialization once it has an HWND, and it only gets one by
            // being loaded. Adding it after the await parks the await
            // forever — no exception, no log, fallback never collapses
            // (the exact silent hang seen live 2026-07-12, round 2).
            _root.Children.Add(view);

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: _userDataFolder);
            await view.EnsureCoreWebView2Async(env);

            view.CoreWebView2.ProcessFailed += (_, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TearDownWebView();
                    ShowFallback($"The embedded browser crashed ({e.ProcessFailedKind}).");
                }));
            };
            // The panel is a BROWSER, nothing more: no host objects, no
            // script injection — the web app talks to the AuditHub service
            // over HTTP exactly as it does in a normal browser tab.
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            // No Chromium "can't connect" page: a failed navigation lands in
            // OnNavigationCompleted, which shows the waiting page and keeps probing.
            view.CoreWebView2.Settings.IsBuiltInErrorPageEnabled = false;
            view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            _fallback.Visibility = Visibility.Collapsed;
            Connect("pane loaded");
        }
        catch (Exception ex)
        {
            TearDownWebView();
            LogPanel(ex.ToString());
            ShowFallback(
                "The embedded view is unavailable: "
                + ex.GetType().Name + " — " + ex.Message
                + $"\n(full details: {LogPath})");
        }
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCPAddin", "panel.log");

    private static void LogPanel(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:s}] {text}\n");
        }
        catch
        {
            // Diagnostics must never throw into Revit.
        }
    }

    // ── waiting for / starting the local service (jr fork) ─────────────────

    /// <summary>
    /// Probe the service; navigate when it answers, otherwise show the waiting page, start the
    /// service (when configured) and poll until it answers. Runs on the UI thread; the probe
    /// itself is async so Revit never blocks on a dead port.
    /// </summary>
    private async void Connect(string reason)
    {
        try
        {
            var view = _webView;
            if (view?.CoreWebView2 is null || _suspended) return;
            if (await LocalServiceLauncher.IsUpAsync(_url))
            {
                StopPolling();
                _navigatingToService = true;
                view.CoreWebView2.Navigate(_url);
                return;
            }
            LogPanel($"{_label}: {_url} not answering ({reason}) — waiting page");
            ShowWaiting(view);
            _launcher?.TryStart(reason);
            StartPolling();
        }
        catch (Exception ex)
        {
            LogPanel("connect: " + ex);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_navigatingToService) return;        // the waiting page itself (NavigateToString)
        _navigatingToService = false;
        if (e.IsSuccess) return;
        // Service went away under us (restart, crash, another Revit closed it): back to waiting.
        LogPanel($"{_label}: navigation to {_url} failed ({e.WebErrorStatus}) — waiting page");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var view = _webView;
            if (view?.CoreWebView2 is null || _suspended) return;
            ShowWaiting(view);
            _launcher?.TryStart($"navigation failed: {e.WebErrorStatus}");
            StartPolling();
        }));
    }

    private void StartPolling()
    {
        if (_poll is not null) return;
        _pollTicks = 0;
        _poll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _poll.Tick += async (_, _) =>
        {
            try
            {
                var view = _webView;
                if (view?.CoreWebView2 is null || _suspended) { StopPolling(); return; }
                if (await LocalServiceLauncher.IsUpAsync(_url))
                {
                    StopPolling();
                    _navigatingToService = true;
                    view.CoreWebView2.Navigate(_url);
                    return;
                }
                _pollTicks++;
                if (_pollTicks == 40)             // ~60 s: say so on the page, try the launcher once more
                {
                    _launcher?.TryStart("still down after 60 s");
                    await SetWaitingStatus(view, _launcher is null
                        ? $"Nothing is answering at {_url} yet."
                        : $"Still starting… if this takes long, see {LocalServiceLauncher.LogPath}");
                }
            }
            catch (Exception ex)
            {
                LogPanel("poll: " + ex);
            }
        };
        _poll.Start();
    }

    private void StopPolling()
    {
        _poll?.Stop();
        _poll = null;
    }

    private void ShowWaiting(WebView2 view)
    {
        try
        {
            _navigatingToService = false;
            view.CoreWebView2.NavigateToString(WaitingHtml());
        }
        catch (Exception ex)
        {
            LogPanel("waiting page: " + ex);
        }
    }

    private static async System.Threading.Tasks.Task SetWaitingStatus(WebView2 view, string text)
    {
        try
        {
            var js = "var s=document.getElementById('s'); if(s) s.textContent=" +
                     System.Text.Json.JsonSerializer.Serialize(text) + ";";
            await view.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    /// <summary>Self-contained waiting page: same dark surface as the pane, label, spinner, one status line.</summary>
    private string WaitingHtml()
    {
        var label = System.Net.WebUtility.HtmlEncode(_label);
        var waiting = System.Net.WebUtility.HtmlEncode(_waiting);
        return "<!doctype html><html><head><meta charset='utf-8'><style>"
             + "html,body{height:100%;margin:0;background:#1C2127;color:#E7EBF0;font:14px system-ui,Segoe UI,sans-serif}"
             + "body{display:flex;align-items:center;justify-content:center;text-align:center}"
             + ".sp{width:28px;height:28px;border:3px solid #3a4250;border-top-color:#E7EBF0;border-radius:50%;"
             + "margin:0 auto 14px;animation:r 1s linear infinite}@keyframes r{to{transform:rotate(360deg)}}"
             + "h1{font-size:16px;font-weight:600;margin:0 0 6px}#s{color:#9aa4b2;font-size:12px;margin-top:10px;min-height:1em}"
             + "</style></head><body><div><div class='sp'></div><h1>" + label + "</h1><div>" + waiting
             + "</div><div id='s'></div></div></body></html>";
    }

    private void TearDownWebView()
    {
        StopPolling();
        if (_webView is null) return;
        try
        {
            _root.Children.Remove(_webView);
            _webView.Dispose();
        }
        catch
        {
            // Disposal must never throw into Revit.
        }
        _webView = null;
    }

    private void ShowFallback(string message)
    {
        _fallbackMessage.Text = message + $"\n\n{_label} keeps working at {_url}";
        _fallback.Visibility = Visibility.Visible;
    }

    private void OpenExternal()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowFallback("Could not launch the default browser: " + ex.Message);
        }
    }
}
