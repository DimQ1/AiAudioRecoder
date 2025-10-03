using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AiAudioRecoder.Models;

namespace AiAudioRecoder.Services
{
    public class AudioMetadataDatabase
    {
        private readonly SQLiteAsyncConnection _db;

        public AudioMetadataDatabase(string dbPath)
        {
            _db = new SQLiteAsyncConnection(dbPath);
            _db.CreateTableAsync<AudioFileMetadata>().Wait();
        }

        public Task<int> AddMetadataAsync(AudioFileMetadata metadata)
            => _db.InsertAsync(metadata);

        public Task<List<AudioFileMetadata>> GetAllAsync()
            => _db.Table<AudioFileMetadata>().ToListAsync();

        public Task<AudioFileMetadata?> GetByIdAsync(int id)
            => _db.Table<AudioFileMetadata>().Where(x => x.Id == id).FirstOrDefaultAsync();
    }
}
