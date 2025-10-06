using SQLite;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiAudioRecoder.Models;

public class ModelInfoDb : INotifyPropertyChanged
{
    [PrimaryKey]
    public string? Name { get; set; }
    public double SizeMB { get; set; }
    public long SizeBytes { get; set; }
    private bool _isDownloaded;
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            _isDownloaded = value;
            OnPropertyChanged();
        }
    }
    public string? LocalPath { get; set; }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            _isDownloading = value;
            OnPropertyChanged();
        }
    }
    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            OnPropertyChanged();
        }
    }
    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
