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

public class DownloadItem : INotifyPropertyChanged
{
    private string _title = "Download";
    private string _savePath = "";
    private double _progress;
    private DownloadStatus _status;
    private string _speedText = "0 KB/s";
    private string _sizeText = "--";
    private string _statusText = "Queued";
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

    public DateTime CreatedAt { get; set; } = DateTime.Now;

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
    public string ActionToolTip => IsActive ? "Pause Download" : "Resume Download";

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
