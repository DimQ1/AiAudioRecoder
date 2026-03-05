using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiAudioRecoder.Models;
using AiAudioRecoder.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;

namespace AiAudioRecoder.Views
{
    public partial class RecordDetailPage : ContentPage
    {
        private const double WideLayoutThreshold = 700;

        private readonly IAudioPlaybackService _playbackService;
        private readonly AudioFileMetadata _record;
        private readonly AudioMetadataDatabase _database;
        private FormattedString? _formattedTranscription;
        private List<Span> _segmentSpans = new();
        private double[] _segmentStartTimes = Array.Empty<double>();
        private double[] _segmentEndTimes = Array.Empty<double>();
        private int _currentHighlightIndex = -1;
        private readonly CancellationTokenSource _highlightCts = new();
        private readonly IDispatcherTimer _highlightTimer;
        private readonly System.Collections.Concurrent.BlockingCollection<double> _positionQueue = new(1);
        private Task? _highlightWorker;

        private TextAnalysisService? _analysisService;
        private CancellationTokenSource? _analysisCts;

        private bool _isWideLayout;

        public RecordDetailPage(AudioFileMetadata record)
        {
            InitializeComponent();
            _record = record;
            BindingContext = _record;

            var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
            var logger = loggerFactory.CreateLogger<AudioMetadataDatabase>();
            var dbRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiAudioRecoder", "Databases");
            Directory.CreateDirectory(dbRoot);
            var dbPath = Path.Combine(dbRoot, "audio_metadata.db3");
            _database = new AudioMetadataDatabase(dbPath, logger);

            _playbackService = new AudioPlaybackService();
            InitializePlaybackService();

            _highlightTimer = Dispatcher.CreateTimer();
            SetupHighlightTimer();
            StartHighlightWorker();

            InitializeAnalysisFeatures();
        }

        private void StartHighlightWorker()
        {
            _highlightWorker = Task.Run(() =>
            {
                while (!_highlightCts.IsCancellationRequested)
                {
                    try
                    {
                        if (_positionQueue.Count == 0)
                        {
                            continue;
                        }

                        double seconds = _positionQueue.Take(_highlightCts.Token);
                        // Drain the queue to only process the latest position
                        while (_positionQueue.TryTake(out var latestSeconds))
                        {
                            seconds = latestSeconds;
                        }

                        int index = FindSegmentIndex(seconds);

                        if (index != _currentHighlightIndex)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                if (!_highlightCts.IsCancellationRequested)
                                {
                                    HighlightSpan(index);
                                }
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when token is cancelled
                        break;
                    }
                }
            }, _highlightCts.Token);
        }

        private void InitializeAnalysisFeatures()
        {
            if (AnalysisPanel == null)
            {
                return;
            }

            try
            {
                var services = App.Current?.Handler?.MauiContext?.Services;
                _analysisService = services?.GetService<TextAnalysisService>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to resolve TextAnalysisService: {ex.Message}");
            }

            if (_analysisService == null)
            {
                AnalysisPanel.IsVisible = false;

                if (AnalysisStatusLabel != null)
                {
                    AnalysisStatusLabel.Text = "Сервис анализа недоступен.";
                }

                if (AnalyzeTextButton != null)
                {
                    AnalyzeTextButton.IsEnabled = false;
                }

                return;
            }

            AnalysisPanel.IsVisible = true;
            SetAnalysisStatus("Готов к анализу.");
        }

    private void SetAnalysisStatus(string message, bool isError = false)
    {
        if (AnalysisStatusLabel == null)
        {
            return;
        }

        AnalysisStatusLabel.Text = message;
        AnalysisStatusLabel.TextColor = isError ? Colors.IndianRed : Colors.Gray;
    }

    private void SetAnalysisBusy(bool isBusy)
    {
        if (AnalysisActivityIndicator != null)
        {
            AnalysisActivityIndicator.IsRunning = isBusy;
            AnalysisActivityIndicator.IsVisible = isBusy;
        }

        if (AnalyzeTextButton != null)
        {
            AnalyzeTextButton.IsEnabled = !isBusy;
        }

        if (CancelAnalysisButton != null)
        {
            CancelAnalysisButton.IsVisible = isBusy;
        }
    }

    private string GetActiveText()
    {
        if (TranscriptionLabel?.IsVisible == true)
        {
            var formatted = TranscriptionLabel.FormattedText;
            var concatenated = MergeFormattedText(formatted);
            if (!string.IsNullOrWhiteSpace(concatenated))
            {
                return concatenated;
            }

            return TranscriptionLabel.Text ?? _record.Transcription ?? string.Empty;
        }

        if (ContentLabel?.IsVisible == true)
        {
            return ContentLabel.Text ?? _record.Summary ?? _record.FilePath ?? string.Empty;
        }

        return _record.Transcription ?? _record.Summary ?? string.Empty;
    }

    private static string MergeFormattedText(FormattedString? formatted)
    {
        if (formatted?.Spans == null || formatted.Spans.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var span in formatted.Spans)
        {
            builder.Append(span.Text);
        }

        return builder.ToString();
    }

    private void CancelAnalysis(bool updateStatus)
    {
        var cts = _analysisCts;
        if (cts == null)
        {
            return;
        }

        _analysisCts = null;
        cts.Cancel();
        cts.Dispose();

        if (updateStatus)
        {
            SetAnalysisStatus("Анализ отменён.");
            SetAnalysisBusy(false);
        }
    }

    private async void OnAnalysisSettingsClicked(object? sender, EventArgs e)
    {
        if (_analysisService == null)
        {
            await DisplayAlertAsync("Недоступно", "Сервис анализа не зарегистрирован.", "OK");
            return;
        }

        var settingsPage = new AnalysisSettingsPage(_analysisService);
        await Navigation.PushModalAsync(settingsPage);
    }

    private async void OnAnalyzeTextClicked(object? sender, EventArgs e)
    {
        if (_analysisService == null)
        {
            await DisplayAlertAsync("Недоступно", "Сервис анализа не зарегистрирован.", "OK");
            return;
        }

        var text = GetActiveText();
        if (string.IsNullOrWhiteSpace(text))
        {
            await DisplayAlertAsync("Нет текста", "Нет данных для анализа.", "OK");
            return;
        }

        CancelAnalysis(false);
        _analysisCts = new CancellationTokenSource();
        SetAnalysisBusy(true);
        SetAnalysisStatus("Выполняется анализ...");

        try
        {
            var result = await _analysisService.AnalyzeAsync(text, null, _analysisCts.Token);
            if (AnalysisResultEditor != null)
            {
                AnalysisResultEditor.Text = result.BuildDisplayString();
            }

            SetAnalysisStatus("Анализ завершён успешно.");
        }
        catch (OperationCanceledException)
        {
            SetAnalysisStatus("Анализ отменён пользователем.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Text analysis failed: {ex.Message}");
            await DisplayAlertAsync("Ошибка", "Не удалось выполнить анализ текста.", "OK");
            SetAnalysisStatus("Ошибка анализа.", true);
        }
        finally
        {
            _analysisCts?.Dispose();
            _analysisCts = null;
            SetAnalysisBusy(false);
        }
    }

    private async void OnCopyAnalysisClicked(object? sender, EventArgs e)
    {
        try
        {
            var text = AnalysisResultEditor?.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                await DisplayAlertAsync("Нет текста", "Результаты анализа отсутствуют.", "OK");
                return;
            }

            await Clipboard.Default.SetTextAsync(text);
            await DisplayAlertAsync("Готово", "Результат анализа скопирован в буфер обмена.", "OK");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard error: {ex.Message}");
            await DisplayAlertAsync("Ошибка", "Не удалось скопировать текст.", "OK");
        }
    }

    private void OnCancelAnalysisClicked(object? sender, EventArgs e)
    {
        if (_analysisCts == null)
        {
            return;
        }

        _analysisCts.Cancel();
        SetAnalysisStatus("Отмена анализа...");
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        if (width <= 0 || MainContentGrid == null)
        {
            return;
        }

        bool shouldBeWide = width >= WideLayoutThreshold;
        if (shouldBeWide == _isWideLayout)
        {
            return;
        }

        _isWideLayout = shouldBeWide;

        if (shouldBeWide)
        {
            // Side-by-side: 2 columns, 1 row
            MainContentGrid.RowDefinitions.Clear();
            MainContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });

            MainContentGrid.ColumnDefinitions.Clear();
            MainContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            MainContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });

            Grid.SetRow(DetailsBorder, 0);
            Grid.SetColumn(DetailsBorder, 0);
            Grid.SetRow(AnalysisPanel, 0);
            Grid.SetColumn(AnalysisPanel, 1);
        }
        else
        {
            // Stacked: 1 column, 2 rows
            MainContentGrid.ColumnDefinitions.Clear();
            MainContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });

            MainContentGrid.RowDefinitions.Clear();
            MainContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
            MainContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });

            Grid.SetRow(DetailsBorder, 0);
            Grid.SetColumn(DetailsBorder, 0);
            Grid.SetRow(AnalysisPanel, 1);
            Grid.SetColumn(AnalysisPanel, 0);
        }
    }

    private void SetupHighlightTimer()
    {
        _highlightTimer.Interval = TimeSpan.FromMilliseconds(100);
        _highlightTimer.Tick += HighlightTimer_Tick;
    }

        private void HighlightTimer_Tick(object? sender, EventArgs e)
        {
            if (_playbackService.IsPlaying)
            {
                UpdateHighlight(_playbackService.CurrentPosition);
            }
        }

        protected override async void OnNavigatedTo(NavigatedToEventArgs args)
        {
            base.OnNavigatedTo(args);
            await ConfigureContentAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _playbackService.Stop();
            _highlightTimer.Stop();
            _highlightCts.Cancel();
            CancelAnalysis(false);
        }

        private async Task ConfigureContentAsync()
        {
            await RefreshMetadataFromDatabaseAsync();

            bool hasTranscription = !string.IsNullOrWhiteSpace(_record.Transcription);

            if (hasTranscription)
            {
                ContentLabel.IsVisible = false;
                TranscriptionLabel.IsVisible = true;
                BuildTranscriptionView(_record.Transcription);
                await SetupPlaybackAsync();
            }
            else
            {
                ContentLabel.IsVisible = true;
                TranscriptionLabel.IsVisible = false;
                ContentLabel.Text = _record.FilePath;
                PlaybackPanel.IsVisible = false;
            }
        }

        private async Task RefreshMetadataFromDatabaseAsync()
        {
            if (_record.Id <= 0) return;

            try
            {
                var latest = await _database.GetByIdAsync(_record.Id);
                if (latest == null) return;

                _record.FilePath = latest.FilePath;
                _record.StartTime = latest.StartTime;
                _record.EndTime = latest.EndTime;
                _record.Transcription = latest.Transcription;
                _record.Summary = latest.Summary;
                _record.WordTimingsJson = latest.WordTimingsJson;
                _record.IsTranscribed = latest.IsTranscribed;
                _record.TranscriptionDate = latest.TranscriptionDate;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Database refresh failed: {ex.Message}");
            }
        }

        private void BuildTranscriptionView(string? transcription)
        {
            if (string.IsNullOrEmpty(transcription)) return;

            _formattedTranscription = new FormattedString();
            _segmentSpans.Clear();

            var timings = _record.WordTimings;
            if (timings == null || timings.Count == 0)
            {
                _formattedTranscription.Spans.Add(new Span { Text = transcription });
                TranscriptionLabel.FormattedText = _formattedTranscription;
                return;
            }

            var orderedTimings = timings
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Word))
                .OrderBy(t => t.Start)
                .ToList();

            if (orderedTimings.Count == 0)
            {
                _formattedTranscription.Spans.Add(new Span { Text = transcription });
                TranscriptionLabel.FormattedText = _formattedTranscription;
                return;
            }

            var segmentStarts = new List<double>(orderedTimings.Count);
            var segmentEnds = new List<double>(orderedTimings.Count);

            for (int i = 0; i < orderedTimings.Count; i++)
            {
                var timing = orderedTimings[i];
                var span = new Span { Text = timing.Word };
                _segmentSpans.Add(span);
                _formattedTranscription.Spans.Add(span);
                segmentStarts.Add(timing.Start);
                segmentEnds.Add(timing.End);

                if (i < orderedTimings.Count - 1)
                {
                    _formattedTranscription.Spans.Add(new Span { Text = " " });
                }
            }

            _segmentStartTimes = segmentStarts.ToArray();
            _segmentEndTimes = segmentEnds.ToArray();
            TranscriptionLabel.FormattedText = _formattedTranscription;
            HighlightSpan(-1);
        }

        private async Task SetupPlaybackAsync()
        {
            if (PlaybackPanel == null || string.IsNullOrEmpty(_record.FilePath) || !File.Exists(_record.FilePath))
            {
                if (PlaybackPanel != null) PlaybackPanel.IsVisible = false;
                return;
            }

            var loaded = await _playbackService.LoadAsync(_record.FilePath);
            if (!loaded)
            {
                PlaybackPanel.IsVisible = false;
                return;
            }

            PlaybackPanel.IsVisible = true;
            PositionSlider.Value = 0;
            PositionSlider.IsEnabled = true;
            CurrentTimeLabel.Text = TimeSpan.Zero.ToString(@"mm\:ss");
            TotalDurationLabel.Text = _playbackService.TotalDuration.ToString(@"mm\:ss");
            UpdateHighlight(TimeSpan.Zero);
        }

        private void InitializePlaybackService()
        {
            _playbackService.PlaybackStopped += (s, e) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    PlayPauseButton.Text = "Play";
                    _highlightTimer.Stop();
                    PositionSlider.Value = 0;
                    CurrentTimeLabel.Text = TimeSpan.Zero.ToString(@"mm\:ss");
                    HighlightSpan(-1);
                });
            };
            _playbackService.PositionChanged += OnPlaybackPositionChanged;
        }

        private void OnPlaybackPositionChanged(object? sender, TimeSpan position)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_playbackService.TotalDuration == TimeSpan.Zero) return;

                CurrentTimeLabel.Text = position.ToString(@"mm\:ss");
                var ratio = position.TotalSeconds / _playbackService.TotalDuration.TotalSeconds;

                if (Math.Abs(PositionSlider.Value - ratio) > 0.01)
                {
                    PositionSlider.Value = ratio;
                }
            });
        }

        private async void OnPlayPauseClicked(object sender, EventArgs e)
        {
            if (!_playbackService.IsLoaded) return;

            try
            {
                if (_playbackService.IsPlaying)
                {
                    _playbackService.Pause();
                    PlayPauseButton.Text = "Play";
                    _highlightTimer.Stop();
                }
                else
                {
                    await _playbackService.PlayAsync();
                    PlayPauseButton.Text = "Pause";
                    _highlightTimer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Playback error: {ex.Message}");
                await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync("Error", "Could not play audio.", "OK"));
            }
        }

        private void OnStopClicked(object sender, EventArgs e)
        {
            _playbackService.Stop();
        }

        private void OnSliderDragStarted(object sender, EventArgs e)
        {
            if (_playbackService.IsPlaying)
            {
                _highlightTimer.Stop();
                _playbackService.Pause();
            }
        }

        private async void OnSliderDragCompleted(object sender, EventArgs e)
        {
            var slider = (Slider)sender;
            var newPosition = _playbackService.TotalDuration * slider.Value;
            _playbackService.CurrentPosition = newPosition;

            await _playbackService.PlayAsync();
            PlayPauseButton.Text = "Pause";
            _highlightTimer.Start();
        }

        private void UpdateHighlight(TimeSpan position)
        {
            if (_segmentStartTimes.Length > 0)
            {
                // Non-blocking add to the queue
                _positionQueue.TryAdd(position.TotalSeconds);
            }
        }

        private int FindSegmentIndex(double seconds)
        {
            // Interval-based search supporting overlapping word timings.
            // Strategy:
            // 1. Fast path: reuse current highlighted word if still covering timestamp.
            // 2. Binary search for greatest index whose start <= seconds.
            // 3. Expand a narrow window left/right while starts are still <= seconds to collect overlapping candidates.
            // 4. Pick the candidate whose interval contains seconds using tie-breakers:
            //    - earliest start
            //    - then shortest duration (more precise word boundary)
            // Returns -1 if no interval contains the timestamp.

            var starts = _segmentStartTimes;
            var ends = _segmentEndTimes;
            int n = starts.Length;
            if (n == 0) return -1;

            int last = _currentHighlightIndex;
            if (last >= 0 && last < n && seconds >= starts[last] && seconds <= ends[last])
            {
                return last; // fast path
            }

            // Binary search: largest start <= seconds
            int lo = 0, hi = n - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (starts[mid] <= seconds)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (best == -1)
            {
                // All starts are greater than the timestamp. Nothing started yet -> choose 0 as a gentle fallback.
                return 0; // highlight first word early instead of returning -1
            }

            // Expand window around 'best' to include any segments starting at same time (overlaps) before and after.
            int left = best;
            while (left > 0 && starts[left - 1] <= seconds) left--;
            int right = best;
            while (right + 1 < n && starts[right + 1] <= seconds) right++;

            int chosen = -1;
            double chosenStart = double.MaxValue;
            double chosenDuration = double.MaxValue;
            for (int i = left; i <= right; i++)
            {
                // Must contain seconds inside interval.
                if (seconds <= ends[i] && seconds >= starts[i])
                {
                    double duration = ends[i] - starts[i];
                    if (chosen == -1 || starts[i] < chosenStart || (starts[i] == chosenStart && duration < chosenDuration))
                    {
                        chosen = i;
                        chosenStart = starts[i];
                        chosenDuration = duration;
                    }
                }
            }

            if (chosen != -1) return chosen;

            // Fallback: choose nearest segment start boundary (best is the largest start <= seconds)
            int fallbackIndex = best;
            int neighbor = best + 1 < n ? best + 1 : best;
            if (neighbor != best)
            {
                double diffBest = Math.Abs(starts[best] - seconds);
                double diffNeighbor = Math.Abs(starts[neighbor] - seconds);
                if (diffNeighbor < diffBest) fallbackIndex = neighbor;
            }
            return fallbackIndex;
        }


        private void HighlightSpan(int index)
        {
            if (_segmentSpans.Count == 0) return;

            if (_currentHighlightIndex >= 0 && _currentHighlightIndex < _segmentSpans.Count)
            {
                _segmentSpans[_currentHighlightIndex].TextColor = Colors.Gray;
                _segmentSpans[_currentHighlightIndex].FontAttributes = FontAttributes.None;
            }

            if (index >= 0 && index < _segmentSpans.Count)
            {
                _segmentSpans[index].TextColor = Colors.White;
                _segmentSpans[index].FontAttributes = FontAttributes.Bold;
                _currentHighlightIndex = index;
            }
            else
            {
                _currentHighlightIndex = -1;
            }
        }

        private async void OnCopyTextClicked(object sender, EventArgs e)
        {
            try
            {
                string? textToCopy = null;

                if (TranscriptionLabel?.IsVisible == true)
                {
                    var formatted = TranscriptionLabel.FormattedText;
                    if (formatted?.Spans?.Count > 0)
                    {
                        textToCopy = string.Concat(formatted.Spans.Select(span => span.Text));
                    }
                    else
                    {
                        textToCopy = TranscriptionLabel.Text ?? _record.Transcription;
                    }
                }
                else if (ContentLabel?.IsVisible == true)
                {
                    textToCopy = ContentLabel.Text ?? _record.Summary;
                }
                else
                {
                    textToCopy = _record.Transcription ?? _record.Summary;
                }

                if (string.IsNullOrWhiteSpace(textToCopy))
                {
                    await DisplayAlertAsync("Нет текста", "Нет данных для копирования.", "OK");
                    return;
                }

                await Clipboard.Default.SetTextAsync(textToCopy);
                await DisplayAlertAsync("Готово", "Текст скопирован в буфер обмена.", "OK");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Clipboard error: {ex.Message}");
                await DisplayAlertAsync("Ошибка", "Не удалось скопировать текст.", "OK");
            }
        }

        private void OnReturnToRecordsClicked(object sender, EventArgs e)
        {
            // Implementation for returning to records
        }

        private async void OnCloseClicked(object sender, EventArgs e)
        {
            try
            {
                if (Navigation?.ModalStack?.Count > 0)
                {
                    await Navigation.PopModalAsync();
                }
                else if (Navigation?.NavigationStack?.Count > 1)
                {
                    await Navigation.PopAsync();
                }
                else if (Shell.Current != null)
                {
                    await Shell.Current.GoToAsync("..");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Navigation error: {ex.Message}");
            }
        }
    }
}
