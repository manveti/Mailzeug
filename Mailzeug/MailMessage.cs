using System;

namespace Mailzeug {
    [Serializable]
    public class MailMessage {
        public readonly string id;
        public readonly string source;
        public readonly string _subject;
        public readonly DateTimeOffset timestamp;
        public readonly string _from;
        public readonly string to;
        public readonly string cc;
        public readonly string bcc;
        public readonly string body;
        //TODO: attachments
        public bool read;

        public string subject => this._subject;
        public string timestamp_string => this.timestamp.ToLocalTime().ToString("G");
        public string from => this._from;

        public MailMessage(string id, string source, string subject, DateTimeOffset timestamp,
            string from, string to, string cc, string bcc, string body, bool read = false) {
            this.id = id;
            this.source = source;
            this._subject = subject;
            this.timestamp = timestamp;
            this._from = from;
            this.to = to;
            this.cc = cc;
            this.bcc = bcc;
            this.body = body;
            this.read = read;
        }
    }
}
