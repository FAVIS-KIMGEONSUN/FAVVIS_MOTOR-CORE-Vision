using System.Collections.Generic;
using System.Runtime.Serialization;

namespace FAVIS
{
    [DataContract]
    public enum SequenceOutputType
    {
        [EnumMember]
        Inspection = 0,
        [EnumMember]
        InspectionAndOk = 1,
        [EnumMember]
        InspectionAndNg = 2,
        [EnumMember]
        ForceOk = 3,
        [EnumMember]
        ForceNg = 4
    }

    public enum GrabberType
    {
        Deediim = 0,
        HikCamera = 1
    }

    public enum ImageSaveMode
    {
        All = 0,
        None = 1,
        OkOnly = 2,
        NgOnly = 3
    }

    public sealed class InspectionConfig
    {
        public string PcName { get; set; }
        public string ImageSavePath { get; set; }
        public int DisplayCount { get; set; }
        public bool InspectionPass { get; set; }
        public ImageSaveMode ImageSaveMode { get; set; } = FAVIS.ImageSaveMode.All;
        public PlcConfig Plc { get; set; }
        public List<GrabberConfig> Grabbers { get; set; }
        public List<LightConfig> Lights { get; set; }
        public List<SequenceConfig> Sequences { get; set; }
    }

    public sealed class PlcConfig
    {
        public string PlcType { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public int StationNumber { get; set; }
        public int ScanTime { get; set; }
        public string PlcAddress { get; set; }
        public string PcAddress { get; set; }
        public string RejectAddress { get; set; }
        public string ModelNoAddress { get; set; }
        public string StepNoAddress { get; set; }
        public string SerialNoAddress { get; set; }
        public int PlcAliveBit { get; } = 0;
        public int PlcResetBit { get; } = 1;
        public int PlcTriggerBit { get; } = 2;
        public int PcAliveBit { get; } = 0;
        public int PcReadyBit { get; } = 1;
        public int PcBusyBit { get; } = 2;
        public int PcGrabBit { get; } = 3;
        public int PcOkBit { get; } = 4;
        public int PcNgBit { get; } = 5;
        public int PcCompleteBit { get; }  = 6;
    }

    public sealed class GrabberConfig
    {
        public int Id { get; set; }
        public GrabberType Type { get; set; }
        public string Name { get; set; }
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public int SensorIndex { get; set; }
        public int PatternIndex { get; set; }
    }

    public sealed class LightConfig
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public sealed class SequenceConfig
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public int CameraIndex { get; set; }
        public int DisplayIndex { get; set; }
        public int ExternalLightIndex { get; set; }
        public int ExternalLightBrightness { get; set; } = 100;
        public bool Enabled { get; set; }
        public int TriggerStep { get; set; }
        public SequenceOutputType OutputType { get; set; }
    }
}
