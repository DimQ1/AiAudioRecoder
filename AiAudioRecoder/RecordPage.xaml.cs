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
    private string _sortColumn = "Date"; // Default sort by EndTime
    private bool _sortAscending = false; // Newest first by default

    private bool _autoTranscribe = true;

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

    private void OnAutoTranscribeChanged(object sender, CheckedChangedEventArgs e)
    {
        _autoTranscribe = e.Value;
    }

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        // Toggle stop if already recording
        if (_isRecordingInProgress)
        {
            _recordingTcs?.SetResult(true);
            return;
        }
        if (_isTranscribing)
        {
            await DisplayAlertAsync("Информация", "Транскрипция выполняется", "OK");
            return;
        }

        bool recordMic = MicrophoneCheckBox?.IsChecked == true;
        bool recordSystem = SystemAudioCheckBox?.IsChecked == true;
        _recorder.SetMicEnabled(recordMic);
        _recorder.SetSystemEnabled(recordSystem);
        if (!recordMic && !recordSystem)
        {
            await DisplayAlertAsync("Информация", "Источники отключены: будет записан файл тишины", "OK");
        }

        try
        {
            var status = await CheckMicrophonePermissionAsync();
            if (status != PermissionStatus.Granted && recordMic)
            {
                await DisplayAlertAsync("Ошибка", "Нет разрешения на микрофон", "OK");
                return;
            }

            var start = DateTime.Now;
            RecordButton.IsEnabled = false;
            RecordButton.Text = "Запись...";

            string? filePath = await _recorder.StartMixedRecordingAsync(string.Empty, string.Empty);

            if (string.IsNullOrEmpty(filePath))
            {
                await DisplayAlertAsync("Ошибка", "Не удалось начать запись", "OK");
                ResetRecordButton();
                return;
            }

            _currentRecording = new AudioFileMetadata
            {
                FilePath = filePath,
                StartTime = start,
                EndTime = start,
                IsTranscribed = false
            };

            _recordingTcs = new TaskCompletionSource<bool>();
            _isRecordingInProgress = true;
            RecordButton.IsEnabled = true;
            RecordButton.Text = "Остановить запись";
            RecordButton.BackgroundColor = Colors.Red;

            // Allow toggling sources on the fly
            MicrophoneCheckBox.CheckedChanged += OnSourceToggleDuringRecording;
            SystemAudioCheckBox.CheckedChanged += OnSourceToggleDuringRecording;

            await _recordingTcs.Task; // wait stop
            RecordButton.IsEnabled = false;

            await _recorder.StopRecordingAsync();
            _currentRecording.EndTime = DateTime.Now;
            await _db.AddMetadataAsync(_currentRecording);

            RecordButton.BackgroundColor = Colors.Green;
            RecordButton.Text = "Записать";
            _isRecordingInProgress = false;
            RecordButton.IsEnabled = true;

            if (_autoTranscribe)
            {
                await StartTranscriptionAsync(_currentRecording);
            }
            else
            {
                var transcribe = await DisplayAlertAsync("Запись завершена", "Распознать аудио?", "Да", "Нет");
                if (transcribe)
                    await StartTranscriptionAsync(_currentRecording);
            }

            await LoadRecordsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during recording");
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            if (_isRecordingInProgress == false)
            {
                RecordButton.BackgroundColor = Colors.Green;
                RecordButton.Text = "Записать";
                RecordButton.IsEnabled = true;
            }
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
            var baseList = _allRecords.ToList();
            var sorted = ApplySort(baseList);
            _displayedRecords.Clear();
            foreach (var record in sorted)
                _displayedRecords.Add(record);
        }
        else
        {
            // Use vector search for better results
            _ = Task.Run(async () =>
            {
                var searchResults = await _db.SearchAsync(SearchText, true);
                var sorted = ApplySort(searchResults);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _displayedRecords.Clear();
                    foreach (var record in sorted)
                        _displayedRecords.Add(record);
                });
            });
        }
    }

    private List<AudioFileMetadata> ApplySort(IEnumerable<AudioFileMetadata> source)
    {
        IOrderedEnumerable<AudioFileMetadata> ordered = _sortColumn switch
        {
            "Date" => _sortAscending ? source.OrderBy(r => r.EndTime) : source.OrderByDescending(r => r.EndTime),
            "Duration" => _sortAscending ? source.OrderBy(r => r.Duration) : source.OrderByDescending(r => r.Duration),
            "Status" => _sortAscending ? source.OrderBy(r => r.IsTranscribed) : source.OrderByDescending(r => r.IsTranscribed),
            "Summary" => _sortAscending ? source.OrderBy(r => r.Summary) : source.OrderByDescending(r => r.Summary),
            "File" => _sortAscending ? source.OrderBy(r => r.FilePath) : source.OrderByDescending(r => r.FilePath),
            _ => _sortAscending ? source.OrderBy(r => r.EndTime) : source.OrderByDescending(r => r.EndTime)
        };
        return ordered.ToList();
    }

    private void OnHeaderTapped(object sender, TappedEventArgs e)
    {
        if (sender is Label lbl && !string.IsNullOrWhiteSpace(lbl.ClassId))
        {
            var col = lbl.ClassId;
            if (_sortColumn == col)
            {
                _sortAscending = !_sortAscending; // toggle direction
            }
            else
            {
                _sortColumn = col;
                _sortAscending = true; // start ascending for a new column
            }
            ApplySearchFilter();
        }
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        try
        {
            AudioFileMetadata? metadata = null;
            if (sender is Button btn)
            {
                var parent = btn.Parent;
                while (parent != null && metadata == null)
                {
                    if (parent.BindingContext is AudioFileMetadata m) metadata = m;
                    parent = parent.Parent;
                }
            }
            if (metadata == null)
            {
                await DisplayAlertAsync("Ошибка", "Не удалось определить запись для удаления", "OK");
                return;
            }

            bool confirm = await DisplayAlertAsync("Удаление", "Удалить запись и связанный файл?", "Да", "Нет");
            if (!confirm) return;

            // Cancel any active transcription for this file
            if (_transcriptionQueue.IsTranscribing(metadata.FilePath))
            {
                _transcriptionQueue.CancelByFilePath(metadata.FilePath);
            }

            // Delete the physical file if exists
            try
            {
                if (System.IO.File.Exists(metadata.FilePath))
                {
                    System.IO.File.Delete(metadata.FilePath);
                }
            }
            catch (Exception exFile)
            {
                _logger.LogWarning(exFile, "Failed to delete file {FilePath}", metadata.FilePath);
                // Continue with metadata deletion
            }

            await _db.DeleteAsync(metadata.Id);
            _allRecords.Remove(metadata);
            _displayedRecords.Remove(metadata);
            await DisplayAlertAsync("Успех", "Запись удалена", "OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting record");
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
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
            RecordButton.IsEnabled = true;
            RecordButton.Text = _autoTranscribe ? "Записать и транскрибировать" : "Записать";
            RecordButton.BackgroundColor = Colors.Green;
            var progressBar = Content?.FindByName("TranscriptionProgress") as ProgressBar;
            if (progressBar != null)
                progressBar.IsVisible = false;
            _recordingTcs = null;
        });
    }

    private void ResetRecordButton()
    {
        RecordButton.IsEnabled = true;
        RecordButton.Text = "Записать";
        RecordButton.BackgroundColor = Colors.Green;
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

    // Hide base implementation intentionally to support INotifyPropertyChanged explicit usage
    public new event PropertyChangedEventHandler? PropertyChanged;

    protected new virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        base.OnPropertyChanged(propertyName);
    }

    #endregion
    
    // Dynamic source toggle during recording
    private void OnSourceToggleDuringRecording(object? sender, CheckedChangedEventArgs e)
    {
        if (!_isRecordingInProgress) return;
        _recorder.SetMicEnabled(MicrophoneCheckBox?.IsChecked == true);
        _recorder.SetSystemEnabled(SystemAudioCheckBox?.IsChecked == true);
    }
}