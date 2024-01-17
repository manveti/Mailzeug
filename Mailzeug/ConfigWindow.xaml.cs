using System.Windows;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for ConfigWindow.xaml
    /// </summary>
    public partial class ConfigWindow : Window {
        public bool valid = false;
        public bool reset = false;

        public ConfigWindow() {
            this.InitializeComponent();
        }

        private void do_reset(object sender, RoutedEventArgs e) {
            this.reset = true;
            Config cfg = new Config();
            this.imap_server_box.Text = cfg.imap_server;
            this.imap_port_box.Text = cfg.imap_port.ToString("d");
            this.smtp_server_box.Text = cfg.smtp_server;
            this.smtp_port_box.Text = cfg.smtp_port.ToString("d");
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
