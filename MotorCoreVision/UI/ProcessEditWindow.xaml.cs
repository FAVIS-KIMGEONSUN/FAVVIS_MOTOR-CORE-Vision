using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FAVIS
{
    public partial class ProcessEditWindow : Window
    {
        public ObservableCollection<ProcessPathItem> MaskItems { get; } =
            new ObservableCollection<ProcessPathItem>();
        public ObservableCollection<ProcessPathItem> GlobalLocalItems { get; } =
            new ObservableCollection<ProcessPathItem>();

        public string Path => PathPreviewTextBox.Text;

        public ProcessEditWindow(string path)
        {
            InitializeComponent();
            DataContext = this;

            SubscribeToItems(MaskItems);
            SubscribeToItems(GlobalLocalItems);

            string[] groups = (path ?? string.Empty).Split('|');
            AddInitialItems(MaskItems, groups, 0);
            AddInitialPhaseItems(GlobalLocalItems, groups, 1, "Global");
            AddInitialPhaseItems(GlobalLocalItems, groups, 2, "Local");
            RefreshPathPreview();
        }

        private static void AddInitialItems(
            ObservableCollection<ProcessPathItem> items,
            string[] groups,
            int groupIndex)
        {
            if (groupIndex >= groups.Length)
            {
                return;
            }

            foreach (string value in groups[groupIndex].Split(';'))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    items.Add(new ProcessPathItem(value));
                }
            }
        }

        private static void AddInitialPhaseItems(
            ObservableCollection<ProcessPathItem> items,
            string[] groups,
            int groupIndex,
            string phase)
        {
            if (groupIndex >= groups.Length)
            {
                return;
            }

            foreach (string value in groups[groupIndex].Split(';'))
            {
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                string template = value.Replace(phase, "{Phase}");
                if (!items.Any(item => string.Equals(
                    item.Value,
                    template,
                    StringComparison.Ordinal)))
                {
                    items.Add(new ProcessPathItem(template));
                }
            }
        }

        private void SubscribeToItems(ObservableCollection<ProcessPathItem> items)
        {
            items.CollectionChanged += Items_CollectionChanged;
        }

        private void Items_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ProcessPathItem item in e.OldItems)
                {
                    item.PropertyChanged -= PathItem_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (ProcessPathItem item in e.NewItems)
                {
                    item.PropertyChanged += PathItem_PropertyChanged;
                }
            }

            RefreshPathPreview();
        }

        private void PathItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProcessPathItem.Value))
            {
                RefreshPathPreview();
            }
        }

        private void RefreshPathPreview()
        {
            PathPreviewTextBox.Text =
                string.Join(
                    "|",
                    SerializeItems(MaskItems),
                    SerializePhaseItems("Global"),
                    SerializePhaseItems("Local"));
        }

        private string SerializePhaseItems(string phase)
        {
            return string.Join(
                ";",
                GlobalLocalItems.Select(item =>
                    (item.Value ?? string.Empty).Replace("{Phase}", phase)));
        }

        private static string SerializeItems(ObservableCollection<ProcessPathItem> items)
        {
            return string.Join(";", items.Select(item => item.Value));
        }

        private ListBox GetListBox(string groupName)
        {
            switch (groupName)
            {
                case "Mask":
                    return MaskListBox;
                case "GlobalLocal":
                    return GlobalLocalListBox;
                default:
                    throw new InvalidOperationException("알 수 없는 경로 그룹입니다: " + groupName);
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            ListBox listBox = GetListBox(((Button)sender).Tag as string);
            var items = (ObservableCollection<ProcessPathItem>)listBox.ItemsSource;
            var item = new ProcessPathItem(string.Empty);
            items.Add(item);
            listBox.SelectedItem = item;
            listBox.ScrollIntoView(item);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            ListBox listBox = GetListBox(((Button)sender).Tag as string);
            int selectedIndex = listBox.SelectedIndex;
            if (selectedIndex < 0)
            {
                return;
            }

            var items = (ObservableCollection<ProcessPathItem>)listBox.ItemsSource;
            items.RemoveAt(selectedIndex);
            if (items.Count > 0)
            {
                listBox.SelectedIndex = Math.Min(selectedIndex, items.Count - 1);
            }
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedItem(((Button)sender).Tag as string, -1);
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedItem(((Button)sender).Tag as string, 1);
        }

        private void MoveSelectedItem(string groupName, int offset)
        {
            ListBox listBox = GetListBox(groupName);
            int currentIndex = listBox.SelectedIndex;
            int targetIndex = currentIndex + offset;
            if (currentIndex < 0 || targetIndex < 0 || targetIndex >= listBox.Items.Count)
            {
                return;
            }

            var items = (ObservableCollection<ProcessPathItem>)listBox.ItemsSource;
            items.Move(currentIndex, targetIndex);
            listBox.SelectedIndex = targetIndex;
            listBox.ScrollIntoView(listBox.SelectedItem);
        }

        private void PathItemTextBox_TextChanged(object sender, TextChangedEventArgs e)
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
            listBox.SelectedItem = null;
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

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }

    public sealed class ProcessPathItem : INotifyPropertyChanged
    {
        private string _value;

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                {
                    return;
                }

                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public ProcessPathItem(string value)
        {
            _value = value;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
