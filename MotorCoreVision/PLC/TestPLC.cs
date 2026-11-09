using System;

namespace FAVIS
{
    public sealed class TestPLC : PlcController
    {
        protected override bool InternalStart()
        {
            return true;
        }

        protected override void InternalStop()
        {

        }

        public override int ReadInput()
        {
            _input.ModelNo = 1;
            return 1;
        }

        public override bool WriteWord(string address, int value)
        {
            return true;
        }

        protected override void WriteOutputWord()
        {

        }

        public override void Dispose()
        {
            Stop();
        }
    }
}
