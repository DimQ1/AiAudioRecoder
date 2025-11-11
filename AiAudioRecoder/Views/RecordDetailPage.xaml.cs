using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiAudioRecoder.Models;
using AiAudioRecoder.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AiAudioRecoder.Views;

public partial class RecordDetailPage : ContentPage
{
    private readonly AudioFileMetadata _metadata;
    private readonly string _column;
    private readonly string _displayValue;
    private readonly List<Span> _transcriptionSpans = new();
    private readonly List<double> _segmentThresholds = new();
    private readonly AudioPlaybackService _playbackService = new();
    private readonly Color _highlightColor = Color.FromArgb("#264F78");
    private TimeSpan _duration;
    private int _lastHighlightedIndex = -1;
    private bool _playbackReady;
    private Color _baseTextColor = Colors.White;
    private string _currentText = string.Empty;

    public RecordDetailPage(AudioFileMetadata metadata, string column, string displayValue)
    {
        InitializeComponent();
        _metadata = metadata;
        _column = column;
        _displayValue = displayValue;
        TitleLabel.Text = GetTitle(column);
        Loaded += OnPageLoaded;
        _playbackService.PositionChanged += OnPlaybackPositionChanged;
        _playbackService.PlaybackEnded += OnPlaybackEnded;
    }

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        Loaded -= OnPageLoaded;
        await ConfigureContentAsync();
    }

    private string GetTitle(string column) => column switch
    {
        "Date" => "Дата записи",
        "Duration" => "Длительность",
        "Status" => "Статус",
        "Summary" => "Распознанный текст",
        "File" => "Файл",
        _ => "Данные"
    };

    private async Task ConfigureContentAsync()
    {
        var resolvedValue = ResolveDisplayValue();
        bool hasTranscription = string.Equals(_column, "Summary", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_metadata.Transcription);

        if (hasTranscription)
        {
            ContentScroll.IsVisible = false;
            TranscriptionScroll.IsVisible = true;
            BuildTranscriptionView(_metadata.Transcription);
            _currentText = _metadata.Transcription;
            await SetupPlaybackAsync();
        }
        else
        {
            ContentScroll.IsVisible = true;
            TranscriptionScroll.IsVisible = false;
            ContentLabel.Text = resolvedValue;
            _currentText = resolvedValue;
            PlaybackPanel.IsVisible = false;
        }
    }

    private string ResolveDisplayValue()
    {
        return _column switch
        {
            "Date" => _metadata.EndTime.ToString("yyyy-MM-dd HH:mm:ss"),
            "Duration" => _metadata.Duration.ToString(@"hh\:mm\:ss"),
            "Status" => _metadata.IsTranscribed ? "Распознано" : "Ожидает",
            "Summary" => _metadata.Summary ?? _displayValue,
            "File" => _metadata.FilePath ?? _displayValue,
            _ => _displayValue
        } ?? string.Empty;
    }

    private void BuildTranscriptionView(string transcription)
    {
        var segments = SplitIntoSegments(transcription);
        if (segments.Count == 0)
        {
            segments.Add(transcription);
        }

        var formatted = new FormattedString();
        _transcriptionSpans.Clear();
        _segmentThresholds.Clear();
        _baseTextColor = ResolveSecondaryTextColor();

        for (int i = 0; i < segments.Count; i++)
        {
            var text = segments[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Preserve spacing between segments
            if (formatted.Spans.Count > 0 && !text.StartsWith('\n'))
            {
                formatted.Spans.Add(new Span { Text = " " });
            }

            var span = new Span
            {
                Text = text,
                TextColor = _baseTextColor
            };

            formatted.Spans.Add(span);
            _transcriptionSpans.Add(span);
            _segmentThresholds.Add((i + 1) / (double)segments.Count);
        }

        TranscriptionLabel.FormattedText = formatted;
        HighlightSegment(-1);
    }

    private static List<string> SplitIntoSegments(string transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            return new List<string>();

        var segments = new List<string>();
        var builder = new StringBuilder();
        foreach (var ch in transcription)
        {
            builder.Append(ch);
            if (IsSentenceTerminator(ch))
            {
                segments.Add(builder.ToString().Trim());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            segments.Add(builder.ToString().Trim());
        }

        return segments.Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    private static bool IsSentenceTerminator(char ch) => ch == '.' || ch == '!' || ch == '?' || ch == '\n';

    private async Task SetupPlaybackAsync()
    {
        if (PlaybackPanel == null)
            return;

        if (string.IsNullOrEmpty(_metadata.FilePath) || !File.Exists(_metadata.FilePath))
        {
            PlaybackPanel.IsVisible = false;
            _playbackReady = false;
            return;
        }

        var loaded = await _playbackService.LoadAsync(_metadata.FilePath);
        _playbackReady = loaded;

        if (!loaded)
        {
            PlaybackPanel.IsVisible = false;
            return;
        }

        _duration = _playbackService.Duration;
        PlaybackPanel.IsVisible = true;
        PositionSlider.Value = 0;
        CurrentTimeLabel.Text = FormatTime(TimeSpan.Zero);
        DurationLabel.Text = FormatTime(_duration);
        UpdatePlayButtonIcon();
    }

    private void OnPlaybackPositionChanged(object? sender, TimeSpan position)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_playbackReady)
                return;

            var duration = _duration > TimeSpan.Zero ? _duration : _playbackService.Duration;
            if (duration <= TimeSpan.Zero)
                return;

            var ratio = Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);

            if (Math.Abs(PositionSlider.Value - ratio) > 0.005)
            {
                PositionSlider.Value = ratio;
            }

            var timeText = FormatTime(position);
            if (!string.Equals(CurrentTimeLabel.Text, timeText, StringComparison.Ordinal))
            {
                CurrentTimeLabel.Text = timeText;
            }

            UpdateHighlight(ratio);
        });
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PositionSlider.Value = 0;
            CurrentTimeLabel.Text = FormatTime(TimeSpan.Zero);
            HighlightSegment(-1);
            UpdatePlayButtonIcon();
        });
    }

    private void UpdateHighlight(double ratio)
    {
        if (_segmentThresholds.Count == 0)
            return;

        int index = GetSegmentIndex(ratio);
        HighlightSegment(index);
    }

    private int GetSegmentIndex(double ratio)
    {
        var thresholds = _segmentThresholds;
        int index = thresholds.BinarySearch(ratio);
        if (index < 0)
        {
            index = ~index;
        }

        if (index >= thresholds.Count)
        {
            index = thresholds.Count - 1;
        }

        return index;
    }

    private void HighlightSegment(int index)
    {
        if (_lastHighlightedIndex == index)
            return;

        if (_lastHighlightedIndex >= 0 && _lastHighlightedIndex < _transcriptionSpans.Count)
        {
            var previous = _transcriptionSpans[_lastHighlightedIndex];
            previous.BackgroundColor = Colors.Transparent;
            previous.TextColor = _baseTextColor;
        }

        if (index >= 0 && index < _transcriptionSpans.Count)
        {
            var current = _transcriptionSpans[index];
            current.BackgroundColor = _highlightColor;
            current.TextColor = _baseTextColor;
        }

        _lastHighlightedIndex = index;
    }

    private static Color ResolveSecondaryTextColor()
    {
        if (Application.Current?.Resources.TryGetValue("SecondaryDarkText", out var textColor) == true && textColor is Color color)
        {
            return color;
        }

        return Colors.White;
    }

    private void UpdatePlayButtonIcon()
    {
        if (PlayPauseButton == null)
            return;

        PlayPauseButton.Text = _playbackService.IsPlaying ? "Pause" : "Play";
    }

    private async void OnPlayPauseClicked(object? sender, EventArgs e)
    {
        if (!_playbackReady)
            return;

        if (_playbackService.IsPlaying)
        {
            _playbackService.Pause();
        }
        else
        {
            await _playbackService.PlayAsync();
        }

        UpdatePlayButtonIcon();
    }

    private async void OnCloseClicked(object sender, EventArgs e) => await CloseAsync(false);

    private async void OnReturnToRecordsClicked(object sender, EventArgs e) => await CloseAsync(true);

    private async void OnCopyTextClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_currentText))
            return;

        try
        {
            await Clipboard.SetTextAsync(_currentText);
            await DisplayAlertAsync("Скопировано", "Текст скопирован в буфер обмена.", "OK");
        }
        catch
        {
            await DisplayAlertAsync("Ошибка", "Не удалось скопировать текст.", "OK");
        }
    }

    private async Task CloseAsync(bool navigateToRecords)
    {
        _playbackService.Stop();
        UpdatePlayButtonIcon();

        if (PositionSlider != null)
        {
            PositionSlider.Value = 0;
        }

        CurrentTimeLabel.Text = FormatTime(TimeSpan.Zero);
        HighlightSegment(-1);

        await Navigation.PopModalAsync();

        if (navigateToRecords && Shell.Current is not null)
        {
            try
            {
                await Shell.Current.GoToAsync("//records");
            }
            catch
            {
                // Ignore navigation failures and remain on current page
            }
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _playbackService.Stop();
        _playbackService.PositionChanged -= OnPlaybackPositionChanged;
        _playbackService.PlaybackEnded -= OnPlaybackEnded;
        _playbackService.Dispose();
        _playbackReady = false;
        _lastHighlightedIndex = -1;
        _currentText = string.Empty;
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return time.ToString(@"hh\:mm\:ss");
        }

        return time.ToString(@"mm\:ss");
    }
}
