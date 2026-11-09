using System;
using System.Collections.Concurrent;
using System.Threading;

namespace FAVIS
{
    public sealed class ImageSaveQueue : IDisposable
    {
        public const int Capacity = 10;

        private readonly BlockingCollection<SaveJob> _jobs =
            new BlockingCollection<SaveJob>(
                new ConcurrentQueue<SaveJob>(),
                Capacity);
        private readonly Action<ImageCollection, string, int, int, string, DateTime> _save;
        private readonly object _errorLock = new object();
        private readonly Thread _worker;
        private int _isSaving;
        private int _failureCount;
        private int _isDisposed;
        private string _lastError;

        public ImageSaveQueue(
            Action<ImageCollection, string, int, int, string, DateTime> save)
        {
            _save = save ?? throw new ArgumentNullException(nameof(save));
            _worker = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Name = "ImageSaveQueue"
            };
            _worker.Start();
        }

        public int PendingCount => _jobs.Count;
        public bool IsSaving => Interlocked.CompareExchange(ref _isSaving, 0, 0) != 0;
        public int FailureCount => Interlocked.CompareExchange(ref _failureCount, 0, 0);

        public string LastError
        {
            get
            {
                lock (_errorLock)
                {
                    return _lastError;
                }
            }
        }

        public void Enqueue(
            ImageCollection images,
            string folderName,
            int triggerStep,
            int sequenceIndex,
            string sequenceName,
            DateTime captureTime)
        {
            if (images == null)
            {
                throw new ArgumentNullException(nameof(images));
            }

            if (Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0)
            {
                throw new ObjectDisposedException(nameof(ImageSaveQueue));
            }

            var job = new SaveJob(
                images.Clone(),
                folderName,
                triggerStep,
                sequenceIndex,
                sequenceName,
                captureTime);

            try
            {
                _jobs.Add(job);
            }
            catch
            {
                job.Images.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            _jobs.CompleteAdding();
            _worker.Join();
        }

        private void ProcessQueue()
        {
            foreach (SaveJob job in _jobs.GetConsumingEnumerable())
            {
                Interlocked.Exchange(ref _isSaving, 1);
                try
                {
                    _save(
                        job.Images,
                        job.FolderName,
                        job.TriggerStep,
                        job.SequenceIndex,
                        job.SequenceName,
                        job.CaptureTime);
                }
                catch (Exception exception)
                {
                    RecordFailure(job, exception);
                }
                finally
                {
                    try
                    {
                        job.Images.Dispose();
                    }
                    catch (Exception exception)
                    {
                        RecordFailure(job, exception);
                    }

                    Interlocked.Exchange(ref _isSaving, 0);
                }
            }
        }

        private void RecordFailure(SaveJob job, Exception exception)
        {
            Interlocked.Increment(ref _failureCount);
            lock (_errorLock)
            {
                _lastError =
                    $"TriggerStep {job.TriggerStep}, Sequence {job.SequenceIndex} " +
                    $"('{job.SequenceName}'): {exception.Message}";
            }
        }

        private sealed class SaveJob
        {
            public ImageCollection Images { get; }
            public string FolderName { get; }
            public int TriggerStep { get; }
            public int SequenceIndex { get; }
            public string SequenceName { get; }
            public DateTime CaptureTime { get; }

            public SaveJob(
                ImageCollection images,
                string folderName,
                int triggerStep,
                int sequenceIndex,
                string sequenceName,
                DateTime captureTime)
            {
                Images = images;
                FolderName = folderName;
                TriggerStep = triggerStep;
                SequenceIndex = sequenceIndex;
                SequenceName = sequenceName;
                CaptureTime = captureTime;
            }
        }
    }
}
