using System.Windows;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for ConfigWindow.xaml
    /// </summary>
    public partial class ConfigWindow : Window {
        public bool valid = false;
        public bool reset = false;

        public ConfigWindow() {
            InitializeComponent();
        }

        private void do_reset(object sender, RoutedEventArgs e) {
            this.reset = true;
            Config cfg = new Config();
            this.server_box.Text = cfg.server;
            this.port_box.Text = cfg.port.ToString("d");
            this.username_box.Text = cfg.username ?? "";
            this.password_box.Password = cfg.password ?? "";
        }

        private void do_ok(object sender, RoutedEventArgs e) {
            this.valid = true;
            this.Close();
        }

        private void do_cancel(object sender, RoutedEventArgs e) {
            this.Close();
        }
    }
}
