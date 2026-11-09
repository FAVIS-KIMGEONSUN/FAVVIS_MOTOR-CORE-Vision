using System;
using System.Collections.Generic;

namespace FAVIS
{
    public sealed class CompositeGrabber : IGrabber
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<int, IGrabber> _grabbers =
            new Dictionary<int, IGrabber>();
        private readonly List<IGrabber> _ownedGrabbers =
            new List<IGrabber>();

        private IGrabber _activeGrabber;
        private bool _isDisposed;

        public CompositeGrabber(IReadOnlyList<GrabberConfig> configurations)
        {
            if (configurations == null)
            {
                throw new ArgumentNullException(nameof(configurations));
            }

            var deediimConfigurations = new List<GrabberConfig>();
            try
            {
                foreach (GrabberConfig configuration in configurations)
                {
                    if (configuration == null)
                    {
                        throw new ArgumentException(
                            "Grabber 설정에 null 항목이 포함되어 있습니다.",
                            nameof(configurations));
                    }

                    switch (configuration.Type)
                    {
                        case GrabberType.Deediim:
                            deediimConfigurations.Add(configuration);
                            break;
                        case GrabberType.HikCamera:
                            AddGrabber(
                                configuration.Id,
                                new HikGrabber(configuration.IpAddress));
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"지원하지 않는 Grabber Type입니다: {configuration.Type}.");
                    }
                }

                if (deediimConfigurations.Count > 0)
                {
                    var deediimGrabber = new DeediimGrabber(deediimConfigurations);
                    _ownedGrabbers.Add(deediimGrabber);
                    foreach (GrabberConfig configuration in deediimConfigurations)
                    {
                        AddMapping(configuration.Id, deediimGrabber);
                    }
                }
            }
            catch
            {
                DisposeOwnedGrabbers();
                throw;
            }
        }

        public void Configure(int cameraIndex, int sequenceIndex)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                IGrabber grabber;
                if (!_grabbers.TryGetValue(cameraIndex, out grabber))
                {
                    throw new InvalidOperationException(
                        $"Grabber ID {cameraIndex}에 대한 설정을 찾을 수 없습니다.");
                }

                _activeGrabber = null;
                grabber.Configure(cameraIndex, sequenceIndex);
                _activeGrabber = grabber;
            }
        }

        public ImageCollection Grab()
        {
            IGrabber grabber;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                grabber = _activeGrabber;
            }

            if (grabber == null)
            {
                throw new InvalidOperationException("Grabber 설정이 완료되지 않았습니다.");
            }

            return grabber.Grab();
        }

        public void Cancel()
        {
            lock (_syncRoot)
            {
                _activeGrabber?.Cancel();
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _activeGrabber?.Cancel();
                DisposeOwnedGrabbers();
            }
        }

        private void AddGrabber(int id, IGrabber grabber)
        {
            try
            {
                AddMapping(id, grabber);
                _ownedGrabbers.Add(grabber);
            }
            catch
            {
                grabber.Dispose();
                throw;
            }
        }

        private void AddMapping(int id, IGrabber grabber)
        {
            if (id <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(id),
                    "Grabber ID는 1 이상이어야 합니다.");
            }

            if (_grabbers.ContainsKey(id))
            {
                throw new InvalidOperationException($"Grabber ID {id}가 중복되었습니다.");
            }

            _grabbers.Add(id, grabber);
        }

        private void DisposeOwnedGrabbers()
        {
            List<Exception> exceptions = null;
            for (int index = _ownedGrabbers.Count - 1; index >= 0; index--)
            {
                try
                {
                    _ownedGrabbers[index].Dispose();
                }
                catch (Exception exception)
                {
                    if (exceptions == null)
                    {
                        exceptions = new List<Exception>();
                    }

                    exceptions.Add(exception);
                }
            }

            _ownedGrabbers.Clear();
            _grabbers.Clear();
            _activeGrabber = null;

            if (exceptions != null)
            {
                throw new AggregateException(
                    "하나 이상의 Grabber 종료에 실패했습니다.",
                    exceptions);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(CompositeGrabber));
            }
        }
    }
}
