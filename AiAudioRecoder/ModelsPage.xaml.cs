using Whisper.net;
using Whisper.net.Ggml;
using System.Net.Http;
using System.IO;
using Microsoft.Maui.Essentials;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Storage;

namespace AiAudioRecoder;

public class ModelInfo : INotifyPropertyChanged
{
    public string Name { get; set; }
    public double SizeMB { get; set; }
    private bool _isDownloaded;
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            _isDownloaded = value;
            OnPropertyChanged();
        }
    }
    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            _isCurrent = value;
            OnPropertyChanged();
        }
    }
    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            _isDownloading = value;
            OnPropertyChanged();
        }
    }
    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            OnPropertyChanged();
        }
    }
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

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
    private ObservableCollection<ModelInfo> _models;
    public ObservableCollection<ModelInfo> Models
    {
        get => _models;
        set
        {
            _models = value;
            OnPropertyChanged();
        }
    }

    public ModelsPage()
    {
        Models = new ObservableCollection<ModelInfo>
        {
            new ModelInfo { Name = "tiny", SizeMB = 75 },
            new ModelInfo { Name = "base", SizeMB = 142 },
            new ModelInfo { Name = "small", SizeMB = 466 },
            new ModelInfo { Name = "medium", SizeMB = 1500 }
        };
        InitializeComponent();
        this.BindingContext = this;
        UpdateModels();
    }

    private void UpdateModels()
    {
        var current = Preferences.Get("CurrentModel", "base");
        System.Diagnostics.Debug.WriteLine($"Current model: {current}");
        foreach (var m in Models)
        {
            m.IsDownloaded = File.Exists(GetModelPath(m.Name));
            m.IsCurrent = m.Name == current;
            System.Diagnostics.Debug.WriteLine($"Model {m.Name}: Downloaded={m.IsDownloaded}, Current={m.IsCurrent}");
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
            await DownloadModel(name);
        }
        else
        {
            SetCurrentModel(name);
        }
    }

    private async Task DownloadModel(string modelName)
    {
        var model = Models.FirstOrDefault(m => m.Name == modelName);
        if (model == null) return;

        string fileName = $"ggml-{modelName}.bin";
        try
        {
            model.IsDownloading = true;
            model.Progress = 0;
            await DisplayAlert("Загрузка", $"Скачивание модели {modelName}...", "OK");
            var ggmlType = modelName switch
            {
                "tiny" => GgmlType.Tiny,
                "base" => GgmlType.Base,
                "small" => GgmlType.Small,
                "medium" => GgmlType.Medium,
                _ => GgmlType.Base
            };
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelName}.bin";
            var root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dir = Path.Combine(root, "AiAudioRecoder", "models");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);

            if (File.Exists(path))
            {
                await DisplayAlert("Успех", $"Модель {fileName} уже существует!", "OK");
                model.IsDownloading = false;
                UpdateModels();
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
                    model.Progress = progress;
                    await MainThread.InvokeOnMainThreadAsync(() => { });
                }
            }
            await DisplayAlert("Успех", $"Модель {fileName} успешно загружена!", "OK");
            model.IsDownloading = false;
            UpdateModels();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
            model.IsDownloading = false;
            model.Progress = 0;
        }
        finally
        {
            DownloadProgress.IsVisible = false;
            DownloadProgress.Progress = 0;
        }
    }

    private async void SetCurrentModel(string name)
    {
        Preferences.Set("CurrentModel", name);
        UpdateModels();
    }
}