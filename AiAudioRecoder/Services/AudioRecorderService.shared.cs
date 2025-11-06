using System.Threading.Tasks;

namespace AiAudioRecoder.Services;

public interface IAudioRecorderService
{
    Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic");
    Task StopRecordingAsync();
    bool IsRecording { get; }
    Task<string?> StartMixedRecordingAsync(string? folderName = null, string? fileName = null);
}

public partial class AudioRecorderService : IAudioRecorderService
{
    public bool IsRecording => _isRecording;
    protected bool _isRecording;
    protected string? _filePath;
}
