using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for InputDialog.xaml
    /// </summary>
    public partial class InputDialog : Window {
        public InputDialog(UIElement valueBox, string title = null) {
            InitializeComponent();
            Grid.SetRow(valueBox, 0);
            Grid.SetColumn(valueBox, 1);
            this.main_grid.Children.Add(valueBox);
            valueBox.Focus();
            if (title is not null) {
                this.Title = title;
            }
        }

        private void do_ok(object sender, RoutedEventArgs e) {
            this.DialogResult = true;
            this.Close();
        }

        private void do_cancel(object sender, RoutedEventArgs e) {
            this.DialogResult = false;
            this.Close();
        }

        public static string prompt(Window owner, string prompt, string title = null) {
            TextBox valueBox = new TextBox() { MinWidth = 100 };
            InputDialog dlg = new InputDialog(valueBox, title) { Owner = owner };
            dlg.prompt_box.Content = prompt;
            bool? result = dlg.ShowDialog();
            if (result != true) {
                return null;
            }
            return valueBox.Text;
        }

        public static string prompt(
            Window owner, string prompt, IEnumerable<string> choices, string title = null, string selected = null, bool canEdit = false
        ) {
            ComboBox valueBox = new ComboBox() { MinWidth = 100, IsEditable = canEdit };
            foreach (string val in choices) {
                valueBox.Items.Add(val);
            }
            if (selected is not null) {
                valueBox.Text = selected;
            }
            InputDialog dlg = new InputDialog(valueBox, title) { Owner = owner };
            dlg.prompt_box.Content = prompt;
            bool? result = dlg.ShowDialog();
            if (result != true) {
                return null;
            }
            return valueBox.Text;
        }
    }
}
