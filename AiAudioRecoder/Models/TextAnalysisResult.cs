using System;
using System.Collections.Generic;
using System.Linq;

namespace AiAudioRecoder.Models;

public sealed class TextAnalysisResult
{
    public bool UsedLocalModel { get; init; }

    public string ModelId { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Sentiment { get; init; } = string.Empty;

    public IReadOnlyList<string> KeyPoints { get; init; } = Array.Empty<string>();

    public string RawResponse { get; init; } = string.Empty;

    public string BuildDisplayString()
    {
        var lines = new List<string>();
        lines.Add($"Источник: {(UsedLocalModel ? "Локальная модель" : "API" )} ({ModelId})");

        if (!string.IsNullOrWhiteSpace(Summary))
        {
            lines.Add("\nРезюме:");
            lines.Add(Summary.Trim());
        }

        if (KeyPoints.Count > 0)
        {
            lines.Add("\nКлючевые мысли:");
            foreach (var point in KeyPoints)
            {
                if (!string.IsNullOrWhiteSpace(point))
                {
                    lines.Add($" • {point.Trim()}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(Sentiment))
        {
            lines.Add("\nОбщий тон: " + Sentiment.Trim());
        }

        if (!string.IsNullOrWhiteSpace(RawResponse))
        {
            lines.Add("\nСырые данные:");
            lines.Add(RawResponse.Trim());
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static TextAnalysisResult FromRaw(string payload, bool local, string modelId)
    {
        return new TextAnalysisResult
        {
            UsedLocalModel = local,
            ModelId = modelId,
            RawResponse = payload
        };
    }
}
