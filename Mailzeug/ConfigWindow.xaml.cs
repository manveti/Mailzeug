﻿using System;
using System.Windows;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for ConfigWindow.xaml
    /// </summary>
    public partial class ConfigWindow : Window {
        public bool valid = false;
        public bool reset = false;
        private bool display_name_changed = false;

        public ConfigWindow(Config cfg = null) {
            this.InitializeComponent();
            if (cfg is null) {
                return;
            }
            this.imap_server_box.Text = cfg.imap_server;
            this.imap_port_box.Text = cfg.imap_port.ToString("d");
            this.smtp_server_box.Text = cfg.smtp_server;
            this.smtp_port_box.Text = cfg.smtp_port.ToString("d");
            this.username_box.Text = cfg.username ?? "";
            this.password_box.Password = cfg.password ?? "";
            this.display_name_box.Text = cfg.display_name ?? "";
            if (this.username_box.Text != this.display_name_box.Text) {
                this.display_name_changed = true;
            }
        }

        private void on_username_change(object sender, EventArgs e) {
            if (this.display_name_changed) {
                return;
            }
            this.display_name_box.Text = this.username_box.Text;
        }

        private void on_display_name_change(object sender, EventArgs e) {
            this.display_name_changed = true;
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
            this.display_name_box.Text = cfg.display_name ?? "";
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
