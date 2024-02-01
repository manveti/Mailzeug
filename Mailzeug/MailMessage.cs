using System;
using System.IO;

using MailKit;
using MimeKit;

namespace Mailzeug {
    [Serializable]
    public class MailMessage {
        public readonly uint id;
        protected string _subject;
        public DateTimeOffset timestamp;
        protected string _from;
        public byte[] source;
        public bool read;
        protected bool _replied;
        public bool deleted;

        public string subject => this._subject;
        public string timestamp_string => this.timestamp.ToLocalTime().ToString("G");
        public string from => this._from;
        public bool loaded => this.source is not null;

        public bool unread => !this.read;

        public bool replied {
            get { return this._replied; }
            set { this._replied = value; }
        }

        public MailMessage(IMessageSummary summary) {
            this.id = summary.UniqueId.Id;
            this.source = null;
            this.update(summary);
        }

        public bool update(IMessageSummary summary) {
            bool dirty = false;
            if (summary.Envelope.Subject != this._subject) {
                this._subject = summary.Envelope.Subject;
                dirty = true;
            }
            if (summary.Date != this.timestamp) {
                this.timestamp = summary.Date;
                dirty = true;
            }
            string from = summary.Envelope.From.ToString();
            if (from != this._from) {
                this._from = from;
                dirty = true;
            }
            bool read = (summary.Flags & MessageFlags.Seen) != 0;
            if (read != this.read) {
                this.read = read;
                dirty = true;
            }
            bool replied = (summary.Flags & MessageFlags.Answered) != 0;
            if (replied != this._replied) {
                this._replied = replied;
                dirty = true;
            }
            bool deleted = (summary.Flags & MessageFlags.Deleted) != 0;
            if (deleted != this.deleted) {
                this.deleted = deleted;
                dirty = true;
            }
            return dirty;
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
