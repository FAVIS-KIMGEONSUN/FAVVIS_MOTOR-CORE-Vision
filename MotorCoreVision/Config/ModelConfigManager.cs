using System;
using System.Collections.Generic;
using System.IO;

namespace FAVIS
{
    public static class ModelConfigManager
    {
        private static readonly string ModelDirectory =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Model");
        private static readonly string ModelFilePath =
            Path.Combine(ModelDirectory, "models.json");
        private static readonly string LastFilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "last.json");

        public static List<ModelConfig> LoadModels()
        {
            DefaultDirectories();
            if (!File.Exists(ModelFilePath))
            {
                return new List<ModelConfig>
                {
                    new ModelConfig { ModelNo = 1, ModelName = "Default Model" }
                };
            }

            ModelListConfig file = Json.Deserialize<ModelListConfig>(ModelFilePath);
            return file?.Models ?? new List<ModelConfig>();
        }

        public static void SaveModels(List<ModelConfig> models)
        {
            DefaultDirectories();
            Json.Serialize(ModelFilePath, new ModelListConfig { Models = models });
        }

        public static int LoadLastModelNo()
        {
            DefaultDirectories();
            if (!File.Exists(LastFilePath))
            {
                return 1;
            }

            LastModel last = Json.Deserialize<LastModel>(LastFilePath);
            return last?.ModelNo > 0 ? last.ModelNo : 1;
        }

        public static void SaveLastModelNo(int modelNo)
        {
            DefaultDirectories();
            Json.Serialize(LastFilePath, new LastModel { ModelNo = modelNo });
        }

        public static string GetModelDirectory(int modelNo)
        {
            if (modelNo <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(modelNo));
            }

            string path = Path.Combine(ModelDirectory, modelNo.ToString());
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DefaultDirectories()
        {
            Directory.CreateDirectory(ModelDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(LastFilePath));
        }
    }
}
