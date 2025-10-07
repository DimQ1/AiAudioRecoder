using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Whisper.net;
using Whisper.net.Ggml;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.IO;
using Microsoft.Maui.Essentials;
using System.Threading.Tasks;
using Microsoft.Maui.Dispatching;

namespace AiAudioRecoder;

public partial class MainPage : ContentPage
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
    private CancellationTokenSource? _downloadCancellationTokenSource;
    private bool _isDownloadInProgress = false;
    private string? _currentDownloadPath;

    public MainPage()
    {
        InitializeComponent();
        RecordsView.ItemsSource = _records;
        // Устанавливаем "base" по умолчанию (индекс 1)
        ModelPicker.SelectedIndex = 1;
    }

    private async void OnDownloadModelClicked(object? sender, EventArgs e)
    {
        if (_isDownloadInProgress)
        {
            // Cancel download
            _downloadCancellationTokenSource?.Cancel();
            return;
        }

        var modelName = ModelPicker.SelectedItem?.ToString() ?? "base";
        string fileName = $"ggml-{modelName}.bin";
        string? path = null;
        
        try
        {
            DownloadProgress.IsVisible = true;
            DownloadProgress.Progress = 0;
            DownloadButton.Text = "Отмена загрузки";
            _isDownloadInProgress = true;
            _downloadCancellationTokenSource = new CancellationTokenSource();
            
            // Show alert after button click
            _ = DisplayAlertAsync("Загрузка", $"Скачивание модели {modelName}...", "OK");
            
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
            path = Path.Combine(dir, fileName);

            if (File.Exists(path))
            {
                await DisplayAlertAsync("Успех", $"Модель {fileName} уже существует в {path}!", "OK");
                ResetDownloadUI();
                return;
            }

            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _downloadCancellationTokenSource.Token);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            
            using var contentStream = await response.Content.ReadAsStreamAsync(_downloadCancellationTokenSource.Token);
            using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[8192];
            var totalBytesRead = 0L;
            int bytesRead;
            
            while ((bytesRead = await contentStream.ReadAsync(buffer, _downloadCancellationTokenSource.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _downloadCancellationTokenSource.Token);
                totalBytesRead += bytesRead;
                
                if (totalBytes > 0 && !(_downloadCancellationTokenSource.Token.IsCancellationRequested))
                {
                    var progress = Math.Min(1.0, (double)totalBytesRead / totalBytes);
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        DownloadProgress.Progress = progress;
                    });
                }
            }
            
            if (!_downloadCancellationTokenSource.Token.IsCancellationRequested)
            {
                await DisplayAlertAsync("Успех", $"Модель {fileName} успешно загружена в {path}!", "OK");
            }
        }
        catch (OperationCanceledException)
        {
            await DisplayAlertAsync("Отменено", "Загрузка модели была отменена.", "OK");
            // Delete partial file if cancelled
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                try { File.Delete(path); } catch { }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("cancelled"))
        {
            await DisplayAlertAsync("Отменено", "Загрузка модели была отменена.", "OK");
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                try { File.Delete(path); } catch { }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", $"Не удалось загрузить модель: {ex.Message}", "OK");
        }
        finally
        {
            ResetDownloadUI();
        }
    }

    private void ResetDownloadUI()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            DownloadProgress.IsVisible = false;
            DownloadProgress.Progress = 0;
            DownloadButton.Text = "Скачать выбранную модель";
            _isDownloadInProgress = false;
        });
        
        _downloadCancellationTokenSource?.Dispose();
        _downloadCancellationTokenSource = null;
    }

    private async void OnRecordAndTranscribeClicked(object? sender, EventArgs e)
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
