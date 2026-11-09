using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Cognex.VisionPro;
using DSProcessingTool.ViewModel;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace FAVIS
{
    public sealed class InspectionService : IDisposable
    {
        private readonly IGrabber _grabber;
        private readonly ILightController _lightController;
        private readonly IReadOnlyList<SequenceConfig> _sequences;
        private readonly string _imageSavePath;
        private readonly bool _saveImages;
        private readonly bool _inspectionPass;
        private readonly ImageSaveMode _imageSaveMode;
        private readonly ImageSaveQueue _imageSaveQueue;

        public InspectionService(
            IGrabber grabber,
            ILightController lightController,
            InspectionConfig configuration,
            bool saveImages)
        {
            _grabber = grabber;
            _lightController = lightController;
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            _sequences = configuration.Sequences ?? throw new ArgumentException(
                "Inspection configuration must contain sequences.",
                nameof(configuration));
            _imageSavePath = configuration.ImageSavePath;
            _saveImages = saveImages;
            _inspectionPass = configuration.InspectionPass;
            _imageSaveMode = configuration.ImageSaveMode;
            if (!Enum.IsDefined(typeof(ImageSaveMode), _imageSaveMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(configuration.ImageSaveMode),
                    _imageSaveMode,
                    "알 수 없는 이미지 저장 방식입니다.");
            }

            _imageSaveQueue = new ImageSaveQueue(SaveQueuedImages);
        }

        public int PendingSaveCount => _imageSaveQueue.PendingCount;
        public bool IsSavingImages => _imageSaveQueue.IsSaving;
        public int SaveFailureCount => _imageSaveQueue.FailureCount;
        public string LastSaveError => _imageSaveQueue.LastError;

        public void Dispose()
        {
            _imageSaveQueue.Dispose();
        }

        public Task<InspectionResult> RunAsync(
            IProgress<InspectionProgress> progress,
            CancellationToken cancellationToken,
            int stepNo,
            string serialNo,
            int modelNo,
            string modelName)
        {
            if (_grabber == null)
            {
                throw new InvalidOperationException(
                    "개발 모드에서 Camera가 비활성화되어 검사를 실행할 수 없습니다.");
            }

            var preparationTimer = Stopwatch.StartNew();
            Report(
                progress,
                InspectionStage.Preparing,
                0,
                new SequenceConfig { Name = "Inspection" },
                "검사를 준비하는 중입니다.");
            LogElapsed(stepNo, 0, "Inspection", "Preparing", preparationTimer);

            // stepNo(TriggerStep)에 매칭되는 모든 Sequence 찾기
            var sequenceGroup = new List<SequenceConfig>();
            for (int index = 0; index < _sequences.Count; index++)
            {
                if (_sequences[index].TriggerStep == stepNo)
                {
                    sequenceGroup.Add(_sequences[index]);
                }
            }

            if (sequenceGroup.Count == 0)
            {
                throw new InvalidOperationException($"StepNo {stepNo}에 해당하는 시퀀스가 없습니다.");
            }

            var sequenceResults = new List<SequenceInspectionResult>();
            BitmapSource lastImage = null;
            bool overallPassed = true;

            try
            {
                // sequenceGroup의 각 Sequence 순차 실행
                for (int groupIndex = 0; groupIndex < sequenceGroup.Count; groupIndex++)
                {
                    var sequence = sequenceGroup[groupIndex];
                    cancellationToken.ThrowIfCancellationRequested();

                    var sequenceTimer = Stopwatch.StartNew();
                    Report(
                        progress,
                        InspectionStage.Capturing,
                        sequence.Index,
                        sequence,
                        "촬영 중입니다.",
                        elapsed: sequenceTimer.Elapsed);

                    var sequenceWorkTimer = Stopwatch.StartNew();
                    if (sequence.ExternalLightIndex > 0 && _lightController == null)
                    {
                        throw new InvalidOperationException(
                            $"시퀀스 '{sequence.Name}'에서 외부 조명 ID " +
                            $"{sequence.ExternalLightIndex}를 사용하지만 조명 controller가 등록되어 있지 않습니다.");
                    }

                    if (sequence.ExternalLightIndex > 0 &&
                        (sequence.ExternalLightBrightness < 0 ||
                         sequence.ExternalLightBrightness > 100))
                    {
                        throw new InvalidOperationException(
                            $"시퀀스 '{sequence.Name}'의 조명 밝기는 0~100 범위여야 합니다.");
                    }

                    var configureTimer = Stopwatch.StartNew();
                    try
                    {
                        _grabber.Configure(sequence.CameraIndex, sequence.Index);
                    }
                    finally
                    {
                        LogElapsed(
                            stepNo,
                            sequence.Index,
                            sequence.Name,
                            "Configure",
                            configureTimer);
                    }

                    ImageCollection capturedImages = null;
                    bool turnOffRequired = false;
                    Exception captureException = null;
                    var captureTimer = Stopwatch.StartNew();
                    try
                    {
                        if (sequence.ExternalLightIndex > 0)
                        {
                            turnOffRequired = true;
                            var lightOnTimer = Stopwatch.StartNew();
                            try
                            {
                                _lightController.SetBrightness(sequence.ExternalLightBrightness);
                                _lightController.On();
                                Thread.Sleep(100); // 조명 안정화 대기
                            }
                            finally
                            {
                                LogElapsed(
                                    stepNo,
                                    sequence.Index,
                                    sequence.Name,
                                    "LightOnAndSettle",
                                    lightOnTimer);
                            }
                        }

                        using (cancellationToken.Register(_grabber.Cancel))
                        {
                            var grabTimer = Stopwatch.StartNew();
                            try
                            {
                                capturedImages = _grabber.Grab();
                            }
                            finally
                            {
                                LogElapsed(
                                    stepNo,
                                    sequence.Index,
                                    sequence.Name,
                                    "Grab",
                                    grabTimer);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        captureException = ex;
                    }
                    finally
                    {
                        LogElapsed(
                            stepNo,
                            sequence.Index,
                            sequence.Name,
                            "Capture",
                            captureTimer);
                    }

                    Exception lightOffException = null;
                    if (turnOffRequired)
                    {
                        var lightOffTimer = Stopwatch.StartNew();
                        try
                        {
                            _lightController.Off();
                        }
                        catch (Exception ex)
                        {
                            lightOffException = ex;
                        }
                        finally
                        {
                            LogElapsed(
                                stepNo,
                                sequence.Index,
                                sequence.Name,
                                "LightOff",
                                lightOffTimer);
                        }
                    }

                    if (captureException != null)
                    {
                        capturedImages?.Dispose();
                        if (lightOffException != null)
                        {
                            throw new AggregateException(
                                $"시퀀스 '{sequence.Name}' 촬영 및 조명 종료에 실패했습니다.",
                                captureException,
                                lightOffException);
                        }

                        ExceptionDispatchInfo.Capture(captureException).Throw();
                    }

                    if (lightOffException != null)
                    {
                        capturedImages?.Dispose();
                        throw new InvalidOperationException(
                            $"시퀀스 '{sequence.Name}' 촬영 후 외부 조명을 끄지 못했습니다.",
                            lightOffException);
                    }

                    if (capturedImages == null)
                    {
                        throw new InvalidOperationException(
                            $"시퀀스 '{sequence.Name}'의 Grabber가 이미지 결과를 반환하지 않았습니다.");
                    }

                    using (ImageCollection images = capturedImages)
                    {
                        if (_saveImages && _imageSaveMode == ImageSaveMode.All)
                        {
                            QueueImagesForSaving(images, serialNo, stepNo, sequence);
                        }

                        ImageFormat image;
                        if (!images.TryGetImage(sequence.Index, out image))
                        {
                            image = images.GetFirstImage();
                        }

                        if (image == null || image.array == null || image.array.Length == 0)
                        {
                            throw new InvalidOperationException("Grab 결과 이미지가 비어 있습니다.");
                        }

                        var imagePreparationTimer = Stopwatch.StartNew();
                        try
                        {
                            using (Mat displayImage = image.ToMat())
                            {
                                lastImage = displayImage.ToBitmapSource();
                                lastImage.Freeze();
                            }
                        }
                        finally
                        {
                            LogElapsed(
                                stepNo,
                                sequence.Index,
                                sequence.Name,
                                "PrepareDisplayImage",
                                imagePreparationTimer);
                        }

                        LogElapsed(
                            stepNo,
                            sequence.Index,
                            sequence.Name,
                            "SequenceWorkTotal",
                            sequenceWorkTimer);

                        Report(
                            progress,
                            InspectionStage.Capturing,
                            sequence.Index,
                            sequence,
                            "촬영 완료",
                            images.Clone(),
                            elapsed: sequenceTimer.Elapsed);

                        Report(
                            progress,
                            InspectionStage.Processing,
                            sequence.Index,
                            sequence,
                            "검사 중입니다.",
                            images.Clone(),
                            elapsed: sequenceTimer.Elapsed);

                        bool? inspectionPassed = null;
                        ImageCollection inspectionDisplayImages = null;
                        ICogRecord displayRecord = null;
                        switch (sequence.OutputType)
                        {
                            case SequenceOutputType.Inspection:
                            case SequenceOutputType.InspectionAndOk:
                            case SequenceOutputType.InspectionAndNg:
                                inspectionPassed = RunInspection(
                                    sequence,
                                    images,
                                    out inspectionDisplayImages,
                                    out displayRecord);
                                break;
                            case SequenceOutputType.ForceOk:
                            case SequenceOutputType.ForceNg:
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(
                                    nameof(sequence.OutputType),
                                    sequence.OutputType,
                                    "알 수 없는 시퀀스 OutputType입니다.");
                        }

                        bool passed;
                        if (_inspectionPass)
                        {
                            passed = true;
                        }
                        else
                        {
                            switch (sequence.OutputType)
                            {
                                case SequenceOutputType.Inspection:
                                    passed = inspectionPassed.Value;
                                    break;
                                case SequenceOutputType.InspectionAndOk:
                                case SequenceOutputType.ForceOk:
                                    passed = true;
                                    break;
                                case SequenceOutputType.InspectionAndNg:
                                case SequenceOutputType.ForceNg:
                                    passed = false;
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException(
                                        nameof(sequence.OutputType),
                                        sequence.OutputType,
                                        "알 수 없는 시퀀스 OutputType입니다.");
                            }
                        }

                        if (_saveImages &&
                            _imageSaveMode != ImageSaveMode.All &&
                            ShouldSaveImage(passed))
                        {
                            QueueImagesForSaving(images, serialNo, stepNo, sequence);
                        }

                        sequenceTimer.Stop();

                        Report(
                            progress,
                            InspectionStage.Completed,
                            sequence.Index,
                            sequence,
                            passed ? "OK" : "NG",
                            displayRecord != null ? (object)displayRecord : (object)(inspectionDisplayImages ?? images.Clone()),
                            passed,
                            sequenceTimer.Elapsed);

                        // 각 Sequence 결과 저장
                        sequenceResults.Add(new SequenceInspectionResult
                        {
                            SequenceIndex = groupIndex,
                            IsPassed = passed
                        });

                        if (!passed)
                        {
                            overallPassed = false;
                        }
                    }
                }

                return Task.FromResult(new InspectionResult
                {
                    DisplayImage = lastImage,
                    IsPassed = overallPassed,
                    ResultText = overallPassed ? "OK" : "NG",
                    SequenceResults = sequenceResults
                });
            }
            catch (Exception)
            {
                throw;
            }
        }

        private bool RunInspection(
            SequenceConfig sequence,
            ImageCollection images,
            out ImageCollection displayImages,
            out ICogRecord record)
        {
            InspectionManager manager = App.GetInspectionManager(_sequences, sequence);
            App.SetInspectionManagerInputImage(manager, images);
            InspectionRunResult result = manager.Run();
            displayImages = (result.DisplayImages?.Count ?? 0) > 0 
                ? new ImageCollection(result.DisplayImages)
                : null;
            record = result.Record;
            return result.IsPassed;
        }

        private void QueueImagesForSaving(
            ImageCollection images,
            string serialNo,
            int triggerStep,
            SequenceConfig sequence)
        {
            var saveTimer = Stopwatch.StartNew();
            try
            {
                _imageSaveQueue.Enqueue(
                    images,
                    serialNo,
                    triggerStep,
                    sequence.Index,
                    sequence.Name,
                    DateTime.Now);
            }
            finally
            {
                LogElapsed(
                    triggerStep,
                    sequence.Index,
                    sequence.Name,
                    "EnqueueSaveImages",
                    saveTimer);
            }
        }

        private bool ShouldSaveImage(bool passed)
        {
            switch (_imageSaveMode)
            {
                case ImageSaveMode.All:
                    return true;
                case ImageSaveMode.None:
                    return false;
                case ImageSaveMode.OkOnly:
                    return passed;
                case ImageSaveMode.NgOnly:
                    return !passed;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(_imageSaveMode),
                        _imageSaveMode,
                        "알 수 없는 이미지 저장 방식입니다.");
            }
        }

        private void SaveQueuedImages(
            ImageCollection images,
            string folderName,
            int triggerStep,
            int sequenceIndex,
            string sequenceName,
            DateTime captureTime)
        {
            var saveTimer = Stopwatch.StartNew();
            try
            {
                SaveImages(images, folderName, sequenceIndex, captureTime);
            }
            finally
            {
                LogElapsed(
                    triggerStep,
                    sequenceIndex,
                    sequenceName,
                    "SaveImagesWorker",
                    saveTimer);
            }
        }

        private void SaveImages(
            ImageCollection images,
            string folderName,
            int stepNo,
            DateTime captureTime)
        {
            string rootPath = ResolvePath(_imageSavePath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new InvalidOperationException("촬영 이미지 저장 폴더가 설정되지 않았습니다.");
            }

            if (string.IsNullOrWhiteSpace(folderName))
            {
                throw new InvalidOperationException("촬영 이미지 저장용 제품 식별자가 없습니다.");
            }

            string directory = Path.Combine(
                rootPath,
                captureTime.ToString("yyyy"),
                captureTime.ToString("MM"),
                captureTime.ToString("dd"),
                SanitizePathPart(folderName),
                stepNo.ToString());
            Directory.CreateDirectory(directory);

            foreach (int imageIndex in images.Indexes)
            {
                ImageFormat image;
                if (!images.TryGetImage(imageIndex, out image))
                {
                    continue;
                }

                string imageName;
                if (!images.TryGetImageName(imageIndex, out imageName) ||
                    string.IsNullOrWhiteSpace(imageName))
                {
                    imageName = "Image";
                }

                imageName = SanitizePathPart(Path.GetFileNameWithoutExtension(imageName));
                if (string.IsNullOrWhiteSpace(imageName))
                {
                    imageName = "Image";
                }

                string filePath = Path.Combine(
                    directory,
                    $"{imageIndex}_{imageName}.png");
                using (Mat mat = image.ToMat())
                {
                    if (!Cv2.ImWrite(filePath, mat))
                    {
                        throw new IOException("촬영 이미지 저장에 실패했습니다: " + filePath);
                    }
                }
            }
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

        private static string SanitizePathPart(string value)
        {
            string result = value;
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(invalidCharacter, '_');
            }

            return result.Trim();
        }

        private void Report(
            IProgress<InspectionProgress> progress,
            InspectionStage stage,
            int sequenceIndex,
            SequenceConfig sequence,
            string message,
            object displayImage = null,     //ImageCollection or ICogRecord
            bool? isPassed = null,
            TimeSpan? elapsed = null)
        {
            progress?.Report(new InspectionProgress
            {
                Stage = stage,
                SequenceIndex = sequenceIndex,
                TotalSequenceCount = _sequences.Count,
                SequenceName = sequence.Name,
                CameraIndex = sequence.CameraIndex,
                DisplayIndex = sequence.DisplayIndex,
                Message = message,
                DisplayImage = displayImage,
                IsPassed = isPassed,
                Elapsed = elapsed ?? TimeSpan.Zero,
                ReportedAt = DateTime.Now
            });
        }

        private static void LogElapsed(
            int stepNo,
            int sequenceIndex,
            string sequenceName,
            string stage,
            Stopwatch timer)
        {
            timer.Stop();
            Debug.WriteLine(
                $"[AutoInspection] TriggerStep={stepNo}, Sequence={sequenceIndex} " +
                $"('{sequenceName}'), {stage}={timer.Elapsed.TotalMilliseconds:F1} ms");
        }
    }
}
