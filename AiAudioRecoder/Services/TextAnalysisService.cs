using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiAudioRecoder.Models;
using Microsoft.Extensions.Logging;

namespace AiAudioRecoder.Services;

public sealed class TextAnalysisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<TextAnalysisService> _logger;
    private TextAnalysisOptions _options;
    private readonly IReadOnlyList<LocalTextAnalysisModel> _localModels;

    public TextAnalysisService(HttpClient httpClient, ILogger<TextAnalysisService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = TextAnalysisOptions.Load();
        _localModels = LocalTextAnalysisModel.BuiltInModels;
    }

    public IReadOnlyList<LocalTextAnalysisModel> LocalModels => _localModels;

    public TextAnalysisOptions GetOptions()
    {
        return _options.Clone();
    }

    public void UpdateOptions(TextAnalysisOptions options, bool persist = true)
    {
        _options = options.Clone();
        if (persist)
        {
            _options.Save();
        }
    }

    public async Task<TextAnalysisResult> AnalyzeAsync(string text, string? promptOverride = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text must not be empty", nameof(text));
        }

        var optionsSnapshot = _options.Clone();
        var prompt = ApplyPrompt(string.IsNullOrWhiteSpace(promptOverride) ? optionsSnapshot.PromptTemplate : promptOverride, text);

        if (optionsSnapshot.UseLocalModel || string.IsNullOrWhiteSpace(optionsSnapshot.ApiEndpoint))
        {
            return await Task.Run(() => RunLocalAnalysis(text, optionsSnapshot, prompt, false), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await RunRemoteAsync(prompt, optionsSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote LLM analysis failed, falling back to local heuristics");
            return await Task.Run(() => RunLocalAnalysis(text, optionsSnapshot, prompt, true), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ApplyPrompt(string template, string text)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return text;
        }

        return template
            .Replace("{input}", text, StringComparison.OrdinalIgnoreCase)
            .Replace("{text}", text, StringComparison.OrdinalIgnoreCase);
    }

    private TextAnalysisResult RunLocalAnalysis(string originalText, TextAnalysisOptions options, string prompt, bool isFallback)
    {
        var model = _localModels.FirstOrDefault(m => string.Equals(m.Id, options.LocalModelId, StringComparison.OrdinalIgnoreCase))
                    ?? _localModels.First();

        var sentences = SplitSentences(originalText);
        var summary = BuildSummary(sentences);
        var keypoints = ExtractKeywords(originalText);
        var sentiment = EvaluateSentiment(originalText);

        var noteBuilder = new StringBuilder();
        if (isFallback)
        {
            noteBuilder.AppendLine("Fallback: удалённый API недоступен, использована локальная модель.");
        }
        noteBuilder.AppendLine("Prompt:");
        noteBuilder.AppendLine(prompt);

        return new TextAnalysisResult
        {
            UsedLocalModel = true,
            ModelId = model.Id,
            Summary = summary,
            Sentiment = sentiment,
            KeyPoints = keypoints.ToArray(),
            RawResponse = noteBuilder.ToString()
        };
    }

    private async Task<TextAnalysisResult> RunRemoteAsync(string prompt, TextAnalysisOptions options, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.ApiEndpoint);

        var payload = new
        {
            model = options.RemoteModel,
            prompt,
            temperature = options.Temperature
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseRemoteResponse(content, options.RemoteModel);
    }

    private TextAnalysisResult ParseRemoteResponse(string payload, string modelId)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            string summary = TryGetString(root, "summary") ?? string.Empty;
            string sentiment = TryGetString(root, "sentiment") ?? string.Empty;
            var keyPoints = TryGetArray(root, "key_points");

            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = ExtractCompletionText(root) ?? summary;
            }

            if (keyPoints.Count == 0)
            {
                keyPoints = TryGetArray(root, "keypoints");
            }

            if (keyPoints.Count == 0)
            {
                keyPoints = TrySplitBullets(summary);
            }

            return new TextAnalysisResult
            {
                UsedLocalModel = false,
                ModelId = modelId,
                Summary = summary,
                Sentiment = sentiment,
                KeyPoints = keyPoints.ToArray(),
                RawResponse = payload
            };
        }
        catch (JsonException jsonEx)
        {
            _logger.LogWarning(jsonEx, "Failed to parse remote analysis payload");
            return TextAnalysisResult.FromRaw(payload, false, modelId);
        }
    }

    private static string? ExtractCompletionText(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }

                if (choice.TryGetProperty("text", out var textElement))
                {
                    var text = textElement.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
        }

        if (root.TryGetProperty("result", out var resultElement))
        {
            var text = resultElement.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static List<string> TryGetArray(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
        {
            return arrayElement
                .EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();
        }

        return new List<string>();
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    private static List<string> TrySplitBullets(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return new List<string>();
        }

        var lines = summary
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('*', '-', '•', ' '))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .Take(6)
            .ToList();

        return lines;
    }

    private static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var sentences = Regex.Split(text, "(?<=[.!?])\\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        return sentences;
    }

    private static string BuildSummary(IReadOnlyList<string> sentences)
    {
        if (sentences.Count == 0)
        {
            return string.Empty;
        }

        var significant = sentences
            .Where(s => s.Length > 25)
            .Take(3)
            .ToArray();

        if (significant.Length == 0)
        {
            significant = sentences.Take(2).ToArray();
        }

        return string.Join(" ", significant);
    }

    private static List<string> ExtractKeywords(string text)
    {
        var tokens = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+");
        var frequencies = new Dictionary<string, int>();
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token) || StopWords.Contains(token))
            {
                continue;
            }

            frequencies[token] = frequencies.TryGetValue(token, out var count) ? count + 1 : 1;
        }

        return frequencies
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Select(pair => pair.Key)
            .Take(8)
            .ToList();
    }

    private static string EvaluateSentiment(string text)
    {
        var lower = text.ToLowerInvariant();
        var positiveHits = PositiveWords.Count(word => lower.Contains(word));
        var negativeHits = NegativeWords.Count(word => lower.Contains(word));

        if (positiveHits == negativeHits)
        {
            return "Нейтральный";
        }

        return positiveHits > negativeHits ? "Позитивный" : "Негативный";
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "и", "в", "во", "на", "с", "со", "по", "к", "ко", "но", "же", "или", "а", "от", "до", "что", "это", "как", "так", "для", "из", "за", "мы", "вы", "они", "он", "она", "оно", "там", "здесь", "тоже", "также", "у", "при", "когда", "чтобы", "если", "уже", "еще"
    };

    private static readonly string[] PositiveWords =
    {
        "хорош", "луч", "отлич", "рад", "успех", "поддерж", "интерес", "сильн", "позит", "эффектив", "полез", "красив", "улучш"
    };

    private static readonly string[] NegativeWords =
    {
        "плох", "слож", "проблем", "негатив", "криз", "ошиб", "страх", "неудов", "провал", "утрат", "наруш"
    };
}
