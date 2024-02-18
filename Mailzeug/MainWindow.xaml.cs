﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;

using NLog;

namespace Mailzeug {
    public interface IMessageWindow {
        public abstract void handle_new_message();
        public abstract void handle_reply(MailFolder folder, MailMessage message);
        public abstract void handle_reply_all(MailFolder folder, MailMessage message);
        public abstract void handle_forward(MailFolder folder, MailMessage message);
        public abstract void handle_mark_spam(MailFolder folder, MailMessage message, bool isSpam);
        public abstract void handle_mark_read(MailFolder folder, MailMessage message, bool isRead);
        public abstract void handle_move(MailFolder folder, MailMessage message);
        public abstract void handle_delete(MailFolder folder, MailMessage message);
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, IMessageWindow {
        private readonly NotifyIcon notify_icon;
        private bool shutdown = false;
        public Config config;
        private string log_file;
        private MailManager mail_manager;

        public MainWindow() {
            this.InitializeComponent();
            this.notify_icon = new NotifyIcon {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                Visible = true,
                ContextMenuStrip = this.setup_notify_menu()
            };
            System.Windows.Application.Current.Exit += (obj, args) => { this.notify_icon.Dispose(); };
            string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dataDir = Path.Join(appDataDir, "manveti", "Mailzeug");
            this.config = Config.load(dataDir);
            this.log_file = Path.Join(dataDir, "activity.log");
            NLog.Config.LoggingConfiguration logConfig = new NLog.Config.LoggingConfiguration();
            NLog.Targets.FileTarget logTarget = new NLog.Targets.FileTarget("logfile") {
                DeleteOldFileOnStartup = true,
                FileName = this.log_file,
                Layout = "${longdate}${onhasproperties: ${event-properties:source}} ${uppercase:${level}} ${message}${onexception:${newline}${exception}}"
            };
            logConfig.AddRule(LogLevel.Info, LogLevel.Fatal, logTarget);
            LogManager.Configuration = logConfig;
            this.mail_manager = new MailManager(this);
            this.folder_list.ItemsSource = this.mail_manager.folders;
        }

        private ContextMenuStrip setup_notify_menu() {
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
                return;
            }
            this.mail_manager.shutdown();
        }

        private void open_main(object sender, EventArgs e) {
            this.Show();
            this.Activate();
        }

        private void open_options(object sender, EventArgs e) {
            ConfigWindow dlg = new ConfigWindow();
            dlg.Owner = this;
            dlg.imap_server_box.Text = this.config.imap_server;
            dlg.imap_port_box.Text = this.config.imap_port.ToString("d");
            dlg.smtp_server_box.Text = this.config.smtp_server;
            dlg.smtp_port_box.Text = this.config.smtp_port.ToString("d");
            dlg.username_box.Text = this.config.username ?? "";
            dlg.password_box.Password = this.config.password ?? "";
            dlg.ShowDialog();
            if (!dlg.valid) {
                return;
            }

            if (dlg.reset) {
                this.config.reset();
            }
            this.config.imap_server = dlg.imap_server_box.Text;
            this.config.imap_port = int.Parse(dlg.imap_port_box.Text);
            this.config.smtp_server = dlg.smtp_server_box.Text;
            this.config.smtp_port = int.Parse(dlg.smtp_port_box.Text);
            this.config.username = dlg.username_box.Text;
            this.config.password = dlg.password_box.Password;
            this.config.save();
        }

        private void open_logfile(object sender, EventArgs e) {
            if (!File.Exists(this.log_file)) {
                return;
            }
            Process.Start("notepad", this.log_file);
        }

        private void exit_main(object sender, EventArgs e) {
            this.shutdown = true;
            this.Close();
        }

        private void fix_listview_column_widths(System.Windows.Controls.ListView listView) {
            GridView gridView = listView.View as GridView;
            if (gridView is null) { return; }
            foreach (GridViewColumn col in gridView.Columns) {
                col.Width = col.ActualWidth;
                col.Width = double.NaN;
            }
        }

        public void fix_folder_list_column_sizes() {
            this.folder_list.Dispatcher.Invoke(() => this.fix_listview_column_widths(this.folder_list));
        }

        private void folder_list_sel_changed(object sender, RoutedEventArgs e) {
            this.mail_manager.select_folder(this.folder_list.SelectedItem as MailFolder);
        }

        private void message_list_sel_changed(object sender, RoutedEventArgs e) {
            this.mail_manager.select_message(this.message_list.SelectedIndex);
        }

        private void message_list_delete(object sender, RoutedEventArgs e) {
            MailMessage message = ((FrameworkElement)sender).DataContext as MailMessage;
            this.handle_delete(this.mail_manager.selected_folder, message);
        }

        public void handle_new_message() {
            //TODO: new message window
        }

        public void handle_reply(MailFolder folder, MailMessage message) {
            //TODO: new message window with reply association; mark message replied if reply sent
        }

        public void handle_reply_all(MailFolder folder, MailMessage message) {
            //TODO: new message window with reply association; mark message replied if reply sent
        }

        public void handle_forward(MailFolder folder, MailMessage message) {
            //TODO: new message window with forward association
        }

        public void handle_mark_spam(MailFolder folder, MailMessage message, bool isSpam) {
            //TODO: outlook doesn't have spam flag, so just move to/from junk folder
        }

        public void handle_mark_read(MailFolder folder, MailMessage message, bool isRead) {
            //TODO: push new flag action
        }

        public void handle_move(MailFolder folder, MailMessage message) {
            //TODO: prompt for destination folder; move if destination selected and different from current folder
        }
        public void handle_delete(MailFolder folder, MailMessage message) {
            //TODO: if folder is trash/junk, delete; else, move to trash
        }
    }

    public class FontWeightConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            bool isBold = (bool)value;
            if (isBold) {
                return FontWeights.Bold;
            }
            return FontWeights.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
