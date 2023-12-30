using System;

namespace Mailzeug {
    [Serializable]
    public class MailMessage {
        public readonly string source;
        public readonly string subject;
        public readonly DateTimeOffset timestamp;
        public readonly string from;
        public readonly string to;
        public readonly string cc;
        public readonly string bcc;
        public readonly string body;

        public MailMessage(string source, string subject, DateTimeOffset timestamp, string from, string to, string cc, string bcc, string body) {
            this.source = source;
            this.subject = subject;
            this.timestamp = timestamp;
            this.from = from;
            this.to = to;
            this.cc = cc;
            this.bcc = bcc;
            this.body = body;
        }
    }
}
