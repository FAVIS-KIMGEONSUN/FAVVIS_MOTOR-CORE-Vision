using System;

namespace FAVIS
{
    public abstract class PlcController
    {
        protected readonly object _sync = new object();
        private int _inputWord;
        private int _outputWord;
        
        protected bool _connected;
        
        //PLC
        protected bool _reset;
        protected bool _trigger;
        
        //PC
        protected bool _pcAlive;
        protected bool _ready;
        protected bool _busy;
        protected bool _grab;
        protected bool _ok;
        protected bool _ng;
        protected bool _complete;

        protected PlcInputData _input = new PlcInputData();
        protected PlcConfig _configuration;

        protected int InputWord
        {
            get { lock (_sync) return _inputWord; }
            set { lock (_sync) _inputWord = value; }
        }

        protected int OutputWord
        {
            get { lock (_sync) return _outputWord; }
            set { lock (_sync) _outputWord = value; }
        }

        public virtual bool Trigger
        {
            get { lock (_sync) return _trigger; }
        }

        public virtual bool Reset
        {
            get { lock (_sync) return _reset; }
        }

        public virtual bool Connected
        {
            get { lock (_sync) return _connected; }
        }

        public virtual bool PlcAlive
        {
            get { lock (_sync) return _connected; }
        }

        public virtual bool PcAlive
        {
            get { lock (_sync) return _pcAlive; }
        }

        public virtual bool Ready
        {
            get { lock (_sync) return _ready; }
            set
            {
                lock (_sync)
                {
                    _ready = value;
                }
                WriteOutputWord();
            }
        }

        public virtual bool Busy
        {
            get { lock (_sync) return _busy; }
            set
            {
                lock (_sync)
                {
                    _busy = value;
                }
                WriteOutputWord();
            }
        }

        public virtual bool Ok
        {
            get { lock (_sync) return _ok; }
            set
            {
                lock (_sync)
                {
                    _ok = value;
                }
                WriteOutputWord();
            }
        }

        public virtual bool Ng
        {
            get { lock (_sync) return _ng; }
            set
            {
                lock (_sync)
                {
                    _ng = value;
                }
                WriteOutputWord();
            }
        }

        public virtual bool Complete
        {
            get { lock (_sync) return _complete; }
            set
            {
                lock (_sync)
                {
                    _complete = value;
                }
                WriteOutputWord();
            }
        }

        public virtual bool Grab
        {
            get { lock (_sync) return _grab; }
            set
            {
                lock (_sync)
                {
                    _grab = value;
                }
                WriteOutputWord();
            }
        }

        public virtual PlcInputData Input
        {
            get
            {
                lock (_sync)
                {
                    return new PlcInputData
                    {
                        ModelNo = _input.ModelNo,
                        StepNo = _input.StepNo,
                        SerialNo = _input.SerialNo
                    };
                }
            }
        }

        public void Start(PlcConfig configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            Stop();
            _configuration = configuration;
            lock (_sync)
            {
                _trigger = false;
                _reset = false;
                _ready = false;
                _busy = false;
                _grab = false;
                _ok = false;
                _ng = false;
                _complete = false;
                _input = new PlcInputData();
                _inputWord = 0;
                _outputWord = 0;
            }

            bool connected = InternalStart();
            lock (_sync)
            {
                _connected = connected;
                _pcAlive = connected;
            }
            WriteOutputWord();
        }

        public void Stop()
        {
            InternalStop();
            
            lock (_sync)
            {
                _connected = false;
                _pcAlive = false;
                _trigger = false;
                _reset = false;
                _ready = false;
                _busy = false;
                _grab = false;
                _ok = false;
                _ng = false;
                _complete = false;
                _input = new PlcInputData();
                _inputWord = 0;
                _outputWord = 0;
            }
        }

        protected abstract bool InternalStart();

        protected abstract void InternalStop();

        public virtual void SetTrigger(bool value)
        {
            lock (_sync)
            {
                _trigger = value;
            }
            WritePlcWord();
        }

        public virtual void SetReset(bool value)
        {
            lock (_sync)
            {
                _reset = value;
            }
            WritePlcWord();
        }

        public virtual void SetReady(bool ready)
        {
            Ready = ready;
        }

        public virtual void SetBusy(bool busy)
        {
            Busy = busy;
        }

        public virtual void SetInput(int modelNo, int stepNo, string serialNo)
        {
            lock (_sync)
            {
                _input.ModelNo = modelNo;
                _input.StepNo = stepNo;
                _input.SerialNo = serialNo ?? string.Empty;
            }
        }

        public virtual void WriteBits(PcBits bits)
        {
            lock (_sync)
            {
                _pcAlive = bits.HasFlag(PcBits.Alive);
                _ready = bits.HasFlag(PcBits.Ready);
                _busy = bits.HasFlag(PcBits.Busy);
                _grab = bits.HasFlag(PcBits.Grab);
                _ok = bits.HasFlag(PcBits.OK);
                _ng = bits.HasFlag(PcBits.NG);
                _complete = bits.HasFlag(PcBits.Complete);
            }
            WriteOutputWord();
        }

        public virtual void SetBits(PcBits bits)
        {
            lock (_sync)
            {
                if (bits.HasFlag(PcBits.Alive)) _pcAlive = true;
                if (bits.HasFlag(PcBits.Ready)) _ready = true;
                if (bits.HasFlag(PcBits.Busy)) _busy = true;
                if (bits.HasFlag(PcBits.Grab)) _grab = true;
                if (bits.HasFlag(PcBits.OK)) _ok = true;
                if (bits.HasFlag(PcBits.NG)) _ng = true;
                if (bits.HasFlag(PcBits.Complete)) _complete = true;
            }
            WriteOutputWord();
        }

        public virtual void ClearBits(PcBits bits)
        {
            lock (_sync)
            {
                if (bits.HasFlag(PcBits.Alive)) _pcAlive = false;
                if (bits.HasFlag(PcBits.Ready)) _ready = false;
                if (bits.HasFlag(PcBits.Busy)) _busy = false;
                if (bits.HasFlag(PcBits.Grab)) _grab = false;
                if (bits.HasFlag(PcBits.OK)) _ok = false;
                if (bits.HasFlag(PcBits.NG)) _ng = false;
                if (bits.HasFlag(PcBits.Complete)) _complete = false;
            }
            WriteOutputWord();
        }

        public virtual void ToggleBits(PcBits bits)
        {
            lock (_sync)
            {
                if (bits.HasFlag(PcBits.Alive)) _pcAlive = !_pcAlive;
                if (bits.HasFlag(PcBits.Ready)) _ready = !_ready;
                if (bits.HasFlag(PcBits.Busy)) _busy = !_busy;
                if (bits.HasFlag(PcBits.Grab)) _grab = !_grab;
                if (bits.HasFlag(PcBits.OK)) _ok = !_ok;
                if (bits.HasFlag(PcBits.NG)) _ng = !_ng;
                if (bits.HasFlag(PcBits.Complete)) _complete = !_complete;
            }
            WriteOutputWord();
        }

        public abstract int ReadInput();

        public abstract bool WriteWord(string address, int value);

        protected abstract void WriteOutputWord();

        protected virtual void WritePlcWord()
        {
        }

        public abstract void Dispose();
    }
}
