#if MACCATALYST
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AiAudioRecoder.Services;

public partial class AudioRecorderService
{
    private FileStream? _macStream;
    private CancellationTokenSource? _macCts;
    private Task? _macWriteTask;
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
            _macStream = File.Create(path);
            WriteWaveHeaderPlaceholder(_macStream, 44100, 2, 16);
            _macCts = new CancellationTokenSource();
            _dataBytes = 0;
            _macWriteTask = Task.Run(async ()=> await SilentLoopAsync(_macCts.Token));
            _filePath = path;
            _isRecording = true;
            return Task.FromResult<string?>(_filePath);
        }
        catch (Exception)
        {
            _macStream?.Dispose();
            return Task.FromResult<string?>(null);
        }
    }

    private async Task SilentLoopAsync(CancellationToken token)
    {
        int blockBytes = 44100 * 4 / 10;
        var buffer = new byte[blockBytes];
        while (!token.IsCancellationRequested)
        {
            _macStream?.Write(buffer, 0, buffer.Length);
            _dataBytes += buffer.Length;
            try { await Task.Delay(100, token); } catch { }
        }
    }

    public Task StopRecordingAsync()
    {
        if (!_isRecording) return Task.CompletedTask;
        _macCts?.Cancel();
        try { _macWriteTask?.Wait(1000); } catch { }
        if (_macStream != null)
        {
            FinalizeWaveHeader(_macStream, _dataBytes);
            _macStream.Dispose();
            _macStream = null;
        }
        _macCts?.Dispose(); _macCts = null;
        _macWriteTask = null;
        _isRecording = false;
        return Task.CompletedTask;
    }

    private static void WriteWaveHeaderPlaceholder(Stream stream, int sampleRate, short channels, short bitsPerSample)
    {
        var bw = new BinaryWriter(stream);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(0);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(0);
        bw.Flush();
    }

    private static void FinalizeWaveHeader(Stream stream, int dataBytes)
    {
        long currentPos = stream.Position;
        stream.Seek(4, SeekOrigin.Begin);
        var bw = new BinaryWriter(stream);
        bw.Write(dataBytes + 36);
        stream.Seek(40, SeekOrigin.Begin);
        bw.Write(dataBytes);
        stream.Seek(currentPos, SeekOrigin.Begin);
        bw.Flush();
    }
}
#endif
