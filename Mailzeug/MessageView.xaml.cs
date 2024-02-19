using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Web.WebView2.Core;
using MimeKit;

namespace Mailzeug {
    public class MessageViewError : MZError {
        public MessageViewError() : base() { }
        public MessageViewError(string message) : base(message) { }
        public MessageViewError(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Interaction logic for MessageView.xaml
    /// </summary>
    public partial class MessageView : UserControl {
        private IMessageWindow window = null;
        private MailFolder folder = null;
        private MailMessage message = null;
        private bool allow_downloads = false;
        private List<string> download_requests;

        public MessageView() {
            this.InitializeComponent();
            this.download_requests = new List<string>();
            this.Loaded += this.init_body;
        }

        private async void init_body(object sender, RoutedEventArgs e) {
            if (DesignerProperties.GetIsInDesignMode(this)) {
                return;
            }
            this.window = Window.GetWindow(this) as IMessageWindow;
            if (this.window is null) {
                this.toolbar.Visibility = Visibility.Collapsed;
            }
            await this.body_box.EnsureCoreWebView2Async();
            this.body_box.CoreWebView2.Settings.IsScriptEnabled = false;
            this.body_box.CoreWebView2.WebResourceRequested += this.web_resource_requested;
            this.body_box.CoreWebView2.AddWebResourceRequestedFilter(null, CoreWebView2WebResourceContext.All);
        }

        private void web_resource_requested(object sender, CoreWebView2WebResourceRequestedEventArgs e) {
            //TODO: rework this to use a DownloadManager class to filter by message id, sender, and uri
            if (this.allow_downloads) {
                return;
            }
            this.download_requests.Add(e.Request.Uri);
            this.download_but.IsEnabled = true;
            e.Response = this.body_box.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not found", null);
        }

        public void show_message(MailFolder folder, MailMessage message) {
            this.folder = folder;
            this.message = message;
            this.allow_downloads = false;
            bool messageShowing = message is not null;
            this.reply_but.IsEnabled = messageShowing;
            this.reply_all_but.IsEnabled = messageShowing;
            this.forward_but.IsEnabled = messageShowing;
            //TODO: this.spam_but.Content = "Mark as (Spam|Ham)"
            this.spam_but.IsEnabled = messageShowing;
            this.read_but.Content = "Mark as " + ((message is null) || (message.unread) ? "Read" : "Unread");
            this.read_but.IsEnabled = messageShowing;
            this.move_but.IsEnabled = messageShowing;
            this.delete_but.IsEnabled = messageShowing;
            this.download_but.IsEnabled = false;
            this.download_requests.Clear();
            MimeMessage mime = message?.get_mime_message();
            this.subject_box.Content = message?.subject ?? "";
            this.timestamp_box.Content = message?.timestamp_string ?? "";
            this.from_box.Content = message?.from ?? "";
            this.to_box.Content = mime?.To?.ToString() ?? "";
            this.cc_box.Content = mime?.Cc?.ToString() ?? "";
            this.bcc_box.Content = mime?.Bcc?.ToString() ?? "";
            this.body_box.NavigateToString(mime?.HtmlBody ?? mime?.TextBody ?? "");
            //TODO:
            //foreach (MimeEntity att in mime.Attachments) {
            //    attachment_list.Add(att.ContentDisposition.FileName);
            //}
        }

        private void do_new_message(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            this.window.handle_new_message();
        }

        private void do_reply(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            if ((this.folder is null) || (this.message is null)) {
                // can only reply when message showing
                return;
            }
            this.window.handle_reply(this.folder, this.message);
        }

        private void do_reply_all(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            if ((this.folder is null) || (this.message is null)) {
                // can only reply when message showing
                return;
            }
            this.window.handle_reply_all(this.folder, this.message);
        }

        private void do_forward(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            if ((this.folder is null) || (this.message is null)) {
                // can only forward when message showing
                return;
            }
            this.window.handle_forward(this.folder, this.message);
        }

        private void do_mark_spam(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            if ((this.folder is null) || (this.message is null)) {
                // can only mark when message showing
                return;
            }
            //TODO: determine if message is spam; pass false below if so
            this.window.handle_mark_spam(this.folder, this.message, true);
            //TODO: change button text
        }

        private void do_mark_read(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            if ((this.folder is null) || (this.message is null)) {
                // can only mark when message showing
                return;
            }
            bool wasUnread = this.message.unread;
            this.window.handle_mark_read(this.folder, this.message, wasUnread);
            this.read_but.Content = "Mark as " + (wasUnread ? "Unread" : "Read");
        }

        private void do_move(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            if ((this.folder is null) || (this.message is null)) {
                // can only move when message showing
                return;
            }
            this.window.handle_move(this.folder, this.message);
        }

        private void do_delete(object sender, RoutedEventArgs e) {
            if (this.window is null) {
                throw new MessageViewError("MessageView cannot perform actions when not embedded in an IMessageWindow");
            }
            if ((this.folder is null) || (this.message is null)) {
                // can only forward when message showing
                return;
            }
            this.window.handle_delete(this.folder, this.message);
        }

        private void download_images(object sender, RoutedEventArgs e) {
            if (this.allow_downloads) {
                return;
            }
            this.download_but.IsEnabled = false;
            this.allow_downloads = true;
            this.body_box.Reload();
        }
    }
}
