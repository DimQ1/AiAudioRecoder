using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiAudioRecoder.Models;
using Microsoft.Extensions.Logging;

namespace AiAudioRecoder.Services
{
    public class AudioMetadataDatabase
    {
        private readonly SQLiteAsyncConnection _db;
        private readonly ILogger<AudioMetadataDatabase> _logger;
        private bool _isInitialized = false;

        public AudioMetadataDatabase(string dbPath, ILogger<AudioMetadataDatabase> logger = null)
        {
            _db = new SQLiteAsyncConnection(dbPath);
            _logger = logger;
            _ = InitializeDatabaseAsync(); // Non-blocking async initialization
        }

        private async Task InitializeDatabaseAsync()
        {
            try
            {
                if (_isInitialized) return;

                _logger?.LogInformation("Initializing AudioMetadataDatabase at {DbPath}", _db.DatabasePath);
                
                // Create table
                await _db.CreateTableAsync<AudioFileMetadata>();
                _logger?.LogDebug("Table AudioFileMetadata created");

                // Add indexes for better performance
                await _db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_audio_date ON AudioFileMetadata(EndTime DESC)");
                _logger?.LogDebug("Index idx_audio_date created");

                await _db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_audio_transcribed ON AudioFileMetadata(IsTranscribed)");
                _logger?.LogDebug("Index idx_audio_transcribed created");

                await _db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_audio_search ON AudioFileMetadata(Transcription)");
                _logger?.LogDebug("Index idx_audio_search created");

                _isInitialized = true;
                _logger?.LogInformation("AudioMetadataDatabase initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize AudioMetadataDatabase");
                throw;
            }
        }

        // Simple initialization check - wait a bit for async init to complete
        private async Task EnsureInitializedAsync()
        {
            int attempts = 0;
            while (!_isInitialized && attempts < 10)
            {
                await Task.Delay(50); // Wait 50ms
                attempts++;
            }
            
            if (!_isInitialized)
            {
                _logger?.LogWarning("Database not fully initialized after wait, proceeding anyway");
                await InitializeDatabaseAsync();
            }
        }

        public async Task<int> AddMetadataAsync(AudioFileMetadata metadata)
        {
            await EnsureInitializedAsync();
            metadata.Id = 0; // Reset ID for new insert
            var result = await _db.InsertAsync(metadata);
            _logger?.LogDebug("Added metadata for file: {FilePath}, ID: {Id}", metadata.FilePath, metadata.Id);
            return result;
        }

        public async Task<int> UpdateMetadataAsync(AudioFileMetadata metadata)
        {
            await EnsureInitializedAsync();
            var result = await _db.UpdateAsync(metadata);
            _logger?.LogDebug("Updated metadata ID: {Id}", metadata.Id);
            return result;
        }

        public async Task<List<AudioFileMetadata>> GetAllAsync()
        {
            await EnsureInitializedAsync();
            var records = await _db.Table<AudioFileMetadata>()
                .OrderByDescending(x => x.EndTime)
                .ToListAsync();
            _logger?.LogDebug("Retrieved {Count} records", records.Count);
            return records;
        }

        public async Task<List<AudioFileMetadata>> GetUntranscribedAsync()
        {
            await EnsureInitializedAsync();
            var records = await _db.Table<AudioFileMetadata>()
                .Where(x => x.IsTranscribed == false || string.IsNullOrEmpty(x.Transcription))
                .OrderByDescending(x => x.EndTime)
                .ToListAsync();
            _logger?.LogDebug("Retrieved {Count} untranscribed records", records.Count);
            return records;
        }

        public async Task<List<AudioFileMetadata>> SearchAsync(string query, bool useVectorSearch = false)
        {
            await EnsureInitializedAsync();
            if (string.IsNullOrWhiteSpace(query))
            {
                return await GetAllAsync();
            }
            var words = query.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sb = new System.Text.StringBuilder();
            sb.Append("SELECT * FROM AudioFileMetadata WHERE IsTranscribed = 1");
            var parameters = new List<object>();
            foreach (var w in words)
            {
                sb.Append(" AND (Transcription LIKE ? OR Summary LIKE ?)");
                var pattern = "%" + w + "%";
                parameters.Add(pattern);
                parameters.Add(pattern);
            }
            sb.Append(" ORDER BY EndTime DESC");
            var results = await _db.QueryAsync<AudioFileMetadata>(sb.ToString(), parameters.ToArray());
            _logger?.LogDebug("Plain search '{Query}' words={Words} results={Count}", query, words.Length, results.Count);
            return results;
        }

        public async Task<AudioFileMetadata?> GetByIdAsync(int id)
        {
            await EnsureInitializedAsync();
            var record = await _db.Table<AudioFileMetadata>().Where(x => x.Id == id).FirstOrDefaultAsync();
            _logger?.LogDebug("Retrieved record by ID: {Id}", id);
            return record;
        }

        public async Task<int> DeleteAsync(int id)
        {
            await EnsureInitializedAsync();
            var result = await _db.DeleteAsync<AudioFileMetadata>(id);
            _logger?.LogDebug("Deleted record ID: {Id}", id);
            return result;
        }

        public async Task<int> GetCountAsync()
        {
            await EnsureInitializedAsync();
            var count = await _db.Table<AudioFileMetadata>().CountAsync();
            _logger?.LogDebug("Total records count: {Count}", count);
            return count;
        }

        // Simple cleanup - no complex disposal needed
        public void Cleanup()
        {
            try
            {
                _logger?.LogDebug("AudioMetadataDatabase cleanup called");
                // SQLiteAsyncConnection manages its own resources
                // No explicit disposal needed in this case
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during AudioMetadataDatabase cleanup");
            }
        }
    }
}
