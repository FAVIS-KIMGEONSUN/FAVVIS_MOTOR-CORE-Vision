using System;
namespace FAVIS
{
    public interface IGrabber : IDisposable
    {
        void Configure(int cameraIndex, int sequenceIndex);
        ImageCollection Grab();
        void Cancel();
    }
}
