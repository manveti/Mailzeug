using Microsoft.Toolkit.Uwp.Notifications;
//using CommunityToolkit.WinUI.Notifications;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Forms;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {
        private readonly NotifyIcon notifyIcon;
        private bool shutdown = false;
        public Config config;

        public MainWindow() {
            this.InitializeComponent();
            this.notifyIcon = new NotifyIcon {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                Visible = true,
                ContextMenuStrip = this.setupNotifyMenu()
            };
            System.Windows.Application.Current.Exit += (obj, args) => { this.notifyIcon.Dispose(); };
            string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dataDir = Path.Join(appDataDir, "manveti", "Mailzeug");
            this.config = Config.load(dataDir);
        }

        private ContextMenuStrip setupNotifyMenu() {
            ToolStripMenuItem openEnt = new ToolStripMenuItem("Open");
            openEnt.Click += this.open_main;
            ToolStripSeparator sep = new ToolStripSeparator();
            ToolStripMenuItem exitEnt = new ToolStripMenuItem("Exit");
            exitEnt.Click += this.exit_main;
            return new ContextMenuStrip { Items = { openEnt, sep, exitEnt } };
        }

        private void close_main(object sender, CancelEventArgs e) {
            this.Hide();
            if (!this.shutdown) {
                e.Cancel = true;
            }
        }

        private void open_main(object sender, EventArgs e) {
            this.Show();
        }

        private void open_options(object sender, EventArgs e) {
            ConfigWindow dlg = new ConfigWindow();
            dlg.Owner = this;
            dlg.server_box.Text = this.config.server;
            dlg.port_box.Text = this.config.port.ToString("d");
            dlg.username_box.Text = this.config.username ?? "";
            dlg.password_box.Password = this.config.password ?? "";
            dlg.ShowDialog();
            if (!dlg.valid) {
                return;
            }

            if (dlg.reset) {
                this.config.reset();
            }
            this.config.server = dlg.server_box.Text;
            this.config.port = int.Parse(dlg.port_box.Text);
            this.config.username = dlg.username_box.Text;
            this.config.password = dlg.password_box.Password;
            this.config.save();
        }

        private void exit_main(object sender, EventArgs e) {
            this.shutdown = true;
            this.Close();
        }
    }
}
