using System.Windows;
using System.Windows.Input;

namespace FAVIS
{
    public partial class PasswordWindow : Window
    {
        public PasswordWindow()
        {
            InitializeComponent();
            Loaded += (sender, args) => PasswordInput.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (PasswordInput.Password != "1")
            {
                MessageBox.Show(this, "Password is incorrect.", "System", MessageBoxButton.OK, MessageBoxImage.Warning);
                PasswordInput.Clear();
                PasswordInput.Focus();
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OkButton_Click(sender, e);
            }
        }
    }
}
