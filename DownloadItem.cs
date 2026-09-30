using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace WrenchDownloader;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed
}

/// <summary>One SABR media segment: the exact ranged request the browser
/// made (signed URL + byte range). Replayed in byte order, the segments
/// concatenate into the rendition file. Pure-SABR gated videos have no
/// other downloadable form.</summary>
public sealed class StreamSegment
{
    public string Url { get; set; } = "";
    /// <summary>HTTP Range value, e.g. "bytes=0-524287". Empty = whole URL.</summary>
    public string Range { get; set; } = "";
}

/// <summary>One browser cookie as handed over by the companion extension.</summary>
public sealed class BrowserCookie
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "/";
    public bool Secure { get; set; }
    public long Expiry { get; set; }
}

public class DownloadItem : INotifyPropertyChanged
{
    private string _title = AppLocalization.Get("common.download");
    private string _savePath = "";
    private double _progress;
    private DownloadStatus _status;
    private string _speedText = "0 KB/s";
    private string _sizeText = "--";
    private string _statusText = AppLocalization.Get("download.queued");
    private string _etaText = "--";
    private DateTime _createdAt = DateTime.Now;
    private DispatcherQueue? _dispatcherQueue;

    public DownloadItem()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread() ?? App.MainWindowInstance?.DispatcherQueue;
    }

    public void SetDispatcherQueue(DispatcherQueue queue)
    {
        _dispatcherQueue = queue;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    public string Url { get; set; } = "";
    public string Quality { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string Referrer { get; set; } = "";
    public string UserAgent { get; set; } = "";
    /// <summary>Live browser-session cookies handed over by the extension
    /// (works while the browser runs, unlike cookie-DB export).</summary>
    public List<BrowserCookie>? Cookies { get; set; }
    /// <summary>Browser-minted PO token harvested from the playing tab's
    /// stream requests (proves a genuine client to YouTube).</summary>
    public string PoToken { get; set; } = "";
    /// <summary>Audio companion stream fully resolved by the extension
    /// (DASH video-only renditions need this for muxing). When present the
    /// app downloads both URLs directly with zero page re-resolve, which is
    /// what age/login gates block.</summary>
    public string AudioUrl { get; set; } = "";
    public string AudioReferrer { get; set; } = "";
    /// <summary>SABR session: the browser's live segment traffic for this
    /// rendition (null/empty when the stream is a plain direct URL).</summary>
    public bool IsSabr { get; set; }
    public int Itag { get; set; }
    /// <summary>Full rendition size when known (clen= param).</summary>
    public long ExpectedBytes { get; set; }
    /// <summary>DASH init byte-range (e.g. "0-739") fetched first so the
    /// reassembled file is playable.</summary>
    public string InitRange { get; set; } = "";
    public string AudioInitRange { get; set; } = "";
    public List<StreamSegment>? Segments { get; set; }
    public List<StreamSegment>? AudioSegments { get; set; }
    /// <summary>MSE recording session id. While set, the engine stays out:
    /// bytes arrive via /api/segment and /api/finish assembles the file.</summary>
    public string MseUploadId { get; set; } = "";
    /// <summary>Safe browser request headers captured for this exact media
    /// rendition. Used to reproduce the request Chrome already proved works.</summary>
    public Dictionary<string, string> StreamHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TargetFolder { get; set; } = "";
    public string QueueName { get; set; } = "Main queue";
    public bool DownloadPlaylist { get; set; }
    public bool WaitForQueueStart { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }

    public string SavePath
    {
        get => _savePath;
        set
        {
            if (_savePath != value)
            {
                _savePath = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set
        {
            if (_createdAt != value)
            {
                _createdAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AddedText));
            }
        }
    }

    /// <summary>Localized "Added ..." line shown under each download.</summary>
    public string AddedText =>
        AppLocalization.Format("download.addedOn", _createdAt);

    private System.Threading.CancellationTokenSource _cts = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Threading.CancellationTokenSource Cts => _cts;

    public void Cancel()
    {
        try { _cts.Cancel(); } catch { }
    }

    public System.Threading.CancellationToken ResetCancellationToken()
    {
        try { _cts.Dispose(); } catch { }
        _cts = new System.Threading.CancellationTokenSource();
        return _cts.Token;
    }

    public double Progress
    {
        get => _progress;
        set { if (Math.Abs(_progress - value) > 0.01) { _progress = value; OnPropertyChanged(); } }
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(CanTogglePause));
                OnPropertyChanged(nameof(ActionGlyph));
                OnPropertyChanged(nameof(ActionToolTip));
            }
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Status == DownloadStatus.Downloading || Status == DownloadStatus.Queued;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPaused => Status == DownloadStatus.Paused;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanTogglePause => Status == DownloadStatus.Downloading || Status == DownloadStatus.Queued || Status == DownloadStatus.Paused || Status == DownloadStatus.Failed;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ActionGlyph => IsActive ? "\uE769" : "\uE768"; // E769 = Pause, E768 = Play

    [System.Text.Json.Serialization.JsonIgnore]
    public string ActionToolTip => IsActive
        ? AppLocalization.Get("menu.pauseDownload")
        : AppLocalization.Get("menu.resumeDownload");

    public string SpeedText
    {
        get => _speedText;
        set { if (_speedText != value) { _speedText = value; OnPropertyChanged(); } }
    }

    public string SizeText
    {
        get => _sizeText;
        set { if (_sizeText != value) { _sizeText = value; OnPropertyChanged(); } }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string EtaText
    {
        get => _etaText;
        set { if (_etaText != value) { _etaText = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var queue = _dispatcherQueue ?? App.MainWindowInstance?.DispatcherQueue;
        if (queue != null && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            });
        }
        else
        {
            try
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
            catch { }
        }
    }
}
