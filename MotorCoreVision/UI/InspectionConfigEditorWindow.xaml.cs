using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Forms = System.Windows.Forms;

namespace FAVIS
{
    public partial class InspectionConfigEditorWindow : Window
    {
        private readonly string _configPath;
        private readonly string _originalJson;
        private readonly string _originalSerializedConfiguration;
        private readonly ObservableCollection<SequenceConfig> _sequences;
        private readonly ObservableCollection<GrabberConfig> _grabbers;
        public ObservableCollection<int> CameraIdOptions { get; } =
            new ObservableCollection<int>();
        public ObservableCollection<int> DisplayIndexOptions { get; } =
            new ObservableCollection<int>();
        public ObservableCollection<LightIndexOption> LightIndexOptions { get; } =
            new ObservableCollection<LightIndexOption>();
        public IReadOnlyList<SequenceOutputType> OutputTypeOptions { get; } =
            (SequenceOutputType[])Enum.GetValues(typeof(SequenceOutputType));
        private bool _isRefreshingDisplayOptions;
        private bool _isRefreshingCameraOptions;
        private InspectionConfig _configuration;

        public bool ConfigurationChanged { get; private set; }

        public InspectionConfigEditorWindow(string configPath)
        {
            InitializeComponent();
            _configPath = configPath;
            _originalJson = File.Exists(configPath)
                ? File.ReadAllText(configPath, Encoding.UTF8)
                : string.Empty;

            _configuration = File.Exists(configPath)
                ? InspectionConfigManager.Deserialize(_originalJson)
                : new InspectionConfig
                {
                    PcName = Environment.MachineName,
                    ImageSavePath = "CapturedImages",
                    DisplayCount = 1,
                    Grabbers = new List<GrabberConfig>(),
                    Lights = new List<LightConfig>(),
                    Sequences = new List<SequenceConfig>()
                };
            EnsureGrabberConfigurations(_configuration);
            if (_configuration.Lights == null)
            {
                _configuration.Lights = new List<LightConfig>();
            }
            if (string.IsNullOrWhiteSpace(_configuration.ImageSavePath))
            {
                _configuration.ImageSavePath = "CapturedImages";
            }
            _originalSerializedConfiguration =
                InspectionConfigManager.Serialize(_configuration);

            _sequences = new ObservableCollection<SequenceConfig>(
                _configuration.Sequences ?? new List<SequenceConfig>());
            _grabbers = new ObservableCollection<GrabberConfig>(
                _configuration.Grabbers ?? new List<GrabberConfig>());
            PcNameTextBox.Text = _configuration.PcName ?? string.Empty;
            ImageSavePathTextBox.Text = _configuration.ImageSavePath ?? string.Empty;
            DisplayCountTextBox.Text = _configuration.DisplayCount.ToString();
            InspectionPassCheckBox.IsChecked = _configuration.InspectionPass;
            ImageSaveModeComboBox.SelectedValue = _configuration.ImageSaveMode;
            PlcConfig plc = _configuration.Plc ?? CreateDefaultPlcConfiguration();
            PlcTypeComboBox.SelectedIndex = 0;
            PlcIpAddressTextBox.Text = string.IsNullOrWhiteSpace(plc.IpAddress)
                ? "192.168.1.2"
                : plc.IpAddress;
            PlcPortTextBox.Text = (plc.Port > 0 ? plc.Port : 2004).ToString();
            PlcStationNumberTextBox.Text = plc.StationNumber.ToString();
            PlcScanTimeTextBox.Text = plc.ScanTime.ToString();
            PlcAddressTextBox.Text = plc.PlcAddress ?? string.Empty;
            PcAddressTextBox.Text = plc.PcAddress ?? string.Empty;
            RejectAddressTextBox.Text = string.IsNullOrWhiteSpace(plc.RejectAddress)
                ? "D201"
                : plc.RejectAddress;
            PlcModelNoAddressTextBox.Text = plc.ModelNoAddress ?? string.Empty;
            PlcStepNoAddressTextBox.Text = plc.StepNoAddress ?? string.Empty;
            PlcSerialNoAddressTextBox.Text = plc.SerialNoAddress ?? string.Empty;
            SequenceGrid.ItemsSource = _sequences;
            CameraGrid.ItemsSource = _grabbers;
            RefreshCameraIdOptions();
            RefreshDisplayIndexOptions();
            RefreshLightIndexOptions();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!SequenceGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                    !SequenceGrid.CommitEdit(DataGridEditingUnit.Row, true))
                {
                    throw new InvalidOperationException(
                        "시퀀스 설정 값이 올바른지 확인하세요. 밝기는 0~100의 정수여야 합니다.");
                }

                CameraGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                CameraGrid.CommitEdit(DataGridEditingUnit.Row, true);

                int displayCount;
                if (!int.TryParse(DisplayCountTextBox.Text, out displayCount))
                {
                    throw new InvalidOperationException("Display Count는 숫자여야 합니다.");
                }

                PlcConfig plc = ReadPlcConfiguration();
                List<GrabberConfig> grabbers = ReadGrabberConfigurations();
                if (!(ImageSaveModeComboBox.SelectedValue is ImageSaveMode imageSaveMode))
                {
                    throw new InvalidOperationException("이미지 저장 방식을 선택하세요.");
                }

                for (int index = 0; index < _sequences.Count; index++)
                {
                    _sequences[index].Index = index + 1;
                }

                var configuration = new InspectionConfig
                {
                    PcName = PcNameTextBox.Text.Trim(),
                    ImageSavePath = string.IsNullOrWhiteSpace(ImageSavePathTextBox.Text)
                        ? "CapturedImages"
                        : ImageSavePathTextBox.Text.Trim(),
                    DisplayCount = displayCount,
                    InspectionPass = InspectionPassCheckBox.IsChecked == true,
                    ImageSaveMode = imageSaveMode,
                    Plc = plc,
                    Grabbers = new List<GrabberConfig>(grabbers),
                    Lights = new List<LightConfig>(_configuration.Lights),
                    Sequences = new List<SequenceConfig>(_sequences)
                };
                ValidateConfiguration(configuration);

                string updatedJson = InspectionConfigManager.Serialize(configuration);
                if (updatedJson == _originalSerializedConfiguration)
                {
                    ConfigurationChanged = false;
                    DialogResult = true;
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                Json.Serialize(_configPath, configuration);

                ConfigurationChanged = true;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "설정 저장 실패:\n" + ex.Message,
                    "Inspection Configuration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var sequence = new SequenceConfig
            {
                Index = _sequences.Count + 1,
                Name = "New Sequence",
                Path = string.Empty,
                CameraIndex = CameraIdOptions.Count > 0 ? CameraIdOptions[0] : 0,
                DisplayIndex = DisplayIndexOptions.Count > 0
                    ? Math.Min(_sequences.Count + 1, DisplayIndexOptions.Count)
                    : 0,
                ExternalLightIndex = 0,
                ExternalLightBrightness = 100,
                TriggerStep = 1,
                Enabled = true
            };
            _sequences.Add(sequence);
            SequenceGrid.SelectedItem = sequence;
            SequenceGrid.ScrollIntoView(sequence);
        }

        private void EditSequencePathButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var sequence = button?.Tag as SequenceConfig;
            if (sequence == null)
            {
                return;
            }

            var dialog = new ProcessEditWindow(sequence.Path)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            sequence.Path = dialog.Path;
            SequenceGrid.Items.Refresh();
        }

        private void SequencePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null || !textBox.IsKeyboardFocusWithin)
            {
                return;
            }

            Grid grid = textBox.Parent as Grid;
            Popup popup = grid?.Children.OfType<Popup>().FirstOrDefault();
            if (popup == null)
            {
                return;
            }

            int caretIndex = textBox.CaretIndex;
            int tokenStart = textBox.Text.LastIndexOf('{', Math.Max(0, caretIndex - 1));
            popup.IsOpen = tokenStart >= 0 &&
                           textBox.Text.IndexOf('}', tokenStart) < caretIndex;
        }

        private void PathTokenListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listBox = sender as ListBox;
            var textBox = listBox?.Tag as TextBox;
            var selectedItem = listBox?.SelectedItem as ListBoxItem;
            if (textBox == null || selectedItem == null)
            {
                return;
            }

            InsertPathToken(textBox, selectedItem.Content.ToString());
        }

        private static void InsertPathToken(TextBox textBox, string token)
        {
            int caretIndex = textBox.CaretIndex;
            int tokenStart = textBox.Text.LastIndexOf('{', Math.Max(0, caretIndex - 1));
            if (tokenStart < 0)
            {
                return;
            }

            textBox.Text = textBox.Text.Substring(0, tokenStart) +
                           token +
                           textBox.Text.Substring(caretIndex);
            textBox.CaretIndex = tokenStart + token.Length;
            textBox.Focus();
        }

        private void BrowseImageSavePathButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Forms.FolderBrowserDialog
            {
                Description = "이미지 저장 폴더를 선택하세요.",
                ShowNewFolderButton = true
            };

            string initialPath = GetExistingBrowsePath(ImageSavePathTextBox.Text);
            if (!string.IsNullOrEmpty(initialPath))
            {
                dialog.SelectedPath = initialPath;
            }

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                ImageSavePathTextBox.Text = dialog.SelectedPath;
            }
        }

        private static string GetExistingBrowsePath(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath) ||
                configuredPath.Contains("{") ||
                configuredPath.Contains("}"))
            {
                return string.Empty;
            }

            string candidate = System.IO.Path.IsPathRooted(configuredPath)
                ? configuredPath
                : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath);

            if (File.Exists(candidate))
            {
                return System.IO.Path.GetDirectoryName(candidate);
            }

            return Directory.Exists(candidate) ? candidate : string.Empty;
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var sequence = SequenceGrid.SelectedItem as SequenceConfig;
            if (sequence == null)
            {
                MessageBox.Show(this, "삭제할 시퀀스를 선택하세요.", "Inspection Configuration",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _sequences.Remove(sequence);
            RenumberSequences();
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedSequence(-1);
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedSequence(1);
        }

        private void AddCameraButton_Click(object sender, RoutedEventArgs e)
        {
            int nextId = _grabbers.Count == 0 ? 1 : _grabbers.Max(item => item.Id) + 1;
            var camera = new GrabberConfig
            {
                Id = nextId,
                Type = GrabberType.Deediim,
                Name = $"Grabber {nextId}",
                IpAddress = "127.0.0.1",
                Port = 5000,
                SensorIndex = 0,
                PatternIndex = 0
            };
            _grabbers.Add(camera);
            RefreshCameraIdOptions();
            CameraGrid.SelectedItem = camera;
            CameraGrid.ScrollIntoView(camera);
        }

        private void AddHikCameraButton_Click(object sender, RoutedEventArgs e)
        {
            int nextId = _grabbers.Count == 0 ? 1 : _grabbers.Max(item => item.Id) + 1;
            var camera = new GrabberConfig
            {
                Id = nextId,
                Type = GrabberType.HikCamera,
                Name = $"HikCamera {nextId}",
                IpAddress = "192.168.102.200",
                SensorIndex = 0,
                PatternIndex = 0
            };
            _grabbers.Add(camera);
            RefreshCameraIdOptions();
            CameraGrid.SelectedItem = camera;
            CameraGrid.ScrollIntoView(camera);
        }

        private void DeleteCameraButton_Click(object sender, RoutedEventArgs e)
        {
            var camera = CameraGrid.SelectedItem as GrabberConfig;
            if (camera == null)
            {
                MessageBox.Show(this, "삭제할 카메라를 선택하세요.", "Inspection Configuration",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_sequences.Any(sequence => sequence.CameraIndex == camera.Id))
            {
                MessageBox.Show(
                    this,
                    "선택한 카메라를 사용하는 시퀀스가 있어 삭제할 수 없습니다.",
                    "Inspection Configuration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _grabbers.Remove(camera);
            RefreshCameraIdOptions();
        }

        private void CameraGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshCameraIdOptions));
        }

        private void DisplayCountTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshDisplayIndexOptions();
        }

        private void RefreshCameraIdOptions()
        {
            if (_isRefreshingCameraOptions || _grabbers == null)
            {
                return;
            }

            List<int> cameraIds = _grabbers
                .Where(camera => camera != null && camera.Id > 0)
                .Select(camera => camera.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            _isRefreshingCameraOptions = true;
            try
            {
                if (cameraIds.Count > 0 && _sequences != null)
                {
                    foreach (SequenceConfig sequence in _sequences)
                    {
                        if (!cameraIds.Contains(sequence.CameraIndex))
                        {
                            sequence.CameraIndex = cameraIds[0];
                        }
                    }
                }

                while (CameraIdOptions.Count > cameraIds.Count)
                {
                    CameraIdOptions.RemoveAt(CameraIdOptions.Count - 1);
                }

                for (int index = CameraIdOptions.Count; index < cameraIds.Count; index++)
                {
                    CameraIdOptions.Add(cameraIds[index]);
                }

                SequenceGrid?.Items.Refresh();
            }
            finally
            {
                _isRefreshingCameraOptions = false;
            }
        }

        private void RefreshDisplayIndexOptions()
        {
            if (_isRefreshingDisplayOptions || DisplayIndexOptions == null)
            {
                return;
            }

            int displayCount;
            if (!int.TryParse(DisplayCountTextBox?.Text, out displayCount) || displayCount < 1)
            {
                return;
            }

            _isRefreshingDisplayOptions = true;
            try
            {
                if (_sequences != null)
                {
                    foreach (SequenceConfig sequence in _sequences)
                    {
                        if (sequence.DisplayIndex < 1)
                        {
                            sequence.DisplayIndex = 1;
                        }
                        else if (sequence.DisplayIndex > displayCount)
                        {
                            sequence.DisplayIndex = displayCount;
                        }
                    }
                }

                while (DisplayIndexOptions.Count > displayCount)
                {
                    DisplayIndexOptions.RemoveAt(DisplayIndexOptions.Count - 1);
                }

                for (int displayIndex = DisplayIndexOptions.Count + 1;
                     displayIndex <= displayCount;
                     displayIndex++)
                {
                    DisplayIndexOptions.Add(displayIndex);
                }

                SequenceGrid?.Items.Refresh();
            }
            finally
            {
                _isRefreshingDisplayOptions = false;
            }
        }

        private void RefreshLightIndexOptions()
        {
            LightIndexOptions.Clear();
            LightIndexOptions.Add(new LightIndexOption(0, "0 (사용안함)"));
            LightIndexOptions.Add(new LightIndexOption(1, "1"));

            foreach (int lightIndex in (_configuration.Lights ?? new List<LightConfig>())
                         .Where(light => light != null && light.Id > 0)
                         .Select(light => light.Id)
                         .Where(lightIndex => lightIndex != 1)
                         .Distinct()
                         .OrderBy(id => id))
            {
                LightIndexOptions.Add(
                    new LightIndexOption(lightIndex, lightIndex.ToString()));
            }
        }

        private void MoveSelectedSequence(int direction)
        {
            int index = SequenceGrid.SelectedIndex;
            int newIndex = index + direction;
            if (index < 0 || newIndex < 0 || newIndex >= _sequences.Count)
            {
                return;
            }

            SequenceConfig sequence = _sequences[index];
            _sequences.RemoveAt(index);
            _sequences.Insert(newIndex, sequence);
            RenumberSequences();
            SequenceGrid.SelectedItem = sequence;
            SequenceGrid.ScrollIntoView(sequence);
        }

        private void RenumberSequences()
        {
            for (int index = 0; index < _sequences.Count; index++)
            {
                _sequences[index].Index = index + 1;
            }
            SequenceGrid.Items.Refresh();
        }

        private static void ValidateConfiguration(InspectionConfig configuration)
        {
            if (configuration == null ||
                configuration.Sequences == null ||
                configuration.Sequences.Count == 0)
            {
                throw new InvalidOperationException("Sequences 설정이 비어 있습니다.");
            }

            if (string.IsNullOrWhiteSpace(configuration.PcName))
            {
                throw new InvalidOperationException("PC Name을 입력하세요.");
            }

            if (configuration.Grabbers == null || configuration.Grabbers.Count == 0)
            {
                throw new InvalidOperationException("Grabber를 하나 이상 등록하세요.");
            }

            var grabberIds = new HashSet<int>();
            foreach (GrabberConfig grabber in configuration.Grabbers)
            {
                if (grabber == null || grabber.Id <= 0)
                {
                    throw new InvalidOperationException("Grabber ID는 1 이상이어야 합니다.");
                }

                if (!grabberIds.Add(grabber.Id))
                {
                    throw new InvalidOperationException($"Grabber ID {grabber.Id}가 중복되었습니다.");
                }

                if (string.IsNullOrWhiteSpace(grabber.Name))
                {
                    throw new InvalidOperationException($"Grabber ID {grabber.Id}의 이름을 입력하세요.");
                }

                IPAddress address;
                if (!IPAddress.TryParse(grabber.IpAddress, out address) ||
                    (grabber.Type == GrabberType.HikCamera &&
                     address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    throw new InvalidOperationException($"Grabber ID {grabber.Id}의 IP 주소가 올바르지 않습니다.");
                }

                if (grabber.Type == GrabberType.Deediim &&
                    (grabber.Port < 1 || grabber.Port > 65535))
                {
                    throw new InvalidOperationException($"Grabber ID {grabber.Id}의 Port는 1~65535 범위여야 합니다.");
                }

                if (grabber.Type == GrabberType.Deediim && grabber.SensorIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Grabber ID {grabber.Id}의 Sensor Index는 0 이상이어야 합니다.");
                }

                if (grabber.Type != GrabberType.Deediim &&
                    grabber.Type != GrabberType.HikCamera)
                {
                    throw new InvalidOperationException(
                        $"Grabber ID {grabber.Id}의 Type이 올바르지 않습니다.");
                }
            }

            var lightIds = new HashSet<int>();
            foreach (LightConfig light in configuration.Lights ?? new List<LightConfig>())
            {
                if (light == null || light.Id <= 0)
                {
                    throw new InvalidOperationException("조명 ID는 1 이상이어야 합니다.");
                }

                if (!lightIds.Add(light.Id))
                {
                    throw new InvalidOperationException($"조명 ID {light.Id}가 중복되었습니다.");
                }
            }

            bool hasEnabledSequence = false;
            int highestDisplayIndex = 0;
            foreach (SequenceConfig sequence in configuration.Sequences)
            {
                if (sequence == null || string.IsNullOrWhiteSpace(sequence.Name))
                {
                    throw new InvalidOperationException("모든 시퀀스의 이름을 입력하세요.");
                }

                if (sequence.Enabled && sequence.DisplayIndex <= 0)
                {
                    throw new InvalidOperationException("활성 시퀀스의 Display 번호는 1 이상이어야 합니다.");
                }

                if (sequence.Enabled && !grabberIds.Contains(sequence.CameraIndex))
                {
                    throw new InvalidOperationException(
                        $"시퀀스 '{sequence.Name}'의 Grabber ID {sequence.CameraIndex}가 등록되어 있지 않습니다.");
                }

                if (sequence.ExternalLightIndex < 0 ||
                    (sequence.ExternalLightIndex > 0 &&
                     sequence.ExternalLightIndex != 1 &&
                     !lightIds.Contains(sequence.ExternalLightIndex)))
                {
                    throw new InvalidOperationException(
                        $"시퀀스 '{sequence.Name}'의 외부 조명 ID {sequence.ExternalLightIndex}가 등록되어 있지 않습니다.");
                }

                if (sequence.ExternalLightBrightness < 0 ||
                    sequence.ExternalLightBrightness > 100)
                {
                    throw new InvalidOperationException(
                        $"시퀀스 '{sequence.Name}'의 조명 밝기는 0~100 범위여야 합니다.");
                }

                if (sequence.Enabled)
                {
                    hasEnabledSequence = true;
                    highestDisplayIndex = Math.Max(highestDisplayIndex, sequence.DisplayIndex);
                }
            }

            if (!hasEnabledSequence)
            {
                throw new InvalidOperationException("활성화된 시퀀스가 없습니다.");
            }

            if (configuration.DisplayCount < 1)
            {
                throw new InvalidOperationException("DisplayCount는 1 이상이어야 합니다.");
            }

            ValidatePlcConfiguration(configuration.Plc);
            if (highestDisplayIndex > configuration.DisplayCount)
            {
                throw new InvalidOperationException(
                    "DisplayCount가 활성 시퀀스의 Display 번호보다 작습니다.");
            }
        }

        private PlcConfig ReadPlcConfiguration()
        {
            string plcType = (PlcTypeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(plcType))
            {
                plcType = "LS";
            }

            string ipAddress = PlcIpAddressTextBox.Text.Trim();
            int port = ParsePort(PlcPortTextBox.Text, "PLC Port");
            int stationNumber = ParseNonNegative(PlcStationNumberTextBox.Text, "Station Number");
            int scanTime = ParsePositive(PlcScanTimeTextBox.Text, "Scan Time");

            return new PlcConfig
            {
                PlcType = plcType,
                IpAddress = ipAddress,
                Port = port,
                StationNumber = stationNumber,
                ScanTime = scanTime,
                PlcAddress = PlcAddressTextBox.Text.Trim(),
                PcAddress = PcAddressTextBox.Text.Trim(),
                RejectAddress = RejectAddressTextBox.Text.Trim(),
                ModelNoAddress = PlcModelNoAddressTextBox.Text.Trim(),
                StepNoAddress = PlcStepNoAddressTextBox.Text.Trim(),
                SerialNoAddress = PlcSerialNoAddressTextBox.Text.Trim()
            };
        }

        private List<GrabberConfig> ReadGrabberConfigurations()
        {
            if (_grabbers.Count == 0)
            {
                throw new InvalidOperationException("Grabber를 하나 이상 등록하세요.");
            }

            return new List<GrabberConfig>(_grabbers);
        }

        private static void ValidatePlcConfiguration(PlcConfig plc)
        {
            if (plc == null)
            {
                throw new InvalidOperationException("PLC 설정이 없습니다.");
            }

            IPAddress address;
            if (!IPAddress.TryParse(plc.IpAddress, out address))
            {
                throw new InvalidOperationException("PLC IP 주소가 올바르지 않습니다.");
            }

            if (plc.Port < 1 || plc.Port > 65535)
            {
                throw new InvalidOperationException("PLC Port는 1~65535 범위여야 합니다.");
            }

            if (!string.Equals(plc.PlcType, "LS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("PLC Type은 LS여야 합니다.");
            }

            if (plc.StationNumber < 0)
            {
                throw new InvalidOperationException("Station Number는 0 이상이어야 합니다.");
            }

            if (plc.ScanTime < 1)
            {
                throw new InvalidOperationException("Scan Time은 1ms 이상이어야 합니다.");
            }

        }

        private static int ParseNonNegative(string text, string name)
        {
            int value;
            if (!int.TryParse(text, out value) || value < 0)
            {
                throw new InvalidOperationException($"{name}은 0 이상 숫자여야 합니다.");
            }
            return value;
        }

        private static int ParsePort(string text, string name)
        {
            int value = ParsePositive(text, name);
            if (value > 65535)
            {
                throw new InvalidOperationException($"{name}는 1~65535 범위여야 합니다.");
            }

            return value;
        }

        private static double ParseNonNegativeDouble(string text, string name)
        {
            double value;
            if (!double.TryParse(text, out value) || value < 0)
            {
                throw new InvalidOperationException($"{name}은 0 이상 숫자여야 합니다.");
            }

            return value;
        }

        private static int ParsePositive(string text, string name)
        {
            int value;
            if (!int.TryParse(text, out value) || value < 1)
            {
                throw new InvalidOperationException($"{name}은 1 이상 숫자여야 합니다.");
            }
            return value;
        }

        private static int ParseBit(string text, string name)
        {
            int value = ParseNonNegative(text, name);
            if (value > 15)
            {
                throw new InvalidOperationException($"{name}은 0~15 범위여야 합니다.");
            }
            return value;
        }

        private static PlcConfig CreateDefaultPlcConfiguration()
        {
            return new PlcConfig
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

        private static void EnsureGrabberConfigurations(InspectionConfig configuration)
        {
            if (configuration.Grabbers == null || configuration.Grabbers.Count == 0)
            {
                var grabberIds = (configuration.Sequences ?? new List<SequenceConfig>())
                    .Select(sequence => sequence.CameraIndex)
                    .Where(id => id > 0)
                    .Distinct()
                    .OrderBy(id => id);

                configuration.Grabbers = grabberIds
                    .Select(id => new GrabberConfig
                    {
                        Id = id,
                        Name = $"Grabber {id}",
                        IpAddress = "127.0.0.1",
                        Port = 5000,
                        SensorIndex = id - 1,
                        PatternIndex = id - 1
                    })
                    .ToList();
            }

            if (configuration.Grabbers.Count == 0)
            {
                configuration.Grabbers.Add(new GrabberConfig
                {
                    Id = 1,
                    Name = "Grabber 1",
                    IpAddress = "127.0.0.1",
                    Port = 5000,
                    SensorIndex = 0,
                    PatternIndex = 0
                });
            }
        }

        public sealed class LightIndexOption
        {
            public int Index { get; }
            public string DisplayName { get; }

            public LightIndexOption(int index, string displayName)
            {
                Index = index;
                DisplayName = displayName;
            }
        }
    }
}
