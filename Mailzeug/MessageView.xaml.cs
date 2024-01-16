using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using Microsoft.Web.WebView2.Core;
using MimeKit;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for MessageView.xaml
    /// </summary>
    public partial class MessageView : UserControl {
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

        public void show_message(MailMessage message, bool allowDownloads) {
            this.allow_downloads = allowDownloads;
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
