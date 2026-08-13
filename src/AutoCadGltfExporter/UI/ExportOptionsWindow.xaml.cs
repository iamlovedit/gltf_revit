using System.Windows;
using GltfExporter.Shared;

namespace AutoCadGltfExporter.UI
{
    public partial class ExportOptionsWindow : Window
    {
        private readonly ExportOptions _options;

        public ExportOptionsWindow(ExportOptions options)
        {
            InitializeComponent();
            _options = options;
            EnableDracoCheckBox.IsChecked = options.EnableDraco;
            CompressionLevelTextBox.Text = options.DracoCompressionLevel.ToString();
            IncludePropertiesCheckBox.IsChecked = options.IncludeProperties;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            _options.EnableDraco = EnableDracoCheckBox.IsChecked == true;
            if (int.TryParse(CompressionLevelTextBox.Text, out var level))
            {
                _options.DracoCompressionLevel = System.Math.Max(0, System.Math.Min(10, level));
            }
            _options.IncludeProperties = IncludePropertiesCheckBox.IsChecked == true;
            DialogResult = true;
            Close();
        }
    }
}
