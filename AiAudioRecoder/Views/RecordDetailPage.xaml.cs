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
    private TimeSpan _duration;
    private int _lastHighlightedIndex = -1;
    private bool _playbackReady;

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
            await SetupPlaybackAsync();
        }
        else
        {
            ContentScroll.IsVisible = true;
            TranscriptionScroll.IsVisible = false;
            ContentLabel.Text = resolvedValue;
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
                TextColor = ResolveSecondaryTextColor()
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
            PositionSlider.Value = ratio;
            CurrentTimeLabel.Text = FormatTime(position);
            UpdateHighlight(position, duration);
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

    private void UpdateHighlight(TimeSpan position, TimeSpan duration)
    {
        if (_segmentThresholds.Count == 0 || duration <= TimeSpan.Zero)
            return;

        var ratio = Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);

        int index = -1;
        for (int i = 0; i < _segmentThresholds.Count; i++)
        {
            if (ratio <= _segmentThresholds[i])
            {
                index = i;
                break;
            }
        }

        HighlightSegment(index);
    }

    private void HighlightSegment(int index)
    {
        var highlightColor = Color.FromArgb("#264F78");
        var baseColor = ResolveSecondaryTextColor();

        for (int i = 0; i < _transcriptionSpans.Count; i++)
        {
            var span = _transcriptionSpans[i];
            span.BackgroundColor = i == index ? highlightColor : Colors.Transparent;
            span.TextColor = baseColor;
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

    private async void OnCloseClicked(object sender, EventArgs e)
    {
        _playbackService.Stop();
        UpdatePlayButtonIcon();
        PositionSlider.Value = 0;
        CurrentTimeLabel.Text = FormatTime(TimeSpan.Zero);
        HighlightSegment(-1);
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _playbackService.Stop();
        _playbackService.PositionChanged -= OnPlaybackPositionChanged;
        _playbackService.PlaybackEnded -= OnPlaybackEnded;
        _playbackService.Dispose();
        _playbackReady = false;
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
