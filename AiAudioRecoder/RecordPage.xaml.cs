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
        // Устанавливаем "base" по умолчанию (индекс 1)
        ModelPicker.SelectedIndex = 1;
    }

    private async void OnDownloadModelClicked(object? sender, EventArgs e)
    {
        var modelName = ModelPicker.SelectedItem?.ToString() ?? "base";
        string fileName = $"ggml-{modelName}.bin";
        try
        {
            DownloadProgress.IsVisible = true;
            DownloadProgress.Progress = 0;
            await DisplayAlertAsync("Загрузка", $"Скачивание модели {modelName}...", "OK");
            // Определяем тип модели и URL (из HuggingFace whisper.cpp)
            var ggmlType = modelName switch
            {
                "tiny" => GgmlType.Tiny,
                "base" => GgmlType.Base,
                "small" => GgmlType.Small,
                "medium" => GgmlType.Medium,
                _ => GgmlType.Base
            };
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelName}.bin";
            // Используем MyDocuments для избежания проблем с разрешениями
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(root, "AiAudioRecoder", "models");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);

            if (File.Exists(path))
            {
                await DisplayAlertAsync("Успех", $"Модель {fileName} уже существует в {path}!", "OK");
                DownloadProgress.IsVisible = false;
                return;
            }

            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[8192];
            var totalBytesRead = 0L;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalBytesRead += bytesRead;
                if (totalBytes > 0)
                {
                    var progress = (double)totalBytesRead / totalBytes;
                    DownloadProgress.Progress = progress;
                    await MainThread.InvokeOnMainThreadAsync(() => { });
                }
            }
            // Обновляем путь модели в сервисе, если нужно (но сервис инициализирован с фиксированным путём, так что перезапуск приложения)
            await DisplayAlertAsync("Успех", $"Модель {fileName} успешно загружена в {path}!", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            DownloadProgress.IsVisible = false;
            DownloadProgress.Progress = 0;
        }
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