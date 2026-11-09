using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OpenCvSharp;

namespace FAVIS
{
    public sealed class FileGrabber : IGrabber
    {
        private static readonly string[] ImageExtensions =
        {
            ".bmp",
            ".jpg",
            ".jpeg",
            ".png",
            ".tif",
            ".tiff"
        };

        private readonly string _fallbackImagePath;
        private readonly object _syncRoot = new object();
        private int _cameraIndex;
        private int _sequenceIndex;
        private CancellationTokenSource _grabCancellation = new CancellationTokenSource();

        public FileGrabber(string fallbackImagePath)
        {
            _fallbackImagePath = fallbackImagePath;
        }

        public void Configure(int cameraIndex, int sequenceIndex)
        {
            if (cameraIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cameraIndex));
            }

            if (sequenceIndex <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequenceIndex));
            }

            lock (_syncRoot)
            {
                _cameraIndex = cameraIndex;
                _sequenceIndex = sequenceIndex;
                _grabCancellation.Cancel();
                _grabCancellation.Dispose();
                _grabCancellation = new CancellationTokenSource();
            }
        }

        public ImageCollection Grab()
        {
            int sequenceIndex;
            CancellationToken cancellationToken;
            lock (_syncRoot)
            {
                if (_sequenceIndex <= 0)
                {
                    throw new InvalidOperationException("Grabber 설정이 완료되지 않았습니다.");
                }

                sequenceIndex = _sequenceIndex;
                cancellationToken = _grabCancellation.Token;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string imagePath = ResolveImagePath(sequenceIndex);

            using (Mat image = Cv2.ImRead(imagePath, ImreadModes.Color))
            {
                if (image.Empty())
                {
                    throw new InvalidOperationException("검사 이미지를 읽을 수 없습니다: " + imagePath);
                }

                var images = new ImageCollection();
                images.Add(sequenceIndex, image, Path.GetFileName(imagePath));
                return images;
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                _grabCancellation.Cancel();
                _grabCancellation.Dispose();
            }
        }

        public void Cancel()
        {
            lock (_syncRoot)
            {
                _grabCancellation.Cancel();
            }
        }

        private string ResolveImagePath(int sequenceIndex)
        {
            string directory = string.Empty;
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                List<string> files = Directory
                    .EnumerateFiles(directory)
                    .Where(IsImageFile)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int fileIndex = sequenceIndex - 1;
                if (fileIndex >= 0 && fileIndex < files.Count)
                {
                    return files[fileIndex];
                }
            }

            if (!string.IsNullOrWhiteSpace(_fallbackImagePath) && File.Exists(_fallbackImagePath))
            {
                return _fallbackImagePath;
            }

            throw new InvalidOperationException(
                $"시퀀스 인덱스 {sequenceIndex}에서 사용할 테스트 이미지 파일을 찾을 수 없습니다.");
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        private static bool IsImageFile(string path)
        {
            string extension = Path.GetExtension(path);
            return ImageExtensions.Any(item =>
                string.Equals(item, extension, StringComparison.OrdinalIgnoreCase));
        }
    }
}
