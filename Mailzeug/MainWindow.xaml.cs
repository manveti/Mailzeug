using Microsoft.Toolkit.Uwp.Notifications;
//using CommunityToolkit.WinUI.Notifications;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Forms;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {
        private readonly NotifyIcon notifyIcon;
        private bool shutdown = false;

        public MainWindow() {
            this.InitializeComponent();
            this.notifyIcon = new NotifyIcon {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                Visible = true,
                ContextMenuStrip = this.setupNotifyMenu()
            };
            System.Windows.Application.Current.Exit += (obj, args) => { this.notifyIcon.Dispose(); };
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

        private void exit_main(object sender, EventArgs e) {
            this.shutdown = true;
            this.Close();
        }
    }
}
