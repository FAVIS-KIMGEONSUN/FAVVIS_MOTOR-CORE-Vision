using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DSProcessingTool.Model;
using DSProcessingTool.Plugins;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FAVIS
{
    public partial class ToolEditWindow : Window
    {
        private static readonly string[] SliderFields =
        {
            "AreaMin_slider",
            "AreaMax_slider",
            "AreaRatioMin_slider",
            "AreaRatioMax_slider",
            "WidthMin_slider",
            "WidthMax_slider",
            "LengthMin_slider",
            "LengthMax_slider",
            "SizeRatioMin_slider",
            "SizeRatioMax_slider",
            "Int_slider",
            "Int_slider2",
            "Param_slider1",
            "Param_slider2",
            "Param_slider3",
            "ScaleMin_slider",
            "ScaleMax_slider",
            "Slider1_slider"
        };

        private static readonly string[] ImageIndexFields =
        {
            "Combo_ImageIndex",
            "Combo_ImageIndex2",
            "Combo_ImageIndex3",
            "Combo_ImageIndex4"
        };

        private readonly string _filePath;
        private readonly JObject _root;
        private readonly ObservableCollection<ToolEntry> _tools =
            new ObservableCollection<ToolEntry>();
        private readonly IReadOnlyList<FilterDescriptor> _availableTools;
        private readonly List<EditorValue> _editorValues = new List<EditorValue>();
        private int _inputImageCount;
        private ToolEntry _selectedTool;
        private bool _isUpdatingSelection;

        public ToolEditWindow(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Process 파일 경로가 비어 있습니다.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Process 설정 파일을 찾을 수 없습니다.", filePath);
            }

            InitializeComponent();
            _filePath = filePath;
            _root = JObject.Parse(File.ReadAllText(filePath, Encoding.UTF8));
            _availableTools = FilterCatalog.Instance.Filters
                .Where(item =>
                    item.FilterClass != null &&
                    !item.FilterClass.IsAbstract &&
                    typeof(Image_Filter).IsAssignableFrom(item.FilterClass))
                .GroupBy(item => item.FilterClass)
                .Select(group => group.First())
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
                .ToList();

            AvailableToolsComboBox.ItemsSource = _availableTools;
            ToolsListBox.ItemsSource = _tools;
            LoadTools();
            if (_tools.Count > 0)
            {
                ToolsListBox.SelectedIndex = 0;
            }
        }

        private void LoadTools()
        {
            var loadedKeys = new HashSet<string>(StringComparer.Ordinal);
            JArray parameterList = _root["ParamList"] as JArray;
            if (parameterList != null)
            {
                foreach (JToken token in parameterList)
                {
                    string key = token.Type == JTokenType.String
                        ? token.Value<string>()
                        : null;
                    if (string.IsNullOrWhiteSpace(key) ||
                        string.Equals(key, "ParamInfo", StringComparison.Ordinal) ||
                        loadedKeys.Contains(key))
                    {
                        continue;
                    }

                    JProperty property = _root.Property(key);
                    if (property != null && AddToolEntry(property))
                    {
                        loadedKeys.Add(key);
                    }
                }
            }

            foreach (JProperty property in _root.Properties())
            {
                if (!loadedKeys.Contains(property.Name))
                {
                    AddToolEntry(property);
                }
            }

            RenumberTools();
            LoadImageReferences();
        }

        private bool AddToolEntry(JProperty property)
        {
            JObject data = property.Value as JObject;
            if (data == null || data["Name"] == null || data["Index"] == null)
            {
                return false;
            }

            string typeName = GetToolTypeName(property.Name);
            FilterDescriptor descriptor = FindDescriptor(typeName);
            string displayName = descriptor?.DisplayName ?? data.Value<string>("Name");
            int index = data.Value<int?>("Index") ?? _tools.Count;
            _tools.Add(new ToolEntry(
                typeName,
                property.Name,
                displayName,
                data,
                index));
            return true;
        }

        private void ToolsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingSelection)
            {
                return;
            }

            string validationError;
            if (!CommitEditorValues(out validationError))
            {
                RestoreSelectedTool();
                MessageBox.Show(this, validationError, "Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _selectedTool = ToolsListBox.SelectedItem as ToolEntry;
            BuildParameterEditors();
        }

        private void BuildParameterEditors()
        {
            ParametersPanel.Children.Clear();
            _editorValues.Clear();
            if (_selectedTool == null)
            {
                return;
            }

            FilterDescriptor descriptor = FindDescriptor(_selectedTool.TypeName);
            if (descriptor == null)
            {
                ParametersPanel.Children.Add(new TextBlock
                {
                    Text = "이 툴의 DLL 파라미터 정의를 찾을 수 없습니다.",
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            var model = Activator.CreateInstance(descriptor.FilterClass) as Image_Filter;
            if (model == null)
            {
                throw new InvalidOperationException(
                    "툴 파라미터 모델을 만들 수 없습니다: " + descriptor.DisplayName);
            }

            ApplyDataToModel(model, _selectedTool.Data);
            AddTextEditor("툴", "Name", model.Name, false);
            AddTextEditor("결과 이름", "ResultName", _selectedTool.Data.Value<string>("ResultName"), true);
            AddBooleanEditor("결과 이미지 저장", "SaveUse", _selectedTool.Data.Value<bool?>("SaveUse") ?? false);
            AddBooleanEditor("빠른 모드", "FastMode", _selectedTool.Data.Value<bool?>("FastMode") ?? false);

            AddImageIndexEditor(model, 0);
            AddImageIndexEditor(model, 1);
            AddImageIndexEditor(model, 2);
            AddImageIndexEditor(model, 3);

            AddComboEditor(
                model,
                "Combo_SelectedIndex",
                "Combo_name",
                "Combo_items",
                "Visibility_Combo");
            AddComboEditor(
                model,
                "Combo_SelectedIndex2",
                "Combo_name2",
                "Combo_items2",
                "Visibility_Combo2");

            AddSliderEditor(model, "Int_slider", "Int_name", "Visibility_IntValue");
            AddSliderEditor(model, "Int_slider2", "Int_name2", "Visibility_IntValue2");
            AddSliderEditor(model, "Param_slider1", "Param_name1", "Visibility_Param1");
            AddSliderEditor(model, "Param_slider2", "Param_name2", "Visibility_Param2");
            AddSliderEditor(model, "Param_slider3", "Param_name3", "Visibility_Param3");
            AddSliderEditor(model, "ScaleMin_slider", "Min_name", "Visibility_ScaleMin");
            AddSliderEditor(model, "ScaleMax_slider", "Max_name", "Visibility_ScaleMax");
            AddSliderEditor(model, "Slider1_slider", "Slider1_name", "Visibility_Slider1");

            AddSliderEditor(model, "AreaMin_slider", "AreaMin_name", "Visibility_AreaMin");
            AddSliderEditor(model, "AreaMax_slider", "AreaMax_name", "Visibility_AreaMax");
            AddSliderEditor(model, "AreaRatioMin_slider", "AreaRatioMin_name", "Visibility_AreaRatioMin");
            AddSliderEditor(model, "AreaRatioMax_slider", "AreaRatioMax_name", "Visibility_AreaRatioMax");
            AddSliderEditor(model, "WidthMin_slider", "WidthMin_name", "Visibility_WidthMin");
            AddSliderEditor(model, "WidthMax_slider", "WidthMax_name", "Visibility_WidthMax");
            AddSliderEditor(model, "LengthMin_slider", "LengthMin_name", "Visibility_LengthMin");
            AddSliderEditor(model, "LengthMax_slider", "LengthMax_name", "Visibility_LengthMax");
            AddSliderEditor(model, "SizeRatioMin_slider", "SizeRatioMin_name", "Visibility_SizeRatioMin");
            AddSliderEditor(model, "SizeRatioMax_slider", "SizeRatioMax_name", "Visibility_SizeRatioMax");

            AddComboEditor(model, "Combo_AreaSelectedIndex", "Area", "Combo_Area", "Visibility_ComboArea");
            AddComboEditor(model, "Combo_AreaRatioSelectedIndex", "Area ratio", "Combo_AreaRatio", "Visibility_ComboAreaRatio");
            AddComboEditor(model, "Combo_WidthSelectedIndex", "Width", "Combo_Width", "Visibility_ComboAreaWidth");
            AddComboEditor(model, "Combo_LengthSelectedIndex", "Length", "Combo_Length", "Visibility_ComboAreaLength");
            AddComboEditor(model, "Combo_SizeRatioSelectedIndex", "Size ratio", "Combo_SizeRatio", "Visibility_ComboAreaSizeRatio");
        }

        private void AddImageIndexEditor(Image_Filter model, int imageNumber)
        {
            string visibilityName = "Visibility_InputImage" + (imageNumber + 1);
            string field = ImageIndexFields[imageNumber];
            ImageReference selectedReference;
            if (!_selectedTool.ImageReferences.TryGetValue(field, out selectedReference))
            {
                selectedReference = DecodeImageReference(
                    _selectedTool.Data.Value<int?>(field) ?? 0);
                _selectedTool.ImageReferences[field] = selectedReference;
            }

            if (!IsControlVisible(model, visibilityName) &&
                !selectedReference.IsUnresolved)
            {
                return;
            }

            string label = imageNumber == 0
                ? "입력 이미지"
                : "입력 이미지 " + (imageNumber + 1);
            JArray inputNames = _root["InputNameList"] as JArray;
            var options = new List<ImageChoice>();
            if (inputNames != null)
            {
                for (int index = 0; index < inputNames.Count; index++)
                {
                    string name = inputNames[index]?.ToString();
                    options.Add(new ImageChoice(
                        ImageReference.ForInput(index),
                        "입력 " + index.ToString(CultureInfo.InvariantCulture) +
                        (string.IsNullOrWhiteSpace(name) ? string.Empty : " - " + name)));
                }
            }

            foreach (ToolEntry source in _tools)
            {
                string resultName = source.Data.Value<string>("ResultName");
                string sourceLabel =
                    $"{source.Index + 1}. {source.Data.Value<string>("Name")} [{source.TypeName}]";
                if (!string.IsNullOrWhiteSpace(resultName))
                {
                    sourceLabel += " - " + resultName;
                }

                options.Add(new ImageChoice(
                    ImageReference.ForTool(source),
                    "툴 결과: " + sourceLabel));
            }

            ImageChoice selectedChoice = options.FirstOrDefault(item =>
                item.Reference.RefersToSameImage(selectedReference));
            if (selectedChoice == null)
            {
                string labelText = selectedReference.IsUnused
                    ? "사용 안 함 (" +
                      selectedReference.UnresolvedIndex.ToString(CultureInfo.InvariantCulture) + ")"
                    : "해석할 수 없는 이미지 인덱스: " +
                      selectedReference.UnresolvedIndex.ToString(CultureInfo.InvariantCulture);
                selectedChoice = new ImageChoice(selectedReference, labelText);
                options.Add(selectedChoice);
            }

            var comboBox = new ComboBox
            {
                ItemsSource = options,
                DisplayMemberPath = nameof(ImageChoice.Label),
                SelectedItem = selectedChoice,
                MinWidth = 180,
                Background = System.Windows.Media.Brushes.White,
                Foreground = System.Windows.Media.Brushes.Black
            };
            AddEditorRow(label, comboBox);
            _editorValues.Add(new EditorValue(field, comboBox, EditorValueKind.ImageReference));
        }

        private void AddComboEditor(
            Image_Filter model,
            string selectedField,
            string labelProperty,
            string optionsProperty,
            string visibilityProperty)
        {
            if (!IsControlVisible(model, visibilityProperty))
            {
                return;
            }

            string label = GetStringProperty(model, labelProperty);
            if (string.IsNullOrWhiteSpace(label))
            {
                label = selectedField;
            }

            object rawOptions = GetPropertyValue(model, optionsProperty);
            var options = rawOptions as System.Collections.IEnumerable;
            if (options == null)
            {
                return;
            }

            var choices = new List<Choice>();
            int index = 0;
            foreach (object option in options)
            {
                choices.Add(new Choice(index++, option?.ToString() ?? string.Empty));
            }

            if (choices.Count == 0)
            {
                return;
            }

            int selected = _selectedTool.Data.Value<int?>(selectedField) ?? 0;
            if (choices.All(item => item.Value != selected))
            {
                choices.Add(new Choice(selected, selected.ToString(CultureInfo.InvariantCulture)));
            }

            AddChoiceEditor(label, selectedField, choices, selected);
        }

        private void AddSliderEditor(Image_Filter model, string field, string labelProperty, string visibilityProperty)
        {
            if (!IsControlVisible(model, visibilityProperty))
            {
                return;
            }

            var slider = GetPropertyValue(model, field) as SliderProperty;
            if (slider == null)
            {
                return;
            }

            string label = GetStringProperty(model, labelProperty);
            if (string.IsNullOrWhiteSpace(label))
            {
                label = field;
            }

            double value = _selectedTool.Data.Value<double?>(field) ?? slider.Value;
            AddNumericEditor(
                label,
                field,
                value,
                slider.Minimum,
                slider.Maximum);
        }

        private void AddTextEditor(string label, string field, string value, bool editable)
        {
            var textBox = new TextBox
            {
                Text = value ?? string.Empty,
                IsReadOnly = !editable,
                Background = editable ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.DimGray,
                Foreground = System.Windows.Media.Brushes.Black
            };
            AddEditorRow(label, textBox);
            if (editable)
            {
                _editorValues.Add(new EditorValue(field, textBox, EditorValueKind.String));
            }
        }

        private void AddBooleanEditor(string label, string field, bool value)
        {
            var checkBox = new CheckBox
            {
                IsChecked = value,
                VerticalAlignment = VerticalAlignment.Center
            };
            AddEditorRow(label, checkBox);
            _editorValues.Add(new EditorValue(field, checkBox, EditorValueKind.Boolean));
        }

        private void AddChoiceEditor(string label, string field, IList<Choice> choices, int selected)
        {
            var comboBox = new ComboBox
            {
                ItemsSource = choices,
                DisplayMemberPath = nameof(Choice.Label),
                SelectedValuePath = nameof(Choice.Value),
                SelectedValue = selected,
                MinWidth = 180,
                Background = System.Windows.Media.Brushes.White,
                Foreground = System.Windows.Media.Brushes.Black
            };
            AddEditorRow(label, comboBox);
            _editorValues.Add(new EditorValue(field, comboBox, EditorValueKind.Choice));
        }

        private void AddNumericEditor(
            string label,
            string field,
            double value,
            double minimum,
            double maximum)
        {
            var textBox = new TextBox
            {
                Text = value.ToString("G", CultureInfo.InvariantCulture),
                Background = System.Windows.Media.Brushes.White,
                Foreground = System.Windows.Media.Brushes.Black
            };
            string range = double.IsNaN(minimum) ||
                double.IsNaN(maximum) ||
                maximum <= minimum ||
                value < minimum ||
                value > maximum
                ? string.Empty
                : $"  ({minimum:G} - {maximum:G})";
            AddEditorRow(label + range, textBox);
            _editorValues.Add(new EditorValue(field, textBox, EditorValueKind.Number));
        }

        private void AddEditorRow(string label, Control control)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(labelBlock);
            grid.Children.Add(control);
            ParametersPanel.Children.Add(grid);
        }

        private bool CommitEditorValues(out string error)
        {
            error = null;
            if (_selectedTool == null)
            {
                return true;
            }

            foreach (EditorValue editor in _editorValues)
            {
                JToken value;
                if (editor.Kind == EditorValueKind.String)
                {
                    value = ((TextBox)editor.Control).Text;
                }
                else if (editor.Kind == EditorValueKind.Boolean)
                {
                    value = ((CheckBox)editor.Control).IsChecked == true;
                }
                else if (editor.Kind == EditorValueKind.Choice)
                {
                    ComboBox comboBox = (ComboBox)editor.Control;
                    if (comboBox.SelectedValue == null)
                    {
                        error = "항목을 선택하세요: " + editor.Field;
                        return false;
                    }

                    value = Convert.ToInt32(comboBox.SelectedValue, CultureInfo.InvariantCulture);
                }
                else if (editor.Kind == EditorValueKind.ImageReference)
                {
                    ComboBox comboBox = (ComboBox)editor.Control;
                    ImageChoice choice = comboBox.SelectedItem as ImageChoice;
                    if (choice == null)
                    {
                        error = "이미지 입력을 선택하세요: " + editor.Field;
                        return false;
                    }

                    _selectedTool.ImageReferences[editor.Field] = choice.Reference;
                    int imageIndex;
                    if (!TryGetImageIndex(
                        choice.Reference,
                        _tools,
                        out imageIndex))
                    {
                        error = "선택한 툴 결과를 이미지 인덱스로 변환할 수 없습니다: " +
                            editor.Field;
                        return false;
                    }

                    value = imageIndex;
                }
                else
                {
                    double number;
                    string text = ((TextBox)editor.Control).Text;
                    if (!double.TryParse(
                            text,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out number) ||
                        double.IsNaN(number) ||
                        double.IsInfinity(number))
                    {
                        error = $"{editor.Field} 값은 유효한 숫자여야 합니다.";
                        return false;
                    }

                    JToken existing = _selectedTool.Data[editor.Field];
                    value = existing != null &&
                        existing.Type == JTokenType.Integer &&
                        number == Math.Truncate(number)
                            ? (JToken)new JValue((long)number)
                            : new JValue(number);
                }

                _selectedTool.Data[editor.Field] = value;
            }

            _selectedTool.DisplayName =
                $"{_selectedTool.Index + 1}. {_selectedTool.Data.Value<string>("Name")} [{_selectedTool.TypeName}]";
            ToolsListBox.Items.Refresh();
            return true;
        }

        private void AddToolButton_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!CommitEditorValues(out error))
            {
                MessageBox.Show(this, error, "Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var descriptor = AvailableToolsComboBox.SelectedItem as FilterDescriptor;
            if (descriptor == null)
            {
                MessageBox.Show(this, "추가할 툴을 선택하세요.", "Tool", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var model = Activator.CreateInstance(descriptor.FilterClass) as Image_Filter;
            if (model == null)
            {
                MessageBox.Show(this, "툴 기본 파라미터를 만들 수 없습니다.", "Tool", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            JObject data = CreateToolData(model, _tools.Count);
            var entry = new ToolEntry(
                descriptor.FilterClass.Name,
                MakeToolKey(descriptor.FilterClass.Name, _tools.Count),
                descriptor.DisplayName,
                data,
                _tools.Count);
            if (!CanChangeToolStructure())
            {
                return;
            }

            InitializeImageReferences(entry);
            _tools.Add(entry);
            RenumberTools();
            ToolsListBox.SelectedItem = entry;
        }

        private void DeleteToolButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = ToolsListBox.SelectedItem as ToolEntry;
            if (selected == null)
            {
                return;
            }

            string error;
            if (!CommitEditorValues(out error))
            {
                MessageBox.Show(this, error, "Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!CanChangeToolStructure())
            {
                return;
            }

            List<ToolEntry> consumers = _tools
                .Where(tool => tool.ImageReferences.Values.Any(reference =>
                    ReferenceEquals(reference.SourceTool, selected)))
                .ToList();
            if (consumers.Count > 0)
            {
                string names = string.Join(
                    Environment.NewLine,
                    consumers.Select(tool =>
                        $"{tool.Index + 1}. {tool.Data.Value<string>("Name")}"));
                MessageBox.Show(
                    this,
                    "이 툴의 결과 이미지를 참조하는 툴이 있어 삭제할 수 없습니다.\n" +
                    names + "\n참조 툴의 입력 이미지를 먼저 변경하세요.",
                    "Tool",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            int oldIndex = _tools.IndexOf(selected);
            _tools.Remove(selected);
            RenumberTools();
            if (_tools.Count > 0)
            {
                ToolsListBox.SelectedIndex = Math.Min(oldIndex, _tools.Count - 1);
            }
            else
            {
                _selectedTool = null;
                BuildParameterEditors();
            }
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedTool(-1);
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedTool(1);
        }

        private void MoveSelectedTool(int offset)
        {
            string error;
            if (!CommitEditorValues(out error))
            {
                MessageBox.Show(this, error, "Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!CanChangeToolStructure())
            {
                return;
            }

            int index = ToolsListBox.SelectedIndex;
            int newIndex = index + offset;
            if (index < 0 || newIndex < 0 || newIndex >= _tools.Count)
            {
                return;
            }

            ToolEntry selected = _tools[index];
            _tools.Move(index, newIndex);
            RenumberTools();
            ToolsListBox.SelectedItem = selected;
            BuildParameterEditors();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!CommitEditorValues(out error))
            {
                MessageBox.Show(this, error, "Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                RenumberTools();
                if (!UpdateImageIndexes(out error))
                {
                    MessageBox.Show(this, error, "Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var updatedRoot = new JObject();
                var parameterList = new JArray();
                foreach (ToolEntry tool in _tools)
                {
                    parameterList.Add(tool.RootKey);
                }
                parameterList.Add("ParamInfo");

                foreach (JProperty property in _root.Properties())
                {
                    if (!IsToolProperty(property) &&
                        !string.Equals(property.Name, "ParamList", StringComparison.Ordinal))
                    {
                        updatedRoot.Add(property.Name, property.Value.DeepClone());
                    }
                }

                updatedRoot["ParamList"] = parameterList;
                foreach (ToolEntry tool in _tools)
                {
                    updatedRoot.Add(tool.RootKey, tool.Data.DeepClone());
                }

                string json = JsonConvert.SerializeObject(updatedRoot, Formatting.Indented);
                string temporaryPath = _filePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    json + Environment.NewLine,
                    new UTF8Encoding(false));
                if (File.Exists(_filePath))
                {
                    File.Replace(temporaryPath, _filePath, null);
                }
                else
                {
                    File.Move(temporaryPath, _filePath);
                }

                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "툴 설정을 저장하지 못했습니다.\n" + ex.Message,
                    "Tool",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RenumberTools()
        {
            for (int index = 0; index < _tools.Count; index++)
            {
                ToolEntry tool = _tools[index];
                tool.Index = index;
                tool.RootKey = MakeToolKey(tool.TypeName, index);
                tool.Data["Index"] = index;
                tool.DisplayName =
                    $"{index + 1}. {tool.Data.Value<string>("Name")} [{tool.TypeName}]";
            }
        }

        private void LoadImageReferences()
        {
            JArray inputNames = _root["InputNameList"] as JArray;
            _inputImageCount = inputNames?.Count ?? 0;

            foreach (ToolEntry tool in _tools)
            {
                foreach (string field in ImageIndexFields)
                {
                    int index = tool.Data.Value<int?>(field) ?? 0;
                        tool.ImageReferences[field] = DecodeImageReference(index);
                }
            }
        }

        private ImageReference DecodeImageReference(int index)
        {
            if (index < 0)
            {
                return ImageReference.Unresolved(index);
            }

            if (index >= 0 && index < _inputImageCount)
            {
                return ImageReference.ForInput(index);
            }

            int sourceIndex = index - _inputImageCount;
            ToolEntry[] matches = _tools
                .Where(tool => tool.OriginalIndex == sourceIndex)
                .ToArray();
            if (index >= _inputImageCount && matches.Length == 1)
            {
                return ImageReference.ForTool(matches[0]);
            }

            return ImageReference.Unresolved(index);
        }

        private void InitializeImageReferences(ToolEntry tool)
        {
            foreach (string field in ImageIndexFields)
            {
                tool.ImageReferences[field] = DecodeImageReference(
                    tool.Data.Value<int?>(field) ?? 0);
            }
        }

        private bool CanChangeToolStructure()
        {
            var unresolved = _tools
                .SelectMany(tool => tool.ImageReferences
                    .Where(item => item.Value.IsUnresolved)
                    .Select(item => new
                    {
                        Tool = tool,
                        Field = item.Key,
                        ImageIndex = item.Value.UnresolvedIndex
                    }))
                .FirstOrDefault();
            if (unresolved == null)
            {
                return true;
            }

            MessageBox.Show(
                this,
                "현재 툴 목록에서 결과 이미지를 찾을 수 없는 참조가 있습니다.\n" +
                $"{unresolved.Tool.Index + 1}. {unresolved.Tool.Data.Value<string>("Name")} / " +
                $"{unresolved.Field} = {unresolved.ImageIndex}\n" +
                "이 입력을 선택 가능한 이미지로 변경한 뒤 툴을 추가·삭제·이동하세요.",
                "Tool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private bool HasValidImageReferences()
        {
            foreach (ToolEntry consumer in _tools)
            {
                foreach (ImageReference reference in consumer.ImageReferences.Values)
                {
                    if (reference.SourceTool != null &&
                        !_tools.Contains(reference.SourceTool))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool UpdateImageIndexes(out string error)
        {
            error = null;
            if (!HasValidImageReferences())
            {
                error = "삭제된 툴을 이미지 입력으로 참조하는 항목이 있습니다.";
                return false;
            }

            foreach (ToolEntry consumer in _tools)
            {
                foreach (string field in ImageIndexFields)
                {
                    ImageReference reference;
                    if (!consumer.ImageReferences.TryGetValue(field, out reference))
                    {
                        continue;
                    }

                    int index;
                    if (!TryGetImageIndex(reference, _tools, out index))
                    {
                        error = "이미지 참조를 현재 툴 순서로 변환할 수 없습니다: " +
                            consumer.Data.Value<string>("Name") + " / " + field;
                        return false;
                    }

                    consumer.Data[field] = index;
                }
            }

            return true;
        }

        private bool TryGetImageIndex(
            ImageReference reference,
            IList<ToolEntry> order,
            out int index)
        {
            if (reference.IsInput &&
                reference.InputIndex >= 0 &&
                reference.InputIndex < _inputImageCount)
            {
                index = reference.InputIndex;
                return true;
            }

            if (reference.SourceTool != null)
            {
                int sourceIndex = order.IndexOf(reference.SourceTool);
                if (sourceIndex >= 0)
                {
                    index = _inputImageCount + sourceIndex;
                    return true;
                }

                index = 0;
                return false;
            }

            index = reference.UnresolvedIndex;
            return reference.IsUnresolved || reference.IsUnused;
        }

        private static JObject CreateToolData(Image_Filter model, int index)
        {
            var data = new JObject
            {
                ["Name"] = model.Name,
                ["ResultName"] = model.ResultFilterName ?? string.Empty,
                ["SaveUse"] = model.SaveUse,
                ["Index"] = index,
                ["FastMode"] = model.FastMode,
                ["Combo_ImageIndex"] = model.Combo_ImageIndex,
                ["Combo_ImageIndex2"] = model.Combo_ImageIndex2,
                ["Combo_ImageIndex3"] = model.Combo_ImageIndex3,
                ["Combo_ImageIndex4"] = model.Combo_ImageIndex4,
                ["Combo_SelectedIndex"] = model.Combo_SelectedIndex,
                ["Combo_SelectedIndex2"] = model.Combo_SelectedIndex2,
                ["Combo_AreaSelectedIndex"] = model.Combo_AreaSelectedIndex,
                ["Combo_AreaRatioSelectedIndex"] = model.Combo_AreaRatioSelectedIndex,
                ["Combo_WidthSelectedIndex"] = model.Combo_WidthSelectedIndex,
                ["Combo_LengthSelectedIndex"] = model.Combo_LengthSelectedIndex,
                ["Combo_SizeRatioSelectedIndex"] = model.Combo_SizeRatioSelectedIndex
            };

            foreach (string field in SliderFields)
            {
                var slider = GetPropertyValue(model, field) as SliderProperty;
                data[field] = slider == null
                    ? new JValue(0)
                    : CreateNumberToken(slider.Value);
            }

            return data;
        }

        private static JToken CreateNumberToken(double value)
        {
            return Math.Abs(value - Math.Round(value)) < 0.000001 &&
                value >= long.MinValue &&
                value <= long.MaxValue
                    ? (JToken)new JValue((long)Math.Round(value))
                    : new JValue(value);
        }

        private static void ApplyDataToModel(Image_Filter model, JObject data)
        {
            foreach (JProperty property in data.Properties())
            {
                string propertyName = property.Name == "ResultName"
                    ? "ResultFilterName"
                    : property.Name;
                PropertyInfo modelProperty = model.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (modelProperty == null || property.Value.Type == JTokenType.Null)
                {
                    continue;
                }

                if (modelProperty.PropertyType == typeof(SliderProperty))
                {
                    var slider = modelProperty.GetValue(model, null) as SliderProperty;
                    if (slider != null)
                    {
                        slider.Value = property.Value.Value<double>();
                    }
                }
                else if (modelProperty.CanWrite)
                {
                    object value = property.Value.ToObject(modelProperty.PropertyType);
                    modelProperty.SetValue(model, value, null);
                }
            }
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(instance, null);
        }

        private static string GetStringProperty(object instance, string propertyName)
        {
            return GetPropertyValue(instance, propertyName) as string;
        }

        private static bool IsControlVisible(object instance, string propertyName)
        {
            object value = GetPropertyValue(instance, propertyName);
            return value is System.Windows.Visibility &&
                (System.Windows.Visibility)value == System.Windows.Visibility.Visible;
        }

        private FilterDescriptor FindDescriptor(string typeName)
        {
            return _availableTools.FirstOrDefault(item =>
                string.Equals(
                    item.FilterClass.Name,
                    typeName,
                    StringComparison.Ordinal));
        }

        private static string GetToolTypeName(string rootKey)
        {
            int separator = rootKey.LastIndexOf('_');
            int index;
            if (separator <= 0 ||
                !int.TryParse(rootKey.Substring(separator + 1), out index))
            {
                throw new InvalidDataException("툴 키에 순번이 없습니다: " + rootKey);
            }

            return rootKey.Substring(0, separator);
        }

        private static string MakeToolKey(string typeName, int index)
        {
            return typeName + "_" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsToolProperty(JProperty property)
        {
            return property.Value is JObject &&
                property.Value["Name"] != null &&
                property.Value["Index"] != null;
        }

        private void RestoreSelectedTool()
        {
            _isUpdatingSelection = true;
            ToolsListBox.SelectedItem = _selectedTool;
            _isUpdatingSelection = false;
        }

        private sealed class ToolEntry
        {
            public string TypeName { get; }
            public string RootKey { get; set; }
            public string DisplayName { get; set; }
            public JObject Data { get; }
            public int Index { get; set; }
            public int OriginalIndex { get; }
            public Dictionary<string, ImageReference> ImageReferences { get; } =
                new Dictionary<string, ImageReference>(StringComparer.Ordinal);

            public ToolEntry(
                string typeName,
                string rootKey,
                string displayName,
                JObject data,
                int index)
            {
                TypeName = typeName;
                RootKey = rootKey;
                DisplayName = $"{index + 1}. {displayName} [{typeName}]";
                Data = data;
                Index = index;
                OriginalIndex = index;
            }
        }

        private sealed class ImageReference
        {
            public int InputIndex { get; }
            public ToolEntry SourceTool { get; }
            public int UnresolvedIndex { get; }
            public bool IsInput => InputIndex >= 0;
            public bool IsUnused =>
                InputIndex < 0 &&
                SourceTool == null &&
                UnresolvedIndex < 0;
            public bool IsUnresolved =>
                InputIndex < 0 &&
                SourceTool == null &&
                UnresolvedIndex >= 0;

            private ImageReference(int inputIndex, ToolEntry sourceTool, int unresolvedIndex)
            {
                InputIndex = inputIndex;
                SourceTool = sourceTool;
                UnresolvedIndex = unresolvedIndex;
            }

            public static ImageReference ForInput(int index)
            {
                return new ImageReference(index, null, -1);
            }

            public static ImageReference ForTool(ToolEntry tool)
            {
                return new ImageReference(-1, tool, -1);
            }

            public static ImageReference Unresolved(int index)
            {
                return new ImageReference(-1, null, index);
            }

            public bool RefersToSameImage(ImageReference other)
            {
                return other != null &&
                    (IsInput && other.IsInput && InputIndex == other.InputIndex ||
                     ReferenceEquals(SourceTool, other.SourceTool) &&
                     SourceTool != null ||
                     IsUnresolved && other.IsUnresolved &&
                     UnresolvedIndex == other.UnresolvedIndex);
            }
        }

        private sealed class Choice
        {
            public int Value { get; }
            public string Label { get; }

            public Choice(int value, string label)
            {
                Value = value;
                Label = label;
            }
        }

        private sealed class ImageChoice
        {
            public ImageReference Reference { get; }
            public string Label { get; }

            public ImageChoice(ImageReference reference, string label)
            {
                Reference = reference;
                Label = label;
            }
        }

        private enum EditorValueKind
        {
            String,
            Boolean,
            Choice,
            ImageReference,
            Number
        }

        private sealed class EditorValue
        {
            public string Field { get; }
            public Control Control { get; }
            public EditorValueKind Kind { get; }

            public EditorValue(
                string field,
                Control control,
                EditorValueKind kind)
            {
                Field = field;
                Control = control;
                Kind = kind;
            }
        }
    }
}
