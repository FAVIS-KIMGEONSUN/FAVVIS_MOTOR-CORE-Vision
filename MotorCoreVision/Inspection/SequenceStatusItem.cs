using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace FAVIS
{
    public sealed class SequenceStatusItem : INotifyPropertyChanged
    {
        private string _text;
        private Brush _foreground;

        public SequenceStatusItem(string sequenceName)
        {
            SequenceName = sequenceName;
            _text = $"[ ] {sequenceName}";
            _foreground = Brushes.Gray;
        }

        public string SequenceName { get; }

        public string Text
        {
            get { return _text; }
            set
            {
                if (_text == value)
                {
                    return;
                }

                _text = value;
                OnPropertyChanged();
            }
        }

        public Brush Foreground
        {
            get { return _foreground; }
            set
            {
                if (_foreground == value)
                {
                    return;
                }

                _foreground = value;
                OnPropertyChanged();
            }
        }

        public void Reset()
        {
            Text = $"[ ] {SequenceName}";
            Foreground = Brushes.Gray;
        }

        public void SetResult(bool passed, string elapsedText)
        {
            Text = $"[{(passed ? "OK" : "NG")}] {SequenceName} [{elapsedText}]";
            Foreground = passed ? Brushes.LimeGreen : Brushes.Red;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
