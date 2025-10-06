using Whisper.net;
using Whisper.net.Ggml;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.IO;
using Microsoft.Maui.Essentials;
using System.Threading.Tasks;

namespace AiAudioRecoder;

public partial class RecordPage : ContentPage
{
    private ObservableCollection<Models.AudioFileMetadata> _records = new();
    private readonly HttpClient _httpClient = new();
    private Services.IAudioRecorderService _recorder =>
        (Services.IAudioRecorderService)App.Current?.Handler?.MauiContext?.Services.GetService(typeof(Services.IAudioRecorderService))!;
    private Services.AudioMetadataDatabase _db =>
        (Services.AudioMetadataDatabase)App.Current?.Handler?.MauiContext?.Services.GetService(typeof(Services.AudioMetadataDatabase))!;
    private Services.WhisperTranscriptionService _whisper =>
        (Services.WhisperTranscriptionService)App.Current?.Handler?.MauiContext?.Services.GetService(typeof(Services.WhisperTranscriptionService))!;
    private TaskCompletionSource<bool>? _recordingTcs;
    private bool _isRecordingInProgress = false;

    public RecordPage()
    {
        InitializeComponent();
        RecordsView.ItemsSource = _records;
    }

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        if (_isRecordingInProgress) return;
        try
        {
            // Запрос разрешения на микрофон для Android/iOS
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Microphone>();
            }
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlertAsync("Ошибка", "Разрешение на микрофон не предоставлено.", "OK");
                return;
            }

            var source = SystemAudioSwitch.IsToggled ? "system" : "mic";
            var start = DateTime.Now;
            var filePath = await _recorder.StartRecordingAsync(string.Empty, string.Empty, source);
            if (string.IsNullOrEmpty(filePath))
            {
                await DisplayAlertAsync("Ошибка", "Не удалось начать запись.", "OK");
                return;
            }

            _recordingTcs = new TaskCompletionSource<bool>();
            _isRecordingInProgress = true;
            RecordButton.IsVisible = false;
            StopButton.IsVisible = true;

            // Ожидаем остановки
            await _recordingTcs.Task;

            await _recorder.StopRecordingAsync();
            var end = DateTime.Now;

            if (!string.IsNullOrEmpty(filePath))
            {
                var transcription = await _whisper.TranscribeAsync(filePath);
                var summary = GenerateSummary(transcription);
                var meta = new Models.AudioFileMetadata
                {
                    FilePath = filePath,
                    Transcription = transcription,
                    Summary = summary,
                    StartTime = start,
                    EndTime = end
                };
                await _db.AddMetadataAsync(meta);
                await DisplayAlertAsync("Готово", $"Транскрипция: {summary}", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            _isRecordingInProgress = false;
            RecordButton.IsVisible = true;
            StopButton.IsVisible = false;
            _recordingTcs = null;
        }
    }

    private void OnStopRecordingClicked(object? sender, EventArgs e)
    {
        _recordingTcs?.SetResult(true);
    }

    private async void OnShowRecordsClicked(object? sender, EventArgs e)
    {
        try
        {
            var all = await _db.GetAllAsync();
            _records.Clear();
            foreach (var rec in all)
                _records.Add(rec);
            RecordsView.IsVisible = _records.Count > 0;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
    }

    private string GenerateSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sentences = text.Split('.', '!', '?');
        return sentences.Length > 0 ? sentences[0].Trim() : text;
    }
}