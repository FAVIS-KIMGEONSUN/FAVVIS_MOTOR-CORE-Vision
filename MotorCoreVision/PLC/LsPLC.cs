using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using XGCommLib;
using XGCommLibDemo;

namespace FAVIS
{
    public sealed class LsPLC : PlcController, ITickable
    {
        private readonly object _communicationSync = new object();
        private readonly XGCommSocket _client = new XGCommSocket();

        public override bool PlcAlive => GetBit(InputWord, _configuration.PlcAliveBit);
        public override bool Reset => GetBit(InputWord, _configuration.PlcResetBit);
        public override bool Trigger => GetBit(InputWord, _configuration.PlcTriggerBit);

        public override bool PcAlive => GetBit(OutputWord, _configuration.PcAliveBit);
        public override bool Ready
        {
            get => GetBit(OutputWord, _configuration.PcReadyBit);
            set
            {
                lock (_sync)
                {
                    _ready = value;
                }
                WriteOutputWord();
            }
        }

        public override bool Busy
        {
            get => GetBit(OutputWord, _configuration.PcBusyBit);
            set
            {
                lock (_sync)
                {
                    _busy = value;
                }
                WriteOutputWord();
            }
        }

        public override bool Ok
        {
            get => GetBit(OutputWord, _configuration.PcOkBit);
            set
            {
                lock (_sync)
                {
                    _ok = value;
                }
                WriteOutputWord();
            }
        }

        public override bool Ng
        {
            get => GetBit(OutputWord, _configuration.PcNgBit);
            set
            {
                lock (_sync)
                {
                    _ng = value;
                }
                WriteOutputWord();
            }
        }

        public override bool Complete
        {
            get => GetBit(OutputWord, _configuration.PcCompleteBit);
            set
            {
                lock (_sync)
                {
                    _complete = value;
                }

                if (value)
                {
                    WriteRejectWord();
                }

                WriteOutputWord();
            }
        }

        public override bool Grab
        {
            get => GetBit(OutputWord, _configuration.PcGrabBit);
            set
            {
                lock (_sync)
                {
                    _grab = value;
                }
                WriteOutputWord();
            }
        }

        protected override bool InternalStart()
        {
            lock (_communicationSync)
            {
                bool connected = _client.Connect(
                    _configuration.IpAddress,
                    _configuration.Port) == (uint)XGCOMM_FUNC_RESULT.RT_XGCOMM_SUCCESS;
                return connected;
            }
        }

        protected override void InternalStop()
        {
            lock (_communicationSync)
            {
                _client.Disconnect();
            }
        }

        public void Tick()
        {
            Scan();
            TogglePcAlive();
        }

        public override bool WriteWord(string address, int value)
        {
            if (!TryParseAddress(address, out char deviceType, out long offset))
            {
                return false;
            }

            lock (_communicationSync)
            {
                uint result = _client.WriteDataWord(
                    deviceType,
                    offset,
                    1,
                    false,
                    new[] { (ushort)value });
                if (result != (uint)XGCOMM_FUNC_RESULT.RT_XGCOMM_SUCCESS)
                {
                    SetDisconnected();
                    return false;
                }
            }

            return true;
        }

        public override void Dispose()
        {
            Stop();
        }

        protected override void WriteOutputWord()
        {
            PlcConfig configuration = _configuration;
            if (configuration == null)
            {
                return;
            }

            bool pcAlive;
            bool ready;
            bool busy;
            bool grab;
            bool complete;
            bool ok;
            bool ng;
            lock (_sync)
            {
                pcAlive = _pcAlive;
                ready = _ready;
                busy = _busy;
                grab = _grab;
                complete = _complete;
                ok = _ok;
                ng = _ng;
            }

            int value = (pcAlive ? 1 << _configuration.PcAliveBit : 0) |
                        (ready ? 1 << _configuration.PcReadyBit : 0) |
                        (busy ? 1 << _configuration.PcBusyBit : 0) |
                        (grab ? 1 << _configuration.PcGrabBit : 0) |
                        (ok ? 1 << _configuration.PcOkBit : 0) |
                        (ng ? 1 << _configuration.PcNgBit : 0) |
                        (complete ? 1 << _configuration.PcCompleteBit : 0);
            if (!TryParseAddress(configuration.PcAddress, out char deviceType, out long offset))
            {
                return;
            }

            lock (_communicationSync)
            {
                uint result = _client.WriteDataWord(
                    deviceType,
                    offset,
                    1,
                    false,
                    new[] { (ushort)value });
                if (result != (uint)XGCOMM_FUNC_RESULT.RT_XGCOMM_SUCCESS)
                {
                    SetDisconnected();
                    return;
                }
            }

            lock (_sync)
            {
                OutputWord = value;
            }
        }

        protected override void WritePlcWord()
        {
            PlcConfig configuration = _configuration;
            if (configuration == null)
            {
                return;
            }

            int plcWord;
            bool trigger;
            bool reset;
            lock (_sync)
            {
                plcWord = InputWord;
                trigger = _trigger;
                reset = _reset;
            }

            int value = SetBit(plcWord, _configuration.PlcResetBit, reset);
            value = SetBit(value, _configuration.PlcTriggerBit, trigger);

            if (!WriteWord(configuration.PlcAddress, value))
            {
                return;
            }

            lock (_sync)
            {
                InputWord = value;
            }
        }

        private bool GetBit(int word, int bit)
        {
            if (bit < 0 || bit > 15 || _configuration == null)
            {
                return false;
            }

            return word >= 0 && (word & (1 << bit)) != 0;
        }

        private void Scan()
        {
            PlcConfig configuration = _configuration;
            if (!Connected || configuration == null)
            {
                return;
            }

            int plcWord = ReadWord(configuration.PlcAddress);
            int pcWord = ReadWord(configuration.PcAddress);
            int modelNo = ReadWord(configuration.ModelNoAddress);
            int stepNo = ReadWord(configuration.StepNoAddress);
            string serialNo = ReadString(configuration.SerialNoAddress);
            if (modelNo < 0 && stepNo < 0)
            {
                return;
            }

            lock (_sync)
            {
                if (plcWord >= 0)
                {
                    InputWord = plcWord;
                }
                if (pcWord >= 0)
                {
                    OutputWord = pcWord;
                }
                if (modelNo >= 0)
                {
                    _input.ModelNo = modelNo;
                }
                if (stepNo >= 0)
                {
                    _input.StepNo = stepNo;
                }
                _input.SerialNo = serialNo;
            }
        }

        public override int ReadInput()
        {
            Scan();
            return _input.ModelNo;
        }

        private int ReadWord(string address)
        {
            if (!TryParseAddress(address, out char deviceType, out long offset))
            {
                return -1;
            }

            ushort[] values = new ushort[1];
            lock (_communicationSync)
            {
                uint result = _client.ReadDataWord(deviceType, offset, 1, false, values);
                if (result != (uint)XGCOMM_FUNC_RESULT.RT_XGCOMM_SUCCESS)
                {
                    SetDisconnected();
                    return -1;
                }
            }

            return values[0];
        }

        private string ReadString(string address)
        {
            if (!TryParseAddress(address, out char deviceType, out long offset))
            {
                return "EMPTY";
            }

            ushort[] values = new ushort[20];
            lock (_communicationSync)
            {
                uint result = _client.ReadDataWord(deviceType, offset, 20, false, values);
                if (result != (uint)XGCOMM_FUNC_RESULT.RT_XGCOMM_SUCCESS)
                {
                    SetDisconnected();
                    return "EMPTY";
                }
            }

            byte[] bytes = new byte[40];
            for (int i = 0; i < values.Length; i++)
            {
                int index = i * 2;
                ushort word = values[i];
                bytes[index + 0] = (byte)(word & 0xFF);
                bytes[index + 1] = (byte)((word >> 8) & 0xFF);
            }

            return Encoding.ASCII.GetString(bytes).Trim();
        }

        private void WriteRejectWord()
        {
            PlcConfig configuration = _configuration;
            if (configuration == null ||
                string.IsNullOrWhiteSpace(configuration.RejectAddress))
            {
                return;
            }

            bool ng;
            lock (_sync)
            {
                ng = _ng;
            }

            WriteWord(configuration.RejectAddress, ng ? 1 : 0);
        }

        private void TogglePcAlive()
        {
            bool connected;
            lock (_sync)
            {
                connected = _connected;
                if (connected)
                {
                    _pcAlive = !_pcAlive;
                }
            }

            if (connected)
            {
                WriteOutputWord();
            }
        }

        private void SetDisconnected()
        {
            lock (_sync)
            {
                _connected = false;
                _pcAlive = false;
            }
        }

        private static int SetBit(int word, int bit, bool value)
        {
            return value ? word | (1 << bit) : word & ~(1 << bit);
        }

        private static bool TryParseAddress(string address, out char deviceType, out long offset)
        {
            deviceType = '\0';
            offset = 0;
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            Match match = Regex.Match(address.Trim().ToUpperInvariant(), @"^%?([A-Z]+)(\d+)$");
            if (!match.Success || !long.TryParse(match.Groups[2].Value, out offset))
            {
                return false;
            }

            string device = match.Groups[1].Value;
            if (device.Length > 1 &&
                (device.EndsWith("X", StringComparison.Ordinal) ||
                 device.EndsWith("B", StringComparison.Ordinal) ||
                 device.EndsWith("W", StringComparison.Ordinal)))
            {
                device = device.Substring(0, device.Length - 1);
            }

            if (device.Length != 1)
            {
                return false;
            }

            deviceType = device[0];
            return true;
        }
    }
}
