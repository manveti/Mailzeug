using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Windows.Data;
using System.Xml;

namespace Mailzeug {
    public class MailStoreError : MZError {
        public MailStoreError() : base() { }
        public MailStoreError(string message) : base(message) { }
        public MailStoreError(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MailLoadError : MailStoreError {
        public MailLoadError() : base() { }
        public MailLoadError(string message) : base(message) { }
        public MailLoadError(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MailStore {
        protected string path;
        protected object message_lock;
        protected Dictionary<uint, MailMessage> messages;
        public BindingList<MailMessage> message_display;
        protected HashSet<int> shards;
        protected HashSet<int> dirty_shards;
        protected HashSet<int> new_shards;
        public DateTimeOffset last_changed;
        public int unread;

        public ICollection<uint> message_ids => this.messages.Keys;
        public int count => this.messages.Count;
        public int display_count => this.message_display.Count;
        public bool dirty => this.dirty_shards.Count > 0;

        public MailStore(string path) {
            this.path = path;
            this.message_lock = new object();
            this.messages = new Dictionary<uint, MailMessage>();
            this.message_display = new BindingList<MailMessage>();
            this.shards = new HashSet<int>();
            this.dirty_shards = new HashSet<int>();
            this.new_shards = new HashSet<int>();
            this.last_changed = DateTimeOffset.Now;
            this.unread = 0;

            BindingOperations.EnableCollectionSynchronization(this.message_display, this.message_lock);

            string indexPath = Path.Join(this.path, "index.xml");
            if (!File.Exists(indexPath)) {
                // no messages to load
                return;
            }
            List<int> shards;
            DataContractSerializer serializer = new DataContractSerializer(typeof(List<int>));
            try {
                using (FileStream f = new FileStream(indexPath, FileMode.Open)) {
                    XmlDictionaryReader xmlReader = XmlDictionaryReader.CreateTextReader(f, new XmlDictionaryReaderQuotas());
                    shards = (List<int>)(serializer.ReadObject(xmlReader, true));
                }
            }
            catch (Exception e) when ((e is IOException) || (e is System.Security.SecurityException) || (e is XmlException)) {
                throw new MailLoadError($"Failed to load {indexPath}: {e.Message}", e);
            }

            foreach (int shard in shards) {
                this.shards.Add(shard);
                string shardPath = Path.Join(this.path, $"{shard}.xml");
                List<MailMessage> loadMsgs;
                serializer = new DataContractSerializer(typeof(List<MailMessage>));
                try {
                    using (FileStream f = new FileStream(shardPath, FileMode.Open)) {
                        XmlDictionaryReader xmlReader = XmlDictionaryReader.CreateTextReader(f, new XmlDictionaryReaderQuotas());
                        loadMsgs = (List<MailMessage>)(serializer.ReadObject(xmlReader, true));
                    }
                }
                catch (Exception e) when ((e is IOException) || (e is System.Security.SecurityException) || (e is XmlException)) {
                    throw new MailLoadError($"Failed to load {shardPath}: {e.Message}", e);
                }
                foreach (MailMessage msg in loadMsgs) {
                    if (msg.unread) {
                        this.unread += 1;
                    }
                    this.messages[msg.id] = msg;
                    if (this.should_display_message(msg)) {
                        this.message_display.Add(msg);
                    }
                }
            }
        }

        protected static int shard_id(MailMessage msg) {
            return msg.timestamp.Year * 100 + msg.timestamp.Month;
        }

        protected string index_path() {
            return Path.Join(this.path, "index.xml");
        }

        protected string shard_path(int shardId) {
            return Path.Join(this.path, $"{shardId}.xml");
        }

        public void save(bool forceAll = false) {
            lock (this.message_lock) {
                if ((this.dirty_shards.Count <= 0) && (!forceAll)) {
                    return;
                }
                // split messages into shards so we don't constantly rewrite whole message list
                Dictionary<int, List<MailMessage>> shards = new Dictionary<int, List<MailMessage>>();
                HashSet<int> seenShards = new HashSet<int>();
                foreach (MailMessage msg in this.messages.Values) {
                    int shardId = shard_id(msg);
                    seenShards.Add(shardId);
                    if ((!this.dirty_shards.Contains(shardId)) && (!forceAll)) {
                        continue;
                    }
                    if (!shards.ContainsKey(shardId)) {
                        shards[shardId] = new List<MailMessage>();
                    }
                    shards[shardId].Add(msg);
                }
                // write dirty shards to disk; if any written shards were new, we'll need to write the list of shards too
                Directory.CreateDirectory(this.path);
                bool shardsDirty = forceAll;
                foreach (int shardId in shards.Keys) {
                    if (this.new_shards.Contains(shardId)) {
                        shardsDirty = true;
                    }
                    this.save_shard(shardId, shards[shardId]);
                    this.shards.Add(shardId);
                }
                // delete any empty shards which aren't marked as new; deleted existing shards mean we'll need to rewrite the shard list
                foreach (int shardId in this.dirty_shards) {
                    if ((shards.ContainsKey(shardId)) || (this.new_shards.Contains(shardId))) {
                        continue;
                    }
                    shardsDirty = true;
                    //TODO: error handling
                    File.Delete(this.shard_path(shardId));
                    if (this.shards.Contains(shardId)) {
                        this.shards.Remove(shardId);
                    }
                }
                if (shardsDirty) {
                    // write shard list in descending order
                    List<int> shardList = new List<int>(seenShards);
                    shardList.Sort();
                    shardList.Reverse();
                    DataContractSerializer serializer = new DataContractSerializer(typeof(List<int>));
                    using (FileStream f = new FileStream(this.index_path(), FileMode.Create)) {
                        serializer.WriteObject(f, shardList);
                    }
                }
                this.dirty_shards.Clear();
                this.new_shards.Clear();
            }
        }

        protected void save_shard(int shardId, List<MailMessage> messages) {
            // NOTE: this assumes we're holding message_lock
            // sort in descending timestamp order
            messages.Sort((x, y) => y.timestamp.CompareTo(x.timestamp));
            DataContractSerializer serializer = new DataContractSerializer(typeof(List<MailMessage>));
            using (FileStream f = new FileStream(this.shard_path(shardId), FileMode.Create)) {
                serializer.WriteObject(f, messages);
            }
        }

        public void move(string path) {
            lock (this.message_lock) {
                if (Directory.Exists(this.path)) {
                    try {
                        Directory.Move(this.path, path);
                    }
                    catch (IOException e) {
                        throw new MailStoreError($"Failed to move {this.path}: {e.Message}", e);
                    }
                }
                this.path = path;
            }
        }

        public void remove() {
            if (Directory.Exists(this.path)) {
                try {
                    Directory.Delete(this.path, true);
                }
                catch (IOException e) {
                    throw new MailStoreError($"Failed to remove {this.path}: {e.Message}", e);
                }
            }
        }

        public void purge() {
            lock (this.message_lock) {
                this.remove();
                this.messages.Clear();
                this.message_display.Clear();
                this.shards.Clear();
                this.dirty_shards.Clear();
                this.new_shards.Clear();
                this.last_changed = DateTimeOffset.Now;
                this.unread = 0;
            }
        }

        protected bool should_display_message(MailMessage msg) {
            return !msg.deleted;
        }

        protected int get_insertion_index(MailMessage msg, int min = 0, int max = -1) {
            // NOTE: this assumes we're holding message_lock
            // sort from newest to oldest, breaking ties by highest to lowest id
            if (max < 0) {
                max = this.message_display.Count;
            }
            if (min >= max) {
                return max;
            }
            int mid = (min + max) / 2;
            MailMessage midMsg = this.message_display[mid];
            if (msg.id == midMsg.id) {
                // we shouldn't be calling this if the message is already in the list, but we'll handle the case for completeness
                return mid;
            }
            if ((msg.timestamp > midMsg.timestamp) || ((msg.timestamp == midMsg.timestamp) && (msg.id > midMsg.id))) {
                return this.get_insertion_index(msg, min, mid);
            }
            return this.get_insertion_index(msg, mid + 1, max);
        }

        protected void set_message_display(MailMessage msg) {
            // NOTE: this assumes we're holding message_lock
            if (!this.should_display_message(msg)) {
                // message shouldn't be displayed; remove it from message_display
                this.message_display.Remove(msg);
                return;
            }
            // message should be displayed; add it to message_display
            int idx = this.get_insertion_index(msg);
            if ((idx < this.message_display.Count) && (this.message_display[idx].id == msg.id)) {
                // message already displayed; nothing more to do
                return;
            }
            this.message_display.Insert(idx, msg);
        }

        public MailMessage add_message(MailKit.IMessageSummary summary) {
            // returns message if message is new, null if not
            uint uid = summary.UniqueId.Id;
            MailMessage newMessage = null;
            bool dirty = true;
            lock (this.message_lock) {
                if (this.messages.ContainsKey(uid)) {
                    // message already exists; update it
                    if (this.messages[uid].unread) {
                        this.unread -= 1;
                    }
                    dirty = this.messages[uid].update(summary);
                }
                else {
                    // new message; add it
                    newMessage = new MailMessage(summary);
                    this.messages[uid] = newMessage;
                }
                if (this.messages[uid].unread) {
                    this.unread += 1;
                }
                if (dirty) {
                    this.set_message_display(this.messages[uid]);
                    int shardId = shard_id(this.messages[uid]);
                    this.dirty_shards.Add(shardId);
                    if (!this.shards.Contains(shardId)) {
                        this.new_shards.Add(shardId);
                    }
                    this.last_changed = DateTimeOffset.Now;
                }
            }
            return newMessage;
        }

        public void remove_message(uint uid) {
            lock (this.message_lock) {
                if (!this.messages.ContainsKey(uid)) {
                    return;
                }
                if (this.messages[uid].unread) {
                    this.unread -= 1;
                }
                this.message_display.Remove(this.messages[uid]);
                this.dirty_shards.Add(shard_id(this.messages[uid]));
                this.messages.Remove(uid);
                this.last_changed = DateTimeOffset.Now;
            }
        }

        public void set_deleted(uint uid, bool deleted) {
            lock (this.message_lock) {
                if ((!this.messages.ContainsKey(uid)) || (this.messages[uid].deleted == deleted)) {
                    return;
                }
                this.messages[uid].deleted = deleted;
                this.set_message_display(this.messages[uid]);
                this.dirty_shards.Add(shard_id(this.messages[uid]));
                this.last_changed = DateTimeOffset.Now;
            }
        }

        public void set_read(uint uid, bool read) {
            lock (this.message_lock) {
                if ((!this.messages.ContainsKey(uid)) || (this.messages[uid].read == read)) {
                    return;
                }
                this.messages[uid].read = read;
                this.set_message_display(this.messages[uid]);
                this.dirty_shards.Add(shard_id(this.messages[uid]));
                this.last_changed = DateTimeOffset.Now;
            }
        }

        public void set_replied(uint uid, bool replied) {
            lock (this.message_lock) {
                if ((!this.messages.ContainsKey(uid)) || (this.messages[uid].replied == replied)) {
                    return;
                }
                this.messages[uid].replied = replied;
                this.set_message_display(this.messages[uid]);
                this.dirty_shards.Add(shard_id(this.messages[uid]));
                this.last_changed = DateTimeOffset.Now;
            }
        }

        public void load_message(uint uid, MimeKit.MimeMessage message) {
            lock (this.message_lock) {
                if (!this.messages.ContainsKey(uid)) {
                    return;
                }
                this.messages[uid].load(message);
                this.dirty_shards.Add(shard_id(this.messages[uid]));
                this.last_changed = DateTimeOffset.Now;
            }
        }
    }
}
