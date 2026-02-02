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
using Microsoft.Extensions.DependencyInjection;
using AiAudioRecoder.Services;

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
            if (model.IsDownloading || model.Status == "Скачивается")
            {
                return "Отмена";
            }
            if (model.Status == "Ошибка")
            {
                return "Перескачать";
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

public class PauseResumeTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ModelInfoDb model)
        {
            if (model.IsDownloading) return "Пауза";
            if (model.IsPaused) return "Продолжить";
        }
        return "Пауза";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class PauseResumeVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ModelInfoDb model)
        {
            return model.IsDownloading || model.IsPaused;
        }
        return false;
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
    private readonly Dictionary<string, CancellationTokenSource> _downloadCtsByModel = new();
    private readonly object _downloadLock = new();
    private readonly HashSet<string> _pauseRequested = new();
    private readonly HashSet<string> _cancelRequested = new();
    private readonly Dictionary<string, ModelInfoDb> _activeModelInstances = new();
    private bool _isInitializingDeviceSettings;
    public ObservableCollection<ModelInfoDb>? Models
    {
        get => _models;
        set
        {
            _models = value;
            OnPropertyChanged(nameof(Models));
        }
    }

    public ModelsPage(ModelInfoDatabase modelDb)
    {
        _modelDb = modelDb;
        Models = new ObservableCollection<ModelInfoDb>();
        InitializeComponent();
        this.BindingContext = this;
        _ = LoadModelsAsync();
    }

    // Parameterless constructor for XAML instantiation
    public ModelsPage() : this(
        (ModelInfoDatabase)App.Current?.Handler?.MauiContext?.Services.GetService(typeof(ModelInfoDatabase))!
    )
    { }

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
                    lock (_downloadLock)
                    {
                        for (int i = 0; i < models.Count; i++)
                        {
                            if (models[i].Name != null && _activeModelInstances.TryGetValue(models[i].Name!, out var active))
                            {
                                models[i] = active;
                            }
                        }
                    }
                    models = models.OrderBy(m => m.Name).ToList();
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
        await LoadDeviceSettingsAsync();
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
        lock (_downloadLock)
        {
            for (int i = 0; i < models.Count; i++)
            {
                if (models[i].Name != null && _activeModelInstances.TryGetValue(models[i].Name!, out var active))
                {
                    models[i] = active;
                }
            }
        }
        models = models.OrderBy(m => m.Name).ToList();
        Models = new ObservableCollection<ModelInfoDb>(models);
        UpdateModels();
    }

    private async Task LoadDeviceSettingsAsync()
    {
        _isInitializingDeviceSettings = true;
        var deviceType = await _modelDb.GetSettingAsync("DeviceType") ?? "CPU";
        var deviceNumberStr = await _modelDb.GetSettingAsync("DeviceNumber") ?? "0";
        if (DevicePicker.ItemsSource is IList<string> deviceItems)
        {
            var index = deviceItems.IndexOf(deviceType);
            DevicePicker.SelectedIndex = index >= 0 ? index : 0;
        }
        else
        {
            DevicePicker.SelectedIndex = deviceType == "GPU" ? 1 : 0;
        }
        if (int.TryParse(deviceNumberStr, out var number))
        {
            DeviceNumberPicker.SelectedIndex = number;
        }
        else
        {
            DeviceNumberPicker.SelectedIndex = 0;
        }
        DeviceNumberPicker.IsVisible = (DevicePicker.SelectedItem?.ToString() ?? "CPU") == "GPU";
        _isInitializingDeviceSettings = false;
    }

    private void UpdateModels()
    {
        if (Models == null) return;
        var current = Preferences.Get("CurrentModel", "base");
        foreach (var m in Models)
        {
            var isActiveDownload = !string.IsNullOrEmpty(m.Name) && _downloadCtsByModel.ContainsKey(m.Name!);
            if (!isActiveDownload)
            {
                m.IsDownloading = false;
                if (!m.IsPaused)
                {
                    m.Status = "";
                }
            }
            var fileExists = !string.IsNullOrEmpty(m.LocalPath) && File.Exists(m.LocalPath);
            var fileSize = fileExists ? new FileInfo(m.LocalPath!).Length : 0;
            m.IsDownloaded = fileExists && ((m.SizeBytes > 0 && fileSize == m.SizeBytes) || (m.SizeBytes == 0 && fileSize > 0));
            m.IsCurrent = m.Name == current;
            if (m.IsDownloading)
            {
                m.Status = "Скачивается";
            }
            else if (m.IsPaused)
            {
                m.Status = "Пауза";
            }
            else if (m.IsDownloaded && !string.IsNullOrEmpty(m.LocalPath))
            {
                m.Status = "Готово";
            }
            else if (fileExists && m.SizeBytes > 0 && fileSize != m.SizeBytes)
            {
                // If file size differs but we are not downloading, it's an error. 
                // However, if we just imported it or updated from folder, SizeBytes should match.
                // If it persists as Error, it means SizeBytes in DB != File on disk.
                m.Status = "Ошибка";
                System.Diagnostics.Debug.WriteLine($"Model {m.Name} error: FileSize={fileSize}, Expected={m.SizeBytes}");
            }
            else if (fileExists && m.SizeBytes == 0 && fileSize > 0)
            {
                m.Status = "Готово";
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
            CancelDownload(model.Name ?? "");
        }
        else if (model.Status == "Ошибка")
        {
            _ = DownloadModelAsync(model);
        }
        else if (!model.IsDownloaded)
        {
            _ = DownloadModelAsync(model);
        }
        else
        {
            await SetCurrentModel(model);
        }
    }

    private async Task DownloadModelAsync(ModelInfoDb model, bool resume = false)
    {
        if (string.IsNullOrWhiteSpace(model.Name)) return;
        if (!TryStartDownload(model.Name)) return;

        lock (_downloadLock) { _activeModelInstances[model.Name] = model; }

        var cts = GetDownloadCts(model.Name);
        var token = cts.Token;
        string fileName = $"ggml-{model.Name}.bin";
        var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dir = Path.Combine(root, "AiAudioRecoder", "models");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model.Name}.bin";

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                model.IsDownloading = true;
                model.IsPaused = false;
                model.Status = "Скачивается";
                model.Progress = 0;
                model.DownloadedBytes = 0;
                model.LocalPath = path;
            });

            await _modelDb.SaveModelAsync(model).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"Starting download for {model.Name} from {url}");

            long existingBytes = 0;
            if (resume && File.Exists(path))
            {
                existingBytes = new FileInfo(path).Length;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (resume && existingBytes > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (resume && existingBytes > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                existingBytes = 0;
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch { }
                }
            }

            long totalBytes;
            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                totalBytes = response.Content.Headers.ContentRange?.Length
                             ?? (existingBytes + (response.Content.Headers.ContentLength ?? 0));
            }
            else
            {
                totalBytes = response.Content.Headers.ContentLength ?? model.SizeBytes;
            }
            if (totalBytes == 0) totalBytes = model.SizeBytes;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                model.SizeBytes = totalBytes;
                model.SizeMB = totalBytes / (1024.0 * 1024.0);
            });
            await _modelDb.SaveModelAsync(model).ConfigureAwait(false);

            await using var contentStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var fileMode = existingBytes > 0 ? FileMode.Append : FileMode.Create;
            await using var fileStream = new FileStream(path, fileMode, FileAccess.ReadWrite, FileShare.ReadWrite, 81920, true);

            var buffer = new byte[81920];
            var totalBytesRead = existingBytes;
            int bytesRead;
            var lastReport = DateTime.UtcNow;

            if (existingBytes > 0 && totalBytes > 0)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    model.DownloadedBytes = existingBytes;
                    model.Progress = (double)existingBytes / totalBytes;
                });
            }

            while ((bytesRead = await contentStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token).ConfigureAwait(false);
                totalBytesRead += bytesRead;

                var now = DateTime.UtcNow;
                if (now - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = now;
                    var progress = totalBytes > 0 ? (double)totalBytesRead / totalBytes : 0;
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        model.DownloadedBytes = totalBytesRead;
                        model.Progress = progress;
                    });
                }
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                model.DownloadedBytes = totalBytesRead;
                model.Progress = totalBytes > 0 ? (double)totalBytesRead / totalBytes : 1;
            });


            var fileSize = fileStream.Length;

            if (totalBytes <= 0)
            {
                totalBytes = fileSize;
            }

            bool shaVerified = true;
            if (fileSize > 0 && (totalBytes == 0 || model.DownloadedBytes == totalBytes) && !string.IsNullOrEmpty(model.Sha))
            {
                await MainThread.InvokeOnMainThreadAsync(() => model.Status = "Проверка...");
                try
                {
                    string calculatedSha = await Task.Run(() =>
                    {
                        fileStream.Position = 0;
                        using var sha1 = System.Security.Cryptography.SHA1.Create();
                        var hash = sha1.ComputeHash(fileStream);
                        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    });

                    if (!string.Equals(calculatedSha, model.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        shaVerified = false;
                        System.Diagnostics.Debug.WriteLine($"SHA verification failed for {model.Name}. Expected {model.Sha}, got {calculatedSha}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error calculating SHA: {ex.Message}");
                    shaVerified = false;
                }
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                model.IsDownloading = false;

                bool sizeCorrect = fileSize > 0 && (totalBytes == 0 || fileSize == totalBytes);

                if (sizeCorrect)
                {
                    if (shaVerified)
                    {
                        model.IsDownloaded = true;
                        model.Status = "Готово";
                    }
                    else
                    {
                        model.IsDownloaded = false;
                        model.Status = "Ошибка SHA";
                        // Optionally delete the bad file here or leave it for the user to retry (which overwrites)
                    }
                }
                else
                {
                    model.IsDownloaded = false;
                    model.Status = "Ошибка";
                }

                model.LocalPath = path;
                if (model.SizeBytes == 0 && fileSize > 0)
                {
                    model.SizeBytes = fileSize;
                    model.SizeMB = fileSize / (1024.0 * 1024.0);
                }
            });

            await _modelDb.SaveModelAsync(model).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Download canceled for {model.Name}");
            var stopMode = GetStopMode(model.Name!);
            var paused = stopMode == StopMode.Pause;
            var currentSize = File.Exists(path) ? new FileInfo(path).Length : 0;
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                model.IsDownloading = false;
                model.IsPaused = paused;
                model.Status = paused ? "Пауза" : "Отменено";
                model.IsDownloaded = false;
                if (paused)
                {
                    model.LocalPath = path;
                    model.DownloadedBytes = currentSize;
                    model.Progress = model.SizeBytes > 0 ? (double)currentSize / model.SizeBytes : 0;
                }
                else
                {
                    model.Progress = 0;
                    model.LocalPath = "";
                }
            });

            if (!paused && File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
            await _modelDb.SaveModelAsync(model).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Download error for {model.Name}: {ex.Message}");
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                model.IsDownloading = false;
                model.Status = "Ошибка";
                model.Progress = 0;
                model.IsDownloaded = false;
                model.LocalPath = "";
            });
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
            await _modelDb.SaveModelAsync(model).ConfigureAwait(false);
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            lock (_downloadLock) { _activeModelInstances.Remove(model.Name!); }
            EndDownload(model.Name!);
        }
    }

    private enum StopMode
    {
        None,
        Pause,
        Cancel
    }

    private StopMode GetStopMode(string modelName)
    {
        lock (_downloadLock)
        {
            if (_pauseRequested.Contains(modelName)) return StopMode.Pause;
            if (_cancelRequested.Contains(modelName)) return StopMode.Cancel;
            return StopMode.None;
        }
    }

    private bool TryStartDownload(string modelName)
    {
        lock (_downloadLock)
        {
            if (_downloadCtsByModel.ContainsKey(modelName))
            {
                return false;
            }
            _downloadCtsByModel[modelName] = new CancellationTokenSource();
            return true;
        }
    }

    private CancellationTokenSource GetDownloadCts(string modelName)
    {
        lock (_downloadLock)
        {
            return _downloadCtsByModel[modelName];
        }
    }

    private void EndDownload(string modelName)
    {
        lock (_downloadLock)
        {
            if (_downloadCtsByModel.TryGetValue(modelName, out var cts))
            {
                cts.Dispose();
                _downloadCtsByModel.Remove(modelName);
            }
            _pauseRequested.Remove(modelName);
            _cancelRequested.Remove(modelName);
        }
    }

    private void CancelDownload(string modelName)
    {
        lock (_downloadLock)
        {
            _cancelRequested.Add(modelName);
            _pauseRequested.Remove(modelName);
            if (_downloadCtsByModel.TryGetValue(modelName, out var cts) && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
    }

    private void PauseDownload(string modelName)
    {
        lock (_downloadLock)
        {
            _pauseRequested.Add(modelName);
            _cancelRequested.Remove(modelName);
            if (_downloadCtsByModel.TryGetValue(modelName, out var cts) && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
    }

    private async Task SetCurrentModel(ModelInfoDb model)
    {
        if (!File.Exists(model.LocalPath) || new FileInfo(model.LocalPath).Length < model.SizeBytes)
        {
            var result = await DisplayAlertAsync("Предупреждение", $"Файл модели {model.Name} поврежден или неполный. Скачать повторно?", "Да", "Нет");
            if (result)
            {
                _ = DownloadModelAsync(model);
                return;
            }
        }
        else
        {
            Preferences.Set("CurrentModel", model.Name);
            var service = this.Handler?.MauiContext?.Services?.GetService<AiAudioRecoder.Services.WhisperTranscriptionService>();
            if (service != null)
            {
                service.ModelPath = model.LocalPath;
            }
            UpdateModels();
            _ = DisplayAlertAsync("Уведомление", "Модель изменена.", "OK");
        }
    }

    private void OnDeviceChanged(object sender, EventArgs e)
    {
        if (_isInitializingDeviceSettings) return;
        var device = DevicePicker.SelectedItem?.ToString() ?? "CPU";
        Preferences.Set("DeviceType", device);
        _ = _modelDb.SetSettingAsync("DeviceType", device);
        DeviceNumberPicker.IsVisible = device == "GPU";
        var service = this.Handler?.MauiContext?.Services?.GetService<AiAudioRecoder.Services.WhisperTranscriptionService>();
        if (service != null)
        {
            service.Device = device == "GPU" ? AiAudioRecoder.Services.DeviceType.Gpu : AiAudioRecoder.Services.DeviceType.Cpu;
        }
        _ = DisplayAlertAsync("Уведомление", "Изменения устройства применены.", "OK");
    }

    private void OnDeviceNumberChanged(object sender, EventArgs e)
    {
        if (_isInitializingDeviceSettings) return;
        var number = DeviceNumberPicker.SelectedIndex;
        Preferences.Set("DeviceNumber", number);
        _ = _modelDb.SetSettingAsync("DeviceNumber", number.ToString());
        var service = this.Handler?.MauiContext?.Services?.GetService<AiAudioRecoder.Services.WhisperTranscriptionService>();
        if (service != null)
        {
            service.DeviceIndex = number;
        }
        _ = DisplayAlertAsync("Уведомление", "Изменения номера устройства применены.", "OK");
    }

    private void OnPauseResumeClicked(object sender, EventArgs e)
    {
        if (Models == null) return;
        var button = sender as Button;
        var name = button?.CommandParameter as string;
        if (string.IsNullOrWhiteSpace(name)) return;
        var model = Models.FirstOrDefault(m => m.Name == name);
        if (model == null) return;

        if (model.IsDownloading)
        {
            PauseDownload(model.Name ?? "");
        }
        else if (model.IsPaused)
        {
            _ = DownloadModelAsync(model, resume: true);
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
                await MainThread.InvokeOnMainThreadAsync(() => { });
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Ошибка", $"Не удалось удалить модель: {ex.Message}", "OK");
            }
        }
    }

    private async void OnImportClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Выберите файл модели Whisper (bin)",
            });

            if (result != null)
            {
                if (!result.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    await DisplayAlertAsync("Ошибка", "Файл должен иметь расширение .bin", "OK");
                    return;
                }

                string modelName = result.FileName.Replace("ggml-", "", StringComparison.OrdinalIgnoreCase).Replace(".bin", "", StringComparison.OrdinalIgnoreCase);

                var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var dir = Path.Combine(root, "AiAudioRecoder", "models");
                Directory.CreateDirectory(dir);
                var destName = $"ggml-{modelName}.bin";
                var destPath = Path.Combine(dir, destName);

                if (File.Exists(destPath))
                {
                    bool answer = await DisplayAlertAsync("Файл существует", $"Модель {modelName} уже существует. Заменить?", "Да", "Нет");
                    if (!answer) return;
                }

                await DisplayAlertAsync("Импорт", "Копирование файла...", "OK");

                using var sourceStream = await result.OpenReadAsync();
                using var destStream = File.Create(destPath);
                await sourceStream.CopyToAsync(destStream);

                await _modelDb.UpdateFromFolderAsync(dir);
                // Reload on main thread to update UI
                await MainThread.InvokeOnMainThreadAsync(async () => await LoadModelsAsync());

                await DisplayAlertAsync("Успех", $"Модель {modelName} добавлена.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", $"Ошибка импорта: {ex.Message}", "OK");
        }
    }
}