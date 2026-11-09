using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FAVIS
{
    public interface ITickable
    {
        void Tick();
    }

    public sealed class TickManager : IDisposable
    {
        private readonly List<ITickable> _tickables = new List<ITickable>();
        private readonly object _tickablesSync = new object();
        private CancellationTokenSource _cts;
        private Thread _tickThread;
        private readonly int _intervalMs;
        private bool _isRunning;

        public TickManager(int intervalMs = 50)
        {
            _intervalMs = Math.Max(10, intervalMs);
        }

        public void Register(ITickable tickable)
        {
            if (tickable == null)
            {
                throw new ArgumentNullException(nameof(tickable));
            }

            lock (_tickablesSync)
            {
                if (!_tickables.Contains(tickable))
                {
                    _tickables.Add(tickable);
                }
            }
        }

        public void Unregister(ITickable tickable)
        {
            if (tickable == null)
            {
                return;
            }

            lock (_tickablesSync)
            {
                _tickables.Remove(tickable);
            }
        }

        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _tickThread = new Thread(() => TickLoop(_cts.Token))
            {
                IsBackground = false,
                Name = "TickThread",
                Priority = ThreadPriority.AboveNormal
            };
            _tickThread.Start();
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _cts?.Cancel();
            _tickThread?.Join(TimeSpan.FromSeconds(5));
            _isRunning = false;
        }

        private void TickLoop(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Restart();

                ITickable[] snapshot;
                lock (_tickablesSync)
                {
                    snapshot = _tickables.ToArray();
                }

                foreach (var tickable in snapshot)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        tickable.Tick();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"TickManager error - {tickable.GetType().Name}: {ex.Message}");
                    }
                }

                int elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                int sleepMs = Math.Max(0, _intervalMs - elapsedMs);

                if (sleepMs > 0)
                {
                    try
                    {
                        Thread.Sleep(sleepMs);
                    }
                    catch (ThreadInterruptedException)
                    {
                        break;
                    }
                }
            }

            stopwatch.Stop();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
