using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

using MailKit;

namespace Mailzeug {
    [Serializable]
    public class MailFolder : INotifyPropertyChanged {
        protected string _name;
        [NonSerialized]
        public int unread;
        [NonSerialized]
        public ObservableCollection<MailMessage> messages;
        [NonSerialized]
        public Dictionary<uint, MailMessage> messages_by_id;
        protected List<int> shards;
        [NonSerialized]
        protected HashSet<int> dirty_shards;
        public int weight;
        public uint uid_validity;
        public uint uid_next;
        [NonSerialized]
        public IMailFolder imap_folder;

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        public string name {
            get { return this._name; }
            set {
                this._name = value;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.name)));
            }
        }

        public string counts {
            get {
                if (this.unread <= 0) {
                    return this.messages.Count.ToString("d");
                }
                return $"{this.unread} / {this.messages.Count}";
            }
        }

        public MailFolder(string name, int weight = 0) {
            this._name = name;
            this.unread = 0;
            this.messages = new ObservableCollection<MailMessage>();
            this.messages_by_id = new Dictionary<uint, MailMessage>();
            this.shards = new List<int>();
            this.dirty_shards = new HashSet<int>();
            this.weight = weight;
            this.uid_validity = 0;
            this.uid_next = 0;
            this.imap_folder = null;
        }

        [OnDeserializing]
        public void deserialize_constructor(StreamingContext context) {
            this.unread = 0;
            this.messages = new ObservableCollection<MailMessage>();
            this.messages_by_id = new Dictionary<uint, MailMessage>();
            this.dirty_shards = new HashSet<int>();
        }

        [OnDeserialized]
        public void deserialize_completed(StreamingContext context) {
            foreach (MailMessage msg in this.messages) {
                this.messages_by_id[msg.id] = msg;
            }
        }

        protected static int shard_id(MailMessage msg) {
            return msg.timestamp.Year * 100 + msg.timestamp.Month;
        }

        public void load_messages(string messagesDir) {
            // this calls ObservableCollection.Add a lot, so don't call this if this.messages is an ItemsSource for anything
            string folderDir = Path.Join(messagesDir, this.name);
            Directory.CreateDirectory(folderDir);
            this.unread = 0;
            this.messages.Clear();
            this.messages_by_id.Clear();
            this.dirty_shards.Clear();
            foreach (int shard in this.shards) {
                string shardPath = Path.Join(folderDir, $"{shard}.xml");
                List<MailMessage> messages;
                //TODO: error handling
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<MailMessage>));
                using (FileStream f = new FileStream(shardPath, FileMode.Open)) {
                    XmlDictionaryReader xmlReader = XmlDictionaryReader.CreateTextReader(f, new XmlDictionaryReaderQuotas());
                    messages = (List<MailMessage>)(serializer.ReadObject(xmlReader, true));
                }
                foreach (MailMessage msg in messages) {
                    if (!msg.read) {
                        this.unread += 1;
                    }
                    this.messages.Add(msg);
                    this.messages_by_id[msg.id] = msg;
                }
            }
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public bool save_messages(string messagesDir, bool forceAll = false) {
            if ((this.dirty_shards.Count <= 0) && (!forceAll)) {
                return false;
            }
            int shardId, lastId = -1;
            List<MailMessage> messages = new List<MailMessage>();
            HashSet<int> seenShards = new HashSet<int>();
            string folderDir = Path.Join(messagesDir, this.name);
            Directory.CreateDirectory(folderDir);
            foreach (MailMessage msg in this.messages) {
                shardId = shard_id(msg);
                seenShards.Add(shardId);
                if (shardId == lastId) {
                    // still in the same shard; just add message and move on
                    messages.Add(msg);
                    continue;
                }
                // we changed shards, so save the previous one if necessary and start a new one
                if (messages.Count > 0) {
                    this.save_shard(folderDir, lastId, messages);
                    messages.Clear();
                }
                if ((!forceAll) && (!this.dirty_shards.Contains(shardId))) {
                    // this message isn't from a shard we need so save, so ignore it
                    continue;
                }
                // start the new shard
                lastId = shardId;
                messages.Add(msg);
            }
            if (messages.Count > 0) {
                // save the last shard
                this.save_shard(folderDir, lastId, messages);
            }
            // update shard list
            bool shardsChanged = false;
            for (int i = this.shards.Count - 1; i >= 0; i--) {
                if (seenShards.Contains(this.shards[i])) {
                    seenShards.Remove(this.shards[i]);
                }
                else {
                    // shard no longer contains messages; remove it
                    string shardPath = Path.Join(folderDir, $"{this.shards[i]}.xml");
                    File.Delete(shardPath);
                    this.shards.RemoveAt(i);
                    shardsChanged = true;
                }
            }
            if (seenShards.Count > 0) {
                foreach (int shard in seenShards) {
                    this.shards.Add(shard);
                }
                // keep shard list in descending order
                this.shards.Sort();
                this.shards.Reverse();
                shardsChanged = true;
            }
            return shardsChanged;
        }

        protected void save_shard(string folderDir, int shardId, List<MailMessage> messages) {
            string shardPath = Path.Join(folderDir, $"{shardId}.xml");
            DataContractSerializer serializer = new DataContractSerializer(typeof(List<MailMessage>));
            using (FileStream f = new FileStream(shardPath, FileMode.Create)) {
                serializer.WriteObject(f, messages);
            }
        }

        public void delete(string messagesDir) {
            string folderDir = Path.Join(messagesDir, this.name);
            Directory.Delete(folderDir, true);
        }

        public void purge(string messagesDir) {
            string folderDir = Path.Join(messagesDir, this.name);
            Directory.Delete(folderDir, true);
            this.messages.Clear();
            this.messages_by_id.Clear();
            this.dirty_shards.Clear();
            Directory.CreateDirectory(folderDir);
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        private int get_insertion_index(MailMessage msg, int min = 0, int max = -1) {
            if (max < 0) {
                max = this.messages.Count;
            }
            if (min >= max) {
                return max;
            }
            int mid = (min + max) / 2;
            if (msg.id < this.messages[min].id) {
                return this.get_insertion_index(msg, min, mid);
            }
            return this.get_insertion_index(msg, mid + 1, max);
        }

        public void add_message(MailMessage msg) {
            int idx = this.get_insertion_index(msg);
            this.messages.Insert(idx, msg);
            this.messages_by_id[msg.id] = msg;
            this.dirty_shards.Add(shard_id(msg));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public void set_read(uint uid, bool read) {
            if (!this.messages_by_id.ContainsKey(uid)) {
                return;
            }
            MailMessage msg = this.messages_by_id[uid];
            if (read == msg.read) {
                return;
            }
            this.dirty_shards.Add(shard_id(msg));
            msg.read = read;
            if (read) {
                this.unread -= 1;
            }
            else {
                this.unread += 1;
            }
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }

        public void set_replied(uint uid, bool replied) {
            if (!this.messages_by_id.ContainsKey(uid)) {
                return;
            }
            MailMessage msg = this.messages_by_id[uid];
            if (replied == msg.replied) {
                return;
            }
            this.dirty_shards.Add(shard_id(msg));
            msg.replied = replied;
        }

        public void remove_message(uint uid) {
            if (!this.messages_by_id.ContainsKey(uid)) {
                return;
            }
            MailMessage msg = this.messages_by_id[uid];
            this.dirty_shards.Add(shard_id(msg));
            this.messages.Remove(msg);
            this.messages_by_id.Remove(uid);
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.counts)));
        }
    }
}
