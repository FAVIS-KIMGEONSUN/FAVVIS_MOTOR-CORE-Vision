using System.Collections.Generic;

namespace FAVIS
{
    public sealed class ModelConfig
    {
        public int ModelNo { get; set; }
        public string ModelName { get; set; }
    }

    public sealed class LastModel
    {
        public int ModelNo { get; set; }
    }

    public sealed class ModelListConfig
    {
        public List<ModelConfig> Models { get; set; }
    }
}
