using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FAVIS
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
        }

        public void SetProgress(int progress, string message)
        {
            LoadingText.Text = message;

            double target = Math.Max(
                LoadingProgressBar.Minimum,
                Math.Min(LoadingProgressBar.Maximum, progress));
            double start = LoadingProgressBar.Value;
            int frameCount = Math.Max(8, (int)Math.Abs(target - start) / 3);

            for (int frame = 1; frame <= frameCount; frame++)
            {
                double ratio = (double)frame / frameCount;
                double easedRatio = 1 - Math.Pow(1 - ratio, 3);
                LoadingProgressBar.Value = start + ((target - start) * easedRatio);
                Dispatcher.Invoke(
                    DispatcherPriority.Render,
                    new Action(delegate { }));
                Thread.Sleep(12);
            }

            LoadingProgressBar.Value = target;
        }
    }
}
