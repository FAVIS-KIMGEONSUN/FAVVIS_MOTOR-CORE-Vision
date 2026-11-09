using System;
namespace FAVIS
{
    public enum InspectionStage
    {
        Preparing,
        Capturing,
        Processing,
        Completed,
        Canceled,
        Failed
    }

    public sealed class InspectionProgress
    {
        public InspectionStage Stage { get; set; }
        public int SequenceIndex { get; set; }
        public int TotalSequenceCount { get; set; }
        public string SequenceName { get; set; }
        public int CameraIndex { get; set; }
        public int DisplayIndex { get; set; }
        public string Message { get; set; }
        public object DisplayImage { get; set; }
        public bool? IsPassed { get; set; }
        public TimeSpan Elapsed { get; set; }
        public DateTime ReportedAt { get; set; }
    }
}
