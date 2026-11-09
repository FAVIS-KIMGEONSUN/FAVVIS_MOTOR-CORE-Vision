using System;
using System.Runtime.InteropServices;

namespace FAVIS
{
    public sealed class MvsLightController : ILightController, IDisposable
    {
        private const int LedChannelCount = 4;
        private const int CCBrightnessMaximum = 300;

        private static readonly object SdkSyncRoot = new object();
        private static bool _sdkInitializationAttempted;
        private static bool _sdkInitialized;
        private static bool _sdkClosed;
        private static int _sdkBoardCount;
        private static int _instanceCount;
        private static Exception _sdkInitializationException;

        private readonly object _syncRoot = new object();
        private readonly int _boardIndex;
        private readonly int _channelIndex;
        private bool _isInitialized;
        private bool _isDisposed;

        public MvsLightController(int boardIndex = 0, int channelIndex = 0)
        {
            if (boardIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(boardIndex));
            }

            if (channelIndex < 0 || channelIndex >= LedChannelCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channelIndex),
                    $"LED 채널은 0~{LedChannelCount - 1} 범위여야 합니다.");
            }

            _boardIndex = boardIndex;
            _channelIndex = channelIndex;
            lock (SdkSyncRoot)
            {
                if (_sdkClosed)
                {
                    throw new InvalidOperationException(
                        "MVS PCIe SDK가 이미 종료되어 controller를 생성할 수 없습니다.");
                }

                _instanceCount++;
            }
        }

        public void On()
        {
            lock (_syncRoot)
            {
                Initialize();
                lock (SdkSyncRoot)
                {
                    if (!NativeMethods.SetLEDOutCh(_boardIndex, _channelIndex, 1))
                    {
                        throw new InvalidOperationException(
                            $"MVS LED 출력 On 실패 (Board={_boardIndex}, Channel={_channelIndex}).");
                    }
                }
            }
        }

        public void Off()
        {
            lock (_syncRoot)
            {
                Initialize();
                lock (SdkSyncRoot)
                {
                    if (!NativeMethods.SetLEDOutCh(_boardIndex, _channelIndex, 0))
                    {
                        throw new InvalidOperationException(
                            $"MVS LED 출력 Off 실패 (Board={_boardIndex}, Channel={_channelIndex}).");
                    }
                }
            }
        }

        public void SetBrightness(int brightness)
        {
            if (brightness < 0 || brightness > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(brightness),
                    "밝기는 0~100 범위여야 합니다.");
            }

            lock (_syncRoot)
            {
                Initialize();
                lock (SdkSyncRoot)
                {
                    int hardwareBrightness = (int)Math.Round(
                        CCBrightnessMaximum * (brightness / 100.0),
                        MidpointRounding.AwayFromZero);

                    if (!NativeMethods.SetLEDCCBrightnessCh(
                        _boardIndex,
                        _channelIndex,
                        hardwareBrightness))
                    {
                        throw new InvalidOperationException(
                            $"MVS CC LED 밝기 설정 실패 (Board={_boardIndex}, " +
                            $"Channel={_channelIndex}, Brightness={brightness}%).");
                    }
                }
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

                Exception outputOffException = null;
                Exception closeException = null;
                lock (SdkSyncRoot)
                {
                    if (_isInitialized && _sdkInitialized && !_sdkClosed)
                    {
                        try
                        {
                            if (!NativeMethods.SetLEDOutCh(_boardIndex, _channelIndex, 0))
                            {
                                outputOffException = new InvalidOperationException(
                                    $"MVS LED 출력 종료 실패 (Board={_boardIndex}, Channel={_channelIndex}).");
                            }
                        }
                        catch (Exception ex)
                        {
                            outputOffException = ex;
                        }
                    }

                    _isInitialized = false;
                    _isDisposed = true;
                    _instanceCount--;

                    if (_instanceCount == 0 && _sdkInitialized && !_sdkClosed)
                    {
                        try
                        {
                            if (!NativeMethods.Close())
                            {
                                closeException = new InvalidOperationException(
                                    "MVS PCIe SDK 종료에 실패했습니다.");
                            }
                        }
                        catch (Exception ex)
                        {
                            closeException = ex;
                        }
                        finally
                        {
                            _sdkClosed = true;
                            _sdkInitialized = false;
                        }
                    }
                }

                if (outputOffException != null && closeException != null)
                {
                    throw new AggregateException(
                        "MVS 조명 종료 및 SDK 정리 중 오류가 발생했습니다.",
                        outputOffException,
                        closeException);
                }

                if (outputOffException != null)
                {
                    throw outputOffException;
                }

                if (closeException != null)
                {
                    throw closeException;
                }
            }
        }

        private void Initialize()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(MvsLightController));
            }

            if (_isInitialized)
            {
                return;
            }

            lock (SdkSyncRoot)
            {
                if (_sdkClosed)
                {
                    throw new InvalidOperationException("MVS PCIe SDK가 이미 종료되었습니다.");
                }

                if (!_sdkInitializationAttempted)
                {
                    _sdkInitializationAttempted = true;
                    try
                    {
                        if (!NativeMethods.Init())
                        {
                            throw new InvalidOperationException(
                                "MVS PCIe SDK 초기화에 실패했습니다.");
                        }

                        _sdkInitialized = true;
                        _sdkBoardCount = NativeMethods.ScanDevices();
                    }
                    catch (Exception ex)
                    {
                        _sdkInitializationException = ex;
                    }
                }

                if (_sdkInitializationException != null)
                {
                    throw new InvalidOperationException(
                        "MVS PCIe SDK 초기화에 실패했습니다.",
                        _sdkInitializationException);
                }

                if (_boardIndex >= _sdkBoardCount)
                {
                    throw new InvalidOperationException(
                        $"MVS Board {_boardIndex}를 찾을 수 없습니다. 감지된 Board 수: {_sdkBoardCount}.");
                }

                if (!NativeMethods.IsCcBoard(_boardIndex))
                {
                    throw new InvalidOperationException(
                        $"MVS Board {_boardIndex}가 CC 보드가 아닙니다.");
                }

                _isInitialized = true;
            }
        }

        private static class NativeMethods
        {
            private const string DllName = "mvedio32pci140.dll";

            [DllImport(DllName, EntryPoint = "MvsPciInit")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool Init();

            [DllImport(DllName, EntryPoint = "MvsPciClose")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool Close();

            [DllImport(DllName, EntryPoint = "MvsPciScanDevices")]
            internal static extern int ScanDevices();

            [DllImport(DllName, EntryPoint = "MvsIsCcBoard")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsCcBoard(int board);

            [DllImport(DllName, EntryPoint = "MvsSetLEDOutCh")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetLEDOutCh(int board, int channel, int value);

            [DllImport(DllName, EntryPoint = "MvsSetLEDCCBrightnessCh")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetLEDCCBrightnessCh(
                int board,
                int channel,
                int value);
        }
    }
}
