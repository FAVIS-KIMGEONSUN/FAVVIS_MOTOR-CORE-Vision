using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.RightsManagement;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Cognex.VisionPro;
using Cognex.VisionPro.ToolBlock;
using DSProcessingTool.ViewModel;
using DeeDiimSensors.DSProcessingTool;
using OpenCvSharp;

namespace FAVIS
{
    [Flags]
    public enum DevelopmentMode
    {
        None = 0,
        DeeDiim = 1,
        Cognex = 2,
        Light = 4,
        PLC = 8,
        Camera = 16,
        All = DeeDiim | Cognex | Light | PLC | Camera

    }

    public partial class App : Application
    {
        public static bool IsDevelopmentMode;
        public static string RestartArguments { get; private set; } = string.Empty;
        public static DevelopmentMode CurrentDevelopmentMode = DevelopmentMode.None;
        public static List<InspectionManager> InspectionManagers { get; } =
            new List<InspectionManager>();
        public static Dictionary<string, CogToolBlock> tools { get; } =
            new Dictionary<string, CogToolBlock>(StringComparer.Ordinal);
        public static Dictionary<string, DSDMProcessing> dicProcesses { get; } =
            new Dictionary<string, DSDMProcessing>(StringComparer.Ordinal);

        private static Dictionary<int, (int width, int height, int count)> dicCamSize = new Dictionary<int, (int width, int height, int count)>();

        private SplashWindow _splashWindow;

        private const string SurfFinderViewerPath =
            @"C:\Program Files\DeeDiimSensors\SurfFinder\Bin\SurfFinder-Viewer.exe";

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumWindowsProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override async void OnStartup(StartupEventArgs e)
        {
            if (Array.Exists(e.Args, argument =>
                string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    "MotorCoreVision.exe [옵션]\n\n" +
                    "옵션 없음: 일반 모드로 모든 기능을 사용합니다.\n" +
                    "--include <항목>: 개발 모드에서 지정한 항목만 켭니다.\n" +
                    "--exclude <항목>: 개발 모드에서 지정한 항목만 끕니다.\n" +
                    "--setup: --include None과 동일하며 메인 화면만 엽니다.\n" +
                    "--help: 사용법을 표시하고 종료합니다.\n\n" +
                    "항목 (쉼표로 구분, 대소문자 무시):\n" +
                    "DeeDiim: DSDM 처리 초기화\n" +
                    "Cognex: ToolBlock 초기화\n" +
                    "Light: 외부 조명 사용\n" +
                    "PLC: PLC 연결\n" +
                    "Camera: DeediimGrabber / HikGrabber 사용\n" +
                    "All: 모든 항목 / None: 항목 없음\n\n" +
                    "예:\n" +
                    "MotorCoreVision.exe --include Cognex,Camera\n" +
                    "MotorCoreVision.exe --exclude PLC,Light\n" +
                    "MotorCoreVision.exe --setup\n\n" +
                    "--setup, --include, --exclude는 동시에 지정할 수 없습니다.\n" +
                    "All과 None은 단독으로 지정해야 합니다.\n" +
                    "System 설정 변경 후 재시작에도 선택한 옵션이 유지됩니다.",
                    "MotorCoreVision 사용법",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            try
            {
                ConfigureDevelopmentMode(e.Args);
            }
            catch (ArgumentException exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "실행 인수 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            base.OnStartup(e);

            if (!IsDevelopmentMode || IsDevelopmentComponentEnabled(DevelopmentMode.DeeDiim))
            {
                _splashWindow = new SplashWindow();
                _splashWindow.Show();
                ReportStartupProgress(5, "프로그램을 시작하는 중입니다.");

                await EnsureSurfFinderViewerReadyAsync();
                ReportStartupProgress(20, "SurfFinder 초기화가 완료되었습니다.");
            }

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
            _splashWindow?.Close();
            _splashWindow = null;
            ActivateMainWindow(mainWindow);
        }

        private static void ConfigureDevelopmentMode(string[] arguments)
        {
            string option = null;
            DevelopmentMode mode = DevelopmentMode.None;
            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];
                bool setup = string.Equals(argument, "--setup", StringComparison.OrdinalIgnoreCase);
                bool include = string.Equals(argument, "--include", StringComparison.OrdinalIgnoreCase);
                bool exclude = string.Equals(argument, "--exclude", StringComparison.OrdinalIgnoreCase);
                if (!setup && !include && !exclude)
                {
                    continue;
                }

                if (option != null)
                {
                    throw new ArgumentException(
                        "--setup, --include, --exclude 중 하나만 한 번 지정할 수 있습니다.");
                }

                option = setup ? "--setup" : include ? "--include" : "--exclude";
                if (setup)
                {
                    continue;
                }

                if (++index >= arguments.Length)
                {
                    throw new ArgumentException(
                        $"{option} 뒤에 사용할 항목을 지정하세요. 예: {option} PLC,Light");
                }

                string[] names = arguments[index].Split(',');
                foreach (string name in names)
                {
                    string trimmedName = name.Trim();
                    if (!Enum.GetNames(typeof(DevelopmentMode)).Any(
                        knownName => string.Equals(
                            knownName, trimmedName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new ArgumentException(
                            $"알 수 없는 DevelopmentMode 항목: '{trimmedName}'\n" +
                            "사용 가능: DeeDiim, Cognex, Light, PLC, Camera, All, None");
                    }

                    DevelopmentMode parsedMode =
                        (DevelopmentMode)Enum.Parse(typeof(DevelopmentMode), trimmedName, true);
                    if (names.Length > 1 &&
                        (parsedMode == DevelopmentMode.None || parsedMode == DevelopmentMode.All))
                    {
                        throw new ArgumentException("All과 None은 다른 항목과 함께 지정할 수 없습니다.");
                    }

                    mode |= parsedMode;
                }
            }

            if (option == null)
            {
                return;
            }

            IsDevelopmentMode = true;
            CurrentDevelopmentMode = option == "--exclude"
                ? DevelopmentMode.All & ~mode
                : mode;
            RestartArguments = option == "--setup"
                ? option
                : option + " " + mode.ToString().Replace(" ", string.Empty);
        }

        public static bool IsDevelopmentComponentEnabled(DevelopmentMode component)
        {
            return !IsDevelopmentMode ||
                (CurrentDevelopmentMode & component) == component;
        }

        private async Task EnsureSurfFinderViewerReadyAsync()
        {
            if (!File.Exists(SurfFinderViewerPath))
            {
                MessageBox.Show(
                    $"SurfFinder Viewer 실행 파일을 찾을 수 없습니다.\n{SurfFinderViewerPath}",
                    "SurfFinder Viewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            bool viewerStarted = false;
            Process viewerProcess = FindSurfFinderViewerProcess();
            if (viewerProcess == null)
            {
                try
                {
                    viewerProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = SurfFinderViewerPath,
                        WorkingDirectory = Path.GetDirectoryName(SurfFinderViewerPath),
                        UseShellExecute = true
                    });
                    viewerStarted = true;
                }
                catch (Win32Exception exception)
                {
                    MessageBox.Show(
                        $"SurfFinder Viewer를 실행하지 못했습니다.\n{exception.Message}",
                        "SurfFinder Viewer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }

            bool autoStartWindowSeen = HasAutoStartWindow();
            if (!viewerStarted && !autoStartWindowSeen)
            {
                return;
            }

            DateTime timeout = DateTime.UtcNow.AddSeconds(60);
            if (viewerStarted)
            {
                ReportStartupProgress(10, "SurfFinder를 실행하는 중입니다.");
            }
            else
            {
                ReportStartupProgress(10, "SurfFinder Auto Start 창이 닫히기를 기다리는 중입니다.");
            }

            while (DateTime.UtcNow < timeout)
            {
                if (HasAutoStartWindow())
                {
                    if (!autoStartWindowSeen)
                    {
                        autoStartWindowSeen = true;
                        ReportStartupProgress(15, "SurfFinder Auto Start 창이 닫히기를 기다리는 중입니다.");
                    }
                }
                else if (autoStartWindowSeen)
                {
                    return;
                }

                if (viewerProcess != null)
                {
                    try
                    {
                        if (viewerProcess.HasExited)
                        {
                            return;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }
                }

                await Task.Delay(100);
            }

            if (viewerStarted && !autoStartWindowSeen)
            {
                MessageBox.Show(
                    "SurfFinder의 Auto Start 창이 나타나지 않아 대기를 종료합니다.",
                    "SurfFinder Viewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static Process FindSurfFinderViewerProcess()
        {
            string processName = Path.GetFileNameWithoutExtension(SurfFinderViewerPath);
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (string.Equals(
                        process.MainModule?.FileName,
                        SurfFinderViewerPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return process;
                    }
                }
                catch (Win32Exception)
                {
                    return process;
                }
                catch (InvalidOperationException)
                {
                    process.Dispose();
                }
            }

            return null;
        }

        private static bool HasAutoStartWindow()
        {
            bool found = false;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                var title = new StringBuilder(256);
                if (GetWindowText(hWnd, title, title.Capacity) > 0 &&
                    title.ToString().IndexOf("Auto Start", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static void ActivateMainWindow(System.Windows.Window mainWindow)
        {
            mainWindow.Activate();
            mainWindow.Topmost = true;
            mainWindow.Topmost = false;
            mainWindow.Focus();

            if (new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle != IntPtr.Zero)
            {
                SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle);
            }
        }

        public void ReportStartupProgress(int progress, string message)
        {
            if (_splashWindow == null)
            {
                return;
            }

            _splashWindow.SetProgress(progress, message);
            Dispatcher.Invoke(
                DispatcherPriority.Render,
                new Action(delegate { }));
        }

        public void InitializeInspectionManagers(
            Func<SequenceConfig, ImageCollection> captureImages,
            InspectionConfig configuration,
            int modelNo,
            string modelName)
        {
            if (captureImages == null)
            {
                throw new ArgumentNullException(nameof(captureImages));
            }

            if (configuration?.Sequences == null || configuration.Sequences.Count == 0)
            {
                throw new InvalidOperationException("InspectionManager를 초기화할 시퀀스가 없습니다.");
            }

            if (InspectionManagers.Count > 0 || tools.Count > 0 || dicProcesses.Count > 0)
            {
                throw new InvalidOperationException("InspectionManager가 이미 초기화되었습니다.");
            }

            var createdManagers = new List<InspectionManager>();

            try
            {
                var sequenceNames = new HashSet<string>(StringComparer.Ordinal);
                var toolPaths = new Dictionary<string, string>(StringComparer.Ordinal);
                var resolvers = new Dictionary<string, ProcessPathResolver>(StringComparer.Ordinal);
                foreach (SequenceConfig sequence in configuration.Sequences)
                {
                    if (sequence == null || string.IsNullOrWhiteSpace(sequence.Name))
                    {
                        throw new InvalidOperationException("시퀀스 이름이 비어 있습니다.");
                    }

                    if (!sequenceNames.Add(sequence.Name))
                    {
                        throw new InvalidOperationException($"시퀀스 이름이 중복되었습니다: {sequence.Name}");
                    }

                    ValidateToolBlockPaths(sequence);
                    var resolver = new ProcessPathResolver(sequence.Path, new ProcessPathContext
                    {
                        ModelName = modelName,
                        ModelNo = modelNo > 0 ? (int?)modelNo : null,
                        StepNo = sequence.Index,
                        StepName = sequence.Name,
                        WorkDir = AppDomain.CurrentDomain.BaseDirectory
                    });
                    resolvers.Add(sequence.Name, resolver);
                    string toolPath = GetToolBlockPath(sequence.Name, resolver);
                    if (toolPath != null)
                    {
                        toolPaths.Add(sequence.Name, toolPath);
                    }
                }

                if (IsDevelopmentComponentEnabled(DevelopmentMode.Cognex))
                {
                    foreach (var toolPath in toolPaths)
                    {
                        ReportStartupProgress(50, $"ToolBlock 로드 중: {toolPath.Key}");
                        tools.Add(toolPath.Key, LoadToolBlock(toolPath.Key, toolPath.Value));
                    }
                }

                bool initializeDeeDiim =
                    IsDevelopmentComponentEnabled(DevelopmentMode.DeeDiim);
                bool initializeCamera =
                    IsDevelopmentComponentEnabled(DevelopmentMode.Camera);

                List<SequenceConfig> startupSequences = configuration.Sequences.ToList();
                if (initializeCamera)
                {
                    List<SequenceConfig> cameraSequences = startupSequences
                        .GroupBy(sequence => sequence.CameraIndex)
                        .Select(group => group.First())
                        .ToList();
                    for (int index = 0; index < cameraSequences.Count; index++)
                    {
                        SequenceConfig sequence = cameraSequences[index];
                        ReportStartupProgress(
                            50 + (10 * (index + 1) / cameraSequences.Count),
                            $"검사기 초기화용 카메라 {index + 1}/{cameraSequences.Count} 촬영 중입니다.");

                        using (ImageCollection capturedImages = captureImages(sequence))
                        {
                            if (capturedImages == null || capturedImages.Count == 0)
                            {
                                throw new InvalidOperationException(
                                    $"시퀀스 '{sequence.Name}'의 초기화 촬영 결과가 비어 있습니다.");
                            }

                            dicCamSize[sequence.CameraIndex] = (capturedImages[0].Width, capturedImages[0].Height, capturedImages.Count);
                            Debug.WriteLine($"Camera[{sequence.CameraIndex}]: Width={capturedImages[0].Width}, Height={capturedImages[0].Height}, Count={capturedImages.Count}");
                        }
                    }
                }

                if (!initializeDeeDiim &&
                    !IsDevelopmentComponentEnabled(DevelopmentMode.Cognex))
                {
                    return;
                }

                if (initializeDeeDiim)
                {
                    foreach (SequenceConfig sequence in configuration.Sequences)
                    {
                        var resolver = resolvers[sequence.Name];
                        (int width, int height, int count) camSize;
                        if (!dicCamSize.TryGetValue(sequence.CameraIndex, out camSize))
                        {
                            camSize = default;
                        }

                        if (!resolver.IsVisionPro)
                        {
                            ReportStartupProgress(60, $"Process 로드 중: {sequence.Name}");
                            LoadProcesses(sequence.Name, resolver, camSize.width, camSize.height, camSize.count);
                        }
                    }
                }

                for (int index = 0; index < startupSequences.Count; index++)
                {
                    SequenceConfig sequence = startupSequences[index];
                    var pathResolver = resolvers[sequence.Name];
                    if (pathResolver.IsVisionPro
                        ? !IsDevelopmentComponentEnabled(DevelopmentMode.Cognex)
                        : !initializeDeeDiim)
                    {
                        continue;
                    }

                    ReportStartupProgress(
                        60 + (10 * (index + 1) / startupSequences.Count),
                        $"검사기 초기화 중: {sequence.Name}");
                    var manager = new InspectionManager(
                        pathResolver, null, tools, dicProcesses, sequence.Name);
                    createdManagers.Add(manager);
                }

                InspectionManagers.AddRange(createdManagers);
            }
            catch (Exception exception)
            {
                var errors = new List<Exception> { exception };
                for (int index = createdManagers.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        createdManagers[index].Dispose();
                    }
                    catch (Exception cleanupException)
                    {
                        errors.Add(cleanupException);
                    }
                }

                DisposeProcesses(dicProcesses.Keys.ToList(), errors);
                DisposeToolBlocks(errors);
                if (errors.Count > 1)
                {
                    throw new AggregateException("검사기 초기화 및 정리에 실패했습니다.", errors);
                }

                throw;
            }
        }

        public static string GetProcessKey(string sequenceName, ResolvedProcessPath processPath)
        {
            return $"{sequenceName}:{processPath.Group}:{processPath.Index}";
        }

        private static void LoadProcesses(string sequenceName, ProcessPathResolver resolver, int width, int height, int count)
        {
            foreach (ResolvedProcessPath path in resolver.Mask.Concat(resolver.Global).Concat(resolver.Local))
            {
                string key = GetProcessKey(sequenceName, path);
                if (!File.Exists(path.Path))
                {
                    throw new FileNotFoundException($"Process '{key}' 파일을 찾을 수 없습니다.", path.Path);
                }

                var process = new DSDMProcessing(false, false);
                try
                {
                    process.DLLInitFromPath((int)path.Group, path.Index, path.Path);
                    
                    Debug.WriteLine($"Process 생성. Key={key}, Path={System.IO.Path.GetFileName(path.Path)}");

                    if (width > 0 && height > 0 && count > 0)
                    {
                        int imageCount = path.Group == ProcessPathGroup.Mask ? count : 17;
                        using (ImageCollection tempImage = new ImageCollection(Enumerable.Range(0, imageCount).Select(_ => new ImageFormat(width, height, MatType.CV_8UC1))))
                        {
                            process.SetImage(tempImage.ToList());
                            process.MemoryOptimize();
                        }
                    }

                    dicProcesses.Add(key, process);
                }
                catch (Exception exception)
                {
                    try
                    {
                        process.DeleteMemory();
                    }
                    catch (Exception cleanupException)
                    {
                        throw new AggregateException(
                            $"Process '{key}' 초기화 및 메모리 정리에 실패했습니다.",
                            exception, cleanupException);
                    }

                    throw new InvalidOperationException($"Process '{key}' 초기화에 실패했습니다: {path.Path}", exception);
                }
            }
        }

        public static void ReloadProcesses(SequenceConfig sequence, ProcessPathResolver resolver)
        {
            (int width, int height, int count) camSize = default;
            dicCamSize.TryGetValue(sequence.CameraIndex, out camSize);

            string prefix = sequence.Name + ":";
            var keys = dicProcesses.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) &&
                    key.Substring(prefix.Length).Split(':').Length == 2)
                .ToList();
            var errors = new List<Exception>();
            DisposeProcesses(keys, errors);
            if (errors.Count > 0)
            {
                throw new AggregateException($"시퀀스 '{sequence.Name}' Process 해제에 실패했습니다.", errors);
            }

            if (resolver.IsVisionPro)
            {
                return;
            }

            try
            {
                LoadProcesses(sequence.Name, resolver, camSize.width, camSize.height, camSize.count);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
                keys = resolver.Mask.Concat(resolver.Global).Concat(resolver.Local)
                    .Select(path => GetProcessKey(sequence.Name, path)).ToList();
                DisposeProcesses(keys, errors);
                if (errors.Count > 1)
                {
                    throw new AggregateException("Process 재로드 및 정리에 실패했습니다.", errors);
                }

                throw;
            }
        }

        private static void DisposeProcesses(IEnumerable<string> keys, List<Exception> errors)
        {
            foreach (string key in keys)
            {
                if (!dicProcesses.TryGetValue(key, out DSDMProcessing process))
                {
                    continue;
                }

                dicProcesses.Remove(key);
                try
                {
                    process.DeleteMemory();
                }
                catch (Exception exception)
                {
                    errors.Add(new InvalidOperationException($"Process '{key}' 해제에 실패했습니다.", exception));
                }
            }
        }

        private static void ValidateToolBlockPaths(SequenceConfig sequence)
        {
            int count = (sequence.Path ?? string.Empty).Split('|', ';')
                .Count(path => string.Equals(
                    Path.GetExtension(path.Trim()), ".vpp", StringComparison.OrdinalIgnoreCase));
            if (count > 1)
            {
                throw new InvalidOperationException(
                    $"시퀀스 '{sequence.Name}'에는 VPP를 하나만 등록할 수 있습니다.");
            }
        }

        private static string GetToolBlockPath(string sequenceName, ProcessPathResolver resolver)
        {
            var paths = resolver.Mask.Concat(resolver.Global).Concat(resolver.Local)
                .Where(path => string.Equals(
                    Path.GetExtension(path.Path), ".vpp", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (paths.Count > 1)
            {
                throw new InvalidOperationException(
                    $"시퀀스 '{sequenceName}'에는 VPP를 하나만 등록할 수 있습니다.");
            }

            return paths.Count == 1 ? paths[0].Path : null;
        }

        private static CogToolBlock LoadToolBlock(string sequenceName, string path)
        {
            object loaded = CogSerializer.LoadObjectFromFile(path);
            var tool = loaded as CogToolBlock;
            if (tool == null)
            {
                (loaded as IDisposable)?.Dispose();
                throw new InvalidOperationException(
                    $"시퀀스 '{sequenceName}'의 Process 파일이 CogToolBlock이 아닙니다: {path}");
            }

            return tool;
        }

        public static void ReloadToolBlock(SequenceConfig sequence, ProcessPathResolver resolver)
        {
            if (!IsDevelopmentComponentEnabled(DevelopmentMode.Cognex))
            {
                return;
            }

            ValidateToolBlockPaths(sequence);
            string path = GetToolBlockPath(sequence.Name, resolver);
            if (path == null)
            {
                return;
            }

            CogToolBlock previous;
            tools.TryGetValue(sequence.Name, out previous);
            tools.Remove(sequence.Name);
            previous?.Dispose();
            tools.Add(sequence.Name, LoadToolBlock(sequence.Name, path));
        }

        private static void DisposeToolBlocks(List<Exception> errors)
        {
            var loadedTools = tools.Values.ToList();
            tools.Clear();
            foreach (CogToolBlock tool in loadedTools)
            {
                try
                {
                    tool.Dispose();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
        }

        public static int GetInspectionManagerIndex(
            IReadOnlyList<SequenceConfig> sequences,
            SequenceConfig target)
        {
            if (sequences == null)
            {
                throw new ArgumentNullException(nameof(sequences));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            for (int managerIndex = 0; managerIndex < sequences.Count; managerIndex++)
            {
                SequenceConfig sequence = sequences[managerIndex];
                if (ReferenceEquals(sequence, target) ||
                    (sequence.TriggerStep == target.TriggerStep && sequence.Index == target.Index))
                {
                    return managerIndex;
                }
            }

            throw new InvalidOperationException(
                $"TriggerStep {target.TriggerStep}의 시퀀스를 찾을 수 없습니다.");
        }

        public static InspectionManager GetInspectionManager(
            IReadOnlyList<SequenceConfig> sequences,
            SequenceConfig sequence)
        {
            GetInspectionManagerIndex(sequences, sequence);
            InspectionManager manager = InspectionManagers.FirstOrDefault(item =>
                string.Equals(item.SequenceName, sequence.Name, StringComparison.Ordinal));
            if (manager == null)
            {
                throw new InvalidOperationException(
                    $"시퀀스 '{sequence.Name}'에 사용할 InspectionManager가 초기화되지 않았습니다.");
            }

            return manager;
        }

        public static void SetInspectionManagerInputImage(
            InspectionManager manager,
            ImageCollection images)
        {
            if (!InspectionManagers.Contains(manager))
            {
                throw new ArgumentException(
                    "애플리케이션 InspectionManager 목록에 없는 manager입니다.",
                    nameof(manager));
            }

            manager.InputImage = images?.Clone();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            List<Exception> cleanupErrors = null;
            for (int index = InspectionManagers.Count - 1; index >= 0; index--)
            {
                try
                {
                    InspectionManagers[index].Dispose();
                }
                catch (Exception exception)
                {
                    if (cleanupErrors == null)
                    {
                        cleanupErrors = new List<Exception>();
                    }

                    cleanupErrors.Add(exception);
                }
            }

            InspectionManagers.Clear();
            var toolCleanupErrors = new List<Exception>();
            DisposeProcesses(dicProcesses.Keys.ToList(), toolCleanupErrors);
            DisposeToolBlocks(toolCleanupErrors);
            if (toolCleanupErrors.Count > 0)
            {
                if (cleanupErrors == null)
                {
                    cleanupErrors = new List<Exception>();
                }

                cleanupErrors.AddRange(toolCleanupErrors);
            }

            if (cleanupErrors != null)
            {
                MessageBox.Show(
                    "InspectionManager 종료 중 오류가 발생했습니다.\n" +
                    new AggregateException(cleanupErrors).Message,
                    "종료 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            base.OnExit(e);
        }
    }
}
