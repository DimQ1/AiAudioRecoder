using SQLite;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;

namespace AiAudioRecoder.Models;

public class ModelInfoDatabase
{
    private readonly SQLiteAsyncConnection _db;
    public ModelInfoDatabase(string dbPath)
    {
        _db = new SQLiteAsyncConnection(dbPath);
        _db.CreateTableAsync<ModelInfoDb>().Wait();
    }

    public Task<List<ModelInfoDb>> GetModelsAsync() => _db.Table<ModelInfoDb>().ToListAsync();
    public Task<ModelInfoDb> GetModelAsync(string name) => _db.Table<ModelInfoDb>().Where(m => m.Name == name).FirstOrDefaultAsync();
    public Task<int> SaveModelAsync(ModelInfoDb model) => _db.InsertOrReplaceAsync(model);
    public Task<int> DeleteModelAsync(ModelInfoDb model) => _db.DeleteAsync(model);
    public async Task EnsureDefaultModelsAsync()
    {
        var models = await GetModelsAsync();
        if (models.Count == 0)
        {
            await SaveModelAsync(new ModelInfoDb { Name = "tiny", SizeMB = 0, SizeBytes = 0, IsDownloaded = false, LocalPath = "" });
            await SaveModelAsync(new ModelInfoDb { Name = "base", SizeMB = 0, SizeBytes = 0, IsDownloaded = false, LocalPath = "" });
            await SaveModelAsync(new ModelInfoDb { Name = "small", SizeMB = 0, SizeBytes = 0, IsDownloaded = false, LocalPath = "" });
            await SaveModelAsync(new ModelInfoDb { Name = "medium", SizeMB = 0, SizeBytes = 0, IsDownloaded = false, LocalPath = "" });
        }
    }
}
