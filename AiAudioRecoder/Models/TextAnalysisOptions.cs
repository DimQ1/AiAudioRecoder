using System;
using Microsoft.Maui.Storage;

namespace AiAudioRecoder.Models;

public sealed class TextAnalysisOptions
{
    public const string PreferenceKey = "TextAnalysisOptions";

    public bool UseLocalModel { get; set; } = true;

    public string ApiEndpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string RemoteModel { get; set; } = "gpt-4o";

    public string PromptTemplate { get; set; } = "Ты — аналитик. Проанализируй текст и верни краткое резюме, ключевые мысли и общий тон. Текст:\n{input}";

    public string LocalModelId { get; set; } = LocalTextAnalysisModel.DefaultModelId;

    public double Temperature { get; set; } = 0.2;

    public TextAnalysisOptions Clone()
    {
        return (TextAnalysisOptions)MemberwiseClone();
    }

    public static TextAnalysisOptions CreateDefault()
    {
        return new TextAnalysisOptions();
    }

    public static TextAnalysisOptions Load()
    {
        try
        {
            var json = Preferences.Default.Get(PreferenceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return CreateDefault();
            }

            return System.Text.Json.JsonSerializer.Deserialize<TextAnalysisOptions>(json) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(this);
            Preferences.Default.Set(PreferenceKey, json);
        }
        catch
        {
            // Ignore persistence failures; caller can retry later.
        }
    }
}
