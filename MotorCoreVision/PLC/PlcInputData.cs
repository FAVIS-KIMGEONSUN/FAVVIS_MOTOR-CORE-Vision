using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FAVIS
{
    [Flags]
    public enum PcBits : uint
    {
        None = 0,
        Alive = 1,
        Ready = 2,
        Busy = 4,
        Grab = 8,
        OK = 16,
        NG = 32,
        Complete = 64,
        All = 0xFFFFFFFF,
    }

    public sealed class PlcInputData
    {
        public int ModelNo { get; set; }
        public int StepNo { get; set; }
        public string SerialNo { get; set; }

        public PlcInputData()
        {
            ModelNo = 0;
            StepNo = 0;
            SerialNo = string.Empty;
        }

        public PlcInputData(PlcInputData other)
        {
            ModelNo = other.ModelNo;
            StepNo = other.StepNo;
            SerialNo = other.SerialNo;
        }
    }
}
