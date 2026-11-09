using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using Cognex.VisionPro;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace FAVIS
{
    public partial class MainWindow : System.Windows.Window
    {
        private DispatcherTimer _timer;
        private DispatcherTimer _ioTimer;
        private DispatcherTimer _clockTimer;
        private DispatcherTimer _usageTimer;
        private DispatcherTimer _saveQueueStatusTimer;
        private readonly Stopwatch _tackTimer = new Stopwatch();
        private ObservableCollection<string> _logItems;
        private ObservableCollection<SequenceStatusItem> _seqStatusItems;
        private Mat _displayImage;
        private readonly IGrabber _grabber;
        private readonly MvsLightController _lightController;
        private readonly InspectionService _inspectionService;
        private InspectionConfig _inspectionConfiguration;
        private readonly List<FrameworkElement> _displayControls = new List<FrameworkElement>();
        private readonly SystemUsageMonitor _systemUsageMonitor = new SystemUsageMonitor();
        private readonly PlcController _plc;
        private readonly TickManager _tickManager;
        private CancellationTokenSource _inspectionCancellation;
        private Task _inspectionTask;
        private int _currentModelNo;
        private ulong _lastInspectionSequenceNo;
        private string _currentInspectionFolderName;
        private string _currentInspectionSerialNo;
        private DateTime _currentInspectionFolderDate;
        private int _maxLogCount = int.MaxValue;
        private bool _isLogCountUpdatePending;
        private bool _isWaitingForLogItemContainer;
        private int _reportedSaveFailureCount;

        private static readonly string InitialImagePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.png");

        public MainWindow()
        {
            InitializeComponent();
            App app = Application.Current as App;
            app?.ReportStartupProgress(30, "검사 설정을 불러오는 중입니다.");
            _inspectionConfiguration = LoadInspectionConfiguration();
            _lastInspectionSequenceNo = InspectionSequenceStore.LoadLastSequenceNo();
            bool usePlc = App.IsDevelopmentComponentEnabled(DevelopmentMode.PLC);
            _plc = usePlc ? (PlcController)new LsPLC() : new TestPLC();
            TestButton.Visibility = Visibility.Visible;

            if (usePlc)
            {
                app?.ReportStartupProgress(35, "PLC를 초기화하는 중입니다.");
                _plc.Start(_inspectionConfiguration.Plc);
            }
            PLCStatusText.Text = _plc.Connected ? "CONNECTED" : "DISCONNECTED";
            PcNameTitleText.Text = $"[{_inspectionConfiguration.PcName}]";
            _grabber = App.IsDevelopmentComponentEnabled(DevelopmentMode.Camera)
                ? GrabberFactory.Create(_inspectionConfiguration.Grabbers, null)
                : null;
            _lightController = App.IsDevelopmentComponentEnabled(DevelopmentMode.Light)
                ? new MvsLightController(boardIndex: 0, channelIndex: 0)
                : null;
            
            // TickManager 초기화
            _tickManager = new TickManager(50); // 50ms 주기
            if (usePlc && _plc is ITickable tickablePlc)
            {
                _tickManager.Register(tickablePlc);
            }
            
            app?.ReportStartupProgress(50, "화면을 구성하는 중입니다.");
            InitializeUI();
            LoadLastModel();
            app?.InitializeInspectionManagers(
                CaptureStartupImages,
                _inspectionConfiguration,
                _currentModelNo,
                ModelNameText.Text);
            _inspectionService = new InspectionService(
                _grabber,
                _lightController,
                _inspectionConfiguration,
                App.IsDevelopmentComponentEnabled(DevelopmentMode.Camera));
            InitializeImageSaveQueueStatus();
            BuildCameraStatusPanel();
            try
            {
                BuildDisplayGrid();
            }
            catch (InvalidOperationException exception)
            {
                AddLog("Display 구성 오류: " + exception.Message);
                MessageBox.Show(
                    this,
                    "Display를 구성하지 못했습니다. Setup에서 시퀀스 Display 설정을 수정하세요.\n\n" +
                    exception.Message,
                    "Display 설정 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            app?.ReportStartupProgress(75, "기본 이미지를 불러오는 중입니다.");
            //LoadInitialImage();
            InitializeTimer();
            InitializeUsageTimer();
            InitializeTitleInformation();
            app?.ReportStartupProgress(100, "준비 완료");
        }

        private ImageCollection CaptureStartupImages(SequenceConfig sequence)
        {
            TimeSpan timeout = TimeSpan.FromMinutes(5);
            TimeSpan retryInterval = TimeSpan.FromSeconds(2);
            var timer = Stopwatch.StartNew();
            int attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    _grabber.Configure(sequence.CameraIndex, sequence.Index);
                    ImageCollection images = _grabber.Grab();
                    if (images == null || images.Count == 0)
                    {
                        images?.Dispose();
                        throw new InvalidOperationException("초기화 촬영 결과가 비어 있습니다.");
                    }

                    if (attempt > 1)
                    {
                        AddLog($"카메라 {sequence.CameraIndex} 초기화 촬영에 성공했습니다 ({attempt}회 시도).");
                    }

                    return images;
                }
                catch (InvalidOperationException exception)
                {
                    TimeSpan remaining = timeout - timer.Elapsed;
                    string message =
                        $"카메라 {sequence.CameraIndex} 초기화 촬영 실패 ({attempt}회): {exception.Message}";
                    AddLog(message);
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new InvalidOperationException(
                            $"카메라 {sequence.CameraIndex} 초기화 촬영의 5분 재시도 시간이 초과되었습니다.",
                            exception);
                    }

                    (Application.Current as App)?.ReportStartupProgress(
                        50,
                        $"카메라 {sequence.CameraIndex} 연결 재시도 중입니다. " +
                        $"남은 시간: {Math.Ceiling(remaining.TotalSeconds)}초\n{exception.Message}");
                    Thread.Sleep(remaining < retryInterval ? remaining : retryInterval);
                    if (timer.Elapsed >= timeout)
                    {
                        throw new InvalidOperationException(
                            $"카메라 {sequence.CameraIndex} 초기화 촬영의 5분 재시도 시간이 초과되었습니다.",
                            exception);
                    }
                }
            }
        }

        private InspectionConfig LoadInspectionConfiguration()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "inspection.json");
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    InspectionConfig configuration =
                        InspectionConfigManager.Deserialize(json);
                    ValidateInspectionConfiguration(configuration);
                    return configuration;
                }
                catch (Exception)
                {
                }
            }

            return CreateDefaultConfiguration();
        }

        private static void ValidateInspectionConfiguration(InspectionConfig configuration)
        {
            if (configuration == null ||
                configuration.Sequences == null ||
                configuration.Sequences.Count == 0)
            {
                throw new InvalidOperationException("시퀀스 설정이 비어 있습니다.");
            }

            if (string.IsNullOrWhiteSpace(configuration.ImageSavePath))
            {
                configuration.ImageSavePath = "CapturedImages";
            }

            configuration.Sequences = configuration.Sequences.FindAll(sequence =>
                sequence != null &&
                sequence.Enabled &&
                !string.IsNullOrWhiteSpace(sequence.Name) &&
                sequence.DisplayIndex > 0);

            if (configuration.Grabbers == null || configuration.Grabbers.Count == 0)
            {
                configuration.Grabbers = new List<GrabberConfig>
                {
                    new GrabberConfig
                    {
                        Id = 1,
                        Name = "Grabber 1",
                        IpAddress = "127.0.0.1",
                        Port = 5000,
                        SensorIndex = 0,
                        PatternIndex = 0
                    }
                };
            }

            if (configuration.Plc == null)
            {
                configuration.Plc = new PlcConfig
                {
                    PlcType = "LS",
                    IpAddress = "192.168.1.2",
                    Port = 2004,
                    StationNumber = 0,
                    ScanTime = 100,
                    PlcAddress = "D100",
                    PcAddress = "D200",
                    RejectAddress = "D201",
                    ModelNoAddress = "D300",
                    StepNoAddress = "D301",
                    SerialNoAddress = "D302"
                };
            }
            else
            {
                configuration.Plc.PlcType = "LS";
                configuration.Plc.IpAddress = string.IsNullOrWhiteSpace(configuration.Plc.IpAddress)
                    ? "192.168.1.2"
                    : configuration.Plc.IpAddress;
                configuration.Plc.RejectAddress = string.IsNullOrWhiteSpace(configuration.Plc.RejectAddress)
                    ? "D201"
                    : configuration.Plc.RejectAddress;
                if (configuration.Plc.Port < 1)
                {
                    configuration.Plc.Port = 2004;
                }
            }

            if (configuration.Sequences.Count == 0)
            {
                throw new InvalidOperationException("활성화된 시퀀스가 없습니다.");
            }

            int highestDisplayIndex = 0;
            foreach (SequenceConfig sequence in configuration.Sequences)
            {
                highestDisplayIndex = Math.Max(highestDisplayIndex, sequence.DisplayIndex);
            }

            configuration.DisplayCount = Math.Max(configuration.DisplayCount, highestDisplayIndex);
        }

        private static InspectionConfig CreateDefaultConfiguration()
        {
            var sequences = new List<SequenceConfig>
            {
                new SequenceConfig { Index = 1, Name = "Stator Varnish Detect", Path = "", CameraIndex = 1, DisplayIndex = 1, Enabled = true },
                new SequenceConfig { Index = 2, Name = "Rotor Winding Check", Path = "", CameraIndex = 1, DisplayIndex = 2, Enabled = true },
                new SequenceConfig { Index = 3, Name = "Oil Hole Inspect", Path = "", CameraIndex = 1, DisplayIndex = 3, Enabled = true },
                new SequenceConfig { Index = 4, Name = "Particle Count", Path = "", CameraIndex = 1, DisplayIndex = 4, Enabled = true },
                new SequenceConfig { Index = 5, Name = "Varnish Level", Path = "", CameraIndex = 1, DisplayIndex = 5, Enabled = true },
                new SequenceConfig { Index = 6, Name = "Motor Load Test", Path = "", CameraIndex = 1, DisplayIndex = 6, Enabled = true },
                new SequenceConfig { Index = 7, Name = "Insulation Test", Path = "", CameraIndex = 1, DisplayIndex = 7, Enabled = true }
            };

            return new InspectionConfig
            {
                PcName = Environment.MachineName,
                ImageSavePath = "CapturedImages",
                DisplayCount = sequences.Count,
                Plc = new PlcConfig
                {
                    PlcType = "LS",
                    IpAddress = "192.168.1.2",
                    Port = 2004,
                    StationNumber = 0,
                    ScanTime = 100,
                    PlcAddress = "D100",
                    PcAddress = "D200",
                    RejectAddress = "D201",
                    ModelNoAddress = "D300",
                    StepNoAddress = "D301",
                    SerialNoAddress = "D302"
                },
                Grabbers = new List<GrabberConfig>
                {
                    new GrabberConfig
                    {
                        Id = 1,
                        Name = "Grabber 1",
                        IpAddress = "127.0.0.1",
                        Port = 5000,
                        SensorIndex = 0,
                        PatternIndex = 0
                    }
                },
                Sequences = sequences
            };
        }

        private void BuildDisplayGrid()
        {
            var displayGroups = _inspectionConfiguration.Sequences
                .Where(sequence => sequence.Enabled)
                .GroupBy(sequence => sequence.DisplayIndex);
            foreach (var displayGroup in displayGroups)
            {
                SequenceConfig vppSequence = displayGroup.FirstOrDefault(
                    sequence => ContainsVppPath(sequence.Path));
                SequenceConfig jsonSequence = displayGroup.FirstOrDefault(
                    sequence => !ContainsVppPath(sequence.Path));
                if (vppSequence != null && jsonSequence != null)
                {
                    throw new InvalidOperationException(
                        $"Display {displayGroup.Key}번을 사용하는 시퀀스 " +
                        $"'{jsonSequence.Name}'과 '{vppSequence.Name}'의 Process 경로 형식이 다릅니다. " +
                        "같은 Display에서는 JSON과 VPP를 함께 사용할 수 없습니다.");
                }
            }

            CameraGrid.Children.Clear();
            CameraGrid.RowDefinitions.Clear();
            CameraGrid.ColumnDefinitions.Clear();
            _displayControls.Clear();

            int displayCount = _inspectionConfiguration.DisplayCount;
            int columns = displayCount <= 2 ? displayCount : (int)Math.Ceiling(Math.Sqrt(displayCount));
            int rows = (int)Math.Ceiling((double)displayCount / columns);

            for (int row = 0; row < rows; row++)
            {
                CameraGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            for (int column = 0; column < columns; column++)
            {
                CameraGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (int index = 0; index < displayCount; index++)
            {
                var panel = new DockPanel
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF)),
                    LastChildFill = true
                };

                var label = new TextBlock
                {
                    Text = $"Display {index + 1}",
                    IsHitTestVisible = false,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xFF, 0x00)),
                    Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
                    Padding = new Thickness(3, 2, 3, 2),
                    FontWeight = FontWeights.Bold
                };

                DockPanel.SetDock(label, Dock.Top);
                panel.Children.Add(label);

                int displayIndex = index + 1;
                bool useVppDisplay = _inspectionConfiguration.Sequences.Any(
                    sequence => sequence.Enabled &&
                                sequence.DisplayIndex == displayIndex &&
                                ContainsVppPath(sequence.Path));
                FrameworkElement display;
                if (useVppDisplay)
                {
                    var recordDisplay = new CogRecordDisplay();
                    var host = new WindowsFormsHost
                    {
                        Child = recordDisplay
                    };
                    host.Loaded += (sender, args) =>
                    {
                        recordDisplay.CreateControl();
                        recordDisplay.HorizontalScrollBar = false;
                        recordDisplay.VerticalScrollBar = false;
                    };
                    display = host;
                }
                else
                {
                    display = new ZoomPanDisplay { Opacity = 0.8 };
                }

                panel.Children.Add(display);

                var border = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    Margin = new Thickness(2),
                    Child = panel
                };

                Grid.SetRow(border, index / columns);
                Grid.SetColumn(border, index % columns);
                CameraGrid.Children.Add(border);
                _displayControls.Add(display);
            }
        }

        private static bool ContainsVppPath(string processPath)
        {
            return !string.IsNullOrWhiteSpace(processPath) &&
                   processPath.IndexOf(".vpp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void BuildCameraStatusPanel()
        {
            CameraStatusPanel.Children.Clear();

            foreach (GrabberConfig camera in _inspectionConfiguration.Grabbers)
            {
                var cameraName = new TextBlock
                {
                    Text = camera.Name,
                    FontSize = 10,
                    Foreground = Brushes.Black,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var status = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x00, 0xAA, 0x00)),
                    Padding = new Thickness(8, 6, 8, 6),
                    CornerRadius = new CornerRadius(2),
                    MinWidth = 40,
                    ToolTip = $"{camera.IpAddress}:{camera.Port}",
                    Child = cameraName
                };

                CameraStatusPanel.Children.Add(status);
            }
        }

        private void LoadInitialImage()
        {
            if (!System.IO.File.Exists(InitialImagePath))
            {
                AddLog($"[{DateTime.Now:HH:mm:ss}] Image not found: {InitialImagePath}");
                return;
            }

            try
            {
                _displayImage = Cv2.ImRead(InitialImagePath, ImreadModes.Color);
                if (_displayImage.Empty())
                {
                    _displayImage.Dispose();
                    _displayImage = null;
                    AddLog($"[{DateTime.Now:HH:mm:ss}] Failed to load image: {InitialImagePath}");
                    return;
                }

                using (var images = new ImageCollection())
                {
                    images.Add(1, _displayImage);
                    SetAllCameraImages(images);
                }
                AddLog($"[{DateTime.Now:HH:mm:ss}] Image loaded: {InitialImagePath}");
            }
            catch (Exception ex)
            {
                _displayImage?.Dispose();
                _displayImage = null;
                AddLog($"[{DateTime.Now:HH:mm:ss}] Image load error: {ex.Message}");
            }
        }

        private void InitializeUI()
        {
            // Initialize log items
            _logItems = new ObservableCollection<string>
            {
                "[15:30:22] Start Inspection Run Seq",
                "[15:30:23] Start Inspection Run Seq",
                "[15:30:24] Stator Varnish Detect OK",
                "[15:30:25] Start Inspection Run Seq",
                "[15:30:26] Rotor Winding Check OK",
                "[15:30:27] Oil Hole Inspection OK",
                "[15:30:28] Particle Count Check OK",
                "[15:30:29] Varnish Level Check OK",
                "[15:30:30] Motor Load Test OK",
                "[15:30:31] Insulation Test OK",
                "[15:30:32] Final Test Complete OK"
            };

            LogListBox.ItemsSource = _logItems;
            LogListBox.Loaded += LogListBox_Loaded;
            LogListBox.SizeChanged += LogListBox_SizeChanged;
            LogListBox.ItemContainerGenerator.StatusChanged += LogListBox_ItemContainerGeneratorStatusChanged;

            // Initialize sequence status items
            _seqStatusItems = new ObservableCollection<SequenceStatusItem>();
            foreach (SequenceConfig sequence in _inspectionConfiguration.Sequences)
            {
                _seqStatusItems.Add(new SequenceStatusItem(sequence.Name));
            }

            SeqStatusListBox.ItemsSource = _seqStatusItems;

            // Sample data
            ModelCodeText.Text = "1";
            ModelNameText.Text = "JG";
            CurrentSeqText.Text = "5746";
            TackTimeText.Text = "00:00.00";
            PLCStatusText.Text = "CONNECTED";
            ResultText.Text = "OK";
            ResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 0, 0));
            SetCurrentStatus("STOP", Brushes.Orange);
        }

        private void LoadLastModel()
        {
            List<ModelConfig> models = ModelConfigManager.LoadModels();
            int lastModelNo = ModelConfigManager.LoadLastModelNo();
            ModelConfig model = models.FirstOrDefault(item => item.ModelNo == lastModelNo)
                ?? models.FirstOrDefault();

            if (model == null)
            {
                ModelCodeText.Text = "-";
                ModelNameText.Text = "No Model";
                _currentModelNo = 0;
                return;
            }

            _currentModelNo = model.ModelNo;
            ModelCodeText.Text = model.ModelNo.ToString();
            ModelNameText.Text = model.ModelName;
            ModelConfigManager.SaveLastModelNo(model.ModelNo);
        }

        private void InitializeTimer()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick += Timer_Tick;

            _ioTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _ioTimer.Tick += IoTimer_Tick;
            _ioTimer.Start();
            UpdatePlcIoStatus();

            // TickManager 시작
            _tickManager.Start();
        }

        private void InitializeImageSaveQueueStatus()
        {
            _saveQueueStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _saveQueueStatusTimer.Tick += SaveQueueStatusTimer_Tick;
            _saveQueueStatusTimer.Start();
            UpdateImageSaveQueueStatus();
        }

        private void SaveQueueStatusTimer_Tick(object sender, EventArgs e)
        {
            UpdateImageSaveQueueStatus();
        }

        private void UpdateImageSaveQueueStatus()
        {
            int pendingCount = _inspectionService.PendingSaveCount;
            int failureCount = _inspectionService.SaveFailureCount;
            bool isSaving = _inspectionService.IsSavingImages;

            ImageSaveQueueStatusText.Text =
                $"대기 {pendingCount}/{ImageSaveQueue.Capacity} | " +
                $"{(isSaving ? "저장 중" : "유휴")} | 오류 {failureCount}";
            ImageSaveQueueStatusText.Foreground = failureCount > 0
                ? Brushes.Red
                : pendingCount > 0 || isSaving
                    ? Brushes.Gold
                    : Brushes.LightGreen;
            ImageSaveQueueStatusText.ToolTip = failureCount > 0
                ? _inspectionService.LastSaveError
                : "이미지 저장 오류가 없습니다.";

            if (failureCount > _reportedSaveFailureCount)
            {
                _reportedSaveFailureCount = failureCount;
                AddLog("이미지 저장 실패: " + _inspectionService.LastSaveError);
            }
        }

        private void IoTimer_Tick(object sender, EventArgs e)
        {
            UpdatePlcIoStatus();
        }

        private void UpdatePlcIoStatus()
        {
            if (_plc == null)
            {
                return;
            }

            SetIoIndicator(PlcAliveIndicator, _plc.PlcAlive);
            SetIoIndicator(PlcResetIndicator, _plc.Reset);
            SetIoIndicator(PlcTriggerIndicator, _plc.Trigger);
            SetIoIndicator(PcAliveIndicator, _plc.PcAlive);
            SetIoIndicator(PcReadyIndicator, _plc.Ready);
            SetIoIndicator(PcBusyIndicator, _plc.Busy);
            SetIoIndicator(PcGrabIndicator, _plc.Grab);
            SetIoIndicator(PcOkIndicator, _plc.Ok);
            SetIoIndicator(PcNgIndicator, _plc.Ng);
            SetIoIndicator(PcCompleteIndicator, _plc.Complete);
        }

        private static void SetIoIndicator(System.Windows.Controls.Border indicator, bool enabled)
        {
            indicator.Background = enabled
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0, 170, 0))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(136, 136, 136));
        }

        private void InitializeUsageTimer()
        {
            _usageTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _usageTimer.Tick += UsageTimer_Tick;
            _usageTimer.Start();
            UpdateSystemUsage();
        }

        private void UsageTimer_Tick(object sender, EventArgs e)
        {
            UpdateSystemUsage();
        }

        private void UpdateSystemUsage()
        {
            SystemUsageSnapshot usage = _systemUsageMonitor.Read();

            CpuUsageProgressBar.Value = usage.CpuUsage;
            CpuUsageText.Text = $"{usage.CpuUsage:F1}%";
            GpuUsageProgressBar.Value = usage.GpuUsage;
            GpuUsageText.Text = $"{usage.GpuUsage:F1}%";
            MemoryUsageProgressBar.Value = usage.MemoryUsage;
            MemoryUsageText.Text = $"{usage.MemoryUsage:F1}% ({usage.UsedMemoryGb:F1}GB)";

            DriveUsage cUsage;
            if (usage.Drives.TryGetValue("C", out cUsage))
            {
                DiskUsageProgressBar.Value = cUsage.Percentage;
                DiskUsageText.Text = $"{cUsage.Percentage:F1}% ({cUsage.UsedGb:F1}GB)";
            }

            DriveUsage dUsage;
            if (usage.Drives.TryGetValue("D", out dUsage))
            {
                DiskUsageDProgressBar.Value = dUsage.Percentage;
                DiskUsageDText.Text = $"{dUsage.Percentage:F1}% ({dUsage.UsedGb:F1}GB)";
            }
        }

        private void InitializeTitleInformation()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            ProgramVersionText.Text = $"Program Version {version}";

            UpdateDateTime();
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();
        }

        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            UpdateDateTime();
        }

        private void UpdateDateTime()
        {
            CurrentDateTimeText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            TimeSpan elapsed = _tackTimer.Elapsed;
            TackTimeText.Text =
                $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds / 10:D2}";
        }

        private async void AutoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!App.IsDevelopmentComponentEnabled(DevelopmentMode.PLC))
            {
                MessageBox.Show(
                    this,
                    "개발 모드에서 PLC가 비활성화되어 자동 검사를 실행할 수 없습니다.",
                    "개발 모드",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_inspectionTask != null && !_inspectionTask.IsCompleted)
            {
                return;
            }

            _inspectionCancellation = new CancellationTokenSource();
            AddLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Auto Inspection Started");

            ResetSequenceStatus();
            SetCurrentStatus("AUTO", Brushes.Yellow);
            _plc.WriteBits(PcBits.Ready);
            _inspectionTask = RunAutoInspectionAsync(_inspectionCancellation.Token);

            try
            {
                await _inspectionTask;
            }
            catch (OperationCanceledException)
            {
                SetCurrentStatus("STOP", Brushes.Orange);
                AddLog($"[{DateTime.Now:HH:mm:ss}] Auto Inspection Canceled");
            }
            catch (Exception ex)
            {
                ResultText.Text = "NG";
                ResultText.Foreground = Brushes.Red;
                SetCurrentStatus("STOP", Brushes.Orange);
                AddLog($"[{DateTime.Now:HH:mm:ss}] Auto Inspection Error: {ex.Message}");
            }
            finally
            {
                _plc.WriteBits(PcBits.None);
                _inspectionCancellation.Dispose();
                _inspectionCancellation = null;
                _inspectionTask = null;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _plc.ClearBits(PcBits.Ready);

            if (_inspectionTask == null || _inspectionTask.IsCompleted)
            {
                return;
            }

            _inspectionCancellation?.Cancel();
            _plc.WriteBits(PcBits.None);
            _tackTimer.Stop();
            Timer_Tick(null, EventArgs.Empty);
            _timer.Stop();
            SetCurrentStatus("STOP", Brushes.Orange);
            AddLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Inspection Stopped");
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void SetupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_inspectionTask != null && !_inspectionTask.IsCompleted)
            {
                MessageBox.Show(
                    this,
                    "자동 검사가 진행 중일 때는 Setup을 열 수 없습니다. 검사를 중지한 후 다시 시도하세요.",
                    "Setup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var setupWindow = new SetupWindow(
                _inspectionConfiguration,
                _grabber,
                _lightController,
                _currentModelNo,
                ModelNameText.Text)
            {
                Owner = this
            };
            setupWindow.ShowDialog();
        }

        private void SystemButton_Click(object sender, RoutedEventArgs e)
        {
            var passwordWindow = new PasswordWindow
            {
                Owner = this
            };

            if (passwordWindow.ShowDialog() != true)
            {
                return;
            }

            string configPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Config",
                "inspection.json");
            var editorWindow = new InspectionConfigEditorWindow(configPath)
            {
                Owner = this
            };

            if (editorWindow.ShowDialog() != true || !editorWindow.ConfigurationChanged)
            {
                return;
            }

            MessageBox.Show(
                this,
                "설정이 변경되었습니다. 확인을 누르면 프로그램이 재시작됩니다.",
                "System",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            RestartApplication();
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var testWindow = new TestWindow(
                _plc,
                _inspectionConfiguration.Sequences.Count,
                _inspectionConfiguration.Grabbers,
                _lightController)
            {
                Owner = this
            };
            testWindow.Show();
        }

        private void ModelButton_Click(object sender, RoutedEventArgs e)
        {
            var modelWindow = new ModelManagementWindow
            {
                Owner = this
            };

            if (modelWindow.ShowDialog() == true && modelWindow.SelectedModel != null)
            {
                _currentModelNo = modelWindow.SelectedModel.ModelNo;
                ModelCodeText.Text = modelWindow.SelectedModel.ModelNo.ToString();
                ModelNameText.Text = modelWindow.SelectedModel.ModelName;
            }
        }

        private static void RestartApplication()
        {
            string executablePath = Process.GetCurrentProcess().MainModule.FileName;
            Process.Start(executablePath, App.RestartArguments);
            Application.Current.Shutdown();
        }

        private void ReserveButton1_Click(object sender, RoutedEventArgs e)
        {
            AddLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Reserve Button 1 Clicked");
        }

        private void ReserveButton2_Click(object sender, RoutedEventArgs e)
        {
            AddLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] Reserve Button 2 Clicked");
        }

        private void AddLog(string message)
        {
            _logItems.Insert(0, message);

            while (_logItems.Count > _maxLogCount)
                _logItems.RemoveAt(_logItems.Count - 1);

            if (LogListBox.Items.Count > 0)
                LogListBox.ScrollIntoView(LogListBox.Items[0]);
        }

        private void LogListBox_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateMaxLogCountAfterLayout();
        }

        private void LogListBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMaxLogCountAfterLayout();
        }

        private void UpdateMaxLogCountAfterLayout()
        {
            if (_isLogCountUpdatePending)
                return;

            _isLogCountUpdatePending = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _isLogCountUpdatePending = false;
                    UpdateMaxLogCount();
                }),
                DispatcherPriority.ContextIdle);
        }

        private void LogListBox_ItemContainerGeneratorStatusChanged(object sender, EventArgs e)
        {
            if (_isWaitingForLogItemContainer &&
                LogListBox.ItemContainerGenerator.Status ==
                    System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                _isWaitingForLogItemContainer = false;
                UpdateMaxLogCountAfterLayout();
            }
        }

        /// <summary>
        /// ListBox 높이와 아이템 높이를 계산해서
        /// 리스트 박스에 표시할수 있는 최대 로그 개수를 계산
        /// </summary>
        private void UpdateMaxLogCount()
        {
            LogListBox.ApplyTemplate();

            ListBoxItem itemContainer =
                LogListBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;

            if (itemContainer == null)
                _isWaitingForLogItemContainer = true;

            LogListBox.UpdateLayout();
            itemContainer =
                LogListBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;

            if (LogListBox.ActualHeight <= 0)
                return;

            if (itemContainer == null || itemContainer.ActualHeight <= 0)
            {
                _isWaitingForLogItemContainer = true;
                return;
            }

            int itemSlotHeight = (int)Math.Round(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(itemContainer).Height + 0.5);
            if (itemSlotHeight <= 0)
            {
                _isWaitingForLogItemContainer = true;
                return;
            }

            _isWaitingForLogItemContainer = false;
            _maxLogCount = Math.Max(1, (int)Math.Floor(LogListBox.ActualHeight / itemSlotHeight));

            while (_logItems.Count > _maxLogCount)
                _logItems.RemoveAt(_logItems.Count - 1);
        }

        private async Task<InspectionResult> RunInspectionAsync(
            IProgress<InspectionProgress> progress,
            CancellationToken cancellationToken,
            int stepNo,
            string serialNo,
            int modelNo,
            string modelName)
        {
            return await Task.Run(
                () => _inspectionService.RunAsync(
                    progress,
                    cancellationToken,
                    stepNo,
                    serialNo,
                    modelNo,
                    modelName),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RunAutoInspectionAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                PlcInputData input = await WaitForTriggerAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                //if (input.StepNo != expectedStepNo)
                //{
                //    await HandleStepSequenceErrorAsync(
                //        expectedStepNo,
                //        input.StepNo,
                //        cancellationToken);
                //    expectedStepNo = 1;
                //    continue;
                //}

                EnsureCurrentModel(input.ModelNo);
                if (input.StepNo == 1)
                {
                    ClearInspectionView();
                }

                string inspectionFolderName = ResolveInspectionFolderName(input);
                AddLog($"[{DateTime.Now:HH:mm:ss}] Trigger - ModelNo:{input.ModelNo}, StepNo:{input.StepNo}, SerialNo:{input.SerialNo}");

                _tackTimer.Restart();
                TackTimeText.Text = "00:00.00";
                _timer.Start();

                _plc.ClearBits(PcBits.Ready);
                _plc.SetBits(PcBits.Busy);
                var progress = new Progress<InspectionProgress>(UpdateInspectionProgress);
                InspectionResult result = await RunInspectionCycleAsync(
                    progress,
                    cancellationToken,
                    input.StepNo,
                    inspectionFolderName,
                    input.ModelNo,
                    ModelNameText.Text);
                if (result == null)
                {
                    _plc.ClearBits(PcBits.Busy);
                    continue;
                }

                _tackTimer.Stop();
                Timer_Tick(null, EventArgs.Empty);
                _timer.Stop();
                ResultText.Text = result.ResultText;
                ResultText.Foreground = Brushes.Black;
                ResultText.Background = result.IsPassed ? Brushes.LimeGreen : Brushes.Red;

                // 신호 순서: 1️⃣ RejectWord → 2️⃣ OK/NG + Complete
                int rejectMask = CalculateRejectBitMask(result);
                _plc.WriteWord(_inspectionConfiguration.Plc.RejectAddress, rejectMask);

                _plc.WriteBits(
                    PcBits.Ready |
                    (result.IsPassed ? PcBits.OK : PcBits.NG) |
                    PcBits.Complete);

                if (App.IsDevelopmentComponentEnabled(DevelopmentMode.Camera) &&
                    IsLastInspectionStep(input.StepNo))
                {
                    string captureSerialNo = string.IsNullOrWhiteSpace(input.SerialNo)
                        ? inspectionFolderName
                        : input.SerialNo.Trim();
                    await Dispatcher.InvokeAsync(
                        () => SaveMainWindowImage(captureSerialNo),
                        DispatcherPriority.ContextIdle,
                        cancellationToken);
                }
                AddLog($"[{DateTime.Now:HH:mm:ss}] Auto Inspection Completed - {result.ResultText}");

                //expectedStepNo++;
                //if (expectedStepNo > _inspectionConfiguration.Sequences.Count)
                //{
                //    expectedStepNo = 1;
                //}

                await WaitForTriggerReleaseAsync(cancellationToken);
            }
        }

        private string ResolveInspectionFolderName(PlcInputData input)
        {
            string serialNo = input.SerialNo?.Trim();
            DateTime folderDate = DateTime.Today;
            if (input.StepNo != 1 &&
                !string.IsNullOrWhiteSpace(_currentInspectionFolderName) &&
                string.Equals(serialNo, _currentInspectionSerialNo, StringComparison.Ordinal) &&
                folderDate == _currentInspectionFolderDate)
            {
                return _currentInspectionFolderName;
            }

            string folderName = serialNo;
            if (string.IsNullOrWhiteSpace(folderName))
            {
                if (_lastInspectionSequenceNo == ulong.MaxValue)
                {
                    throw new InvalidOperationException("검사 시퀀스 번호가 최대값에 도달했습니다.");
                }

                _lastInspectionSequenceNo++;
                InspectionSequenceStore.SaveLastSequenceNo(_lastInspectionSequenceNo);
                folderName = _lastInspectionSequenceNo.ToString();
            }

            if (App.IsDevelopmentComponentEnabled(DevelopmentMode.Camera))
            {
                string rootPath = ResolveImageSavePath(_inspectionConfiguration.ImageSavePath);
                if (string.IsNullOrWhiteSpace(rootPath))
                {
                    throw new InvalidOperationException("촬영 이미지 저장 폴더가 설정되지 않았습니다.");
                }

                string dateDirectory = Path.Combine(
                    rootPath,
                    folderDate.ToString("yyyy"),
                    folderDate.ToString("MM"),
                    folderDate.ToString("dd"));
                string baseFolderName = SanitizePathPart(folderName);
                folderName = baseFolderName;
                int suffix = 1;
                while (Directory.Exists(Path.Combine(dateDirectory, folderName)) ||
                    File.Exists(Path.Combine(dateDirectory, folderName)))
                {
                    folderName = $"{baseFolderName}({suffix++})";
                }

                Directory.CreateDirectory(Path.Combine(dateDirectory, folderName));
            }

            _currentInspectionFolderName = folderName;
            _currentInspectionSerialNo = serialNo;
            _currentInspectionFolderDate = folderDate;
            return _currentInspectionFolderName;
        }

        private void EnsureCurrentModel(int modelNo)
        {
            if (modelNo <= 0)
            {
                throw new InvalidOperationException("PLC ModelNo가 올바르지 않습니다.");
            }

            if (modelNo == _currentModelNo)
            {
                return;
            }

            ModelConfig model = ModelConfigManager
                .LoadModels()
                .FirstOrDefault(item => item.ModelNo == modelNo);
            if (model == null)
            {
                throw new InvalidOperationException(
                    $"ModelNo {modelNo}에 해당하는 모델이 등록되어 있지 않습니다.");
            }

            ChangeModel(modelNo);
            _currentModelNo = modelNo;
            ModelCodeText.Text = model.ModelNo.ToString();
            ModelNameText.Text = model.ModelName;
            ModelConfigManager.SaveLastModelNo(modelNo);
        }

        private void ChangeModel(int modelNo)
        {
            // 모델별 검사 설정 전환 로직을 추후 이 위치에 추가합니다.
        }

        private async Task HandleStepSequenceErrorAsync(
            int expectedStepNo,
            int receivedStepNo,
            CancellationToken cancellationToken)
        {
            _plc.WriteBits(PcBits.NG | PcBits.Complete);
            ResultText.Text = "NG";
            ResultText.Foreground = Brushes.Red;
            SetCurrentStatus("STOP", Brushes.Orange);

            AddLog(
                $"[{DateTime.Now:HH:mm:ss}] Step Sequence Error - " +
                $"Expected: {expectedStepNo}, Received: {receivedStepNo}");

            await WaitForProtocolResetAsync(cancellationToken);

            _plc.Complete = false;
            _plc.Ok = false;
            _plc.Ng = false;
            ResetSequenceStatus();
            SetCurrentStatus("AUTO", Brushes.Yellow);
            AddLog($"[{DateTime.Now:HH:mm:ss}] Step sequence reset. Restart from Step 1.");
        }

        private async Task WaitForProtocolResetAsync(CancellationToken cancellationToken)
        {
            while (!_plc.Reset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }

            while (_plc.Reset || _plc.Trigger)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }
        }

        private async Task<InspectionResult> RunInspectionCycleAsync(
            IProgress<InspectionProgress> progress,
            CancellationToken cancellationToken,
            int stepNo,
            string serialNo,
            int modelNo,
            string modelName)
        {
            using (var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<InspectionResult> inspectionTask =
                    RunInspectionAsync(
                        progress,
                        cycleCancellation.Token,
                        stepNo,
                        serialNo,
                        modelNo,
                        modelName);

                while (!inspectionTask.IsCompleted)
                {
                    if (_plc.Reset)
                    {
                        cycleCancellation.Cancel();
                        try
                        {
                            await inspectionTask;
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        //ResetSequenceStatus();
                        _plc.WriteBits(PcBits.Ready);
                        _tackTimer.Stop();
                        Timer_Tick(null, EventArgs.Empty);
                        _timer.Stop();
                        AddLog($"[{DateTime.Now:HH:mm:ss}] PLC Reset");
                        return null;
                    }

                    await Task.Delay(50, cancellationToken);
                }

                return await inspectionTask;
            }
        }

        private async Task<PlcInputData> WaitForTriggerAsync(CancellationToken cancellationToken)
        {
            SetCurrentStatus("AUTO", Brushes.Yellow);

            _plc.SetBits(PcBits.Ready);
            _plc.ClearBits(PcBits.Busy);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_plc.Reset)
                {
                    ResetSequenceStatus();
                    _plc.WriteBits(PcBits.Ready);
                    await Task.Delay(50, cancellationToken);
                    continue;
                }

                if (_plc.Trigger)
                {
                    _plc.ReadInput();
                    _plc.WriteBits(PcBits.Busy);
                    return _plc.Input;
                }

                await Task.Delay(50, cancellationToken);
            }
        }

        private async Task WaitForTriggerReleaseAsync(CancellationToken cancellationToken)
        {
            while (_plc.Trigger && !_plc.Reset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(50, cancellationToken);
            }

            if (_plc.Reset)
            {
                ResetSequenceStatus();
                _plc.ClearBits(PcBits.OK | PcBits.NG | PcBits.Complete);
            }
        }

        private void UpdateInspectionProgress(InspectionProgress progress)
        {
            CurrentSeqText.Text = progress.SequenceIndex > 0
                ? $"{progress.SequenceIndex} / {progress.TotalSequenceCount}"
                : "-";
            SetCurrentStatus("RUN", Brushes.LimeGreen);

            if (progress.DisplayImage != null)
            {
                if (progress.DisplayImage is ImageCollection imageCollection)
                {
                    SetDisplayImage(progress.DisplayIndex, imageCollection);
                }
                else if (progress.DisplayImage is ICogRecord record)
                {
                    SetDisplayRecord(progress.DisplayIndex, record);
                }
                else
                {
                    throw new InvalidOperationException("ImageCollection과 ICogRecord만 지원합니다.");
                }
            }

            if (progress.Stage == InspectionStage.Capturing &&
                progress.Message == "촬영 완료")
            {
                _plc.Grab = true;
            }

            if (progress.SequenceIndex > 0 &&
                progress.SequenceIndex <= _seqStatusItems.Count &&
                progress.Stage == InspectionStage.Completed)
            {
                _seqStatusItems[progress.SequenceIndex - 1].SetResult(
                    progress.IsPassed == true,
                    FormatElapsed(progress.Elapsed));
            }

            AddLog(
                $"[{progress.ReportedAt:HH:mm:ss}] " +
                $"{progress.SequenceIndex}/{progress.TotalSequenceCount} " +
                $"{progress.SequenceName}: {progress.Message}");
        }

        private void ClearInspectionView()
        {
            SetAllCameraImages(null);
            ResetSequenceStatus();
            CurrentSeqText.Text = "-";
            ResultText.Text = string.Empty;
            ResultText.Foreground = Brushes.Black;
            TackTimeText.Text = "00:00.00";
        }

        private bool IsLastInspectionStep(int stepNo)
        {
            return _inspectionConfiguration.Sequences.Select(x => x.TriggerStep).Max() <= stepNo;
        }

        private void SaveMainWindowImage(string serialNumber)
        {
            string rootPath = ResolveImageSavePath(_inspectionConfiguration.ImageSavePath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new InvalidOperationException("촬영 이미지 저장 폴더가 설정되지 않았습니다.");
            }

            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                throw new InvalidOperationException("MainWindow 저장용 제품 식별자가 없습니다.");
            }

            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                throw new InvalidOperationException("MainWindow가 표시된 상태에서만 화면을 저장할 수 있습니다.");
            }

            UpdateLayout();
            foreach (FrameworkElement displayControl in _displayControls)
            {
                if (displayControl is WindowsFormsHost host)
                {
                    host.Child?.Refresh();
                }
            }

            System.Windows.Point topLeft = PointToScreen(new System.Windows.Point(0, 0));
            System.Windows.Point bottomRight =
                PointToScreen(new System.Windows.Point(ActualWidth, ActualHeight));
            int x = (int)Math.Round(topLeft.X);
            int y = (int)Math.Round(topLeft.Y);
            int width = (int)Math.Round(bottomRight.X) - x;
            int height = (int)Math.Round(bottomRight.Y) - y;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("MainWindow 캡처 영역이 올바르지 않습니다.");
            }

            DateTime captureTime = DateTime.Now;
            string directory = Path.Combine(
                rootPath,
                captureTime.ToString("yyyy"),
                captureTime.ToString("MM"),
                captureTime.ToString("dd"));
            Directory.CreateDirectory(directory);

            string filePath = Path.Combine(
                directory,
                $"ScreenCapture_{SanitizePathPart(serialNumber)}_{captureTime:yyyyMMddHHmmss}.png");
            using (var bitmap = new System.Drawing.Bitmap(width, height))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    x, y, 0, 0,
                    new System.Drawing.Size(width, height),
                    System.Drawing.CopyPixelOperation.SourceCopy);
                bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static string ResolveImageSavePath(string path)
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

        private void SetCurrentStatus(string status, Brush color)
        {
            CurrentStatusText.Text = status;
            CurrentStatusText.Foreground = color;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            return $"{elapsed.TotalSeconds:F2} sec";
        }

        private void SetAllCameraImages(ImageCollection images)
        {
            foreach (FrameworkElement displayControl in _displayControls)
            {
                ZoomPanDisplay display = displayControl as ZoomPanDisplay;
                if (display != null)
                {
                    display.Source = images;
                }
            }
        }

        private void SetDisplayImage(
            int displayIndex,
            ImageCollection images)
        {
            if (displayIndex > 0 && displayIndex <= _displayControls.Count)
            {
                ZoomPanDisplay display = _displayControls[displayIndex - 1] as ZoomPanDisplay;
                if (display != null)
                {
                    display.Source = images;
                }
            }
        }

        private void SetDisplayRecord(
            int displayIndex,
            ICogRecord record)
        {
            if (displayIndex > 0 && displayIndex <= _displayControls.Count)
            {
                WindowsFormsHost host = _displayControls[displayIndex - 1] as WindowsFormsHost;
                CogRecordDisplay recordDisplay = host.Child as CogRecordDisplay;
                if (recordDisplay != null)
                {
                    recordDisplay.Record = record;
                    recordDisplay.Fit();
                }
            }
        }

        private void ResetSequenceStatus()
        {
            for (int index = 0; index < _seqStatusItems.Count; index++)
            {
                _seqStatusItems[index].Reset();
            }
        }

        private int CalculateRejectBitMask(InspectionResult result)
        {
            int bitMask = 0;

            if (result?.SequenceResults != null)
            {
                foreach (var seqResult in result.SequenceResults)
                {
                    if (!seqResult.IsPassed)
                    {
                        // seqResult.SequenceIndex 번째 비트를 1로 설정
                        bitMask |= (1 << seqResult.SequenceIndex);
                    }
                }
            }

            return bitMask;
        }

        protected override void OnClosed(EventArgs e)
        {
            _tickManager?.Stop();

            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            if (_ioTimer != null)
            {
                _ioTimer.Stop();
                _ioTimer = null;
            }

            if (_clockTimer != null)
            {
                _clockTimer.Stop();
                _clockTimer = null;
            }

            if (_usageTimer != null)
            {
                _usageTimer.Stop();
                _usageTimer = null;
            }

            _saveQueueStatusTimer?.Stop();
            _inspectionCancellation?.Cancel();
            try
            {
                _inspectionService?.Dispose();
                if (_inspectionService != null &&
                    _inspectionService.SaveFailureCount > _reportedSaveFailureCount)
                {
                    MessageBox.Show(
                        $"{_inspectionService.SaveFailureCount}건의 이미지 저장이 실패했습니다.\n" +
                        _inspectionService.LastSaveError,
                        "이미지 저장 오류",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "이미지 저장 큐 종료 중 오류가 발생했습니다.\n" + exception.Message,
                    "이미지 저장 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            foreach (FrameworkElement displayControl in _displayControls)
            {
                ZoomPanDisplay display = displayControl as ZoomPanDisplay;
                if (display != null)
                {
                    display.Source = null;
                }
                else
                {
                    WindowsFormsHost host = displayControl as WindowsFormsHost;
                    host?.Child?.Dispose();
                }
            }

            _plc.Dispose();
            _grabber?.Dispose();
            _lightController?.Dispose();
            _systemUsageMonitor.Dispose();
            _tickManager?.Dispose();
            _displayImage?.Dispose();
            _displayImage = null;
            base.OnClosed(e);
        }
    }
}
