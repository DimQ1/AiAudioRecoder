using SQLite;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Linq;

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
        var existingNames = models.Select(m => m.Name).ToHashSet();

        var defaultModels = new List<ModelInfoDb>
        {
            new ModelInfoDb { Name = "tiny", SizeMB = 75, SizeBytes = 78643200 },
            new ModelInfoDb { Name = "base", SizeMB = 142, SizeBytes = 148897792 },
            new ModelInfoDb { Name = "small", SizeMB = 466, SizeBytes = 488380416 },
            new ModelInfoDb { Name = "medium", SizeMB = 1500, SizeBytes = 1572864000 },
            new ModelInfoDb { Name = "large", SizeMB = 2900, SizeBytes = 3040870400 },
            new ModelInfoDb { Name = "large-v2", SizeMB = 2900, SizeBytes = 3040870400 },
            new ModelInfoDb { Name = "large-v3", SizeMB = 2900, SizeBytes = 3040870400 }
        };

        foreach (var model in defaultModels)
        {
            if (!existingNames.Contains(model.Name))
            {
                await SaveModelAsync(model);
            }
        }
    }
}
