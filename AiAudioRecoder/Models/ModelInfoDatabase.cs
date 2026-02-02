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
            new ModelInfoDb { Name = "tiny", SizeMB = 78, SizeBytes = 0, Sha = "bd577a113a864445d4c299885e0cb97d4ba92b5f" },
            new ModelInfoDb { Name = "tiny.en", SizeMB = 78, SizeBytes = 0, Sha = "c78c86eb1a8faa21b369bcd33207cc90d64ae9df" },
            new ModelInfoDb { Name = "tiny-q5_1", SizeMB = 32, SizeBytes = 0, Sha = "2827a03e495b1ed3048ef28a6a4620537db4ee51" },
            new ModelInfoDb { Name = "tiny.en-q5_1", SizeMB = 32, SizeBytes = 0, Sha = "3fb92ec865cbbc769f08137f22470d6b66e071b6" },
            new ModelInfoDb { Name = "tiny-q8_0", SizeMB = 44, SizeBytes = 0, Sha = "19e8118f6652a650569f5a949d962154e01571d9" },
            new ModelInfoDb { Name = "tiny.en-q8_0", SizeMB = 44, SizeBytes = 0, Sha = "802d6668e7d411123e672abe4cb6c18f12306abb" },

            new ModelInfoDb { Name = "base", SizeMB = 148, SizeBytes = 0, Sha = "465707469ff3a37a2b9b8d8f89f2f99de7299dac" },
            new ModelInfoDb { Name = "base.en", SizeMB = 148, SizeBytes = 0, Sha = "137c40403d78fd54d454da0f9bd998f78703390c" },
            new ModelInfoDb { Name = "base-q5_1", SizeMB = 60, SizeBytes = 0, Sha = "a3733eda680ef76256db5fc5dd9de8629e62c5e7" },
            new ModelInfoDb { Name = "base.en-q5_1", SizeMB = 60, SizeBytes = 0, Sha = "d26d7ce5a1b6e57bea5d0431b9c20ae49423c94a" },
            new ModelInfoDb { Name = "base-q8_0", SizeMB = 82, SizeBytes = 0, Sha = "7bb89bb49ed6955013b166f1b6a6c04584a20fbe" },
            new ModelInfoDb { Name = "base.en-q8_0", SizeMB = 82, SizeBytes = 0, Sha = "bb1574182e9b924452bf0cd1510ac034d323e948" },

            new ModelInfoDb { Name = "small", SizeMB = 488, SizeBytes = 0, Sha = "55356645c2b361a969dfd0ef2c5a50d530afd8d5" },
            new ModelInfoDb { Name = "small.en", SizeMB = 488, SizeBytes = 0, Sha = "db8a495a91d927739e50b3fc1cc4c6b8f6c2d022" },
            new ModelInfoDb { Name = "small.en-tdrz", SizeMB = 488, SizeBytes = 0, Sha = "b6c6e7e89af1a35c08e6de56b66ca6a02a2fdfa1" },
            new ModelInfoDb { Name = "small-q5_1", SizeMB = 190, SizeBytes = 0, Sha = "6fe57ddcfdd1c6b07cdcc73aaf620810ce5fc771" },
            new ModelInfoDb { Name = "small.en-q5_1", SizeMB = 190, SizeBytes = 0, Sha = "20f54878d608f94e4a8ee3ae56016571d47cba34" },
            new ModelInfoDb { Name = "small-q8_0", SizeMB = 264, SizeBytes = 0, Sha = "bcad8a2083f4e53d648d586b7dbc0cd673d8afad" },
            new ModelInfoDb { Name = "small.en-q8_0", SizeMB = 264, SizeBytes = 0, Sha = "9d75ff4ccfa0a8217870d7405cf8cef0a5579852" },

            new ModelInfoDb { Name = "medium", SizeMB = 1530, SizeBytes = 0, Sha = "fd9727b6e1217c2f614f9b698455c4ffd82463b4" },
            new ModelInfoDb { Name = "medium.en", SizeMB = 1530, SizeBytes = 0, Sha = "8c30f0e44ce9560643ebd10bbe50cd20eafd3723" },
            new ModelInfoDb { Name = "medium-q5_0", SizeMB = 539, SizeBytes = 0, Sha = "7718d4c1ec62ca96998f058114db98236937490e" },
            new ModelInfoDb { Name = "medium.en-q5_0", SizeMB = 539, SizeBytes = 0, Sha = "bb3b5281bddd61605d6fc76bc5b92d8f20284c3b" },
            new ModelInfoDb { Name = "medium-q8_0", SizeMB = 823, SizeBytes = 0, Sha = "e66645948aff4bebbec71b3485c576f3d63af5d6" },
            new ModelInfoDb { Name = "medium.en-q8_0", SizeMB = 823, SizeBytes = 0, Sha = "b1cf48c12c807e14881f634fb7b6c6ca867f6b38" },

            new ModelInfoDb { Name = "large-v1", SizeMB = 3090, SizeBytes = 0, Sha = "b1caaf735c4cc1429223d5a74f0f4d0b9b59a299" },
            new ModelInfoDb { Name = "large-v2", SizeMB = 3090, SizeBytes = 0, Sha = "0f4c8e34f21cf1a914c59d8b3ce882345ad349d6" },
            new ModelInfoDb { Name = "large-v2-q5_0", SizeMB = 1080, SizeBytes = 0, Sha = "00e39f2196344e901b3a2bd5814807a769bd1630" },
            new ModelInfoDb { Name = "large-v2-q8_0", SizeMB = 1660, SizeBytes = 0, Sha = "da97d6ca8f8ffbeeb5fd147f79010eeea194ba38" },
            new ModelInfoDb { Name = "large-v3", SizeMB = 3100, SizeBytes = 0, Sha = "ad82bf6a9043ceed055076d0fd39f5f186ff8062" },
            new ModelInfoDb { Name = "large-v3-q5_0", SizeMB = 1080, SizeBytes = 0, Sha = "e6e2ed78495d403bef4b7cff42ef4aaadcfea8de" },
            new ModelInfoDb { Name = "large-v3-turbo", SizeMB = 1620, SizeBytes = 0, Sha = "4af2b29d7ec73d781377bfd1758ca957a807e941" },
            new ModelInfoDb { Name = "large-v3-turbo-q5_0", SizeMB = 574, SizeBytes = 0, Sha = "e050f7970618a659205450ad97eb95a18d69c9ee" },
            new ModelInfoDb { Name = "large-v3-turbo-q8_0", SizeMB = 874, SizeBytes = 0, Sha = "01bf15bedffe9f39d65c1b6ff9b687ea91f59e0e" },
        };

        foreach (var model in defaultModels)
        {
            if (!existingNames.Contains(model.Name))
            {
                await SaveModelAsync(model);
            }
            else
            {
                // Update SHA if missing
                var existing = models.First(m => m.Name == model.Name);
                if (string.IsNullOrEmpty(existing.Sha))
                {
                    existing.Sha = model.Sha;
                    await SaveModelAsync(existing);
                }
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
