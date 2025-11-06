using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Essentials;
using AiAudioRecoder.Models;
using AiAudioRecoder.Services;

namespace AiAudioRecoder;

public partial class RecordPage : ContentPage, INotifyPropertyChanged
{
    private readonly Services.IAudioRecorderService _recorder;
    private readonly Services.AudioMetadataDatabase _db;
    private readonly Services.TranscriptionQueueService _transcriptionQueue;
    private readonly ILogger<RecordPage> _logger;

    private ObservableCollection<AudioFileMetadata> _allRecords = new();
    private ObservableCollection<AudioFileMetadata> _displayedRecords = new();
    private string _searchText = string.Empty;
    private bool _isRecordingInProgress = false;
    private bool _isTranscribing = false;
    private TaskCompletionSource<bool>? _recordingTcs;
    private AudioFileMetadata? _currentRecording;

    public RecordPage(Services.IAudioRecorderService recorder, 
                     Services.AudioMetadataDatabase db,
                     Services.TranscriptionQueueService transcriptionQueue,
                     ILogger<RecordPage> logger)
    {
        InitializeComponent();
        _recorder = recorder;
        _db = db;
        _transcriptionQueue = transcriptionQueue;
        _logger = logger;

        BindingContext = this;
        RecordsView.ItemsSource = _displayedRecords;

        // Load records on page load
        Loaded += async (s, e) => await LoadRecordsAsync();

        // Setup search
        if (SearchEntry != null)
            SearchEntry.TextChanged += OnSearchTextChanged;
    }

    #region Properties

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                ApplySearchFilter();
            }
        }
    }

    public int QueueCount => _transcriptionQueue.QueueCount;
    public int ActiveTranscriptions => _transcriptionQueue.ActiveTranscriptions;

    #endregion

    #region Event Handlers

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        if (_isRecordingInProgress || _isTranscribing) 
        {
            await DisplayAlertAsync("Информация", "Действие уже выполняется", "OK");
            return;
        }

        // Check if at least one source is selected
        bool recordMic = MicrophoneCheckBox?.IsChecked == true;
        bool recordSystem = SystemAudioCheckBox?.IsChecked == true;

        if (!recordMic && !recordSystem)
        {
            await DisplayAlertAsync("Ошибка", "Выберите хотя бы один источник записи (Микрофон или Системное аудио)", "OK");
            return;
        }

        try
        {
            var status = await CheckMicrophonePermissionAsync();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlertAsync("Ошибка", "Разрешение на микрофон не предоставлено.", "OK");
                return;
            }

            var start = DateTime.Now;
            
            RecordButton.IsEnabled = false;
            RecordButton.Text = "Запись...";
            
            string? filePath = null;

            if (recordMic && recordSystem)
            {
                // Mixed recording - both sources in one file
                filePath = await _recorder.StartMixedRecordingAsync(string.Empty, string.Empty);
                if (string.IsNullOrEmpty(filePath))
                {
                    await DisplayAlertAsync("Ошибка", "Не удалось начать запись.", "OK");
                    ResetRecordButton();
                    return;
                }
            }
            else if (recordMic)
            {
                // Microphone only
                filePath = await _recorder.StartRecordingAsync(string.Empty, string.Empty, "mic");
            }
            else
            {
                // System audio only
                filePath = await _recorder.StartRecordingAsync(string.Empty, string.Empty, "system");
            }

            if (string.IsNullOrEmpty(filePath))
            {
                await DisplayAlertAsync("Ошибка", "Не удалось начать запись.", "OK");
                ResetRecordButton();
                return;
            }

            _currentRecording = new AudioFileMetadata
            {
                FilePath = filePath,
                StartTime = start,
                EndTime = DateTime.Now,
                IsTranscribed = false
            };

            _recordingTcs = new TaskCompletionSource<bool>();
            _isRecordingInProgress = true;
            RecordButton.IsVisible = false;
            StopButton.IsVisible = true;
            StopButton.IsEnabled = true;

            // Wait for stop
            await _recordingTcs.Task;

            // Immediately disable stop button
            StopButton.IsEnabled = false;
            
            await _recorder.StopRecordingAsync();
            _currentRecording.EndTime = DateTime.Now;

            // Save recording metadata (without transcription)
            await _db.AddMetadataAsync(_currentRecording);

            // Ask user if they want to transcribe
            var transcribe = await DisplayAlertAsync(
                "Запись завершена", 
                $"Запись сохранена: {_currentRecording.FilePath}\nДлительность: {_currentRecording.EndTime - _currentRecording.StartTime}\n\nХотите распознать аудио?", 
                "Да", "Нет");

            if (transcribe)
            {
                await StartTranscriptionAsync(_currentRecording);
            }

            // Refresh the records list
            await LoadRecordsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during recording");
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            ResetRecordingState();
        }
    }

    private void OnStopRecordingClicked(object? sender, EventArgs e)
    {
        if (_isRecordingInProgress)
        {
            _recordingTcs?.SetResult(true);
        }
    }

    private async void OnTranscribeClicked(object? sender, EventArgs e)
    {
        // Handle transcribe button click from CollectionView
        if (sender is Button button)
        {
            // Get the metadata from the button's binding context
            var parentView = button.Parent;
            AudioFileMetadata? metadata = null;
            
            while (parentView != null)
            {
                if (parentView.BindingContext is AudioFileMetadata recordMetadata)
                {
                    metadata = recordMetadata;
                    break;
                }
                parentView = parentView.Parent;
            }

            if (metadata != null)
            {
                await StartTranscriptionAsync(metadata);
            }
            else
            {
                // Fallback: get first untranscribed record
                var untranscribed = await _db.GetUntranscribedAsync();
                if (untranscribed.Any())
                {
                    await StartTranscriptionAsync(untranscribed.First());
                }
            }
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchText = e.NewTextValue;
    }

    private async void OnShowRecordsClicked(object? sender, EventArgs e)
    {
        await LoadRecordsAsync();
    }

    #endregion

    #region Private Methods

    private async Task LoadRecordsAsync()
    {
        try
        {
            IsBusy = true;
            
            var allRecords = await _db.GetAllAsync();
            _allRecords.Clear();
            foreach (var record in allRecords)
            {
                _allRecords.Add(record);
            }
            
            ApplySearchFilter();
            
            // Update queue status labels
            UpdateQueueStatusUI();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading records");
            await DisplayAlertAsync("Ошибка", "Не удалось загрузить записи", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateQueueStatusUI()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var queueLabel = Content?.FindByName("QueueLabel") as Label;
            var activeLabel = Content?.FindByName("ActiveLabel") as Label;
            
            if (queueLabel != null)
                queueLabel.Text = $"В очереди: {QueueCount} задач";
            
            if (activeLabel != null)
                activeLabel.Text = $"Активно: {ActiveTranscriptions} задач";
        });
    }

    private void ApplySearchFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            _displayedRecords.Clear();
            foreach (var record in _allRecords)
            {
                _displayedRecords.Add(record);
            }
        }
        else
        {
            // Use vector search for better results
            _ = Task.Run(async () =>
            {
                var searchResults = await _db.SearchAsync(SearchText, true);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _displayedRecords.Clear();
                    foreach (var record in searchResults)
                    {
                        _displayedRecords.Add(record);
                    }
                });
            });
        }
    }

    private async Task StartTranscriptionAsync(AudioFileMetadata metadata)
    {
        if (_isTranscribing || metadata.IsTranscribed)
        {
            await DisplayAlertAsync("Информация", "Файл уже транскрибируется или уже распознан", "OK");
            return;
        }

        try
        {
            metadata.IsTranscribed = true; // Mark as in progress
            await _db.UpdateMetadataAsync(metadata);

            // Show progress
            var progressBar = Content?.FindByName("TranscriptionProgress") as ProgressBar;
            if (progressBar != null)
            {
                progressBar.IsVisible = true;
                progressBar.Progress = 0;
            }

            _isTranscribing = true;

            // Start transcription in queue
            var result = await _transcriptionQueue.QueueTranscriptionAsync(metadata);

            if (result.Success)
            {
                await DisplayAlertAsync("Успех", $"Транскрипция завершена: {result.Summary}", "OK");
                
                // Refresh the specific record
                var index = _displayedRecords.IndexOf(metadata);
                if (index >= 0)
                {
                    _displayedRecords[index] = result.Metadata;
                }
            }
            else
            {
                await DisplayAlertAsync("Ошибка", "Не удалось выполнить транскрипцию", "OK");
                metadata.IsTranscribed = false;
                await _db.UpdateMetadataAsync(metadata);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting transcription");
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
            if (metadata != null)
            {
                metadata.IsTranscribed = false;
                await _db.UpdateMetadataAsync(metadata);
            }
        }
        finally
        {
            _isTranscribing = false;
            
            // Hide progress
            var progressBar = Content?.FindByName("TranscriptionProgress") as ProgressBar;
            if (progressBar != null)
            {
                progressBar.IsVisible = false;
            }
            
            UpdateQueueStatusUI();
            
            // Force UI refresh
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(ActiveTranscriptions));
        }
    }

    private async Task ResetRecordingState()
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _isRecordingInProgress = false;
            RecordButton.IsVisible = true;
            StopButton.IsVisible = false;
            StopButton.IsEnabled = true;
            StopButton.Text = "Остановить запись";
            StopButton.BackgroundColor = Colors.Red;
            RecordButton.IsEnabled = true;
            RecordButton.Text = "Записать и транскрибировать";
            
            var progressBar = Content?.FindByName("TranscriptionProgress") as ProgressBar;
            if (progressBar != null)
                progressBar.IsVisible = false;
            
            _recordingTcs = null;
        });
    }

    private void ResetRecordButton()
    {
        RecordButton.IsEnabled = true;
        RecordButton.Text = "Записать и транскрибировать";
    }

    private async Task<PermissionStatus> CheckMicrophonePermissionAsync()
    {
        try
        {
#if WINDOWS
            return PermissionStatus.Granted;
#else
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Microphone>();
            }
            return status;
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Permission check failed");
            return PermissionStatus.Denied;
        }
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion
}