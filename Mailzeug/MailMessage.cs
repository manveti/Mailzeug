using System;
using System.Collections.Generic;
using System.IO;

using MailKit;
using MimeKit;

namespace Mailzeug {
    [Serializable]
    public class MailMessage {
        public readonly uint id;
        public readonly string _subject;
        public readonly DateTimeOffset timestamp;
        public readonly string _from;
        public byte[] source;
        public bool read;
        public bool replied;

        public string subject => this._subject;
        public string timestamp_string => this.timestamp.ToLocalTime().ToString("G");
        public string from => this._from;
        public bool loaded => this.source is not null;

        public MailMessage(IMessageSummary summary) {
            this.id = summary.UniqueId.Id;
            this._subject = summary.Envelope.Subject;
            this.timestamp = summary.Date;
            this._from = summary.Envelope.From.ToString();
            this.source = null;
            this.read = (summary.Flags & MessageFlags.Seen) != 0;
            this.replied = (summary.Flags & MessageFlags.Answered) != 0;
        }

        public void load(MimeMessage message) {
            using (MemoryStream stream = new MemoryStream()) {
                message.WriteTo(stream);
                this.source = stream.ToArray();
            }
        }

        public MimeMessage get_mime_message() {
            if (this.source is null) {
                return null;
            }
            using (MemoryStream stream = new MemoryStream(this.source)) {
                return MimeMessage.Load(stream);
            }
        }
    }
}
