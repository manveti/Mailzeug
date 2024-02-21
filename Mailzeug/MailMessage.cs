using System;
using System.ComponentModel;
using System.IO;

using MailKit;
using MimeKit;

namespace Mailzeug {
    [Serializable]
    public class MailMessage : INotifyPropertyChanged {
        public uint id;
        protected string _subject;
        public DateTimeOffset timestamp;
        protected string _from;
        public byte[] source;
        protected bool _read;
        protected bool _replied;
        public bool deleted;
        public MailFolder restore_folder;

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        public string subject => this._subject;
        public string timestamp_string => this.timestamp.ToLocalTime().ToString("G");
        public string from => this._from;
        public bool loaded => this.source is not null;

        public bool unread => !this._read;

        public bool read {
            get { return this._read; }
            set {
                this._read = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.unread)));
            }
        }

        public bool replied {
            get { return this._replied; }
            set {
                this._replied = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.replied)));
            }
        }

        public MailMessage(IMessageSummary summary) {
            this.id = summary.UniqueId.Id;
            this.source = null;
            this._subject = summary.Envelope.Subject;
            this.timestamp = summary.Date;
            this._from = summary.Envelope.From.ToString();
            this._read = (summary.Flags & MessageFlags.Seen) != 0;
            this._replied = (summary.Flags & MessageFlags.Answered) != 0;
            this.deleted = (summary.Flags & MessageFlags.Deleted) != 0;
        }

        public bool update(MailMessage msg) {
            bool dirty = false;
            if (msg._subject != this._subject) {
                this._subject = msg._subject;
                dirty = true;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.subject)));
            }
            if (msg.timestamp != this.timestamp) {
                this.timestamp = msg.timestamp;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.timestamp_string)));
                dirty = true;
            }
            if (msg._from != this._from) {
                this._from = msg._from;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.from)));
                dirty = true;
            }
            if (msg._read != this._read) {
                this._read = msg._read;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.unread)));
                dirty = true;
            }
            if (msg._replied != this._replied) {
                this._replied = msg._replied;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.replied)));
                dirty = true;
            }
            if (msg.deleted != this.deleted) {
                this.deleted = msg.deleted;
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
