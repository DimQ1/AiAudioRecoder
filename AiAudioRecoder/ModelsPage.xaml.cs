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

public partial class ModelsPage : ContentPage
{
    private readonly HttpClient _httpClient = new();
    private readonly ModelInfoDatabase _modelDb;
    public ObservableCollection<ModelInfoDb> Models { get; set; }

    public ModelsPage(ModelInfoDatabase modelDb)
    {
        _modelDb = modelDb;
        Models = new ObservableCollection<ModelInfoDb>();
        InitializeComponent();
        this.BindingContext = this;
        LoadModelsAsync();
    }

    private async void LoadModelsAsync()
    {
        await _modelDb.EnsureDefaultModelsAsync();
        var models = await _modelDb.GetModelsAsync();
        Models = new ObservableCollection<ModelInfoDb>(models);
        UpdateModels();
    }

    private void UpdateModels()
    {
        var current = Preferences.Get("CurrentModel", "base");
        foreach (var m in Models)
        {
            m.IsDownloaded = !string.IsNullOrEmpty(m.LocalPath) && File.Exists(m.LocalPath);
            m.IsCurrent = m.Name == current;
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
        var button = sender as Button;
        var name = button?.CommandParameter as string;
        if (name == null) return;
        var model = Models.FirstOrDefault(m => m.Name == name);
        if (model == null) return;
        if (!model.IsDownloaded)
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
        string fileName = $"ggml-{model.Name}.bin";
        try
        {
            model.IsDownloading = true;
            model.Progress = 0;
            await DisplayAlertAsync("Загрузка", $"Скачивание модели {model.Name}...", "OK");
            var ggmlType = model.Name switch
            {
                "tiny" => GgmlType.Tiny,
                "base" => GgmlType.Base,
                "small" => GgmlType.Small,
                "medium" => GgmlType.Medium,
                _ => GgmlType.Base
            };
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model.Name}.bin";
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(root, "AiAudioRecoder", "models");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);

            // Check if already downloaded and size matches
            if (File.Exists(path) && new FileInfo(path).Length == model.SizeBytes)
            {
                await DisplayAlertAsync("Успех", $"Модель {fileName} уже загружена!", "OK");
                model.IsDownloading = false;
                model.IsDownloaded = true;
                model.LocalPath = path;
                await _modelDb.SaveModelAsync(model);
                UpdateModels();
                return;
            }

            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            model.SizeBytes = totalBytes;
            model.SizeMB = totalBytes / (1024.0 * 1024.0);
            await _modelDb.SaveModelAsync(model);

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
                    model.Progress = progress;
                    await MainThread.InvokeOnMainThreadAsync(() => { });
                }
            }
            await DisplayAlertAsync("Успех", $"Модель {fileName} успешно загружена!", "OK");
            model.IsDownloading = false;
            model.IsDownloaded = true;
            model.LocalPath = path;
            await _modelDb.SaveModelAsync(model);
            UpdateModels();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
            model.IsDownloading = false;
            model.Progress = 0;
        }
        finally
        {
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
}