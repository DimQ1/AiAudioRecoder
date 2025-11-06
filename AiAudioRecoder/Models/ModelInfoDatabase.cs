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
        _db.CreateTableAsync<AppSetting>().Wait();
    }

    public Task<List<ModelInfoDb>> GetModelsAsync() => _db.Table<ModelInfoDb>().ToListAsync();
    public Task<ModelInfoDb> GetModelAsync(string name) => _db.Table<ModelInfoDb>().Where(m => m.Name == name).FirstOrDefaultAsync();
    public Task<int> SaveModelAsync(ModelInfoDb model) => _db.InsertOrReplaceAsync(model);
    public Task<int> DeleteModelAsync(ModelInfoDb model) => _db.DeleteAsync(model);
    public async Task<string?> GetSettingAsync(string key)
    {
        var setting = await _db.Table<AppSetting>().Where(s => s.Key == key).FirstOrDefaultAsync();
        return setting?.Value;
    }

    public Task<int> SetSettingAsync(string key, string value)
    {
        var setting = new AppSetting
        {
            Key = key,
            Value = value
        };
        return _db.InsertOrReplaceAsync(setting);
    }

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

    public async Task UpdateFromFolderAsync(string modelsDir)
    {
        var models = await GetModelsAsync();
        var existingNames = models.Where(m => m.Name != null).ToDictionary(m => m.Name!);

        // Scan folder for ggml-*.bin files
        if (Directory.Exists(modelsDir))
        {
            var files = Directory.GetFiles(modelsDir, "ggml-*.bin");
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var modelName = fileName.Replace("ggml-", "").Replace(".bin", "");
                var fileInfo = new FileInfo(file);
                var sizeBytes = fileInfo.Length;

                if (existingNames.TryGetValue(modelName, out var model))
                {
                    // Update existing
                    model.IsDownloaded = true;
                    model.LocalPath = file;
                    model.SizeBytes = sizeBytes;
                    model.SizeMB = sizeBytes / (1024.0 * 1024.0);
                    await SaveModelAsync(model);
                }
                else
                {
                    // Add new
                    var newModel = new ModelInfoDb
                    {
                        Name = modelName,
                        SizeBytes = sizeBytes,
                        SizeMB = sizeBytes / (1024.0 * 1024.0),
                        IsDownloaded = true,
                        LocalPath = file
                    };
                    await SaveModelAsync(newModel);
                }
            }

            // Check for models in DB but file not exists
            foreach (var model in models)
            {
                if (!string.IsNullOrEmpty(model.LocalPath) && !File.Exists(model.LocalPath))
                {
                    model.IsDownloaded = false;
                    model.LocalPath = "";
                    await SaveModelAsync(model);
                }
            }
        }
    }
}
