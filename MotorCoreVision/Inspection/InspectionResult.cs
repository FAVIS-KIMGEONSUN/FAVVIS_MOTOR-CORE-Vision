using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace FAVIS
{
    public sealed class InspectionResult
    {
        public BitmapSource DisplayImage { get; set; }
        public bool IsPassed { get; set; }
        public string ResultText { get; set; }
        public List<SequenceInspectionResult> SequenceResults { get; set; }
    }

    public sealed class SequenceInspectionResult
    {
        public int SequenceIndex { get; set; }
        public bool IsPassed { get; set; }
    }
}
