using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;

using MailKit.Net.Smtp;
using MimeKit;

namespace Mailzeug {
    /// <summary>
    /// Interaction logic for ComposeWindow.xaml
    /// </summary>
    public partial class ComposeWindow : Window {
        private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private NLog.Logger logger;
        private Config config;
        private MailMessage message = null;
        private MimeMessage mime_message = null;
        bool forward = false;
        bool dirty = false;
        bool body_unmodified = true;

        //TODO: use some interface/Window sublcass instead of MainWindow
        public ComposeWindow(MainWindow owner, MailMessage message = null, bool replyAll = false, bool forward = false) {
            this.logger = Logger.WithProperty("source", "USER");
            this.config = owner.config;
            this.message = message;
            this.forward = forward;
            InitializeComponent();
            this.Owner = owner;
            this.from_box.Text = owner.config.display_name ?? "";
            if (message is not null) {
                this.mime_message = message.get_mime_message();
                string subject = message.subject;
                if (forward) {
                    if (
                        (!subject.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase)) &&
                        (!subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
                    ) {
                        subject = "Fw: " + subject;
                    }
                }
                else {
                    if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)) {
                        subject = "Re: " + subject;
                    }
                }
                this.subject_box.Text = subject;
                InternetAddressList toAddrs = new InternetAddressList(), ccAddrs = new InternetAddressList();
                if (!forward) {
                    if (this.mime_message.ReplyTo.Count > 0) {
                        toAddrs.AddRange(this.mime_message.ReplyTo);
                    }
                    else if (this.mime_message.From.Count > 0) {
                        toAddrs.AddRange(this.mime_message.From);
                    }
                    else if (this.mime_message.Sender is not null) {
                        toAddrs.Add(this.mime_message.Sender);
                    }
                    if (replyAll) {
                        //TODO: filter out self
                        toAddrs.AddRange(this.mime_message.To);
                        ccAddrs.AddRange(this.mime_message.Cc);
                    }
                }
                this.to_box.Text = toAddrs.ToString();
                if (ccAddrs.Count > 0) {
                    this.show_cc();
                }
                this.cc_box.Text = ccAddrs.ToString();
                StringBuilder body = new StringBuilder();
                if (this.mime_message.TextBody is not null) {
                    body.Append("\n\n\n");
                    if (forward) {
                        body.AppendLine("----- Forwarded Message -----");
                    }
                    else {
                        body.AppendLine("----- Original Message -----");
                    }
                    body.AppendLine("From: " + this.mime_message.From.ToString());
                    body.AppendLine("To: " + this.mime_message.To.ToString());
                    body.AppendLine("Sent: " + message.timestamp_string);
                    body.AppendLine("Subject: " + message.subject);
                    body.AppendLine();
                    using (StringReader bodyReader = new StringReader(this.mime_message.TextBody)) {
                        string line;
                        while ((line = bodyReader.ReadLine()) is not null) {
                            body.Append("> ");
                            body.AppendLine(line);
                        }
                    }
                }
                this.body_box.Text = body.ToString();
                this.dirty = false;
                this.body_unmodified = true;
            }
        }

        private void on_close(object sender, CancelEventArgs e) {
            if (!this.dirty) {
                return;
            }
            MessageBoxResult result = MessageBox.Show("Your message is unsent. Discard it?", "Discard new message?", MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes) {
                e.Cancel = true;
            }
        }

        private void on_change(object sender, EventArgs e) {
            this.dirty = true;
        }

        private void on_body_change(object sender, EventArgs e) {
            this.on_change(sender, e);
            this.body_unmodified = false;
        }

        private void show_cc() {
            this.cc_but.Content = "Hide CC/BCC";
            this.cc_lbl.Visibility = Visibility.Visible;
            this.cc_box.Visibility = Visibility.Visible;
            this.bcc_lbl.Visibility = Visibility.Visible;
            this.bcc_box.Visibility = Visibility.Visible;
        }

        private void hide_cc() {
            this.cc_but.Content = "Show CC/BCC";
            this.cc_lbl.Visibility = Visibility.Collapsed;
            this.cc_box.Visibility = Visibility.Collapsed;
            this.bcc_lbl.Visibility = Visibility.Collapsed;
            this.bcc_box.Visibility = Visibility.Collapsed;
        }

        private void toggle_cc(object sender, RoutedEventArgs e) {
            if (this.cc_lbl.Visibility == Visibility.Collapsed) {
                this.show_cc();
            }
            else {
                this.hide_cc();
            }
        }

        private void do_send(object sender, RoutedEventArgs e) {
            MimeMessage msg = new MimeMessage();
            string errName = "recipient";
            try {
                msg.To.AddRange(InternetAddressList.Parse(this.to_box.Text));
                if (this.cc_box.Text != "") {
                    errName = "CC";
                    msg.Cc.AddRange(InternetAddressList.Parse(this.cc_box.Text));
                }
                if (this.bcc_box.Text != "") {
                    errName = "BCC";
                    msg.Bcc.AddRange(InternetAddressList.Parse(this.bcc_box.Text));
                }
                errName = "sender";
                msg.From.Add(InternetAddress.Parse(this.from_box.Text));
            }
            catch (ParseException) {
                MessageBox.Show($"Empty/malformed {errName}", "Error");
                return;
            }
            if ((this.subject_box.Text == "") || (this.body_unmodified) || (this.body_box.Text == "")) {
                if (this.subject_box.Text == "") {
                    errName = "subject";
                    if ((this.body_unmodified) || (this.body_box.Text == "")) {
                        errName += " and body";
                    }
                }
                else {
                    errName = "body";
                }
                if (MessageBox.Show($"Empty {errName}. Send anyway?", "Warning", MessageBoxButton.YesNo) != MessageBoxResult.Yes) {
                    return;
                }
            }
            //TODO: maybe search body for "attached" and warn if no attachments
            msg.Subject = this.subject_box.Text;
            if ((this.message is not null) && (!this.forward) && (!string.IsNullOrEmpty(this.message.message_id))) {
                // set reply headers
                msg.InReplyTo = this.message.message_id;
                msg.References.AddRange(this.message.get_mime_message().References);
                msg.References.Add(this.message.message_id);
            }
            BodyBuilder body = new BodyBuilder();
            body.TextBody = this.body_box.Text;
            //TODO: attachments
            msg.Body = body.ToMessageBody();
            this.logger.Info("Sending message to {recipient}", this.to_box.Text);
            using (SmtpClient client = new SmtpClient()) {
                client.Connect(this.config.smtp_server, this.config.smtp_port, MailKit.Security.SecureSocketOptions.StartTls);
                client.Authenticate(this.config.username, this.config.password);
                client.Send(msg);
                client.Disconnect(true);
            }
            //TODO: if message { if forward: mark message forwarded; else: mark message replied }
            this.dirty = false;
            this.Close();
        }
    }
}
