using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Mailzeug {
    [Serializable]
    public class Config {
        [NonSerialized]
        public string data_dir;
        public string server;
        public int port;
        [NonSerialized]
        protected byte[] key;
        protected byte[] username_enc;
        protected byte[] password_enc;

        public string username {
            get { return this.decrypt(this.username_enc); }
            set { this.username_enc = this.encrypt(value); }
        }

        public string password {
            get { return this.decrypt(this.password_enc); }
            set { this.password_enc = this.encrypt(value); }
        }

        public Config() {
            this.key = new byte[256];
            this.reset(false);
        }

        public void reset(bool resetKey = true) {
            this.server = "";
            this.port = 993;
            this.username_enc = null;
            this.password_enc = null;
            if (resetKey) {
                this.reset_key();
            }
        }

        protected void reset_key() {
            RandomNumberGenerator.Fill(this.key);
        }

        protected string decrypt(byte[] enc) {
            if (enc is null) {
                return null;
            }
            byte[] dec = ProtectedData.Unprotect(enc, this.key, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }

        protected byte[] encrypt(string str) {
            if (str is null) {
                return null;
            }
            byte[] dec = Encoding.UTF8.GetBytes(str);
            return ProtectedData.Protect(dec, this.key, DataProtectionScope.CurrentUser);
        }

        public static Config load(string dataDir) {
            Directory.CreateDirectory(dataDir);
            string configPath = Path.Join(dataDir, "config.xml");
            string keyPath = Path.Join(dataDir, "key.dat");
            Config config;
            if (File.Exists(configPath)) {
                DataContractSerializer serializer = new DataContractSerializer(typeof(Config));
                using (FileStream f = new FileStream(configPath, FileMode.Open)) {
                    XmlDictionaryReader xmlReader = XmlDictionaryReader.CreateTextReader(f, new XmlDictionaryReaderQuotas());
                    config = (Config)(serializer.ReadObject(xmlReader, true));
                }
            }
            else {
                config = new Config();
            }
            config.data_dir = dataDir;
            if (File.Exists(keyPath)) {
                config.key = File.ReadAllBytes(keyPath);
            }
            else {
                config.reset_key();
            }
            return config;
        }

        public void save() {
            Directory.CreateDirectory(this.data_dir);
            string configPath = Path.Join(this.data_dir, "config.xml");
            string keyPath = Path.Join(this.data_dir, "key.dat");
            DataContractSerializer serializer = new DataContractSerializer(typeof(Config));
            using (FileStream f = new FileStream(configPath, FileMode.Create)) {
                serializer.WriteObject(f, this);
            }
            File.WriteAllBytes(keyPath, this.key);
        }
    }
}
