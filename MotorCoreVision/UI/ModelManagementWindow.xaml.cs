using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace FAVIS
{
    public partial class ModelManagementWindow : Window
    {
        private readonly ObservableCollection<ModelConfig> _models;
        public ModelConfig SelectedModel { get; private set; }

        public ModelManagementWindow()
        {
            InitializeComponent();
            _models = new ObservableCollection<ModelConfig>(
                ModelConfigManager.LoadModels());
            ModelGrid.ItemsSource = _models;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            int nextModelNo = _models.Count == 0
                ? 1
                : _models.Max(item => item.ModelNo) + 1;
            var model = new ModelConfig
            {
                ModelNo = nextModelNo,
                ModelName = $"Model {nextModelNo}"
            };
            _models.Add(model);
            ModelGrid.SelectedItem = model;
            ModelGrid.ScrollIntoView(model);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var model = ModelGrid.SelectedItem as ModelConfig;
            if (model == null)
            {
                return;
            }

            _models.Remove(model);
        }

        private void UseButton_Click(object sender, RoutedEventArgs e)
        {
            var model = ModelGrid.SelectedItem as ModelConfig;
            if (model == null)
            {
                MessageBox.Show(this, "사용할 모델을 선택하세요.", "Model",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveModels();
            SelectedModel = model;
            ModelConfigManager.SaveLastModelNo(model.ModelNo);
            DialogResult = true;
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var model = ModelGrid.SelectedItem as ModelConfig;
            if (model == null)
            {
                MessageBox.Show(this, "폴더를 열 모델을 선택하세요.", "Model",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string folderPath = ModelConfigManager.GetModelDirectory(model.ModelNo);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            SaveModels();
            DialogResult = false;
        }

        private void SaveModels()
        {
            ModelGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            ModelGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            if (_models.Any(model => model.ModelNo <= 0) ||
                _models.GroupBy(model => model.ModelNo).Any(group => group.Count() > 1) ||
                _models.Any(model => string.IsNullOrWhiteSpace(model.ModelName)))
            {
                throw new InvalidOperationException("모델 번호는 중복 없이 1 이상이어야 하며 모델 이름을 입력해야 합니다.");
            }

            ModelConfigManager.SaveModels(new List<ModelConfig>(_models));
        }
    }
}
