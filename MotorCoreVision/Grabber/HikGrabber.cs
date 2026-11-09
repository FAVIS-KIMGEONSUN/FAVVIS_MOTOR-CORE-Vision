using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using MvCameraControl;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace FAVIS
{
    public sealed class HikGrabber : IGrabber
    {
        private const int GrabTimeoutMilliseconds = 5000;
        private const uint FrameWaitMilliseconds = 100;

        private static readonly object SdkSyncRoot = new object();
        private static int _sdkReferenceCount;

        private readonly object _syncRoot = new object();
        private readonly uint _cameraIpAddress;

        private IDevice _device;
        private CancellationTokenSource _activeGrabCancellation;
        private int _sequenceIndex;
        private float? _exposureTime;
        private bool _sdkAcquired;
        private bool _isDisposed;

        public HikGrabber(string cameraIpAddress)
        {
            _cameraIpAddress = ParseIpAddress(cameraIpAddress);
            AcquireSdk();
            _sdkAcquired = true;
            _exposureTime = 200000;
        }

        public void Configure(int cameraIndex, int sequenceIndex)
        {
            if (cameraIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cameraIndex),
                    "카메라 ID는 0 이상이어야 합니다.");
            }

            if (sequenceIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequenceIndex));
            }

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_activeGrabCancellation != null)
                {
                    throw new InvalidOperationException(
                        "이미 진행 중인 Hikvision Grab 요청이 있습니다.");
                }

                if (_device == null)
                {
                    OpenDevice();
                }

                _sequenceIndex = sequenceIndex;
            }
        }

        public ImageCollection Grab()
        {
            IDevice device;
            int sequenceIndex;
            CancellationTokenSource cancellation;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_device == null)
                {
                    throw new InvalidOperationException("Grabber 설정이 완료되지 않았습니다.");
                }

                if (_activeGrabCancellation != null)
                {
                    throw new InvalidOperationException(
                        "이미 진행 중인 Hikvision Grab 요청이 있습니다.");
                }

                device = _device;
                sequenceIndex = _sequenceIndex > 0 ? _sequenceIndex : 1;
                cancellation = new CancellationTokenSource();
                _activeGrabCancellation = cancellation;
            }

            try
            {
                return GrabFrame(device, sequenceIndex, cancellation.Token);
            }
            finally
            {
                lock (_syncRoot)
                {
                    _activeGrabCancellation = null;
                    cancellation.Dispose();
                    Monitor.PulseAll(_syncRoot);
                }
            }
        }

        public void SetExposureTime(float exposureTime)
        {
            if (float.IsNaN(exposureTime) ||
                float.IsInfinity(exposureTime) ||
                exposureTime <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(exposureTime),
                    "ExposureTime은 0보다 큰 유한 값이어야 합니다.");
            }

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_activeGrabCancellation != null)
                {
                    throw new InvalidOperationException(
                        "Grab 중에는 ExposureTime을 변경할 수 없습니다.");
                }

                if (_device != null)
                {
                    SetExposureTime(_device, exposureTime);
                }

                _exposureTime = exposureTime;
            }
        }

        public void Cancel()
        {
            lock (_syncRoot)
            {
                _activeGrabCancellation?.Cancel();
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

                _activeGrabCancellation?.Cancel();
                while (_activeGrabCancellation != null)
                {
                    Monitor.Wait(_syncRoot);
                }

                _isDisposed = true;
                Exception closeException = null;
                try
                {
                    CloseDevice();
                }
                catch (Exception exception)
                {
                    closeException = exception;
                }

                Exception sdkException = null;
                if (_sdkAcquired)
                {
                    _sdkAcquired = false;
                    try
                    {
                        ReleaseSdk();
                    }
                    catch (Exception exception)
                    {
                        sdkException = exception;
                    }
                }

                if (closeException != null && sdkException != null)
                {
                    throw new AggregateException(
                        "Hikvision 장치 및 SDK 종료 중 오류가 발생했습니다.",
                        closeException,
                        sdkException);
                }

                if (closeException != null)
                {
                    throw closeException;
                }

                if (sdkException != null)
                {
                    throw sdkException;
                }
            }
        }

        private static void AcquireSdk()
        {
            lock (SdkSyncRoot)
            {
                if (_sdkReferenceCount == 0)
                {
                    SDKSystem.Initialize();
                }

                _sdkReferenceCount++;
            }
        }

        private static void ReleaseSdk()
        {
            lock (SdkSyncRoot)
            {
                _sdkReferenceCount--;
                if (_sdkReferenceCount == 0)
                {
                    SDKSystem.Finalize();
                }
            }
        }

        private void OpenDevice()
        {
            var deviceInfoList = new List<IDeviceInfo>();
            EnsureSuccess(
                DeviceEnumerator.EnumDevices(
                    DeviceTLayerType.MvGigEDevice,
                    out deviceInfoList),
                "Hikvision 카메라 검색");

            IDeviceInfo selectedDeviceInfo = null;
            foreach (IDeviceInfo deviceInfo in deviceInfoList)
            {
                IGigEDeviceInfo gigEDeviceInfo = deviceInfo as IGigEDeviceInfo;
                if (gigEDeviceInfo != null &&
                    gigEDeviceInfo.CurrentIp == _cameraIpAddress)
                {
                    selectedDeviceInfo = deviceInfo;
                    break;
                }
            }

            if (selectedDeviceInfo == null)
            {
                throw new InvalidOperationException(
                    $"IP 주소 {FormatIpAddress(_cameraIpAddress)}인 Hikvision GigE 카메라를 찾을 수 없습니다.");
            }

            IDevice device = DeviceFactory.CreateDevice(selectedDeviceInfo);
            bool opened = false;
            try
            {
                EnsureSuccess(device.Open(), "Hikvision 카메라 열기");
                opened = true;
                DefaultParameters(device);

                _device = device;
            }
            catch
            {
                if (opened)
                {
                    device.Close();
                }

                device.Dispose();
                throw;
            }
        }

        private void DefaultParameters(IDevice device)
        {
            EnsureSuccess(
                device.Parameters.SetEnumValueByString("AcquisitionMode", "Continuous"),
                "AcquisitionMode 설정");
            EnsureSuccess(
                device.Parameters.SetEnumValueByString("TriggerMode", "Off"),
                "TriggerMode 설정");
            EnsureSuccess(
                device.Parameters.SetEnumValueByString("ExposureAuto", "Off"),
                "ExposureAuto 설정");
            EnsureSuccess(
                device.Parameters.SetEnumValueByString("GainAuto", "Off"),
                "GainAuto 설정");

            if (_exposureTime.HasValue)
            {
                SetExposureTime(device, _exposureTime.Value);
            }
        }

        private void SetExposureTime(IDevice device, float exposureTime)
        {
            EnsureSuccess(
                device.Parameters.SetEnumValueByString("ExposureAuto", "Off"),
                "ExposureAuto 설정");
            EnsureSuccess(
                device.Parameters.SetFloatValue("ExposureTime", exposureTime),
                "ExposureTime 설정");
        }

        private ImageCollection GrabFrame(
            IDevice device,
            int sequenceIndex,
            CancellationToken cancellationToken)
        {
            EnsureSuccess(device.StreamGrabber.StartGrabbing(), "Hikvision 영상 취득 시작");
            ImageCollection images = null;
            Exception grabException = null;

            try
            {
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    IFrameOut frame;
                    int result = device.StreamGrabber.GetImageBuffer(
                        FrameWaitMilliseconds,
                        out frame);
                    if (result == MvError.MV_OK)
                    {
                        if (frame == null)
                        {
                            throw new InvalidOperationException(
                                "Hikvision 카메라가 빈 프레임을 반환했습니다.");
                        }

                        try
                        {
                            if (frame.Image == null)
                            {
                                throw new InvalidOperationException(
                                    "Hikvision 카메라가 이미지 데이터가 없는 프레임을 반환했습니다.");
                            }

                            using (Bitmap bitmap = frame.Image.ToBitmap())
                            {
                                if (bitmap == null)
                                {
                                    throw new InvalidOperationException(
                                        "Hikvision 프레임을 Bitmap으로 변환하지 못했습니다.");
                                }

                                using (Mat image = BitmapConverter.ToMat(bitmap))
                                {
                                    images = new ImageCollection();
                                    images.Add(
                                        sequenceIndex,
                                        image,
                                        $"Hik_{frame.FrameNum}");
                                }
                            }
                        }
                        finally
                        {
                            if (frame != null)
                            {
                                EnsureSuccess(
                                    device.StreamGrabber.FreeImageBuffer(frame),
                                    "Hikvision 프레임 버퍼 해제");
                            }
                        }

                        break;
                    }

                    if (result != MvError.MV_E_NODATA)
                    {
                        EnsureSuccess(result, "Hikvision 프레임 취득");
                    }

                    if (timer.ElapsedMilliseconds >= GrabTimeoutMilliseconds)
                    {
                        throw new TimeoutException(
                            $"Hikvision 프레임 취득 시간이 {GrabTimeoutMilliseconds}ms를 초과했습니다.");
                    }
                }
            }
            catch (Exception exception)
            {
                grabException = exception;
            }

            Exception stopException = null;
            try
            {
                EnsureSuccess(
                    device.StreamGrabber.StopGrabbing(),
                    "Hikvision 영상 취득 중지");
            }
            catch (Exception exception)
            {
                stopException = exception;
            }

            if (grabException != null)
            {
                images?.Dispose();
                if (stopException != null)
                {
                    throw new AggregateException(
                        "Hikvision 프레임 취득 및 스트림 종료에 실패했습니다.",
                        grabException,
                        stopException);
                }

                ExceptionDispatchInfo.Capture(grabException).Throw();
            }

            if (stopException != null)
            {
                images?.Dispose();
                ExceptionDispatchInfo.Capture(stopException).Throw();
            }

            return images;
        }

        private void CloseDevice()
        {
            if (_device == null)
            {
                return;
            }

            IDevice device = _device;
            _device = null;

            Exception closeException = null;
            try
            {
                EnsureSuccess(device.Close(), "Hikvision 카메라 닫기");
            }
            catch (Exception exception)
            {
                closeException = exception;
            }

            device.Dispose();
            if (closeException != null)
            {
                ExceptionDispatchInfo.Capture(closeException).Throw();
            }
        }

        private static uint ParseIpAddress(string cameraIpAddress)
        {
            IPAddress parsedAddress;
            if (!IPAddress.TryParse(cameraIpAddress, out parsedAddress) ||
                parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new ArgumentException(
                    "카메라 IP 주소는 IPv4 형식이어야 합니다.",
                    nameof(cameraIpAddress));
            }

            byte[] bytes = parsedAddress.GetAddressBytes();
            return ((uint)bytes[0] << 24) |
                ((uint)bytes[1] << 16) |
                ((uint)bytes[2] << 8) |
                bytes[3];
        }

        private static string FormatIpAddress(uint address)
        {
            return $"{(address >> 24) & 0xff}." +
                $"{(address >> 16) & 0xff}." +
                $"{(address >> 8) & 0xff}." +
                $"{address & 0xff}";
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(HikGrabber));
            }
        }

        private static void EnsureSuccess(int result, string operation)
        {
            if (result != MvError.MV_OK)
            {
                throw new InvalidOperationException(
                    $"{operation}에 실패했습니다. SDK 오류 코드: 0x{result:X8}.");
            }
        }
    }
}
