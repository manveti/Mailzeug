using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

using MailKit;

namespace Mailzeug {
    [Serializable]
    public class MailFolder : INotifyPropertyChanged {
        [NonSerialized]
        protected string messages_dir;
        protected string _name;
        [NonSerialized]
        protected MailStore store;
        public int weight;
        public uint uid_validity;
        public uint uid_next;
        [NonSerialized]
        public IMailFolder imap_folder;

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        protected string folder_path => Path.Join(this.messages_dir, this._name);

        public string name {
            get { return this._name; }
            set {
                this._name = value;
                this.store.move(this.folder_path);
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.name)));
            }
        }

        public DateTimeOffset last_changed => this.store.last_changed;
        public ICollection<uint> message_ids => this.store.message_ids;
        public int unread => this.store.unread;
        public int count => this.store.count;
        public int display_count => this.store.display_count;
        public bool dirty => this.store.dirty;
        public BindingList<MailMessage> message_display => this.store.message_display;

        public string counts {
            get {
                if (this.unread <= 0) {
                    return this.display_count.ToString("d");
                }
                return $"{this.unread} / {this.display_count}";
            }
        }

        public MailFolder(string messagesDir, string name, int weight = 0) {
            this.messages_dir = messagesDir;
            this._name = name;
            this.store = new MailStore(this.folder_path);
            this.weight = weight;
            this.uid_validity = 0;
            this.uid_next = 0;
            this.imap_folder = null;
        }

        public void load_messages(string messagesDir) {
            // this must be called immediately after deserialization
            this.messages_dir = messagesDir;
            this.store = new MailStore(this.folder_path);
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public void save_messages(bool forceAll = false) {
            this.store.save(forceAll);
        }

        public void move(string messagesDir) {
            this.messages_dir = messagesDir;
            this.store.move(this.folder_path);
        }

        public void remove() {
            this.store.remove();
        }

        public void purge() {
            this.store.purge();
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public MailMessage add_message(IMessageSummary summary) {
            MailMessage newMessage = this.store.add_message(summary);
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
            return newMessage;
        }

        public void remove_message(uint uid) {
            this.store.remove_message(uid);
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public void set_deleted(uint uid, bool deleted) {
            this.store.set_deleted(uid, deleted);
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public void set_read(uint uid, bool read) {
            this.store.set_read(uid, read);
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public void set_replied(uint uid, bool replied) {
            this.store.set_replied(uid, replied);
        }

        public void load_message(uint uid, MimeKit.MimeMessage message) {
            this.store.load_message(uid, message);
        }
    }
}
