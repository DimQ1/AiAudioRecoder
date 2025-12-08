using System.Collections.Generic;

namespace AiAudioRecoder.Models;

public sealed class LocalTextAnalysisModel
{
    public const string DefaultModelId = "heuristic-summary";

    public string Id { get; init; } = DefaultModelId;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Capabilities { get; init; } = string.Empty;

    public static IReadOnlyList<LocalTextAnalysisModel> BuiltInModels { get; } = new List<LocalTextAnalysisModel>
    {
        new()
        {
            Id = DefaultModelId,
            Name = "Heuristic Summarizer",
            Description = "Быстрое резюме на основе первых значимых предложений",
            Capabilities = "summary, key-points"
        },
        new()
        {
            Id = "wordcloud-insight",
            Name = "Keyword Extractor",
            Description = "Выделяет ключевые темы и теги, оценивает тональность текста",
            Capabilities = "keywords, tags, sentiment"
        },
        new()
        {
            Id = "meeting-notes-assistant",
            Name = "Meeting Notes Assistant",
            Description = "Структурирует длинные записи, выделяя решения и задачи",
            Capabilities = "structured-notes"
        }
    };
}
