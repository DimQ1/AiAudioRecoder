using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAudioRecoder.Models;
using AiAudioRecoder.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;

namespace AiAudioRecoder.Views
{
    public partial class RecordDetailPage : ContentPage
    {
        private readonly IAudioPlaybackService _playbackService;
        private readonly AudioFileMetadata _record;
        private readonly AudioMetadataDatabase _database;
        private FormattedString? _formattedTranscription;
        private List<Span> _wordSpans = new();
        private double[] _wordStartTimes = Array.Empty<double>();
        private double[] _wordEndTimes = Array.Empty<double>();
        private int _currentHighlightIndex = -1;
        private readonly CancellationTokenSource _highlightCts = new();
        private readonly IDispatcherTimer _highlightTimer;
        private readonly System.Collections.Concurrent.BlockingCollection<double> _positionQueue = new(1);
        private Task? _highlightWorker;

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
        }

        private void StartHighlightWorker()
        {
            _highlightWorker = Task.Run(() =>
            {
                while (!_highlightCts.IsCancellationRequested)
                {
                    try
                    {
                        double seconds = _positionQueue.Take(_highlightCts.Token);
                        // Drain the queue to only process the latest position
                        while (_positionQueue.TryTake(out var latestSeconds))
                        {
                            seconds = latestSeconds;
                        }

                        int index = FindWordIndex(seconds);

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
        }

        private async Task ConfigureContentAsync()
        {
            await RefreshMetadataFromDatabaseAsync();

            bool hasTranscription = !string.IsNullOrWhiteSpace(_record.Transcription);

            if (hasTranscription)
            {
                ContentScroll.IsVisible = false;
                TranscriptionScroll.IsVisible = true;
                BuildTranscriptionView(_record.Transcription);
                await SetupPlaybackAsync();
            }
            else
            {
                ContentScroll.IsVisible = true;
                TranscriptionScroll.IsVisible = false;
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
            _wordSpans.Clear();

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

            var wordStarts = new List<double>(orderedTimings.Count);
            var wordEnds = new List<double>(orderedTimings.Count);

            for (int i = 0; i < orderedTimings.Count; i++)
            {
                var timing = orderedTimings[i];
                var span = new Span { Text = timing.Word };
                _wordSpans.Add(span);
                _formattedTranscription.Spans.Add(span);
                wordStarts.Add(timing.Start);
                wordEnds.Add(timing.End);

                if (i < orderedTimings.Count - 1)
                {
                    _formattedTranscription.Spans.Add(new Span { Text = " " });
                }
            }

            _wordStartTimes = wordStarts.ToArray();
            _wordEndTimes = wordEnds.ToArray();
            TranscriptionLabel.FormattedText = _formattedTranscription;
            HighlightSpan(-1);
        }

        private async Task SetupPlaybackAsync()
        {
            if (PlaybackPanel == null || string.IsNullOrEmpty(_record.FilePath) || !File.Exists(_record.FilePath))
            {
                if(PlaybackPanel != null) PlaybackPanel.IsVisible = false;
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
            if (_wordStartTimes.Length > 0)
            {
                // Non-blocking add to the queue
                _positionQueue.TryAdd(position.TotalSeconds);
            }
        }

        private int FindWordIndex(double seconds)
        {
            if (_wordStartTimes.Length == 0) return -1;

            int index = Array.BinarySearch(_wordStartTimes, seconds);

            if (index >= 0)
            {
                return index;
            }
            
            index = ~index;

            if (index == 0)
            {
                return -1;
            }

            int candidateIndex = index - 1;

            if (seconds < _wordEndTimes[candidateIndex])
            {
                return candidateIndex;
            }

            return -1;
        }

        private void HighlightSpan(int index)
        {
            if (_wordSpans.Count == 0) return;

            if (_currentHighlightIndex >= 0 && _currentHighlightIndex < _wordSpans.Count)
            {
                _wordSpans[_currentHighlightIndex].TextColor = Colors.Gray;
                _wordSpans[_currentHighlightIndex].FontAttributes = FontAttributes.None;
            }

            if (index >= 0 && index < _wordSpans.Count)
            {
                _wordSpans[index].TextColor = Colors.White;
                _wordSpans[index].FontAttributes = FontAttributes.Bold;
                _currentHighlightIndex = index;
            }
            else
            {
                _currentHighlightIndex = -1;
            }
        }
        
        private void OnCopyTextClicked(object sender, EventArgs e)
        {
            // Implementation for copying text
        }

        private void OnReturnToRecordsClicked(object sender, EventArgs e)
        {
            // Implementation for returning to records
        }

        private void OnCloseClicked(object sender, EventArgs e)
        {
            // Implementation for closing
        }
    }
}
