using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMCPAddin.Panel;

/// <summary>
/// Starts the local service behind the extra pane when its URL is not answering (jr fork).
/// The pane used to show the browser's "can't connect" page from Revit start until a pyRevit
/// button launched the service; now the add-in launches it itself, hidden, and the pane waits.
///
/// Rules that keep this harmless:
///  * never starts while the URL answers (a second Revit, or a service started by hand, wins);
///  * at most one launch every <see cref="MinInterval"/> and <see cref="MaxStarts"/> per Revit
///    session — a broken command (python missing, port taken) is logged, not retried forever;
///  * fire-and-forget: the child outlives Revit on purpose (today's behaviour), nothing here
///    blocks the Revit UI thread, and every failure lands in panel.log, never in a dialog.
/// </summary>
internal sealed class LocalServiceLauncher
{
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    internal const int MaxStarts = 4;

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

    private readonly ServiceStart _start;
    private readonly string _url;
    private readonly string _label;
    private readonly object _gate = new();
    private DateTime _lastStart = DateTime.MinValue;
    private int _starts;

    public LocalServiceLauncher(ServiceStart start, string url, string label)
    {
        _start = start;
        _url = url;
        _label = label;
    }

    public string Url => _url;

    /// <summary>True when the service answers at <paramref name="url"/> (any HTTP status).</summary>
    public static async Task<bool> IsUpAsync(string url)
    {
        try
        {
            using var response = await Probe.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Probe, and launch when down. Safe to call from any thread, as often as wanted.</summary>
    public async Task<bool> StartIfDownAsync(string reason)
    {
        if (await IsUpAsync(_url).ConfigureAwait(false)) return false;
        return TryStart(reason);
    }

    /// <summary>Launch the configured command (rate-limited). Returns true when a process was started.</summary>
    public bool TryStart(string reason)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastStart < MinInterval) return false;
            if (_starts >= MaxStarts)
            {
                Log($"{_label}: not starting again ({_starts} launches this session) — {reason}");
                return false;
            }
            _lastStart = now;
            _starts++;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _start.File,
                Arguments = _start.Args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (_start.Cwd is not null) psi.WorkingDirectory = _start.Cwd;
            using var process = Process.Start(psi);
            Log($"{_label}: started \"{_start.File}\" {_start.Args} (pid {process?.Id}) — {reason}");
            return process is not null;
        }
        catch (Exception ex)
        {
            Log($"{_label}: could not start \"{_start.File}\": {ex.GetType().Name} — {ex.Message}");
            return false;
        }
    }

    internal static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitMCPAddin", "panel.log");

    internal static void Log(string text)
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
}
