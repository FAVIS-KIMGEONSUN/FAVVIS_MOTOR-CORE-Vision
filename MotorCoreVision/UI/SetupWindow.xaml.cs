using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using DeeDiimSensors.DSProcessingTool;
using DSProcessingTool.ViewModel;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Bitmap = System.Drawing.Bitmap;
using Forms = System.Windows.Forms;

namespace FAVIS
{
    public partial class SetupWindow : System.Windows.Window
    {
        private static readonly string[] ImageExtensions =
        {
            ".png",
            ".bmp",
            ".jpg",
            ".jpeg",
            ".tif",
            ".tiff"
        };

        private static readonly Regex IndexedImageNamePattern =
            new Regex(@"^(?<index>\d+)_(?<name>.+)$", RegexOptions.Compiled);

        private static readonly Regex NumberedSequenceNamePattern =
            new Regex(@"^(?<prefix>.*?#)\d+(?<suffix>.*)$", RegexOptions.Compiled);

        private const int MaxLogCount = 500;

        private static readonly string LastLoadedImageFolderFilePath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FAVIS",
                "last-image-folder.txt");

        private readonly InspectionConfig _configuration;
        private readonly IGrabber _grabber;
        private readonly ILightController _lightController;
        private readonly int _modelNo;
        private readonly string _modelName;
        private readonly string _workDir;
        private readonly ObservableCollection<SetupProcessPathItem> _maskPaths =
            new ObservableCollection<SetupProcessPathItem>();
        private readonly ObservableCollection<SetupProcessPathItem> _globalPaths =
            new ObservableCollection<SetupProcessPathItem>();
        private readonly ObservableCollection<SetupProcessPathItem> _localPaths =
            new ObservableCollection<SetupProcessPathItem>();
        private readonly ObservableCollection<string> _logItems =
            new ObservableCollection<string>();
        private ImageCollection _inspectionInputImages;
        private InspectionManager _inspectionManager;
        private string _currentImageFolder;
        private bool _isInspectionRunning;
        private bool _isUpdatingProcessSelection;
        private bool _isClosing;
        private int _waitCursorDepth;
        private Cursor _previousOverrideCursor;
        private readonly Cognex.VisionPro.CogRecordDisplay RecordDisplay;

        public SetupWindow(
            InspectionConfig configuration,
            IGrabber grabber,
            ILightController lightController,
            int modelNo,
            string modelName)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            InitializeComponent();
            RecordDisplay = new Cognex.VisionPro.CogRecordDisplay();
            RecordDisplayHost.Child = RecordDisplay;
            RecordDisplayHost.Loaded += (sender, args) => InitializeRecordDisplay();
            RecordDisplayHost.IsVisibleChanged += (sender, args) => InitializeRecordDisplay();
            if (App.IsDevelopmentMode &&
                !App.IsDevelopmentComponentEnabled(DevelopmentMode.Cognex) &&
                !App.IsDevelopmentComponentEnabled(DevelopmentMode.DeeDiim))
            {
                InspectionButton.IsEnabled = false;
                RunSelectedButton.IsEnabled = false;
                InspectionButton.ToolTip = "개발 모드에서 Cognex와 DeeDiim이 모두 비활성화되어 있습니다.";
                RunSelectedButton.ToolTip = InspectionButton.ToolTip;
            }
            ImageDisplay.ShowImageSize = true;
            _configuration = configuration;
            _grabber = grabber;
            _lightController = lightController;
            _modelNo = modelNo;
            _modelName = modelName;
            _workDir = GetExecutableDirectory();

            MaskListBox.ItemsSource = _maskPaths;
            GlobalListBox.ItemsSource = _globalPaths;
            LocalListBox.ItemsSource = _localPaths;
            LogListBox.ItemsSource = _logItems;
            SequenceListBox.ItemsSource =
                _configuration.Sequences ?? new List<SequenceConfig>();
            string lastLoadedImageFolder = GetLastLoadedImageFolder();
            _currentImageFolder = Directory.Exists(lastLoadedImageFolder)
                ? lastLoadedImageFolder
                : GetConfiguredImageFolder();

            if (SequenceListBox.Items.Count > 0)
            {
                SequenceListBox.SelectedIndex = 0;
            }

            AddLog("Setup 창을 열었습니다.");
        }

        private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }

            const double minimumPanelWidth = 360;
            const double displayPanelGap = 10;
            double displayWidth = Math.Min(
                e.NewSize.Height * 4.0 / 3.0 + displayPanelGap,
                e.NewSize.Width - minimumPanelWidth);
            displayWidth = Math.Max(0, displayWidth);

            ContentGrid.ColumnDefinitions[0].Width = new GridLength(displayWidth);
            ContentGrid.ColumnDefinitions[1].Width =
                new GridLength(Math.Max(0, e.NewSize.Width - displayWidth));
        }

        private void SequenceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sequence = SequenceListBox.SelectedItem as SequenceConfig;
            _maskPaths.Clear();
            _globalPaths.Clear();
            _localPaths.Clear();
            _inspectionManager = null;
            UpdateDisplayVisibility(sequence?.Path);

            if (sequence == null)
            {
                return;
            }

            ProcessPathResolver resolver;
            try
            {
                ProcessPathContext context = CreatePathContext(sequence);
                resolver = new ProcessPathResolver(sequence.Path, context);
                AddResolvedPaths(_maskPaths, resolver[ProcessPathGroup.Mask]);
                AddResolvedPaths(_globalPaths, resolver[ProcessPathGroup.Global]);
                AddResolvedPaths(_localPaths, resolver[ProcessPathGroup.Local]);
            }
            catch (Exception ex)
            {
                _maskPaths.Clear();
                _globalPaths.Clear();
                _localPaths.Clear();
                AddLog("경로 해석 실패 (" + sequence.Name + "): " + ex.Message);
                return;
            }

            try
            {
                CreateInspectionManager(sequence, resolver);
                AddLog("경로를 갱신했습니다: " + sequence.Name);
            }
            catch (Exception ex)
            {
                AddLog("시퀀스 검사 준비 실패 (" + sequence.Name + "): " + ex.Message);
                if (!_isClosing)
                {
                    ShowMessage(
                        "시퀀스 검사기를 준비하지 못했습니다.\n" + ex.Message,
                        MessageBoxImage.Error);
                }
            }
        }

        private void ApplyToSimilarSequencesButton_Click(object sender, RoutedEventArgs e)
        {
            SequenceConfig sourceSequence = SequenceListBox.SelectedItem as SequenceConfig;
            if (sourceSequence == null)
            {
                ShowMessage("전체 적용할 시퀀스를 선택하세요.", MessageBoxImage.Information);
                return;
            }

            if (_modelNo <= 0)
            {
                string message = "모델 번호를 확인할 수 없어 시퀀스 폴더를 찾을 수 없습니다.";
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
                return;
            }

            Match nameMatch = NumberedSequenceNamePattern.Match(sourceSequence.Name ?? string.Empty);
            if (!nameMatch.Success)
            {
                string message = "시퀀스 이름에서 번호 패턴(#숫자)을 찾을 수 없습니다: " +
                    sourceSequence.Name;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Information);
                return;
            }

            List<SequenceConfig> targetSequences = (_configuration.Sequences ??
                    new List<SequenceConfig>())
                .Where(sequence =>
                {
                    if (sequence == null ||
                        string.Equals(sequence.Name, sourceSequence.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    Match targetMatch = NumberedSequenceNamePattern.Match(sequence.Name ?? string.Empty);
                    return targetMatch.Success &&
                        string.Equals(
                            targetMatch.Groups["prefix"].Value,
                            nameMatch.Groups["prefix"].Value,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            targetMatch.Groups["suffix"].Value,
                            nameMatch.Groups["suffix"].Value,
                            StringComparison.OrdinalIgnoreCase);
                })
                .GroupBy(sequence => sequence.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (targetSequences.Count == 0)
            {
                ShowMessage("이름이 유사한 대상 시퀀스를 찾을 수 없습니다.", MessageBoxImage.Information);
                return;
            }

            string modelDirectory = Path.Combine(
                _workDir,
                "Model",
                _modelNo.ToString(CultureInfo.InvariantCulture));
            string sourceDirectory = Path.GetFullPath(
                Path.Combine(modelDirectory, sourceSequence.Name));
            if (!Directory.Exists(sourceDirectory))
            {
                string message = "복사할 원본 폴더가 없습니다: " + sourceDirectory;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
                return;
            }

            List<string> targetDirectories = targetSequences
                .Select(sequence => Path.GetFullPath(Path.Combine(modelDirectory, sequence.Name)))
                .Where(path => !string.Equals(
                    path,
                    sourceDirectory,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            string confirmation = "다음 대상 폴더에 파일을 복사합니다.\n" +
                "대상에 같은 이름의 파일이 있으면 덮어씁니다.\n\n" +
                string.Join("\n", targetDirectories) +
                "\n\n계속하시겠습니까?";
            if (MessageBox.Show(
                    this,
                    confirmation,
                    "전체 적용 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                AddLog("전체 적용을 취소했습니다: " + sourceSequence.Name);
                return;
            }

            BeginWaitCursor();
            try
            {
                int succeededCount = 0;
                foreach (string targetDirectory in targetDirectories)
                {
                    try
                    {
                        int copiedFileCount = CopyDirectoryFiles(sourceDirectory, targetDirectory);
                        succeededCount++;
                        AddLog($"전체 적용 완료 ({copiedFileCount}개 파일): {sourceDirectory} -> {targetDirectory}");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"전체 적용 실패 ({targetDirectory}): {ex.Message}");
                    }
                }

                int failedCount = targetDirectories.Count - succeededCount;
                if (failedCount == 0)
                {
                    ShowMessage(
                        $"전체 적용이 완료되었습니다. 대상 폴더 {succeededCount}개에 복사했습니다.",
                        MessageBoxImage.Information);
                }
                else
                {
                    ShowMessage(
                        $"전체 적용이 일부 실패했습니다. 성공: {succeededCount}개, 실패: {failedCount}개. 자세한 내용은 로그를 확인하세요.",
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                EndWaitCursor();
            }
        }

        private static int CopyDirectoryFiles(string sourceDirectory, string targetDirectory)
        {
            string sourceRoot = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            int copiedFileCount = 0;
            foreach (string sourceFile in Directory.GetFiles(
                sourceDirectory,
                "*",
                SearchOption.AllDirectories))
            {
                string relativePath = sourceFile.Substring(sourceRoot.Length);
                string targetFile = Path.Combine(targetDirectory, relativePath);
                string targetFileDirectory = Path.GetDirectoryName(targetFile);
                Directory.CreateDirectory(targetFileDirectory);
                File.Copy(sourceFile, targetFile, true);
                copiedFileCount++;
            }

            return copiedFileCount;
        }

        private void CreateInspectionManager(
            SequenceConfig sequence,
            ProcessPathResolver pathResolver,
            bool forceReload = false)
        {
            if (pathResolver.IsVisionPro
                ? !App.IsDevelopmentComponentEnabled(DevelopmentMode.Cognex)
                : !App.IsDevelopmentComponentEnabled(DevelopmentMode.DeeDiim))
            {
                return;
            }

            InspectionManager manager =
                App.GetInspectionManager(_configuration.Sequences, sequence);
            if (forceReload)
            {
                App.ReloadToolBlock(sequence, pathResolver);
                App.ReloadProcesses(sequence, pathResolver);
            }

            manager.Configure(pathResolver, sequence.Name, forceReload);
            if (_inspectionInputImages != null)
            {
                App.SetInspectionManagerInputImage(manager, _inspectionInputImages);
            }

            _inspectionManager = manager;
        }

        private void ClearInspectionManagerReference()
        {
            _inspectionManager = null;
        }

        private void RefreshInspectionManagerForCurrentSequence(bool forceReload = false)
        {
            var sequence = SequenceListBox.SelectedItem as SequenceConfig;
            if (sequence == null)
            {
                _inspectionManager = null;
                return;
            }

            var resolver = new ProcessPathResolver(
                sequence.Path,
                CreatePathContext(sequence));
            CreateInspectionManager(sequence, resolver, forceReload);
        }

        private void InitializeRecordDisplay()
        {
            if (!RecordDisplayHost.IsLoaded || !RecordDisplayHost.IsVisible)
            {
                return;
            }

            RecordDisplayHost.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (_isClosing || !RecordDisplayHost.IsVisible)
                    {
                        return;
                    }

                    try
                    {
                        RecordDisplay.CreateControl();
                        RecordDisplay.HorizontalScrollBar = false;
                        RecordDisplay.VerticalScrollBar = false;
                        RecordDisplay.Record = null;
                        RecordDisplay.Image = _inspectionInputImages?.ToCognexImage();
                        RecordDisplay.Fit();
                    }
                    catch (Exception ex)
                    {
                        AddLog("RecordDisplay 초기화 실패: " + ex.Message);
                        ShowMessage("RecordDisplay를 초기화하지 못했습니다.\n" + ex.Message, MessageBoxImage.Error);
                    }
                }));
        }

        private void UpdateDisplayVisibility(string processPath)
        {
            bool useRecordDisplay = !string.IsNullOrWhiteSpace(processPath) &&
                processPath.IndexOf(".vpp", StringComparison.OrdinalIgnoreCase) >= 0;
            RecordDisplayHost.Visibility = useRecordDisplay ? Visibility.Visible : Visibility.Collapsed;
            ImageDisplay.Visibility = useRecordDisplay ? Visibility.Collapsed : Visibility.Visible;
            InitializeRecordDisplay();
        }

        private void ProcessPathListBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_isInspectionRunning || _isUpdatingProcessSelection)
            {
                return;
            }

            var selectedListBox = sender as ListBox;
            if (selectedListBox == null)
            {
                return;
            }

            _isUpdatingProcessSelection = true;
            try
            {
                if (selectedListBox != MaskListBox)
                {
                    MaskListBox.SelectedIndex = -1;
                }

                if (selectedListBox != GlobalListBox)
                {
                    GlobalListBox.SelectedIndex = -1;
                }

                if (selectedListBox != LocalListBox)
                {
                    LocalListBox.SelectedIndex = -1;
                }
            }
            finally
            {
                _isUpdatingProcessSelection = false;
            }

            var selectedPath = selectedListBox.SelectedItem as SetupProcessPathItem;
            var sequence = SequenceListBox.SelectedItem as SequenceConfig;
            UpdateDisplayVisibility(selectedPath?.ProcessPath.Path ?? sequence?.Path);
        }

        private ProcessPathContext CreatePathContext(SequenceConfig sequence)
        {
            return new ProcessPathContext
            {
                ModelName = _modelName,
                ModelNo = _modelNo > 0 ? (int?)_modelNo : null,
                StepNo = sequence.Index,
                StepName = sequence.Name,
                WorkDir = _workDir
            };
        }

        private static string GetExecutableDirectory()
        {
            using (Process currentProcess = Process.GetCurrentProcess())
            {
                string executablePath = currentProcess.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
                }

                return Path.GetDirectoryName(executablePath);
            }
        }

        private static void AddResolvedPaths(
            ObservableCollection<SetupProcessPathItem> destination,
            IReadOnlyList<ResolvedProcessPath> paths)
        {
            foreach (ResolvedProcessPath path in paths)
            {
                destination.Add(new SetupProcessPathItem(path));
            }
        }

        private void ProcessPathListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_isInspectionRunning)
            {
                e.Handled = true;
                return;
            }

            var listBox = sender as ListBox;
            var selectedPath = listBox?.SelectedItem as SetupProcessPathItem;
            var processPath = selectedPath?.ProcessPath;
            if (processPath == null || string.IsNullOrWhiteSpace(processPath.Path))
            {
                return;
            }

            e.Handled = true;
            if (!EnsureProcessFileExists(selectedPath))
            {
                return;
            }

            if (string.Equals(
                Path.GetExtension(processPath.Path),
                ".vpp",
                StringComparison.OrdinalIgnoreCase))
            {
                ShowToolblockEdit(processPath.Path);
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OpenToolEditor(processPath.Path, selectedPath);
                return;
            }

            string openStage = "Check process file";
            DSDMProcessing processEditor = null;
            string errorMessage = null;
            try
            {
                if (!File.Exists(processPath.Path))
                {
                    throw new FileNotFoundException("Process 설정 파일을 찾을 수 없습니다.", processPath.Path);
                }

                ClearInspectionManagerReference();
                openStage = "Create DSDMProcessing";

                bool isInspectionProcess =
                    processPath.Group == ProcessPathGroup.Global ||
                    processPath.Group == ProcessPathGroup.Local;
                ulong size = 0;
                var images = CreateProcessImages();

                processEditor = new DSDMProcessing(false, false);

                openStage = "DLLInitFromPath";
                processEditor.DLLInitFromPath(
                    (int)processPath.Group,
                    processPath.Index,
                    processPath.Path);
                openStage = "SetImage";
                processEditor.SetImage(images);
                processEditor.MemoryOptimize();
                processEditor.GetProcessCudaMemory(out size);

                processEditor.Owner = this;
                processEditor.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                processEditor.WindowState = WindowState.Maximized;
                AddLog("Process 편집창을 열었습니다: " + processPath);
                openStage = "ShowDialog";
                processEditor.ShowDialog();
            }
            catch (Exception ex)
            {
                errorMessage = $"Process Open Error ({openStage}) : {ex.Message}";
            }
            finally
            {
                if (processEditor != null)
                {
                    processEditor.DeleteMemory();
                    processEditor = null;
                }

                RefreshInspectionManagerAfterEditor();
            }

            if (errorMessage != null)
            {
                AddLog(errorMessage);
                ShowMessage(errorMessage, MessageBoxImage.Error);
            }
        }

        private void ShowToolblockEdit(string processPath)
        {
            if (!File.Exists(processPath))
            {
                string message = "툴 편집창을 열지 못했습니다. Process 파일이 존재하지 않습니다: " + processPath;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
                return;
            }

            try
            {
                object loaded = Cognex.VisionPro.CogSerializer.LoadObjectFromFile(processPath);
                var toolBlock = loaded as Cognex.VisionPro.ToolBlock.CogToolBlock;
                if (toolBlock == null)
                {
                    (loaded as IDisposable)?.Dispose();
                    throw new InvalidOperationException(
                        "Process 파일이 CogToolBlock이 아닙니다: " + processPath);
                }

                using (toolBlock)
                {
                    if (_inspectionInputImages != null && _inspectionInputImages.Count > 0)
                    {
                        toolBlock.Inputs["InputImage"].Value = _inspectionInputImages.ToCognexImage();
                        toolBlock.Run();
                    }

                    using (Forms.Form form = new Forms.Form())
                    {
                        var edit = new Cognex.VisionPro.ToolBlock.CogToolBlockEditV2
                        {
                            Name = "ToolBlockEdit",
                            Dock = Forms.DockStyle.Fill,
                            Subject = toolBlock
                        };
                        var button = new Forms.Button
                        {
                            Text = "저장",
                            DialogResult = Forms.DialogResult.OK,
                            Dock = Forms.DockStyle.Bottom,
                            Height = 80
                        };

                        form.Controls.Add(edit);
                        form.Controls.Add(button);
                        form.WindowState = Forms.FormWindowState.Maximized;

                        if (form.ShowDialog() == Forms.DialogResult.OK)
                        {
                            Cognex.VisionPro.CogSerializer.SaveObjectToFile(toolBlock, processPath);
                            AddLog("툴 설정을 저장했습니다: " + processPath);
                            ClearInspectionManagerReference();
                            RefreshInspectionManagerForCurrentSequence(forceReload: true);
                            AddLog("저장된 ToolBlock을 다시 로드했습니다: " + processPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                string message = "ToolBlock 편집 또는 저장 후 검사기 갱신에 실패했습니다.\n" + ex.Message;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
            }
        }

        private void OpenToolEditor(string processPath, SetupProcessPathItem pathItem)
        {
            try
            {
                ClearInspectionManagerReference();
                var editor = new ToolEditWindow(processPath)
                {
                    Owner = this
                };
                if (editor.ShowDialog() == true)
                {
                    pathItem.RefreshExists();
                    AddLog("툴 설정을 저장했습니다: " + processPath);
                }
            }
            catch (Exception ex)
            {
                string message = "툴 편집창을 열지 못했습니다.\n" + ex.Message;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
            }
            finally
            {
                RefreshInspectionManagerAfterEditor();
            }
        }

        private void RefreshInspectionManagerAfterEditor()
        {
            if (_isClosing)
            {
                return;
            }

            try
            {
                RefreshInspectionManagerForCurrentSequence(forceReload: true);
            }
            catch (Exception ex)
            {
                string message = "검사기를 다시 준비하지 못했습니다.\n" + ex.Message;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
            }
        }

        private bool EnsureProcessFileExists(SetupProcessPathItem pathItem)
        {
            pathItem.RefreshExists();
            if (pathItem.Exists)
            {
                return true;
            }

            string targetDirectory = Path.GetDirectoryName(pathItem.ProcessPath.Path);
            var dialog = new OpenFileDialog
            {
                Title = "누락된 Process 파일을 선택하세요.",
                Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                InitialDirectory = Directory.Exists(targetDirectory) ? targetDirectory : _workDir,
                FileName = Path.GetFileName(pathItem.ProcessPath.Path)
            };

            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(targetDirectory))
                {
                    throw new InvalidOperationException("Process 파일의 대상 폴더를 확인할 수 없습니다.");
                }

                Directory.CreateDirectory(targetDirectory);
                File.Copy(dialog.FileName, pathItem.ProcessPath.Path, false);
                pathItem.RefreshExists();
                AddLog($"누락된 Process 파일을 복사했습니다: {dialog.FileName} -> {pathItem.ProcessPath.Path}");
                return true;
            }
            catch (Exception ex)
            {
                pathItem.RefreshExists();
                string message = "Process 파일을 복사하지 못했습니다.\n" + ex.Message;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
                return false;
            }
        }

        private static string AppendCleanupError(string errorMessage, Exception exception)
        {
            string cleanupError = "Process 메모리 정리 실패: " + exception.Message;
            return string.IsNullOrEmpty(errorMessage)
                ? cleanupError
                : errorMessage + "\n" + cleanupError;
        }

        private List<ImageFormat> CreateProcessImages()
        {
            ImageCollection images = ImageDisplay.Source;
            if (images == null)
            {
                return new List<ImageFormat>();
            }

            return images.ToImageFormatList();
        }

        private async void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            var sequence = SequenceListBox.SelectedItem as SequenceConfig;
            if (sequence == null)
            {
                ShowMessage("촬영할 시퀀스를 선택하세요.", MessageBoxImage.Information);
                return;
            }

            BeginWaitCursor();
            CaptureButton.IsEnabled = false;
            try
            {
                if (_grabber == null)
                {
                    throw new InvalidOperationException(
                        "개발 모드에서 Camera가 비활성화되어 촬영할 수 없습니다.");
                }

                _grabber.Configure(sequence.CameraIndex, 0);
                bool useExternalLight = sequence.ExternalLightIndex > 0 &&
                    App.IsDevelopmentComponentEnabled(DevelopmentMode.Light);
                if (useExternalLight && _lightController == null)
                {
                    throw new InvalidOperationException(
                        $"시퀀스 '{sequence.Name}'에서 외부 조명 ID " +
                        $"{sequence.ExternalLightIndex}를 사용하지만 조명 controller가 등록되어 있지 않습니다.");
                }

                if (useExternalLight &&
                    (sequence.ExternalLightBrightness < 0 ||
                     sequence.ExternalLightBrightness > 100))
                {
                    throw new InvalidOperationException(
                        $"시퀀스 '{sequence.Name}'의 조명 밝기는 0~100 범위여야 합니다.");
                }

                ImageCollection images = null;
                bool turnOffRequired = false;
                Exception captureException = null;
                try
                {
                    if (useExternalLight)
                    {
                        turnOffRequired = true;
                        _lightController.SetBrightness(sequence.ExternalLightBrightness);
                        _lightController.On();
                        await Task.Delay(100);
                    }

                    images = await Task.Run(() => _grabber.Grab());
                }
                catch (Exception exception)
                {
                    captureException = exception;
                }

                Exception lightOffException = null;
                if (turnOffRequired)
                {
                    try
                    {
                        _lightController.Off();
                    }
                    catch (Exception exception)
                    {
                        lightOffException = exception;
                    }
                }

                if (captureException != null)
                {
                    images?.Dispose();
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
                    images?.Dispose();
                    throw new InvalidOperationException(
                        $"시퀀스 '{sequence.Name}' 촬영 후 외부 조명을 끄지 못했습니다.",
                        lightOffException);
                }

                if (images == null)
                {
                    throw new InvalidOperationException(
                        $"시퀀스 '{sequence.Name}'의 Grabber가 이미지 결과를 반환하지 않았습니다.");
                }

                if (_isClosing)
                {
                    images.Dispose();
                    return;
                }

                SetInspectionInputImagesAndDisplay(images);
                AddLog("촬영 완료: " + sequence.Name);
            }
            catch (Exception ex)
            {
                AddLog("촬영 실패: " + ex.Message);
                ShowMessage("촬영에 실패했습니다.\n" + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                EndWaitCursor();
                if (!_isClosing)
                {
                    CaptureButton.IsEnabled = true;
                }
            }
        }

        private async void InspectionButton_Click(object sender, RoutedEventArgs e)
        {
            await RunInspectionAsync(false);
        }

        private async void RunSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            await RunInspectionAsync(true);
        }

        private async Task RunInspectionAsync(bool selectedOnly)
        {
            if (App.IsDevelopmentMode &&
                !App.IsDevelopmentComponentEnabled(DevelopmentMode.Cognex) &&
                !App.IsDevelopmentComponentEnabled(DevelopmentMode.DeeDiim))
            {
                ShowMessage(
                    "개발 모드에서 Cognex와 DeeDiim이 모두 비활성화되어 검사를 실행할 수 없습니다.",
                    MessageBoxImage.Information);
                return;
            }

            if (_isInspectionRunning)
            {
                return;
            }

            var sequence = SequenceListBox.SelectedItem as SequenceConfig;
            if (sequence == null)
            {
                ShowMessage("검사할 시퀀스를 선택하세요.", MessageBoxImage.Information);
                return;
            }

            if (!CaptureButton.IsEnabled)
            {
                ShowMessage("촬영이 끝난 후 검사를 실행하세요.", MessageBoxImage.Information);
                return;
            }

            ProcessPathGroup selectedGroup = ProcessPathGroup.Mask;
            int selectedProcessIndex = -1;
            if (selectedOnly)
            {
                bool hasSelectedProcess = TryGetSelectedProcess(
                    out selectedGroup,
                    out selectedProcessIndex,
                    out bool multipleSelections);
                if (multipleSelections || !hasSelectedProcess)
                {
                    ShowMessage("검사할 Process 항목을 하나 선택하세요.", MessageBoxImage.Information);
                    return;
                }
            }

            if (_inspectionInputImages == null || _inspectionInputImages.Count == 0)
            {
                ShowMessage("검사할 이미지가 없습니다. 먼저 촬영하거나 이미지를 불러오세요.", MessageBoxImage.Information);
                return;
            }

            InspectionManager manager = _inspectionManager;
            if (manager == null)
            {
                const string message = "선택한 시퀀스의 검사기가 준비되지 않았습니다.";
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
                return;
            }

            _isInspectionRunning = true;
            InspectionButton.IsEnabled = false;
            RunSelectedButton.IsEnabled = false;
            InspectionButton.Content = "검사 중...";
            CaptureButton.IsEnabled = false;
            SequenceListBox.IsEnabled = false;
            MaskListBox.IsEnabled = false;
            GlobalListBox.IsEnabled = false;
            LocalListBox.IsEnabled = false;

            BeginWaitCursor();
            try
            {
                if (selectedOnly)
                {
                    SelectedProcessInspectionResult result =
                        await Task.Run(() => manager.RunSelected(
                            selectedGroup,
                            selectedProcessIndex));
                    if (result.Record != null)
                    {
                        RecordDisplay.Record = result.Record;
                        RecordDisplay.Fit(false);
                    }
                    else
                    {
                        RecordDisplay.Record = null;
                        ImageCollection resultImages = new ImageCollection(result.Images);
                        if (!_isClosing)
                        {
                            ImageDisplay.Source = resultImages;
                            AddLog(
                                $"선택 Process 검사 완료: {sequence.Name}, {result.Group}, " +
                                $"{result.ProcessPath}, 결과 이미지 {result.Images.Count}개");
                        }
                        else
                        {
                            resultImages.Dispose();
                        }
                    }
                }
                else
                {
                    InspectionRunResult result = await Task.Run(() => manager.Run());
                    if (result.Record != null)
                    {
                        RecordDisplay.Record = result.Record;
                        RecordDisplay.Fit(false);
                    }
                    else
                    {
                        RecordDisplay.Record = null;
                        ImageCollection resultImages = result.FinalImages.Count > 0
                        ? new ImageCollection(result.FinalImages)
                        : null;
                        if (!_isClosing)
                        {
                            ImageDisplay.Source = resultImages;
                            AddLog(
                                $"검사 완료: {sequence.Name}, Global/Local 쌍 " +
                                $"{result.ProcessPairs.Count}개, 최종 이미지 " +
                                $"{result.FinalImages.Count}개");
                        }
                        else
                        {
                            resultImages?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    AddLog("검사 실패: " + ex.Message);
                    ShowMessage("검사에 실패했습니다.\n" + ex.Message, MessageBoxImage.Error);
                }
            }
            finally
            {
                EndWaitCursor();
                _isInspectionRunning = false;
                if (!_isClosing)
                {
                    InspectionButton.IsEnabled = true;
                    RunSelectedButton.IsEnabled = true;
                    InspectionButton.Content = "전체검사";
                    CaptureButton.IsEnabled = true;
                    SequenceListBox.IsEnabled = true;
                    MaskListBox.IsEnabled = true;
                    GlobalListBox.IsEnabled = true;
                    LocalListBox.IsEnabled = true;
                }
            }
        }

        private bool TryGetSelectedProcess(
            out ProcessPathGroup group,
            out int processIndex,
            out bool multipleSelections)
        {
            group = ProcessPathGroup.Mask;
            processIndex = -1;
            multipleSelections = false;

            ListBox[] processListBoxes = { MaskListBox, GlobalListBox, LocalListBox };
            ProcessPathGroup[] processGroups =
            {
                ProcessPathGroup.Mask,
                ProcessPathGroup.Global,
                ProcessPathGroup.Local
            };

            for (int i = 0; i < processListBoxes.Length; i++)
            {
                int selectedIndex = processListBoxes[i].SelectedIndex;
                if (selectedIndex < 0)
                {
                    continue;
                }

                if (processIndex >= 0)
                {
                    multipleSelections = true;
                    return false;
                }

                group = processGroups[i];
                processIndex = selectedIndex;
            }

            if (group == ProcessPathGroup.Mask)
            {
                if (processIndex == 0)
                {
                    return true;
                }
                else if (processIndex == 1)
                {
                    group = ProcessPathGroup.Hole;
                    processIndex = 0;

                    return true;
                }
            }

            return processIndex >= 0;
        }

        private void LoadImagesButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = SelectImageFolder("이미지가 저장된 폴더를 선택하세요.");
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            BeginWaitCursor();
            try
            {
                ImageCollection images = LoadImageCollection(folder, out int skippedFileCount);
                SetInspectionInputImagesAndDisplay(images);
                _currentImageFolder = folder;
                try
                {
                    SaveLastLoadedImageFolder(folder);
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is System.Security.SecurityException)
                {
                    AddLog("이미지 폴더 기본 위치를 저장하지 못했습니다: " + ex.Message);
                    ShowMessage(
                        "이미지를 불러왔지만 다음 실행을 위한 기본 위치는 저장하지 못했습니다.\n" +
                        ex.Message,
                        MessageBoxImage.Warning);
                }

                AddLog($"이미지 {images.Count}개를 불러왔습니다: {folder}");
                if (skippedFileCount > 0)
                {
                    AddLog($"인덱스_이름 형식이 아닌 이미지 {skippedFileCount}개를 건너뛰었습니다.");
                }
            }
            catch (Exception ex)
            {
                AddLog("이미지 불러오기 실패: " + ex.Message);
                ShowMessage("이미지를 불러오지 못했습니다.\n" + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                EndWaitCursor();
            }
        }

        private static ImageCollection LoadImageCollection(string folder, out int skippedFileCount)
        {
            var images = new ImageCollection();
            var indexes = new HashSet<int>();
            skippedFileCount = 0;

            int indexOffset = 0;

            try
            {
                List<string> files = Directory
                    .EnumerateFiles(folder)
                    .Where(IsImageFile)
                    .GroupBy(
                        path => Path.GetFileNameWithoutExtension(path),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderBy(path => string.Equals(
                            Path.GetExtension(path),
                            ".png",
                            StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .First())
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    Match match = IndexedImageNamePattern.Match(fileName);
                    int imageIndex;
                    if (!match.Success ||
                        !int.TryParse(
                            match.Groups["index"].Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out imageIndex) ||
                        imageIndex < 0)
                    {
                        skippedFileCount++;
                        continue;
                    }
                    if (imageIndex == 0)
                    {
                        indexOffset = 1;
                    }
                    imageIndex += indexOffset;

                    if (!indexes.Add(imageIndex))
                    {
                        throw new InvalidDataException(
                            $"이미지 인덱스 {imageIndex}가 중복되었습니다: {folder}");
                    }

                    using (Mat image = new Mat(file, ImreadModes.Unchanged))
                    {
                        if (image.Empty())
                        {
                            throw new InvalidDataException("이미지를 읽을 수 없습니다: " + file);
                        }

                        images.Add(imageIndex, image, match.Groups["name"].Value);
                    }
                }

                if (images.Count == 0)
                {
                    throw new InvalidDataException(
                        "index_name 형식의 이미지를 찾을 수 없습니다. 예: 1_Camera.png");
                }

                return images;
            }
            catch
            {
                images.Dispose();
                throw;
            }
        }

        private void SetInspectionInputImagesAndDisplay(ImageCollection images)
        {
            ImageCollection inputImages;
            try
            {
                inputImages = images.Clone();
            }
            catch
            {
                images.Dispose();
                throw;
            }

            try
            {
                ImageDisplay.Source = images;
            }
            catch
            {
                inputImages.Dispose();
                images.Dispose();
                throw;
            }

            ImageCollection previousInputImages = _inspectionInputImages;
            _inspectionInputImages = inputImages;
            previousInputImages?.Dispose();
            InitializeRecordDisplay();

            try
            {
                RefreshInspectionManagerForCurrentSequence();
            }
            catch (Exception ex)
            {
                string message = "이미지는 준비했지만 검사기를 갱신하지 못했습니다.\n" + ex.Message;
                AddLog(message);
                ShowMessage(message, MessageBoxImage.Error);
            }
        }

        private void SaveImagesButton_Click(object sender, RoutedEventArgs e)
        {
            ImageCollection images = ImageDisplay.Source;
            if (images == null || images.Count == 0)
            {
                ShowMessage("저장할 이미지가 없습니다.", MessageBoxImage.Information);
                return;
            }

            string folder = SelectImageFolder("이미지를 저장할 폴더를 선택하세요.");
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            BeginWaitCursor();
            try
            {
                SaveImageCollection(images, folder);
                _currentImageFolder = folder;
                AddLog($"이미지 {images.Count}개를 저장했습니다: {folder}");
            }
            catch (Exception ex)
            {
                AddLog("이미지 저장 실패: " + ex.Message);
                ShowMessage("이미지를 저장하지 못했습니다.\n" + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                EndWaitCursor();
            }
        }

        private static void SaveImageCollection(ImageCollection images, string folder)
        {
            Directory.CreateDirectory(folder);
            foreach (int imageIndex in images.Indexes.OrderBy(index => index))
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

                string filePath = Path.Combine(folder, $"{imageIndex}_{imageName}.png");
                using (Mat mat = image.ToMat())
                {
                    if (!Cv2.ImWrite(filePath, mat))
                    {
                        throw new IOException("이미지 저장에 실패했습니다: " + filePath);
                    }
                }
            }
        }

        private void OpenExplorerButton_Click(object sender, RoutedEventArgs e)
        {
            string folder = Path.Combine(_workDir, "Model");
            BeginWaitCursor();
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
                AddLog("탐색기를 열었습니다: " + folder);
            }
            catch (Exception ex)
            {
                AddLog("탐색기 실행 실패: " + ex.Message);
                ShowMessage("탐색기를 열지 못했습니다.\n" + ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                EndWaitCursor();
            }
        }

        private void BeginWaitCursor()
        {
            if (_waitCursorDepth++ == 0)
            {
                _previousOverrideCursor = Mouse.OverrideCursor;
                Mouse.OverrideCursor = Cursors.Wait;
            }
        }

        private void EndWaitCursor()
        {
            if (_waitCursorDepth == 0)
            {
                return;
            }

            if (--_waitCursorDepth == 0)
            {
                Mouse.OverrideCursor = _previousOverrideCursor;
                _previousOverrideCursor = null;
            }
        }

        private string SelectImageFolder(string description)
        {
            var dialog = new Forms.FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = true
            };
            if (!string.IsNullOrWhiteSpace(_currentImageFolder) &&
                Directory.Exists(_currentImageFolder))
            {
                dialog.SelectedPath = _currentImageFolder;
            }

            return dialog.ShowDialog() == Forms.DialogResult.OK
                ? dialog.SelectedPath
                : string.Empty;
        }

        private string GetConfiguredImageFolder()
        {
            if (string.IsNullOrWhiteSpace(_configuration.ImageSavePath))
            {
                return string.Empty;
            }

            string path = Path.IsPathRooted(_configuration.ImageSavePath)
                ? _configuration.ImageSavePath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _configuration.ImageSavePath);
            return Directory.Exists(path) ? path : string.Empty;
        }

        private string GetLastLoadedImageFolder()
        {
            try
            {
                return File.Exists(LastLoadedImageFolderFilePath)
                    ? File.ReadAllText(LastLoadedImageFolderFilePath).Trim()
                    : string.Empty;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException)
            {
                AddLog("이전에 불러온 이미지 폴더 위치를 읽지 못했습니다: " + ex.Message);
                return string.Empty;
            }
        }

        private static void SaveLastLoadedImageFolder(string folder)
        {
            string settingsDirectory = Path.GetDirectoryName(LastLoadedImageFolderFilePath);
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(LastLoadedImageFolderFilePath, folder);
        }

        private static bool IsImageFile(string path)
        {
            string extension = Path.GetExtension(path);
            return ImageExtensions.Any(item =>
                string.Equals(item, extension, StringComparison.OrdinalIgnoreCase));
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

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void AddLog(string message)
        {
            _logItems.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            while (_logItems.Count > MaxLogCount)
            {
                _logItems.RemoveAt(_logItems.Count - 1);
            }
        }

        private void ShowMessage(string message, MessageBoxImage image)
        {
            MessageBox.Show(this, message, "Setup", MessageBoxButton.OK, image);
        }

        private void SetupWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;

            ImageDisplay.Source = null;
            RecordDisplayHost.Dispose();
            try
            {
                ClearInspectionManagerReference();
            }
            catch (Exception ex)
            {
                AddLog("검사기 종료 실패: " + ex.Message);
                ShowMessage("검사기를 종료하지 못했습니다.\n" + ex.Message, MessageBoxImage.Error);
            }

            _inspectionInputImages?.Dispose();
            _inspectionInputImages = null;
        }
    }

    public sealed class SetupProcessPathItem : INotifyPropertyChanged
    {
        private bool _exists;

        public ResolvedProcessPath ProcessPath { get; }
        public bool Exists
        {
            get { return _exists; }
            private set
            {
                if (_exists == value)
                {
                    return;
                }

                _exists = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(Exists)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public SetupProcessPathItem(ResolvedProcessPath path)
        {
            ProcessPath = path ?? throw new ArgumentNullException(nameof(path));
            RefreshExists();
        }

        public void RefreshExists()
        {
            Exists = File.Exists(ProcessPath.Path);
        }
    }
}
