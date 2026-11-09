using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FAVIS
{
    public partial class TestWindow : Window
    {
        private readonly PlcController _plc;
        private readonly int _stepCount;
        private readonly IReadOnlyList<GrabberConfig> _grabberConfigurations;
        private readonly DeediimGrabber _grabber;
        private readonly ILightController _lightController;
        private CancellationTokenSource _sequenceCancellation;
        private bool _grabberRunning;
        private bool _lightIsOn;

        public TestWindow(
            PlcController plc,
            int stepCount,
            IReadOnlyList<GrabberConfig> grabberConfigurations,
            ILightController lightController)
        {
            InitializeComponent();
            _plc = plc ?? throw new ArgumentNullException(nameof(plc));
            _stepCount = stepCount;
            _grabberConfigurations = grabberConfigurations;
            _lightController = lightController ?? throw new ArgumentNullException(nameof(lightController));
            _grabber = new DeediimGrabber(grabberConfigurations);
            _grabber.MessageSent += Grabber_MessageSent;
            _grabber.MessageReceived += Grabber_MessageReceived;
            UpdateView();
        }

        private void LightBrightnessSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (LightBrightnessText != null)
            {
                LightBrightnessText.Text = $"{(int)LightBrightnessSlider.Value}%";
            }
        }

        private void SetLightBrightnessButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _lightController.SetBrightness((int)LightBrightnessSlider.Value);
                LightStatusText.Text =
                    $"밝기 설정: {(int)LightBrightnessSlider.Value}%";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "External Light",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LightOnButton_Click(object sender, RoutedEventArgs e)
        {
            SetLightOutput(true);
        }

        private void LightOffButton_Click(object sender, RoutedEventArgs e)
        {
            SetLightOutput(false);
        }

        private void SetLightOutput(bool isOn)
        {
            try
            {
                if (isOn)
                {
                    _lightController.On();
                }
                else
                {
                    _lightController.Off();
                }

                _lightIsOn = isOn;
                LightStatusText.Text = $"조명 출력: {(isOn ? "ON" : "OFF")}";
                LightOnButton.Background = isOn ? Brushes.LimeGreen : Brushes.Gray;
                LightOffButton.Background = isOn ? Brushes.Gray : Brushes.Red;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "External Light",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TriggerButton_Click(object sender, RoutedEventArgs e)
        {
            int modelNo;
            int stepNo;
            if (!int.TryParse(ModelNoTextBox.Text, out modelNo) ||
                !int.TryParse(StepNoTextBox.Text, out stepNo))
            {
                MessageBox.Show(this, "Model No와 Step No는 숫자여야 합니다.", "Test PLC",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _plc.SetInput(modelNo, stepNo, SerialNoTextBox.Text);
                _plc.SetTrigger(!_plc.Trigger);
                UpdateView();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Test PLC",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _plc.SetReset(!_plc.Reset);
            UpdateView();
        }

        private void PcSignalButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            string signal = button?.Tag as string;
            if (string.IsNullOrWhiteSpace(signal))
            {
                return;
            }

            PcBits bits = GetPcBits();
            switch (signal)
            {
                case "Ready":
                    bits = ToggleBit(bits, PcBits.Ready);
                    break;
                case "Busy":
                    bits = ToggleBit(bits, PcBits.Busy);
                    break;
                case "Grab":
                    bits = ToggleBit(bits, PcBits.Grab);
                    break;
                case "OK":
                    bits = ToggleBit(bits, PcBits.OK);
                    break;
                case "NG":
                    bits = ToggleBit(bits, PcBits.NG);
                    break;
                case "Complete":
                    bits = ToggleBit(bits, PcBits.Complete);
                    break;
                default:
                    return;
            }
            Thread.Sleep(100);
            _plc.WriteBits(bits);
            Thread.Sleep(100);
            UpdateView();
        }

        private async void RunSequenceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sequenceCancellation != null)
            {
                _sequenceCancellation.Cancel();
                return;
            }

            if (_stepCount < 1)
            {
                MessageBox.Show(this, "활성화된 시퀀스가 없습니다.", "Test PLC",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _sequenceCancellation = new CancellationTokenSource();
            RunSequenceButton.Content = "Stop Test";
            TriggerButton.IsEnabled = false;
            ResetButton.IsEnabled = false;

            try
            {
                await RunSequenceTestAsync(_stepCount, _sequenceCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                OutputStatusText.Text = "Sequence test stopped.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Test PLC",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _plc.SetTrigger(false);
                RunSequenceButton.Content = "Step All";
                TriggerButton.IsEnabled = true;
                ResetButton.IsEnabled = true;
                _sequenceCancellation.Dispose();
                _sequenceCancellation = null;
                UpdateView();
            }
        }

        private async void RunGrabberButton_Click(object sender, RoutedEventArgs e)
        {
            if (_grabberRunning)
            {
                return;
            }

            int sensorIndex;
            int patternIndex;
            if (!int.TryParse(GrabberCameraTextBox.Text, out sensorIndex) ||
                !int.TryParse(GrabberSequenceTextBox.Text, out patternIndex) ||
                sensorIndex < 0)
            {
                MessageBox.Show(this, "SensorIndex와 PatternIndex는 0 이상이어야 합니다.", "Grabber",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _grabberRunning = true;
            RunGrabberButton.IsEnabled = false;
            try
            {
                GrabberConfig configuration = _grabberConfigurations.FirstOrDefault(
                    item => item != null && item.SensorIndex == sensorIndex);
                if (configuration == null)
                {
                    throw new InvalidOperationException(
                        $"SensorIndex {sensorIndex}에 대한 Grabber 설정을 찾을 수 없습니다.");
                }

                _grabber.Configure(configuration.Id, patternIndex);
                ImageCollection images = await Task.Run(() => _grabber.Grab());
                GrabberDisplay.Source = images;
                ReceivedMessageTextBox.Text +=
                    Environment.NewLine +
                    $"Images: {images.Count}";
            }
            catch (Exception ex)
            {
                ReceivedMessageTextBox.Text +=
                    Environment.NewLine + $"ERROR: {ex.Message}";
            }
            finally
            {
                _grabberRunning = false;
                RunGrabberButton.IsEnabled = true;
            }
        }

        private void Grabber_MessageSent(string message)
        {
            Dispatcher.Invoke(() => SentMessageTextBox.Text = message);
        }

        private void Grabber_MessageReceived(string message)
        {
            Dispatcher.Invoke(() => ReceivedMessageTextBox.Text = message);
        }

        private async Task RunSequenceTestAsync(int stepCount, CancellationToken cancellationToken)
        {
            for (int stepNo = 1; stepNo <= stepCount; stepNo++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _plc.SetInput(1, stepNo, SerialNoTextBox.Text);
                _plc.SetTrigger(true);
                OutputStatusText.Text = $"Step {stepNo}/{stepCount}: waiting for Complete...";
                UpdateView();

                DateTime timeout = DateTime.UtcNow.AddSeconds(60);
                while (!_plc.Complete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= timeout)
                    {
                        throw new TimeoutException($"Step {stepNo} 검사 완료를 기다리는 중 시간 초과되었습니다.");
                    }

                    await Task.Delay(50, cancellationToken);
                }

                _plc.SetTrigger(false);
                UpdateView();
            }

            OutputStatusText.Text = $"Sequence test completed: 1-{stepCount}";
        }

        private void UpdateView()
        {
            TriggerButton.Content = $"Trigger: {(_plc.Trigger ? "ON" : "OFF")}";
            TriggerButton.Background = _plc.Trigger ? Brushes.LimeGreen : Brushes.Gray;
            ResetButton.Content = $"Reset: {(_plc.Reset ? "ON" : "OFF")}";
            ResetButton.Background = _plc.Reset ? Brushes.Red : Brushes.Gray;
            UpdateSignalButton(ReadyButton, "Ready", _plc.Ready, Brushes.LimeGreen);
            UpdateSignalButton(BusyButton, "Busy", _plc.Busy, Brushes.Orange);
            UpdateSignalButton(GrabButton, "Grab", _plc.Grab, Brushes.LimeGreen);
            UpdateSignalButton(OkButton, "OK", _plc.Ok, Brushes.LimeGreen);
            UpdateSignalButton(NgButton, "NG", _plc.Ng, Brushes.Red);
            UpdateSignalButton(CompleteButton, "Complete", _plc.Complete, Brushes.LimeGreen);
            OutputStatusText.Text =
                $"Ready: {_plc.Ready}\nBusy: {_plc.Busy}\n" +
                $"Grab: {_plc.Grab}\n" +
                $"OK: {_plc.Ok}\nNG: {_plc.Ng}\nComplete: {_plc.Complete}\n";
        }

        private PcBits GetPcBits()
        {
            PcBits bits = PcBits.None;
            if (_plc.Ready)
            {
                bits |= PcBits.Ready;
            }
            if (_plc.Busy)
            {
                bits |= PcBits.Busy;
            }
            if (_plc.Grab)
            {
                bits |= PcBits.Grab;
            }
            if (_plc.Ok)
            {
                bits |= PcBits.OK;
            }
            if (_plc.Ng)
            {
                bits |= PcBits.NG;
            }
            if (_plc.Complete)
            {
                bits |= PcBits.Complete;
            }
            return bits;
        }

        private static PcBits ToggleBit(PcBits bits, PcBits bit)
        {
            return bits.HasFlag(bit) ? bits & ~bit : bits | bit;
        }

        private static void UpdateSignalButton(
            Button button,
            string name,
            bool isOn,
            Brush onBrush)
        {
            button.Content = $"{name}: {(isOn ? "ON" : "OFF")}";
            button.Background = isOn ? onBrush : Brushes.Gray;
        }

        protected override void OnClosed(EventArgs e)
        {
            _sequenceCancellation?.Cancel();
            _plc.SetTrigger(false);
            if (_lightIsOn)
            {
                try
                {
                    _lightController.Off();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "External Light",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            GrabberDisplay.Source = null;
            _grabber.MessageSent -= Grabber_MessageSent;
            _grabber.MessageReceived -= Grabber_MessageReceived;
            _grabber.Dispose();
            base.OnClosed(e);
        }
    }
}
