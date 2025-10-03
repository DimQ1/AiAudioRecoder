using SQLite;
using System;

namespace AiAudioRecoder.Models
{
    public class AudioFileMetadata
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? Transcription { get; set; }
        public string? Summary { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }
}
