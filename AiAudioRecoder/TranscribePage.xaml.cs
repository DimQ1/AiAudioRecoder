using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Essentials;

namespace AiAudioRecoder;

public partial class TranscribePage : ContentPage
{
    private string? _selectedFilePath;
    private Services.WhisperTranscriptionService _whisper =>
        (Services.WhisperTranscriptionService)App.Current?.Handler?.MauiContext?.Services.GetService(typeof(Services.WhisperTranscriptionService))!;
    private Services.AudioMetadataDatabase _db =>
        (Services.AudioMetadataDatabase)App.Current?.Handler?.MauiContext?.Services.GetService(typeof(Services.AudioMetadataDatabase))!;

    public TranscribePage()
    {
        InitializeComponent();
    }

    public TranscribePage(bool resolveServices) : this()
    {
        if (resolveServices)
        {
            _ = _whisper;
            _ = _db;
        }
    }

    private async void OnPickFileClicked(object? sender, EventArgs e)
    {
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".wav", ".mp3", ".m4a" } },
                    { DevicePlatform.Android, new[] { "audio/*" } },
                    { DevicePlatform.iOS, new[] { "public.audio" } }
                }
            );

            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Выберите аудиофайл",
                FileTypes = customFileType
            });

            if (result != null)
            {
                _selectedFilePath = result.FullPath;
                FileLabel.Text = Path.GetFileName(_selectedFilePath);
                TranscribeButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
    }

    private async void OnTranscribeClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedFilePath))
        {
            await DisplayAlertAsync("Ошибка", "Файл не выбран.", "OK");
            return;
        }

        try
        {
            TranscribeProgress.IsVisible = true;
            TranscribeProgress.Progress = 0;
            TranscribeButton.IsEnabled = false;

            var transcriptionOutput = await _whisper.TranscribeAsync(_selectedFilePath);
            var summary = GenerateSummary(transcriptionOutput.Text);
            var start = DateTime.Now;
            var end = DateTime.Now;
            var meta = new Models.AudioFileMetadata
            {
                FilePath = _selectedFilePath,
                Transcription = transcriptionOutput.Text,
                Summary = summary,
                WordTimings = transcriptionOutput.WordTimings,
                StartTime = start,
                EndTime = end
            };
            await _db.AddMetadataAsync(meta);

            ResultLabel.Text = $"Транскрипция: {transcriptionOutput.Text}\nСуммари: {summary}";
            ResultLabel.IsVisible = true;
            await DisplayAlertAsync("Готово", $"Транскрипция сохранена!", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            TranscribeProgress.IsVisible = false;
            TranscribeProgress.Progress = 0;
            TranscribeButton.IsEnabled = true;
        }
    }

    private async void OnShowRecordsClicked(object? sender, EventArgs e)
    {
        try
        {
            var all = await _db.GetAllAsync();
            // Отображение списка (можно добавить CollectionView как в RecordPage)
            await DisplayAlertAsync("Записи", $"Всего записей: {all.Count}", "OK");
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