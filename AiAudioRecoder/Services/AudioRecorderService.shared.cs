using System;
using System.Threading.Tasks;

namespace AiAudioRecoder.Services;

public interface IAudioRecorderService
{
    Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic");
    Task StopRecordingAsync();
    bool IsRecording { get; }
    Task<string?> StartMixedRecordingAsync(string? folderName = null, string? fileName = null);
    void SetMicEnabled(bool enabled);
    void SetSystemEnabled(bool enabled);
    bool MicEnabled { get; }
    bool SystemEnabled { get; }
    event Action<double>? MicLevelChanged;
    event Action<double>? SystemLevelChanged;
}

public partial class AudioRecorderService : IAudioRecorderService
{
    public bool IsRecording => _isRecording;
    protected bool _isRecording;
    protected string? _filePath;
    protected bool _micEnabled = true;
    protected bool _systemEnabled = false;

    public bool MicEnabled => _micEnabled;
    public bool SystemEnabled => _systemEnabled;

    public virtual void SetMicEnabled(bool enabled) => _micEnabled = enabled;
    public virtual void SetSystemEnabled(bool enabled) => _systemEnabled = enabled;

    public event Action<double>? MicLevelChanged;
    public event Action<double>? SystemLevelChanged;

    protected void ReportMicLevel(double level) => MicLevelChanged?.Invoke(level);
    protected void ReportSystemLevel(double level) => SystemLevelChanged?.Invoke(level);
}
