using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FAVIS
{
    public class MelsecHandler
    {
        private ActUtlType64Lib.IActUtlType64 _melsec;

        public bool Open(int stationNumber)
        {
            Close();

            try
            {
                _melsec = new ActUtlType64Lib.ActUtlType64Class();
                _melsec.ActLogicalStationNumber = stationNumber;
                return _melsec.Open() == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open connection to MELSEC PLC: {ex.Message}");
                return false;
            }
        }

        public void Close()
        {
            if (_melsec != null)
            {
                _melsec.Close();
                _melsec = null;
            }
        }

        public int ReadWord(string device)
        {
            int result = 0;
            int data = 0;

            try
            {
                result = _melsec.ReadDeviceBlock(device, 1, out data);
                return result == 0 ? data : -1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read word from device {device}: {ex.Message}");
                return -1;
            }
        }

        public bool ReadBit(string device, int bit)
        {
            if (bit < 0 || bit > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(bit));
            }

            int word = ReadWord(device);
            return word >= 0 && (word & (1 << bit)) != 0;
        }

        public bool WriteWord(string device, int data)
        {
            try
            {
                return _melsec != null &&
                        _melsec.WriteDeviceBlock(device, 1, ref data) == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write word to device {device}: {ex.Message}");
                return false;
            }
        }
    }
}
