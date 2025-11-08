#if ANDROID
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AiAudioRecoder.Services;

public partial class AudioRecorderService
{
    private FileStream? _androidStream;
    private CancellationTokenSource? _androidCts;
    private Task? _androidWriteTask;
    private int _dataBytes;

    public Task<string?> StartRecordingAsync(string? folderName = null, string? fileName = null, string source = "mic")
        => StartMixedRecordingAsync(folderName, fileName);

    public Task<string?> StartMixedRecordingAsync(string? folderName = null, string? fileName = null)
    {
        if (_isRecording) return Task.FromResult<string?>(null);
        var dateFolder = string.IsNullOrEmpty(folderName) ? DateTime.Now.ToString("yyyy-MM-dd") : folderName;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(root, "AiAudioRecoder", "audioData", dateFolder);
        Directory.CreateDirectory(dir);
        var baseName = string.IsNullOrEmpty(fileName) ? $"audio_{DateTime.Now:yyyyMMdd_HHmmss}" : fileName;
        var path = Path.Combine(dir, baseName + ".wav");
        try
        {
            _androidStream = File.Create(path);
            WriteWaveHeaderPlaceholder(_androidStream, 44100, 2, 16); // placeholder header
            _androidCts = new CancellationTokenSource();
            _dataBytes = 0;
            _androidWriteTask = Task.Run(async ()=> await SilentLoopAsync(_androidCts.Token));
            _filePath = path;
            _isRecording = true;
            return Task.FromResult<string?>(_filePath);
        }
        catch (Exception)
        {
            _androidStream?.Dispose();
            return Task.FromResult<string?>(null);
        }
    }

    private async Task SilentLoopAsync(CancellationToken token)
    {
        // 100ms block for 44.1kHz stereo 16-bit: 44100 * 4 /10 = 17640 bytes
        int blockBytes = 44100 * 4 / 10;
        var buffer = new byte[blockBytes];
        while (!token.IsCancellationRequested)
        {
            // If any source enabled we would normally write captured data.
            // Currently (stub) we always write silence; dynamic enable just keeps timing.
            _androidStream?.Write(buffer, 0, buffer.Length);
            _dataBytes += buffer.Length;
            try { await Task.Delay(100, token); } catch { }
        }
    }

    public Task StopRecordingAsync()
    {
        if (!_isRecording) return Task.CompletedTask;
        _androidCts?.Cancel();
        try { _androidWriteTask?.Wait(1000); } catch { }
        if (_androidStream != null)
        {
            // finalize header
            FinalizeWaveHeader(_androidStream, _dataBytes);
            _androidStream.Dispose();
            _androidStream = null;
        }
        _androidCts?.Dispose(); _androidCts = null;
        _androidWriteTask = null;
        _isRecording = false;
        return Task.CompletedTask;
    }

    private static void WriteWaveHeaderPlaceholder(Stream stream, int sampleRate, short channels, short bitsPerSample)
    {
        // RIFF header with zero sizes to be patched later
        var bw = new BinaryWriter(stream);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(0); // file size - 8
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // PCM chunk size
        bw.Write((short)1); // PCM format
        bw.Write(channels);
        bw.Write(sampleRate);
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(0); // data size placeholder
        bw.Flush();
    }

    private static void FinalizeWaveHeader(Stream stream, int dataBytes)
    {
        long currentPos = stream.Position;
        stream.Seek(4, SeekOrigin.Begin);
        var bw = new BinaryWriter(stream);
        bw.Write(dataBytes + 36); // file size - 8
        stream.Seek(40, SeekOrigin.Begin);
        bw.Write(dataBytes);
        stream.Seek(currentPos, SeekOrigin.Begin);
        bw.Flush();
    }
}
#endif
