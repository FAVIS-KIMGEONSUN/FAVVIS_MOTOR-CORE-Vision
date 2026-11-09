using Cognex.VisionPro;
using Cognex.VisionPro.ToolBlock;
using DeeDiimSensors.DSProcessingTool;
using DSProcessingTool.ViewModel;
using OpenCvSharp;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Media3D;

namespace FAVIS
{
    public sealed class InspectionManager : IDisposable
    {
        private bool _isVisionPro;

        private ProcessPathResolver _pathResolver;
        private ImageCollection _inputImage;

        private Cognex.VisionPro.ToolBlock.CogToolBlock _toolBlock;
        private readonly IReadOnlyDictionary<string, CogToolBlock> _tools;
        private readonly IReadOnlyDictionary<string, DSDMProcessing> _dicProcesses;
        private string _sequenceName;

        private readonly Dictionary<string, DSDMProcessing> _processes =
            new Dictionary<string, DSDMProcessing>(StringComparer.Ordinal);
        private DSDMProcessing _maskProcess;
        private DSDMProcessing _holeProcess;
        private readonly List<DSDMProcessing> _globalProcess = new List<DSDMProcessing>();
        private readonly List<DSDMProcessing> _localProcess = new List<DSDMProcessing>();

        private readonly List<ImageCollection> _maskResult = new List<ImageCollection>();
        private readonly List<ImageCollection> _holeResult = new List<ImageCollection>();
        private readonly List<ImageCollection> _globalResult = new List<ImageCollection>();
        private readonly List<ImageCollection> _localResult = new List<ImageCollection>();

        private readonly List<ImageCollection> _maskDisplay = new List<ImageCollection>();
        private readonly List<ImageCollection> _holeDisplay = new List<ImageCollection>();
        private readonly List<ImageCollection> _globalDisplay = new List<ImageCollection>();
        private readonly List<ImageCollection> _localDisplay = new List<ImageCollection>();

        private int _runStarted;
        private bool _isDisposed;
        private bool _isProcessConfigurationValid;

        public IReadOnlyList<ImageCollection> MaskResult => _maskResult.AsReadOnly();
        public IReadOnlyList<ImageCollection> HoleResult => _holeResult.AsReadOnly();
        public IReadOnlyList<ImageCollection> GlobalResult => _globalResult.AsReadOnly();
        public IReadOnlyList<ImageCollection> LocalResult => _localResult.AsReadOnly();
        public IReadOnlyList<ImageCollection> MaskDisplay => _maskDisplay.AsReadOnly();
        public IReadOnlyList<ImageCollection> HoleDisplay => _holeDisplay.AsReadOnly();
        public IReadOnlyList<ImageCollection> GlobalDisplay => _globalDisplay.AsReadOnly();
        public IReadOnlyList<ImageCollection> LocalDisplay => _localDisplay.AsReadOnly();
        public string SequenceName => _sequenceName;

        public ImageCollection InputImage
        {
            get => _inputImage;
            set
            {
                ThrowIfDisposed();
                if (_inputImage != value)
                {
                    ImageCollection previousInputImage = _inputImage;
                    _inputImage = value;
                    previousInputImage?.Dispose();
                    previousInputImage = null;
                }
            }
        }

        public InspectionManager(
            ProcessPathResolver pathResolver,
            ImageCollection images,
            IReadOnlyDictionary<string, CogToolBlock> tools,
            IReadOnlyDictionary<string, DSDMProcessing> dicProcesses,
            string sequenceName)
        {
            _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _dicProcesses = dicProcesses ?? throw new ArgumentNullException(nameof(dicProcesses));
            _sequenceName = sequenceName ?? throw new ArgumentNullException(nameof(sequenceName));
            _inputImage = images;
            _isVisionPro = _pathResolver.IsVisionPro;

            try
            {
                RunOnStaThread(() =>
                {
                    if (_isVisionPro)
                    {
                        UpdateToolBlock();
                        return true;
                    }

                    UpdateProcess();
                    return true;
                });
                _isProcessConfigurationValid = true;
            }
            catch
            {
                _inputImage?.Dispose();
                _inputImage = null;
                throw;
            }
        }

        public void Configure(ProcessPathResolver pathResolver, string sequenceName, bool forceReload = false)
        {
            if (pathResolver == null)
            {
                throw new ArgumentNullException(nameof(pathResolver));
            }

            if (sequenceName == null)
            {
                throw new ArgumentNullException(nameof(sequenceName));
            }

            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _runStarted, 0, 0) != 0)
            {
                throw new InvalidOperationException(
                    "검사 실행 중에는 InspectionManager 설정을 변경할 수 없습니다.");
            }

            if (!forceReload &&
                _isProcessConfigurationValid &&
                string.Equals(_sequenceName, sequenceName, StringComparison.Ordinal) &&
                (!_isVisionPro ||
                    (_tools.TryGetValue(sequenceName, out CogToolBlock cachedTool) &&
                     ReferenceEquals(_toolBlock, cachedTool))) &&
                _processes.All(process =>
                    _dicProcesses.TryGetValue(process.Key, out DSDMProcessing cachedProcess) &&
                    ReferenceEquals(process.Value, cachedProcess)) &&
                AreSameResolver(_pathResolver, pathResolver))
            {
                return;
            }

            RunOnStaThread(() =>
            {
                _isProcessConfigurationValid = false;
                ClearAll(_maskResult);
                ClearAll(_holeResult);
                ClearAll(_globalResult);
                ClearAll(_localResult);
                ClearAll(_maskDisplay);
                ClearAll(_holeDisplay);
                ClearAll(_globalDisplay);
                ClearAll(_localDisplay);
                ClearProcessReferences();

                _pathResolver = pathResolver;
                _sequenceName = sequenceName;
                _isVisionPro = pathResolver.IsVisionPro;
                _toolBlock = null;
                if (_isVisionPro)
                {
                    UpdateToolBlock();
                }
                else
                {
                    UpdateProcess();
                }

                _isProcessConfigurationValid = true;
                return true;
            });
        }

        private static bool AreSameResolver(
            ProcessPathResolver left,
            ProcessPathResolver right)
        {
            return AreSamePaths(left.Mask, right.Mask) &&
                AreSamePaths(left.Hole, right.Hole) &&
                AreSamePaths(left.Global, right.Global) &&
                AreSamePaths(left.Local, right.Local);
        }

        private static bool AreSamePaths(
            IReadOnlyList<ResolvedProcessPath> left,
            IReadOnlyList<ResolvedProcessPath> right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index].Path, right[index].Path, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private void UpdateToolBlock()
        {
            if (!_tools.TryGetValue(_sequenceName, out _toolBlock) || _toolBlock == null)
            {
                throw new InvalidOperationException(
                    $"시퀀스 '{_sequenceName}'의 ToolBlock이 App.tools에 로드되지 않았습니다.");
            }
        }

        /// <summary>
        /// App에서 관리하는 시퀀스의 Process 참조를 연결한다.
        /// </summary>
        /// <exception cref="InvalidOperationException">Mask Process 경로가 설정되지 않은 경우</exception>
        private void UpdateProcess()
        {
            if (_pathResolver.Mask.Count == 0)
            {
                throw new InvalidOperationException("Mask Process 경로가 설정되지 않았습니다.");
            }

            try
            {
                _maskProcess = GetProcess(_pathResolver.Mask[0]);

                foreach (ResolvedProcessPath processPath in _pathResolver.Global)
                {
                    _globalProcess.Add(GetProcess(processPath));
                }

                foreach (ResolvedProcessPath processPath in _pathResolver.Local)
                {
                    _localProcess.Add(GetProcess(processPath));
                }
            }
            catch
            {
                ClearProcessReferences();
                throw;
            }
        }

        public ImageCollection RunMask()
        {
            ThrowIfDisposed();
            return RunOnStaThread(RunMaskCore);
        }

        public ImageCollection RunHole()
        {
            ThrowIfDisposed();
            return RunOnStaThread(RunHoleCore);
        }

        public IReadOnlyList<ImageCollection> RunGlobal()
        {
            ThrowIfDisposed();
            return RunOnStaThread(() =>
            {
                RunGlobalCore();
                return _globalResult.AsReadOnly();
            });
        }

        public ImageCollection RunGlobal(int processIndex)
        {
            ThrowIfDisposed();
            return RunOnStaThread(() => RunGlobalCore(processIndex));
        }

        public IReadOnlyList<ImageCollection> RunLocal()
        {
            ThrowIfDisposed();
            return RunOnStaThread(() =>
            {
                RunLocalCore();
                return _localResult.AsReadOnly();
            });
        }

        public ImageCollection RunLocal(int processIndex)
        {
            ThrowIfDisposed();
            return RunOnStaThread(() => RunLocalCore(processIndex));
        }

        public InspectionRunResult Run()
        {
            ThrowIfDisposed();
            BeginRun();
            try
            {
                return RunOnStaThread(RunCore);
            }
            finally
            {
                EndRun();
            }
        }

        public bool Judge()
        {
            return false;
        }

        public SelectedProcessInspectionResult RunSelected(ProcessPathGroup group, int processIndex)
        {
            ThrowIfDisposed();
            BeginRun();
            try
            {
                return RunOnStaThread(() =>
                {
                    if (_isVisionPro)
                        return RunSelectedToolBlock();
                    return RunSelectedCore(group, processIndex);
                });
            }
            finally
            {
                EndRun();
            }
        }

        private SelectedProcessInspectionResult RunSelectedToolBlock()
        {
            RunToolBlock();

            ICogRecord record = null;

            if (_toolBlock.RunStatus.Result == CogToolResultConstants.Accept)
            {
                var mainRecord = _toolBlock.CreateLastRunRecord();
                if (mainRecord.SubRecords.Count > 0)
                {
                    record = mainRecord.SubRecords[mainRecord.SubRecords.Count - 1];
                }
            }

            return new SelectedProcessInspectionResult(record);
        }

        private bool RunToolBlock()
        {
            UpdateToolBlock();
            if (_inputImage == null || _inputImage.Count == 0)
            {
                throw new InvalidOperationException("ToolBlock 검사 입력 이미지가 비어 있습니다.");
            }

            _toolBlock.Inputs["InputImage"].Value = _inputImage.ToCognexImage();
            _toolBlock.Run();

            bool isPass = _toolBlock.RunStatus.Result == CogToolResultConstants.Accept;

            try
            {
                ArrayList defects = _toolBlock.Outputs["Defects"].Value as ArrayList;
                isPass &= defects != null && defects.Count <= 0;
            }
            catch (Exception)
            {
                isPass = false;
                throw;
            }
            

            return isPass;
        }

        private void BeginRun()
        {
            if (Interlocked.CompareExchange(ref _runStarted, 1, 0) != 0)
            {
                throw new InvalidOperationException("InspectionManager에서 다른 검사가 실행 중입니다.");
            }
        }

        private void EndRun()
        {
            Interlocked.Exchange(ref _runStarted, 0);
        }

        private static TResult RunOnStaThread<TResult>(Func<TResult> operation)
        {
            System.Windows.Threading.Dispatcher dispatcher =
                Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                return dispatcher.CheckAccess()
                    ? operation()
                    : dispatcher.Invoke(operation);
            }

            TResult result = default(TResult);
            Exception runException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = operation();
                }
                catch (Exception ex)
                {
                    runException = ex;
                }
            })
            {
                IsBackground = true,
                Name = "InspectionManager"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (runException != null)
            {
                ExceptionDispatchInfo.Capture(runException).Throw();
            }

            return result;
        }

        private InspectionRunResult RunCore()
        {
            if (_isVisionPro)
            {
                bool passed = RunToolBlock();
                ICogRecord record = null;
                var mainRecrod = _toolBlock?.CreateLastRunRecord();
                if (mainRecrod != null && mainRecrod.SubRecords.Count > 0)
                {
                    record = mainRecrod.SubRecords[mainRecrod.SubRecords.Count - 1];
                }
                return new InspectionRunResult(record, passed);
            }

            RunMaskCore();
            if (_holeProcess != null)
            {
                RunHoleCore();
            }

            RunGlobalCore();
            RunLocalCore();

            ImageCollection finalMaskResult = GetFinalMaskResult();
            List<ImageFormat> maskImages = new List<ImageFormat>(finalMaskResult);
            var processPairs = new List<InspectionProcessPairResult>();
            var finalImages = new List<ImageFormat>();
            var displayImages = new List<ImageFormat>();

            if (_globalProcess.Count == 0)
            {
                finalImages.AddRange(maskImages);
            }

            int rejectCount = 0;

            for (int processIndex = 0; processIndex < _globalProcess.Count; processIndex++)
            {
                ImageCollection localResult = _localResult[processIndex];
                List<ImageFormat> localImages = new List<ImageFormat>(localResult);

                foreach (var imageFormat in localResult)
                {
                    if (imageFormat != null && imageFormat.reult.ROI.Count > 0)
                    {
                        rejectCount += imageFormat.reult.ROI.Count;
                    }
                }

                processPairs.Add(new InspectionProcessPairResult(
                    processIndex,
                    _pathResolver.Local[processIndex].Path,
                    localImages.AsReadOnly()));
                finalImages.AddRange(localImages);

                foreach (int imageIndex in localResult.Indexes)
                {
                    string imageName;
                    if (localResult.TryGetImageName(imageIndex, out imageName) &&
                        imageName.IndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ImageFormat image;
                        if (localResult.TryGetImage(imageIndex, out image))
                        {
                            displayImages.Add(image);
                        }
                    }
                }
            }

            bool isPassed = rejectCount <= 0;
            return new InspectionRunResult(
                maskImages.AsReadOnly(),
                processPairs.AsReadOnly(),
                finalImages.AsReadOnly(),
                displayImages.AsReadOnly(),
                isPassed);
        }

        private SelectedProcessInspectionResult RunSelectedCore(
            ProcessPathGroup group,
            int processIndex)
        {
            switch (group)
            {
                case ProcessPathGroup.Mask:
                    ImageCollection mask = RunMaskCore();
                    return CreateSelectedResult(group, processIndex, mask);

                case ProcessPathGroup.Hole:
                    ImageCollection hole = RunHoleCore();
                    return CreateSelectedResult(group, processIndex, hole);
                case ProcessPathGroup.Global:
                    //RunMaskAndHole();
                    return CreateSelectedResult(
                        group,
                        processIndex,
                        RunGlobalCore(processIndex));
                case ProcessPathGroup.Local:
                    return CreateSelectedResult(
                        group,
                        processIndex,
                        RunLocalCore(processIndex));
                default:
                    throw new ArgumentOutOfRangeException(nameof(group), group, "알 수 없는 Process 그룹입니다.");
            }
        }

        private SelectedProcessInspectionResult CreateSelectedResult(
            ProcessPathGroup group,
            int processIndex,
            ImageCollection images)
        {
            IReadOnlyList<ResolvedProcessPath> paths = _pathResolver[group];
            return new SelectedProcessInspectionResult(
                group,
                processIndex,
                paths[processIndex].Path,
                new List<ImageFormat>(images).AsReadOnly());
        }

        private void RunMaskAndHole()
        {
            RunMaskCore();
            if (_holeProcess != null)
            {
                RunHoleCore();
            }
        }

        private ImageCollection RunMaskCore()
        {
            ClearAll(_maskResult);
            ClearAll(_maskDisplay);
            ClearAll(_globalResult);
            ClearAll(_globalDisplay);
            ClearAll(_localResult);
            ClearAll(_localDisplay);
            List<ImageFormat> outputImages = ExecuteProcess(
                _maskProcess,
                _inputImage.ToList(),
                _pathResolver.Mask[0]);
            ImageCollection result = new ImageCollection(outputImages);

            StoreResult(_maskResult, _maskDisplay, 0, result);
            return result;
        }

        private ImageCollection RunHoleCore()
        {
            ClearAll(_holeResult); ClearAll(_holeDisplay);
            ClearAll(_globalResult); ClearAll(_globalDisplay);
            ClearAll(_localResult); ClearAll(_localDisplay);
            if (_holeProcess == null)
            {
                return null;
            }

            List<ImageFormat> outputImages = ExecuteProcess(
                _holeProcess,
                _inputImage.ToImageFormatList(),
                _pathResolver.Hole[0]);
            ImageCollection result = new ImageCollection(outputImages);

            StoreResult(_holeResult, _holeDisplay, 0, result);
            return result;
        }

        private void RunGlobalCore()
        {
            ValidateResultInput(GetFinalMaskResult(), "Mask/Hole");

            ClearAll(_globalResult);
            ClearAll(_globalDisplay);
            ClearAll(_localResult);
            ClearAll(_localDisplay);

            for (int index = 0; index < _globalProcess.Count; index++)
            {
                RunGlobalCore(index);
            }
        }

        private ImageCollection RunGlobalCore(int processIndex)
        {
            ValidateProcessIndex(processIndex, _globalProcess.Count, nameof(processIndex));
            ImageCollection input = GetFinalMaskResult();
            ClearAt(_globalResult, processIndex); ClearAt(_globalDisplay, processIndex);
            ClearAt(_localResult, processIndex); ClearAt(_localDisplay, processIndex);

            List<ImageFormat> outputImages = ExecuteProcess(
                _globalProcess[processIndex],
                input.ToList(),
                _pathResolver.Global[processIndex]);
            ImageCollection result = new ImageCollection(outputImages);
            StoreResult(_globalResult, _globalDisplay, processIndex, result);
            return result;
        }

        private void RunLocalCore()
        {
            
            ClearAll(_localResult);
            ClearAll(_localDisplay);

            for (int index = 0; index < _globalProcess.Count; index++)
            {
                RunLocalCore(index);
            }
        }

        private ImageCollection RunLocalCore(int processIndex)
        {
            ValidateProcessIndex(processIndex, _localProcess.Count, nameof(processIndex));

            ImageCollection input = GetResultAt(_globalResult, processIndex, $"Local Process {processIndex}를 실행하기 전에 해당 Global Process를 실행해야 합니다.");
            
            ClearAt(_localResult, processIndex); 
            ClearAt(_localDisplay, processIndex);

            List<ImageFormat> outputImages = ExecuteProcess(
                _localProcess[processIndex],
                input.ToList(),
                _pathResolver.Local[processIndex]);
            WriteResultBlobInfo(processIndex, outputImages);
            ImageCollection result = new ImageCollection(outputImages);
            StoreResult(_localResult, _localDisplay, processIndex, result);
            return result;
        }

        private static void WriteResultBlobInfo(
            int processIndex,
            IEnumerable<ImageFormat> resultImages)
        {
            int imageIndex = 0;
            foreach (ImageFormat resultImage in resultImages)
            {
                imageIndex++;

                if (resultImage == null)
                {
                    Debug.WriteLine(
                        $"[RunLocalCore] Process={processIndex}, ImageIndex={imageIndex}, Image=null");
                    continue;
                }

                string imageName = resultImage.ResultName ?? resultImage.Name;
                var blobGroups = resultImage.reult?.ResultBlobInfo;
                if (blobGroups == null)
                {
                    Debug.WriteLine(
                        $"[RunLocalCore] Process={processIndex}, ImageIndex={imageIndex}, " +
                        $"Image={imageName}, ResultBlobInfo=null");
                    continue;
                }

                Debug.WriteLine(
                    $"[RunLocalCore] Process={processIndex}, ImageIndex={imageIndex}, Image={imageName}, " +
                    $"ResultBlobInfo groups={blobGroups.Count}");

                for (int groupIndex = 0; groupIndex < blobGroups.Count; groupIndex++)
                {
                    List<DSProcessingTool.ViewModel.Resultdata.ResultDataInfo> blobGroup =
                        blobGroups[groupIndex];
                    if (blobGroup == null)
                    {
                        continue;
                    }

                    for (int blobIndex = 0; blobIndex < blobGroup.Count; blobIndex++)
                    {
                        DSProcessingTool.ViewModel.Resultdata.ResultDataInfo blob =
                            blobGroup[blobIndex];
                        if (blob == null)
                        {
                            continue;
                        }

                        Debug.WriteLine(
                            $"[RunLocalCore] Process={processIndex}, ImageIndex={imageIndex}, " +
                            $"Image={imageName}, " +
                            $"ResultBlobInfo[{groupIndex}][{blobIndex}]: " +
                            $"ROI=({blob.ROI.X},{blob.ROI.Y},{blob.ROI.Width},{blob.ROI.Height}), " +
                            $"Length={blob.Length}, Thickness={blob.Thickness}, Area={blob.Area}, " +
                            $"Names=[{string.Join(", ", blob.Names ?? new string[0])}], " +
                            $"Min=[{string.Join(", ", blob.Min ?? new int[0])}], " +
                            $"Max=[{string.Join(", ", blob.Max ?? new int[0])}], " +
                            $"Std=[{string.Join(", ", blob.Std ?? new double[0])}], " +
                            $"Avg=[{string.Join(", ", blob.Avg ?? new double[0])}], " +
                            $"Ratio=[{string.Join(", ", blob.Ratio ?? new double[0])}], " +
                            $"ProcessRatio=[{string.Join(", ", blob.ProcessRatio ?? new double[0])}], " +
                            $"WeightedSum=[{string.Join(", ", blob.WeightedSum ?? new double[0])}], " +
                            $"Scalemax=[{string.Join(", ", blob.Scalemax ?? new double[0])}]");
                    }
                }
            }
        }

        private ImageCollection GetFinalMaskResult()
        {
            return GetResultAt(
                _maskResult,
                0,
                "Global/Local Process를 실행하기 전에 Mask Process를 실행해야 합니다.");
        }

        private static List<ImageFormat> CreateProcessInputs(
            ImageCollection images,
            string stageName)
        {
            List<ImageFormat> inputs = images.CopyWithoutDisplayImage();
            if (inputs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{stageName} Process 결과에 다음 단계로 전달할 이미지가 없습니다.");
            }

            return inputs;
        }

        private static List<ImageFormat> ExecuteProcess(
            DSDMProcessing process,
            List<ImageFormat> inputImages,
            ResolvedProcessPath processPath)
        {
            List<ImageFormat> outputImages = null;
            try
            {
                //process.SetImage(inputImages);
                //process.GetProcessCudaMemory(out ulong memory);
                //process.ShowDialog();

                process.AlgorithmRunStart(inputImages, out outputImages, null, null, null, false);
                if (outputImages == null)
                {
                    throw new InvalidOperationException("Process 실행 결과 이미지 목록이 null입니다.");
                }

                return outputImages;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{processPath.Group}[{processPath.Index}] Process 실행 실패: {processPath.Path}",
                    ex);
            }
            finally
            {
                //DisposeImageFormats(inputImages, outputImages);
            }
        }

        private static void DisposeImageFormats(
            IEnumerable<ImageFormat> first,
            IEnumerable<ImageFormat> second = null)
        {
            var disposed = new HashSet<ImageFormat>(ImageFormatReferenceComparer.Instance);
            foreach (IEnumerable<ImageFormat> images in new[] { first, second })
            {
                if (images == null)
                {
                    continue;
                }

                foreach (ImageFormat image in images)
                {
                    if (image != null && disposed.Add(image))
                    {
                        image.Dispose();
                    }
                }
            }
        }

        private static ImageCollection CreateDisplayCollection(ImageCollection result)
        {
            var displayImages = new ImageCollection();
            try
            {
                foreach (int index in result.Indexes)
                {
                    ImageFormat image;
                    string imageName;
                    if (result.TryGetImage(index, out image) &&
                        result.TryGetImageName(index, out imageName) &&
                        imageName.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
                    {
                        displayImages.Add(index, image, imageName);
                    }
                }

                return displayImages;
            }
            catch
            {
                displayImages.Dispose();
                throw;
            }
        }

        private static void StoreResult(
            List<ImageCollection> results,
            List<ImageCollection> displays,
            int index,
            ImageCollection result)
        {
            ImageCollection display = null;
            try
            {
                display = CreateDisplayCollection(result);
                EnsureResultIndex(results, index);
                EnsureResultIndex(displays, index);
                results[index]?.Dispose();
                displays[index]?.Dispose();
                results[index] = result;
                displays[index] = display;
            }
            catch
            {
                result.Dispose();
                display?.Dispose();
                throw;
            }
        }

        private static void EnsureResultIndex(List<ImageCollection> results, int index)
        {
            while (results.Count <= index)
            {
                results.Add(null);
            }
        }

        private static ImageCollection GetResultAt(List<ImageCollection> results, int index, string errorMessage)
        {
            if (index < 0 || index >= results.Count || results[index] == null)
            {
                throw new InvalidOperationException(errorMessage);
            }

            return results[index];
        }

        private static void ClearAt(List<ImageCollection> imageList, int index)
        {
            if (index < imageList.Count)
            {
                imageList[index]?.Dispose();
                imageList[index] = null;
            }
        }

        private static void ClearAll(List<ImageCollection> imageList)
        {
            for (int i = 0; i < imageList.Count; i++)
            {
                ClearAt(imageList, i);
            }
        }

        private static void ValidateResultInput(ImageCollection images, string stageName)
        {
            if (images == null || images.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{stageName} Process 결과가 비어 있어 다음 단계를 실행할 수 없습니다.");
            }
        }

        private static void ValidateProcessIndex(int index, int count, string parameterName)
        {
            if (index < 0 || index >= count)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    index,
                    "선택한 Process 인덱스가 설정 범위를 벗어났습니다.");
            }
        }

        private DSDMProcessing GetProcess(ResolvedProcessPath processPath)
        {
            string key = App.GetProcessKey(_sequenceName, processPath);
            if (!_dicProcesses.TryGetValue(key, out DSDMProcessing process) || process == null)
            {
                throw new InvalidOperationException(
                    $"Process '{key}'가 App.dicProcesses에 로드되지 않았습니다.");
            }

            _processes.Add(key, process);
            return process;
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(InspectionManager));
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            RunOnStaThread(() =>
            {
                if (_isDisposed)
                {
                    return true;
                }

                _isDisposed = true;
                _toolBlock = null;

                ClearAll(_maskResult);
                ClearAll(_holeResult);
                ClearAll(_maskDisplay);
                ClearAll(_holeDisplay);
                ClearAll(_globalResult);
                ClearAll(_globalDisplay);
                ClearAll(_localResult);
                ClearAll(_localDisplay);

                try
                {
                    ClearProcessReferences();
                }
                finally
                {
                    _inputImage?.Dispose();
                    _inputImage = null;
                }

                return true;
            });
        }

        private void ClearProcessReferences()
        {
            _maskProcess = null;
            _holeProcess = null;
            _localProcess.Clear();
            _globalProcess.Clear();
            _processes.Clear();

        }

        private sealed class ImageFormatReferenceComparer : IEqualityComparer<ImageFormat>
        {
            public static readonly ImageFormatReferenceComparer Instance =
                new ImageFormatReferenceComparer();

            public bool Equals(ImageFormat left, ImageFormat right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(ImageFormat image)
            {
                return RuntimeHelpers.GetHashCode(image);
            }
        }

    }

    public sealed class InspectionRunResult
    {
        public IReadOnlyList<ImageFormat> MaskImages { get; }
        public IReadOnlyList<InspectionProcessPairResult> ProcessPairs { get; }
        public IReadOnlyList<ImageFormat> FinalImages { get; }
        public IReadOnlyList<ImageFormat> DisplayImages { get; }
        public bool IsPassed { get; }

        public ICogRecord Record { get; }

        internal InspectionRunResult(
            IReadOnlyList<ImageFormat> maskImages,
            IReadOnlyList<InspectionProcessPairResult> processPairs,
            IReadOnlyList<ImageFormat> finalImages,
            IReadOnlyList<ImageFormat> displayImages,
            bool isPassed)
        {
            MaskImages = maskImages;
            ProcessPairs = processPairs;
            FinalImages = finalImages;
            DisplayImages = displayImages;
            IsPassed = isPassed;
        }

        internal InspectionRunResult(ICogRecord record, bool isPassed)
        {
            Record = record;
            IsPassed = isPassed;
        }
    }

    public sealed class InspectionProcessPairResult
    {
        public int ProcessIndex { get; }
        public string LocalPath { get; }
        public IReadOnlyList<ImageFormat> LocalImages { get; }

        internal InspectionProcessPairResult(
            int processIndex,
            string localPath,
            IReadOnlyList<ImageFormat> localImages)
        {
            ProcessIndex = processIndex;
            LocalPath = localPath;
            LocalImages = localImages;
        }
    }

    public sealed class SelectedProcessInspectionResult
    {
        public ProcessPathGroup Group { get; }
        public int ProcessIndex { get; }
        public string ProcessPath { get; }
        public IReadOnlyList<ImageFormat> Images { get; }
        public ICogRecord Record { get; }

        internal SelectedProcessInspectionResult(
            ProcessPathGroup group,
            int processIndex,
            string processPath,
            IReadOnlyList<ImageFormat> images)
        {
            Group = group;
            ProcessIndex = processIndex;
            ProcessPath = processPath;
            Images = images;
        }

        internal SelectedProcessInspectionResult(ICogRecord record)
        {
            Group = ProcessPathGroup.Global;
            ProcessIndex = 0;
            ProcessPath = string.Empty;
            Images = null;
            Record = record;
        }
    }
}
