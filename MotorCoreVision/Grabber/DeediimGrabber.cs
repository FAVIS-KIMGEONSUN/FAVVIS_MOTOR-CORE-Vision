using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using OpenCvSharp;

namespace FAVIS
{
    public sealed class DeediimGrabber : IGrabber
    {
        private readonly object _syncRoot = new object();

        private readonly IReadOnlyList<GrabberConfig> _configurations;
        private GrabberConfig _activeConfiguration;

        private TcpClient _client;
        private NetworkStream _stream;

        private CancellationTokenSource _connectionCancellation;
        private CancellationTokenSource _grabCancellation = new CancellationTokenSource();

        private Task _receiveTask;
        private TaskCompletionSource<ReceiveMessage> _pendingRun;

        private bool _started;

        public event Action<string> MessageSent;
        public event Action<string> MessageReceived;

        public DeediimGrabber(IReadOnlyList<GrabberConfig> configurations)
        {
            _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
            if (_configurations.Count == 0)
            {
                throw new ArgumentException("Grabber 설정이 비어 있습니다.", nameof(configurations));
            }
        }

        public void Configure(int cameraIndex, int sequenceIndex)
        {
            if (cameraIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cameraIndex));
            }

            lock (_syncRoot)
            {
                _activeConfiguration = FindConfiguration(cameraIndex);
                _grabCancellation.Cancel();
                _grabCancellation.Dispose();
                _grabCancellation = new CancellationTokenSource();
            }
        }

        public ImageCollection Grab()
        {
            CancellationToken cancellationToken;
            lock (_syncRoot)
            {
                if (_activeConfiguration == null)
                {
                    throw new InvalidOperationException("Grabber 설정이 완료되지 않았습니다.");
                }

                cancellationToken = _grabCancellation.Token;
            }

            Start();

            var pendingRun = new TaskCompletionSource<ReceiveMessage>();
            lock (_syncRoot)
            {
                if (_pendingRun != null)
                {
                    throw new InvalidOperationException("이미 진행 중인 Deediim Grab 요청이 있습니다.");
                }

                _pendingRun = pendingRun;
            }

            try
            {
                using (cancellationToken.Register(() => pendingRun.TrySetCanceled()))
                {
                    GrabberConfig configuration;
                    lock (_syncRoot)
                    {
                        configuration = _activeConfiguration;
                    }

                    string runMessage = CreateRunMessage(configuration.SensorIndex, configuration.PatternIndex);
                    MessageSent?.Invoke(runMessage);
                    SendMessage(runMessage);

                    int timeout = GetTimeoutMilliseconds();
                    if (!pendingRun.Task.Wait(timeout, cancellationToken))
                    {
                        throw new TimeoutException($"Deediim Grab 응답 시간이 {timeout}ms를 초과했습니다.");
                    }

                    ReceiveMessage response = pendingRun.Task.Result;
                    ValidateRunResponse(response);
                    return ReadSharedMemory(response);
                }
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_pendingRun, pendingRun))
                    {
                        _pendingRun = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_client != null)
                {
                    StopConnection();
                    _client = null;
                }

                _grabCancellation.Cancel();
                _grabCancellation.Dispose();
                _started = false;
            }
        }

        public void Cancel()
        {
            lock (_syncRoot)
            {
                _grabCancellation.Cancel();
            }
        }

        private void Start()
        {
            lock (_syncRoot)
            {
                if (_started && IsConnectionAlive())
                {
                    return;
                }

                StopConnection();
                _client = new TcpClient();
                try
                {
                    Task connectTask = _client.ConnectAsync(
                        _activeConfiguration.IpAddress,
                        _activeConfiguration.Port);
                    int timeout = GetTimeoutMilliseconds();
                    if (!connectTask.Wait(timeout))
                    {
                        throw new TimeoutException(
                            $"Deediim 서버 연결 시간이 {timeout}ms를 초과했습니다.");
                    }

                    _stream = _client.GetStream();
                    _connectionCancellation = new CancellationTokenSource();
                    _started = true;
                    _receiveTask = Task.Run(
                        () => ReceiveLoop(_stream, _connectionCancellation.Token));
                }
                catch
                {
                    _started = false;
                    StopConnection();
                    throw;
                }
            }
        }

        private string CreateRunMessage(int sensorIndex, int patternIndex)
        {
            var message = new
            {
                Func = "Run",
                SensorIndex = Math.Max(0, sensorIndex).ToString(),
                FuncParam = new
                {
                    Process = Math.Max(-1, patternIndex).ToString()
                }
            };

            return JsonConvert.SerializeObject(message);
        }

        private GrabberConfig FindConfiguration(int grabberId)
        {
            foreach (GrabberConfig configuration in _configurations)
            {
                if (configuration != null && configuration.Id == grabberId)
                {
                    return configuration;
                }
            }

            throw new InvalidOperationException(
                $"Grabber ID {grabberId}에 대한 설정을 찾을 수 없습니다.");
        }

        private int GetTimeoutMilliseconds()
        {
            return 10000;
        }

        private void SendMessage(string message)
        {
            lock (_syncRoot)
            {
                if (!IsConnectionAlive())
                {
                    StopConnection();
                    throw new IOException("Deediim 서버 연결이 끊어졌습니다.");
                }

                try
                {
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }
                catch (IOException exception)
                {
                    _started = false;
                    StopConnection();
                    throw new IOException(
                        "Deediim 서버로 메시지를 전송하지 못했습니다.",
                        exception);
                }
                catch (SocketException exception)
                {
                    _started = false;
                    StopConnection();
                    throw new IOException(
                        "Deediim 서버로 메시지를 전송하지 못했습니다.",
                        exception);
                }
            }
        }

        private async Task ReceiveLoop(NetworkStream stream, CancellationToken cancellationToken)
        {
            var receiveBuffer = new byte[4096];
            var jsonBuffer = new StringBuilder();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(
                        receiveBuffer,
                        0,
                        receiveBuffer.Length,
                        cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        throw new IOException("Deediim 서버가 연결을 종료했습니다.");
                    }

                    jsonBuffer.Append(Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead));
                    foreach (string json in ExtractJsonMessages(jsonBuffer))
                    {
                        ReceiveMessage message = DeserializeReceiveMessage(json);
                        if (message != null &&
                            string.Equals(message.Func, "Run", StringComparison.OrdinalIgnoreCase))
                        {
                            MessageReceived?.Invoke(json);
                            OnReceive(message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    HandleConnectionFailure(new IOException("Deediim 수신 작업이 취소되었습니다."));
                }
            }
            catch (IOException exception)
            {
                HandleConnectionFailure(exception);
            }
            catch (SocketException exception)
            {
                HandleConnectionFailure(exception);
            }
            catch (JsonException exception)
            {
                HandleConnectionFailure(exception);
            }
        }

        private void OnReceive(ReceiveMessage message)
        {
            TaskCompletionSource<ReceiveMessage> pendingRun;
            lock (_syncRoot)
            {
                pendingRun = _pendingRun;
            }

            pendingRun?.TrySetResult(message);
        }

        private static ReceiveMessage DeserializeReceiveMessage(string json)
        {
            return JsonConvert.DeserializeObject<ReceiveMessage>(json);
        }

        private static IEnumerable<string> ExtractJsonMessages(StringBuilder buffer)
        {
            int startIndex = -1;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            int consumedLength = 0;

            for (int index = 0; index < buffer.Length; index++)
            {
                char current = buffer[index];
                if (startIndex < 0)
                {
                    if (current == '{')
                    {
                        startIndex = index;
                        depth = 1;
                    }

                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                }
                else if (current == '{')
                {
                    depth++;
                }
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return buffer.ToString(
                            startIndex,
                            index - startIndex + 1);
                        consumedLength = index + 1;
                        startIndex = -1;
                    }
                }
            }

            if (consumedLength > 0)
            {
                buffer.Remove(0, consumedLength);
            }
        }

        private bool IsConnectionAlive()
        {
            try
            {
                return _client != null &&
                    _client.Connected &&
                    !(_client.Client.Poll(0, SelectMode.SelectRead) &&
                      _client.Client.Available == 0);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private void HandleConnectionFailure(Exception exception)
        {
            lock (_syncRoot)
            {
                if (_started)
                {
                    _started = false;
                    StopConnection();
                }

                _pendingRun?.TrySetException(exception);
            }
        }

        private void StopConnection()
        {
            _connectionCancellation?.Cancel();
            _connectionCancellation?.Dispose();
            _connectionCancellation = null;
            _stream?.Close();
            _stream = null;
            _client?.Close();
            _receiveTask = null;
        }

        private static void ValidateRunResponse(ReceiveMessage response)
        {
            if (response == null)
            {
                throw new InvalidOperationException("Deediim Grab 응답이 없습니다.");
            }

            if (!string.Equals(response.Result, "OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Deediim Grab 실패: {response.Result} ({response.ResultCode})");
            }

            if (response.ResultData == null)
            {
                throw new InvalidOperationException("Deediim Grab 결과 데이터가 없습니다.");
            }

            if (response.ResultData.FileName == null ||
                response.ResultData.FileName.Count == 0)
            {
                throw new InvalidOperationException("Deediim Grab 결과에 이미지 목록이 없습니다.");
            }

            if (string.IsNullOrWhiteSpace(response.ResultData.SHM_Address))
            {
                throw new InvalidOperationException("Deediim Grab 결과에 공유 메모리 주소가 없습니다.");
            }

            if (response.ResultData.Width <= 0 || response.ResultData.Height <= 0)
            {
                throw new InvalidOperationException("Deediim Grab 결과의 이미지 크기가 올바르지 않습니다.");
            }

            if (response.ResultData.Channel != 1 && response.ResultData.Channel != 3)
            {
                throw new InvalidOperationException($"지원하지 않는 Deediim 이미지 채널 수입니다: {response.ResultData.Channel}");
            }
        }

        private ImageCollection ReadSharedMemory(ReceiveMessage response)
        {
            var result = new ImageCollection();
            int imageWidth = response.ResultData.Width;
            int imageHeight = response.ResultData.Height;
            int channelCount = response.ResultData.Channel;
            int bytesPerImage = checked(imageWidth * imageHeight * channelCount);
            long requiredCapacity = (long)bytesPerImage * response.ResultData.FileName.Count;

            using (MemoryMappedFile sharedMemory = MemoryMappedFile.OpenExisting(response.ResultData.SHM_Address,MemoryMappedFileRights.Read))
            {
                using (MemoryMappedViewStream capacityStream = sharedMemory.CreateViewStream(0, 0, MemoryMappedFileAccess.Read))
                {
                    if (capacityStream.Capacity < requiredCapacity)
                    {
                        throw new InvalidOperationException($"Deediim 공유 메모리 용량이 이미지 목록보다 작습니다: {response.ResultData.SHM_Address}");
                    }

                    for (int imageIndex = 0; imageIndex < response.ResultData.FileName.Count; imageIndex++)
                    {
                        byte[] buffer = new byte[bytesPerImage];
                        long offset = (long)imageIndex * bytesPerImage;
                        using (MemoryMappedViewStream stream = sharedMemory.CreateViewStream(offset, bytesPerImage, MemoryMappedFileAccess.Read))
                        {
                            int totalRead = 0;
                            while (totalRead < buffer.Length)
                            {
                                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                                if (read == 0)
                                {
                                    throw new InvalidOperationException("Deediim 공유 메모리 이미지 읽기가 중단되었습니다.");
                                }

                                totalRead += read;
                            }
                        }

                        MatType matType = channelCount == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3;
                        using (Mat image = new Mat(imageHeight, imageWidth, matType, buffer))
                        {
                            result.Add(
                                imageIndex + 1,
                                image,
                                response.ResultData.FileName[imageIndex]);
                        }
                    }
                }
            }

            return result;
        }

        private sealed class ReceiveMessage
        {
            public string Result { get; set; }
            public int ResultCode { get; set; }
            public string Func { get; set; }
            public ResultData ResultData { get; set; }
        }

        private sealed class ResultData
        {
            public List<string> FileName { get; set; }
            public string SHM_Address { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Channel { get; set; }
        }
    }
}
