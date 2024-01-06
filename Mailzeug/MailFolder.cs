using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace Mailzeug {
    [Serializable]
    public class MailFolder {
        public string _name;
        [NonSerialized]
        public int count;
        [NonSerialized]
        public int unread;
        [NonSerialized]
        public ObservableCollection<MailMessage> messages;
        [NonSerialized]
        public Dictionary<string, MailMessage> messages_by_id;
        protected List<int> shards;
        [NonSerialized]
        protected HashSet<int> dirty_shards;
        //TODO: last sync, sequence numbers, display weight

        public string name {
            get { return this._name; }
            set { this._name = value; }
        }

        public string counts {
            get {
                if (this.unread <= 0) {
                    return this.count.ToString("d");
                }
                return $"{this.unread} / {this.count}";
            }
        }

        public MailFolder(string name) {
            this._name = name;
            this.count = 0;
            this.unread = 0;
            this.messages = new ObservableCollection<MailMessage>();
            this.messages_by_id = new Dictionary<string, MailMessage>();
            this.shards = new List<int>();
            this.dirty_shards = new HashSet<int>();
        }

        private MailFolder() {
            // only used for deserialization, so only need to initialize non-serialized fields
            this.count = 0;
            this.unread = 0;
            this.messages = null;
            this.messages_by_id = null;
            this.dirty_shards = new HashSet<int>();
        }

        public void load_messages(string dataDir) {
            // this calls ObservableCollection.Add a lot, so don't call this if this.messages is an ItemsSource for anything
            string folderPath = Path.Join(dataDir, this.name);
            this.count = 0;
            this.unread = 0;
            this.messages.Clear();
            this.messages_by_id.Clear();
            this.dirty_shards.Clear();
            foreach (int shard in this.shards) {
                string shardPath = Path.Join(folderPath, $"{shard}.dat");
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
                }
            }
            this.count = this.messages.Count;
        }

        //TODO: save_messages, ...
    }
}
