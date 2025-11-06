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

            if (useVectorSearch)
            {
                return await SearchWithVectorSimilarityAsync(query);
            }
            else
            {
                // Simple keyword search as fallback
                var records = await _db.Table<AudioFileMetadata>()
                    .Where(x => !string.IsNullOrEmpty(x.Transcription) && 
                               (x.Transcription.Contains(query) || x.Summary.Contains(query)))
                    .OrderByDescending(x => x.EndTime)
                    .ToListAsync();
                _logger?.LogDebug("Keyword search for '{Query}' returned {Count} results", query, records.Count);
                return records;
            }
        }

        private async Task<List<AudioFileMetadata>> SearchWithVectorSimilarityAsync(string query)
        {
            try
            {
                var allRecords = await _db.Table<AudioFileMetadata>()
                    .Where(x => !string.IsNullOrEmpty(x.Transcription))
                    .ToListAsync();

                if (allRecords.Count == 0)
                {
                    _logger?.LogDebug("No records available for vector search");
                    return new List<AudioFileMetadata>();
                }

                var queryVector = GenerateTextVector(query.ToLower());
                var results = new List<(AudioFileMetadata Record, double Similarity)>();

                foreach (var record in allRecords)
                {
                    if (!string.IsNullOrEmpty(record.Transcription))
                    {
                        var recordVector = GenerateTextVector(record.Transcription.ToLower());
                        var similarity = CalculateCosineSimilarity(queryVector, recordVector);
                        
                        // Only include relevant results (similarity > 0.1)
                        if (similarity > 0.1)
                        {
                            results.Add((record, similarity));
                        }
                    }
                }

                // Sort by similarity descending
                var searchResults = results
                    .OrderByDescending(x => x.Similarity)
                    .Take(50) // Limit results
                    .Select(x => x.Record)
                    .ToList();

                _logger?.LogDebug("Vector search for '{Query}' returned {Count} results", query, searchResults.Count);
                return searchResults;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Vector search failed, falling back to keyword search for '{Query}'", query);
                return await SearchAsync(query, false);
            }
        }

        private double[] GenerateTextVector(string text)
        {
            // Simple TF-IDF like vector generation
            // In production, use a proper embedding model like SentenceTransformers
            var words = text.Split(new char[] { ' ', '.', ',', '!', '?', ';', ':' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var uniqueWords = words.Distinct().ToArray();
            
            // Use a fixed-size vector (e.g., 100 dimensions)
            var vector = new double[100];
            
            foreach (var word in uniqueWords)
            {
                if (!string.IsNullOrWhiteSpace(word))
                {
                    var hash = Math.Abs(word.GetHashCode()) % 100;
                    vector[hash] += 1.0 / (words.Length + 1); // Simple frequency weighting
                }
            }
            
            // Normalize vector
            var magnitude = Math.Sqrt(vector.Sum(x => x * x));
            if (magnitude > 0)
            {
                for (int i = 0; i < vector.Length; i++)
                {
                    vector[i] /= magnitude;
                }
            }
            
            return vector;
        }

        private double CalculateCosineSimilarity(double[] vectorA, double[] vectorB)
        {
            if (vectorA.Length != vectorB.Length) return 0.0;
            
            double dotProduct = 0.0;
            double magnitudeA = 0.0;
            double magnitudeB = 0.0;
            
            for (int i = 0; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
                magnitudeA += vectorA[i] * vectorA[i];
                magnitudeB += vectorB[i] * vectorB[i];
            }
            
            magnitudeA = Math.Sqrt(magnitudeA);
            magnitudeB = Math.Sqrt(magnitudeB);
            
            if (magnitudeA == 0 || magnitudeB == 0) return 0.0;
            
            return dotProduct / (magnitudeA * magnitudeB);
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
