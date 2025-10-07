using Whisper.net;
using Whisper.net.Ggml;
using System.Net.Http;
using System.IO;
using Microsoft.Maui.Essentials;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Storage;
using AiAudioRecoder.Models;
using System.ComponentModel;

namespace AiAudioRecoder;

public class BoolToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (bool?)value == true ? "Выбрать" : "Скачать";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class DownloadButtonTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ModelInfoDb model)
        {
            if (model.IsDownloading)
            {
                return "Отмена";
            }
            return model.IsDownloaded ? "Выбрать" : "Скачать";
        }
        return "Скачать";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public partial class ModelsPage : ContentPage
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ModelInfoDatabase _modelDb;
    private ObservableCollection<ModelInfoDb>? _models;
    public ObservableCollection<ModelInfoDb>? Models
    {
        get => _models;
        set
        {
            _models = value;
            OnPropertyChanged(nameof(Models));
        }
    }

    private CancellationTokenSource? _downloadCts;
    private ModelInfoDb? _currentDownloadingModel;

    public ModelsPage(ModelInfoDatabase modelDb)
    {
        _modelDb = modelDb;
        Models = new ObservableCollection<ModelInfoDb>();
        InitializeComponent();
        this.BindingContext = this;
        _ = LoadModelsAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Update models from folder after page appears
        _ = Task.Run(async () =>
        {
            try
            {
                var modelsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiAudioRecoder", "models");
                await _modelDb.UpdateFromFolderAsync(modelsDir);
                // Reload models on main thread
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var models = await _modelDb.GetModelsAsync();
                    Models = new ObservableCollection<ModelInfoDb>(models);
                    UpdateModels();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating models from folder: {ex.Message}");
            }
        });
    }

    private async Task LoadModelsAsync()
    {
        await _modelDb.EnsureDefaultModelsAsync();
        var models = await _modelDb.GetModelsAsync();
        System.Diagnostics.Debug.WriteLine($"Loaded {models.Count} models");
        foreach (var m in models)
        {
            System.Diagnostics.Debug.WriteLine($"Model: {m.Name}, SizeMB: {m.SizeMB}");
            if (m.SizeMB == 0 && m.SizeBytes > 0)
            {
                m.SizeMB = m.SizeBytes / (1024.0 * 1024.0);
                await _modelDb.SaveModelAsync(m);
            }
        }
        Models = new ObservableCollection<ModelInfoDb>(models);
        UpdateModels();
    }

    private void UpdateModels()
    {
        if (Models == null) return;
        var current = Preferences.Get("CurrentModel", "base");
        foreach (var m in Models)
        {
            m.IsDownloaded = !string.IsNullOrEmpty(m.LocalPath) && File.Exists(m.LocalPath);
            m.IsCurrent = m.Name == current;
            if (m.IsDownloading)
            {
                m.Status = "Скачивается";
            }
            else if (m.IsDownloaded && !string.IsNullOrEmpty(m.LocalPath))
            {
                var fileSize = new FileInfo(m.LocalPath).Length;
                if (fileSize == m.SizeBytes)
                    m.Status = "Готово";
                else
                    m.Status = "Ошибка";
            }
            else
            {
                m.Status = "Не скачано";
            }
        }
    }

    private string GetModelPath(string name)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(root, "AiAudioRecoder", "models");
        return Path.Combine(dir, $"ggml-{name}.bin");
    }

    private async void OnModelActionClicked(object sender, EventArgs e)
    {
        if (Models == null) return;
        var button = sender as Button;
        var name = button?.CommandParameter as string;
        if (name == null) return;
        var model = Models.FirstOrDefault(m => m.Name == name);
        if (model == null) return;
        if (model.IsDownloading)
        {
            // Cancel download
            System.Diagnostics.Debug.WriteLine($"Canceling download for {model.Name}");
            if (_downloadCts != null && !_downloadCts.Token.IsCancellationRequested)
            {
                _downloadCts.Cancel();
                model.IsDownloading = false;
                model.Status = "Отменено";
                // Delete partial file
                if (!string.IsNullOrEmpty(model.LocalPath) && File.Exists(model.LocalPath))
                {
                    try
                    {
                        File.Delete(model.LocalPath);
                        model.LocalPath = "";
                        model.IsDownloaded = false;
                        await _modelDb.SaveModelAsync(model);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error deleting partial file: {ex.Message}");
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Cannot cancel: CTS is null or already canceled");
            }
        }
        else if (!model.IsDownloaded)
        {
            await DownloadModel(model);
        }
        else
        {
            await SetCurrentModel(model);
        }
    }

    private async Task DownloadModel(ModelInfoDb model)
    {
        _downloadCts = new CancellationTokenSource();
        _currentDownloadingModel = model;
        string fileName = $"ggml-{model.Name}.bin";
        try
        {
            System.Diagnostics.Debug.WriteLine($"Starting download for {model.Name}");
            model.IsDownloading = true;
            model.Status = "Скачивается";
            model.Progress = 0;
            await DisplayAlertAsync("Загрузка", $"Скачивание модели {model.Name}...", "OK");
            var ggmlType = model.Name switch
            {
                "tiny" => GgmlType.Tiny,
                "base" => GgmlType.Base,
                "small" => GgmlType.Small,
                "medium" => GgmlType.Medium,
                "large" => GgmlType.LargeV2,
                "large-v2" => GgmlType.LargeV2,
                "large-v3" => GgmlType.LargeV3,
                _ => GgmlType.Base
            };
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model.Name}.bin";
            System.Diagnostics.Debug.WriteLine($"URL: {url}");
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(root, "AiAudioRecoder", "models");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _downloadCts.Token);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? model.SizeBytes;
            if (totalBytes == 0)
            {
                totalBytes = model.SizeBytes; // Fallback to stored size
            }
            System.Diagnostics.Debug.WriteLine($"Total bytes: {totalBytes}");
            model.SizeBytes = totalBytes;
            model.SizeMB = totalBytes / (1024.0 * 1024.0);
            await _modelDb.SaveModelAsync(model);

            using var contentStream = await response.Content.ReadAsStreamAsync(_downloadCts.Token);
            using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[8192];
            var totalBytesRead = 0L;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, _downloadCts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _downloadCts.Token);
                totalBytesRead += bytesRead;
                model.DownloadedBytes = totalBytesRead;
                if (totalBytes > 0)
                {
                    var progress = (double)totalBytesRead / totalBytes;
                    model.Progress = progress;
                    await MainThread.InvokeOnMainThreadAsync(() => { });
                }
            }
            System.Diagnostics.Debug.WriteLine($"Download completed for {model.Name}");
            await DisplayAlertAsync("Успех", $"Модель {fileName} успешно загружена!", "OK");
            model.IsDownloading = false;
            model.IsDownloaded = true;
            model.LocalPath = path;
            model.Status = "Готово";
            await _modelDb.SaveModelAsync(model);
            UpdateModels();
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Download canceled for {model.Name}");
            model.IsDownloading = false;
            model.Status = "Отменено";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Download error for {model.Name}: {ex.Message}");
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
            model.IsDownloading = false;
            model.Status = "Ошибка";
            model.Progress = 0;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            _currentDownloadingModel = null;
            DownloadProgress.IsVisible = false;
            DownloadProgress.Progress = 0;
        }
    }

    private async Task SetCurrentModel(ModelInfoDb model)
    {
        if (!File.Exists(model.LocalPath) || new FileInfo(model.LocalPath).Length < model.SizeBytes)
        {
            var result = await DisplayAlertAsync("Предупреждение", $"Файл модели {model.Name} поврежден или неполный. Скачать повторно?", "Да", "Нет");
            if (result)
            {
                await DownloadModel(model);
                return;
            }
        }
        else
        {
            Preferences.Set("CurrentModel", model.Name);
            UpdateModels();
        }
    }

    private void OnDeviceChanged(object sender, EventArgs e)
    {
        var device = DevicePicker.SelectedItem?.ToString() ?? "CPU";
        Preferences.Set("DeviceType", device);
        DeviceNumberPicker.IsVisible = device == "GPU";
        DisplayAlertAsync("Уведомление", "Изменения устройства вступят в силу после перезапуска приложения.", "OK");
    }

    private void OnDeviceNumberChanged(object sender, EventArgs e)
    {
        var number = DeviceNumberPicker.SelectedIndex;
        Preferences.Set("DeviceNumber", number);
        DisplayAlertAsync("Уведомление", "Изменения номера устройства вступят в силу после перезапуска приложения.", "OK");
    }

    private void OnPauseResumeClicked(object sender, EventArgs e)
    {
        if (_currentDownloadingModel != null)
        {
            if (_downloadCts != null && !_downloadCts.Token.IsCancellationRequested)
            {
                _downloadCts.Cancel();
            }
            else
            {
                // Resume download
                _ = DownloadModel(_currentDownloadingModel);
            }
        }
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        if (Models == null) return;
        var button = sender as Button;
        var name = button?.CommandParameter as string;
        if (name == null) return;
        var model = Models.FirstOrDefault(m => m.Name == name);
        if (model == null || !model.IsDownloaded || string.IsNullOrEmpty(model.LocalPath)) return;

        var result = await DisplayAlertAsync("Подтверждение", $"Удалить модель {model.Name}?", "Да", "Нет");
        if (result)
        {
            try
            {
                if (File.Exists(model.LocalPath))
                {
                    File.Delete(model.LocalPath);
                }
                model.IsDownloaded = false;
                model.LocalPath = "";
                model.Status = "Не скачано";
                await _modelDb.SaveModelAsync(model);
                UpdateModels();
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Ошибка", $"Не удалось удалить модель: {ex.Message}", "OK");
            }
        }
    }
}