using SQLite;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AiAudioRecoder.Models
{
    [Table("AudioFileMetadata")]
    public class AudioFileMetadata
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [NotNull]
        public string FilePath { get; set; } = string.Empty;

        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public string Transcription { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string WordTimingsJson { get; set; } = string.Empty;

        // Transcription status
        public bool IsTranscribed { get; set; } = false;
        public DateTime? TranscriptionDate { get; set; }

        // Computed properties for UI
        [Ignore]
        public TimeSpan Duration => EndTime - StartTime;

        [Ignore]
        public string SearchableText => string.IsNullOrEmpty(Summary) ? 
            (string.IsNullOrEmpty(Transcription) ? string.Empty : Transcription.ToLower()) : 
            $"{Summary} {Transcription}".ToLower();

        public AudioFileMetadata()
        {
            FilePath = string.Empty;
            Transcription = string.Empty;
            Summary = string.Empty;
            WordTimingsJson = string.Empty;
        }

        [Ignore]
        public List<TranscriptionWordTiming> WordTimings
        {
            get
            {
                if (string.IsNullOrWhiteSpace(WordTimingsJson))
                    return new List<TranscriptionWordTiming>();

                try
                {
                    var data = JsonSerializer.Deserialize<List<TranscriptionWordTiming>>(WordTimingsJson);
                    return data ?? new List<TranscriptionWordTiming>();
                }
                catch
                {
                    return new List<TranscriptionWordTiming>();
                }
            }
            set
            {
                WordTimingsJson = value is null || value.Count == 0
                    ? string.Empty
                    : JsonSerializer.Serialize(value);
            }
        }
    }
}
