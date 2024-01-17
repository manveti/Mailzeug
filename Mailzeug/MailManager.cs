using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Data;
using System.Xml;

using MailKit;
using MailKit.Net.Imap;

namespace Mailzeug {
    public class MailManager {
        private const int WEIGHT_INBOX = 90;
        private const int WEIGHT_FLAGGED = 80;
        private const int WEIGHT_IMPORTANT = 70;
        private const int WEIGHT_ARCHIVE = 60;
        private const int WEIGHT_DRAFTS = 50;
        private const int WEIGHT_SENT = 40;
        private const int WEIGHT_JUNK = 30;
        private const int WEIGHT_TRASH = 20;
        private const int WEIGHT_ALL = 10;
        private const int WEIGHT_NONE = 0;

        private const int SYNC_BATCH_SIZE = 100;

        private static readonly TimeSpan FULL_SYNC_INTERVAL = new TimeSpan(1, 0, 0);
        private static readonly TimeSpan IDLE_SYNC_INTERVAL = new TimeSpan(0, 9, 0);

        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private bool running = true;
        private readonly MainWindow window;
        private readonly NLog.Logger sync_log;
        private readonly NLog.Logger user_log;
        private ImapClient client;
        public object cache_lock;
        public BindingList<MailFolder> folders;
        public MailFolder selected_folder = null;
        public int selected_message = -1;
        private List<SyncTask> sync_tasks;
        private SyncTask priority_task = null;
        private ManualResetEvent priority_event;
        private DateTimeOffset last_sync;
        private object token_lock;
        private CancellationTokenSource shutdown_token;
        private CancellationTokenSource idle_token;
        private Thread sync_thread;

        private Config config => this.window.config;
        private string data_dir => this.config.data_dir;

        private class SyncTask {
            public readonly IMailFolder imap_folder;

            public SyncTask(IMailFolder imapFolder) {
                this.imap_folder = imapFolder;
            }
        }

        private class FullSyncTask : SyncTask {
            public FullSyncTask(IMailFolder imapFolder = null) : base(imapFolder) { }
        }

        private abstract class FolderSyncTask : SyncTask {
            public readonly MailFolder mz_folder;

            public FolderSyncTask(MailFolder mzFolder, IMailFolder imapFolder) : base(imapFolder) {
                this.mz_folder = mzFolder;
            }
        }

        private class FolderContentsSyncTask : FolderSyncTask {
            public readonly int offset;

            public FolderContentsSyncTask(MailFolder mzFolder, IMailFolder imapFolder, int offset = 0) : base(mzFolder, imapFolder) {
                this.offset = offset;
            }
        }

        //TODO: MessageDownloadTask

        public MailManager(MainWindow window) {
            this.window = window;
            this.sync_log = Logger.WithProperty("source", "SYNC");
            this.user_log = Logger.WithProperty("source", "USER");
            this.client = new ImapClient();
            this.cache_lock = new object();
            this.sync_tasks = new List<SyncTask>();
            this.priority_event = new ManualResetEvent(false);
            this.last_sync = DateTimeOffset.MinValue;
            this.token_lock = new object();
            this.shutdown_token = new CancellationTokenSource();
            this.shutdown_token.Token.ThrowIfCancellationRequested();
            this.idle_token = new CancellationTokenSource();
            this.idle_token.Token.ThrowIfCancellationRequested();
            this.load_cache();
            this.sync_thread = new Thread(this.sync_loop);
            this.sync_thread.Start();
        }

        public void shutdown() {
            this.running = false;
            lock (this.token_lock) {
                this.idle_token.Cancel();
                this.shutdown_token.Cancel();
            }
            this.sync_thread.Join();
            this.client.Disconnect(true);
        }

        private void load_cache() {
            string messagesDir = Path.Join(this.data_dir, "messages");
            Directory.CreateDirectory(messagesDir);
            string foldersPath = Path.Join(messagesDir, "folders.xml");
            BindingList<MailFolder> folders = new BindingList<MailFolder>();
            if (File.Exists(foldersPath)) {
                List<MailFolder> folderList;
                //TODO: error handling
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<MailFolder>));
                using (FileStream f = new FileStream(foldersPath, FileMode.Open)) {
                    XmlDictionaryReader xmlReader = XmlDictionaryReader.CreateTextReader(f, new XmlDictionaryReaderQuotas());
                    folderList = (List<MailFolder>)(serializer.ReadObject(xmlReader, true));
                }
                foreach (MailFolder folder in folderList) {
                    folder.load_messages(messagesDir);
                    folders.Add(folder);
                }
            }
            lock (this.cache_lock) {
                this.folders = folders;
                this.selected_folder = null;
                this.selected_message = -1;
            }
            BindingOperations.EnableCollectionSynchronization(this.folders, this.cache_lock);
        }

        private void save_cache(bool forceAll = false) {
            string messagesDir = Path.Join(this.data_dir, "messages");
            Directory.CreateDirectory(messagesDir);
            List<MailFolder> folderList;
            lock (this.cache_lock) {
                folderList = new List<MailFolder>(this.folders);
            }
            foreach (MailFolder folder in folderList) {
                folder.save_messages(messagesDir, forceAll);
            }
            this.save_folders(messagesDir, folderList);
        }

        private void save_folders(string messagesDir, List<MailFolder> folderList = null) {
            if (folderList is null) {
                lock (this.cache_lock) {
                    folderList = new List<MailFolder>(this.folders);
                }
            }
            string foldersPath = Path.Join(messagesDir, "folders.xml");
            DataContractSerializer serializer = new DataContractSerializer(typeof(List<MailFolder>));
            using (FileStream f = new FileStream(foldersPath, FileMode.Create)) {
                serializer.WriteObject(f, folderList);
            }
        }

        private void sync_loop() {
            this.client.Disconnected += (obj, args) => this.connect_client();
            //TODO: handler for this.client.FolderCreated
            this.connect_client();
            while (this.running) {
                if (this.priority_task is not null) {
                    this.handle_sync_task(this.priority_task);
                    this.priority_task = null;
                    this.priority_event.Set();
                    continue;
                }
                SyncTask task = null;
                if (DateTimeOffset.UtcNow >= this.last_sync + FULL_SYNC_INTERVAL) {
                    task = new FullSyncTask();
                }
                else {
                    lock (this.sync_tasks) {
                        if (this.sync_tasks.Count > 0) {
                            task = this.sync_tasks[0];
                            this.sync_tasks.RemoveAt(0);
                        }
                    }
                }
                if (task is not null) {
                    this.handle_sync_task(task);
                    continue;
                }
                this.sync_log.Info("Idling for new activity");
                //TODO: error handling
                //TODO: setup notify for idling
                this.client.Inbox.Open(FolderAccess.ReadOnly);
                this.idle_token.CancelAfter(IDLE_SYNC_INTERVAL);
                try {
                    this.client.Idle(this.idle_token.Token, this.shutdown_token.Token);
                }
                catch (OperationCanceledException) {
                    // this may be due to a new event coming in or shutdown; only terminate in the latter case
                    if (!this.running) {
                        this.sync_log.Info("Terminating sync due to shutdown");
                        return;
                    }
                }
                this.client.Inbox.Close();
                lock (this.token_lock) {
                    this.idle_token.Dispose();
                    this.idle_token = new CancellationTokenSource();
                    this.idle_token.Token.ThrowIfCancellationRequested();
                    this.shutdown_token.Dispose();
                    this.shutdown_token = new CancellationTokenSource();
                    this.shutdown_token.Token.ThrowIfCancellationRequested();
                }
            }
        }

        private void connect_client() {
            //TODO: error handling
            this.client.Connect(this.config.imap_server, this.config.imap_port, MailKit.Security.SecureSocketOptions.SslOnConnect);
            this.client.Authenticate(this.config.username, this.config.password);
        }

        private static int get_weight(IMailFolder folder) {
            int weight = WEIGHT_NONE;
            if ((weight < WEIGHT_INBOX) && (folder.Attributes.HasFlag(FolderAttributes.Inbox))) {
                weight = WEIGHT_INBOX;
            }
            if ((weight < WEIGHT_FLAGGED) && (folder.Attributes.HasFlag(FolderAttributes.Flagged))) {
                weight = WEIGHT_FLAGGED;
            }
            if ((weight < WEIGHT_IMPORTANT) && (folder.Attributes.HasFlag(FolderAttributes.Important))) {
                weight = WEIGHT_IMPORTANT;
            }
            if ((weight < WEIGHT_ARCHIVE) && (folder.Attributes.HasFlag(FolderAttributes.Archive))) {
                weight = WEIGHT_ARCHIVE;
            }
            if ((weight < WEIGHT_DRAFTS) && (folder.Attributes.HasFlag(FolderAttributes.Drafts))) {
                weight = WEIGHT_DRAFTS;
            }
            if ((weight < WEIGHT_SENT) && (folder.Attributes.HasFlag(FolderAttributes.Sent))) {
                weight = WEIGHT_SENT;
            }
            if ((weight < WEIGHT_JUNK) && (folder.Attributes.HasFlag(FolderAttributes.Junk))) {
                weight = WEIGHT_JUNK;
            }
            if ((weight < WEIGHT_TRASH) && (folder.Attributes.HasFlag(FolderAttributes.Trash))) {
                weight = WEIGHT_TRASH;
            }
            if ((weight < WEIGHT_ALL) && (folder.Attributes.HasFlag(FolderAttributes.All))) {
                weight = WEIGHT_ALL;
            }
            return weight;
        }

        private int get_insertion_index(IMailFolder folder, int weight) {
            // this function assumes caller holds cache_lock
            int insertIdx = this.folders.Count;
            for (int i = 0; i < this.folders.Count; i++) {
                if (this.folders[i].name == folder.FullName) {
                    return i;
                }
                if ((i >= insertIdx) || (this.folders[i].weight > weight)) {
                    continue;
                }
                if ((this.folders[i].weight < weight) || (folder.FullName.CompareTo(this.folders[i].name) < 0)) {
                    insertIdx = i;
                }
            }
            return insertIdx;
        }

        private void handle_sync_task(SyncTask task) {
            if (task is FullSyncTask fullSyncTask) {
                if (fullSyncTask.imap_folder is null) {
                    // full sync of all folders
                    this.sync_folders();
                }
                else {
                    // full sync of single folder (e.g. new folder was created)
                    //TODO: handle full sync of single folder
                }
                this.last_sync = DateTimeOffset.UtcNow;
                return;
            }
            if (task is FolderContentsSyncTask folderContentsSyncTask) {
                this.sync_messages(folderContentsSyncTask);
                return;
            }
            //TODO: MessageDownloadTask
        }

        private void sync_folders() {
            this.sync_log.Info("Syncing all folders");
            List<IMailFolder> folders = new List<IMailFolder>();
            foreach (FolderNamespace ns in this.client.PersonalNamespaces) {
                //TODO: error handling
                try {
                    folders.AddRange(
                        this.client.GetFolders(
                            ns, StatusItems.Count | StatusItems.UidNext | StatusItems.UidValidity, false, this.shutdown_token.Token
                        )
                    );
                }
                catch (OperationCanceledException) {
                    this.sync_log.Info("Terminating sync due to shutdown");
                    return;
                }
                lock (this.token_lock) {
                    this.shutdown_token.Dispose();
                    this.shutdown_token = new CancellationTokenSource();
                    this.shutdown_token.Token.ThrowIfCancellationRequested();
                }
            }
            List<MailFolder> deleted = new List<MailFolder>(this.folders);
            bool dirty = false;
            string messagesDir = Path.Join(this.data_dir, "messages");
            lock (this.cache_lock) {
                foreach (IMailFolder imapFolder in folders) {
                    int weight = get_weight(imapFolder);
                    int idx = this.get_insertion_index(imapFolder, weight);
                    MailFolder mzFolder;
                    if ((idx < this.folders.Count) && (this.folders[idx].name == imapFolder.FullName)) {
                        mzFolder = this.folders[idx];
                        deleted.Remove(mzFolder);
                    }
                    else {
                        this.sync_log.Info("Adding new folder {name}", imapFolder.FullName);
                        mzFolder = new MailFolder(imapFolder.FullName, weight);
                        this.folders.Insert(idx, mzFolder);
                        dirty = true;
                    }
                    mzFolder.imap_folder = imapFolder;
                    //TODO: handlers for imapFolder.CountChanged, .Deleted, .MessageExpunged, .MessageFlagsChanged, .UidValidityChanged
                    if (
                        (imapFolder.UidValidity != mzFolder.uid_validity) ||
                        (imapFolder.Count != mzFolder.messages.Count) ||
                        (imapFolder.UidNext.Value.Id != mzFolder.uid_next)
                    ) {
                        // folder needs sync
                        this.sync_log.Info("Scheduling sync of folder {name}", imapFolder.FullName);
                        if (imapFolder.UidValidity != mzFolder.uid_validity) {
                            // UID validity differs; invalidate whole folder cache
                            mzFolder.purge(messagesDir);
                        }
                        FolderContentsSyncTask task = new FolderContentsSyncTask(mzFolder, imapFolder);
                        bool needTask = true;
                        lock (this.sync_tasks) {
                            for (int i = 0; i < this.sync_tasks.Count; i++) {
                                if ((this.sync_tasks[i] is FolderContentsSyncTask folderTask) && (folderTask.mz_folder.name == mzFolder.name)) {
                                    // already a pending sync task on this folder; replace it with a new task
                                    this.sync_tasks[i] = task;
                                    needTask = false;
                                    break;
                                }
                            }
                            if (needTask) {
                                // no pending sync task on this folder; add a new one
                                this.sync_tasks.Add(task);
                            }
                        }
                        mzFolder.uid_validity = imapFolder.UidValidity;
                        mzFolder.uid_next = imapFolder.UidNext.Value.Id;
                        dirty = true;
                    }
                }
                foreach (MailFolder folder in deleted) {
                    this.sync_log.Info("Deleting folder {name}", folder.name);
                    this.delete_folder(messagesDir, folder);
                    dirty = true;
                }
            }
            if (dirty) {
                this.save_cache();
            }
            this.sync_log.Info("Done syncing all folders");
        }

        private void sync_messages(FolderContentsSyncTask task) {
            if (task.offset < task.mz_folder.messages.Count) {
                sync_cached_messages(task);
            }
            else {
                sync_new_messages(task);
            }
            //TODO: update folders list in UI
            string messagesDir = Path.Join(this.data_dir, "messages");
            bool shardsChanged;
            lock (this.cache_lock) {
                shardsChanged = task.mz_folder.save_messages(messagesDir);
            }
            if (shardsChanged) {
                this.save_folders(messagesDir);
            }
        }

        private void sync_cached_messages(FolderContentsSyncTask task) {
            MailFolder mzFolder = task.mz_folder;
            this.sync_log.Info("Syncing old messages in {name}, {offset} / {count}", mzFolder.name, task.offset, mzFolder.messages.Count);
            List<MailKit.UniqueId> uids = new List<MailKit.UniqueId>(SYNC_BATCH_SIZE);
            HashSet<uint> unseen = new HashSet<uint>();
            lock (this.cache_lock) {
                for (int i = 0; i < SYNC_BATCH_SIZE; i++) {
                    int mzIdx = task.offset + i;
                    if (mzIdx >= mzFolder.messages.Count) {
                        break;
                    }
                    uint uid = mzFolder.messages[mzIdx].id;
                    uids.Add(new MailKit.UniqueId(uid));
                    unseen.Add(uid);
                }
            }
            FetchRequest req = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
            task.imap_folder.Open(FolderAccess.ReadOnly);
            //TODO: error handling
            IList<IMessageSummary> summaries;
            try {
                summaries = task.imap_folder.Fetch(uids, req, this.shutdown_token.Token);
            }
            catch (OperationCanceledException) {
                this.sync_log.Info("Terminating sync due to shutdown");
                return;
            }
            task.imap_folder.Close();
            lock (this.token_lock) {
                this.shutdown_token.Dispose();
                this.shutdown_token = new CancellationTokenSource();
                this.shutdown_token.Token.ThrowIfCancellationRequested();
            }
            int msgCount = 0;
            lock (this.cache_lock) {
                foreach (IMessageSummary sum in summaries) {
                    uint uid = sum.UniqueId.Id;
                    unseen.Remove(sum.UniqueId.Id);
                    if (!mzFolder.messages_by_id.ContainsKey(uid)) {
                        continue;
                    }
                    if ((sum.Flags & MessageFlags.Deleted) != 0) {
                        mzFolder.remove_message(uid);
                        continue;
                    }
                    MailMessage msg = mzFolder.messages_by_id[uid];
                    msgCount += 1;
                    mzFolder.set_read(uid, (sum.Flags & MessageFlags.Seen) != 0);
                    mzFolder.set_replied(uid, (sum.Flags & MessageFlags.Answered) != 0);
                }
                foreach (uint uid in unseen) {
                    mzFolder.remove_message(uid);
                }
            }
            lock (this.sync_tasks) {
                this.sync_tasks.Add(new FolderContentsSyncTask(task.mz_folder, task.imap_folder, task.offset + msgCount));
            }
            this.sync_log.Info("Synced {count} old messages in {name}", msgCount, mzFolder.name);
        }

        private void sync_new_messages(FolderContentsSyncTask task) {
            this.sync_log.Info("Syncing new messages in {name}, {offset} / {count}", task.mz_folder.name, task.offset, task.imap_folder.Count);
            //TODO: notify new unread messages
            int startIdx = task.offset;
            if (startIdx >= task.imap_folder.Count) {
                this.sync_log.Info("No more new messages to sync");
                return;
            }
            int endIdx = startIdx + SYNC_BATCH_SIZE;
            if (endIdx >= task.imap_folder.Count) {
                endIdx = -1;
            }
            FetchRequest req = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Envelope);
            task.imap_folder.Open(FolderAccess.ReadOnly);
            //TODO: error handling
            IList<IMessageSummary> summaries;
            try {
                summaries = task.imap_folder.Fetch(startIdx, endIdx, req, this.shutdown_token.Token);
            }
            catch (OperationCanceledException) {
                this.sync_log.Info("Terminating sync due to shutdown");
                return;
            }
            task.imap_folder.Close();
            lock (this.token_lock) {
                this.shutdown_token.Dispose();
                this.shutdown_token = new CancellationTokenSource();
                this.shutdown_token.Token.ThrowIfCancellationRequested();
            }
            lock (this.cache_lock) {
                foreach (IMessageSummary sum in summaries) {
                    if ((sum.Flags & MessageFlags.Deleted) != 0) {
                        continue;
                    }
                    task.mz_folder.add_message(new MailMessage(sum));
                }
            }
            if ((endIdx < 0) || (summaries.Count <= 0)) {
                this.sync_log.Info("Synced all new messages in {name}", task.mz_folder.name);
                return;
            }
            lock (this.sync_tasks) {
                this.sync_tasks.Add(new FolderContentsSyncTask(task.mz_folder, task.imap_folder, task.offset + summaries.Count));
            }
            this.sync_log.Info("Synced {count} new messages in {name}", summaries.Count, task.mz_folder.name);
        }

        //TODO: handle MessageDownloadTask

        public void delete_folder(string messagesDir, MailFolder folder) {
            // delete any pending sync tasks on this folder
            lock (this.sync_tasks) {
                for (int i = this.sync_tasks.Count - 1; i >= 0; i--) {
                    if ((this.sync_tasks[i] is FolderSyncTask folderTask) && (folderTask.mz_folder.name == folder.name)) {
                        this.sync_tasks.RemoveAt(i);
                    }
                }
            }
            folder.delete(messagesDir);
            if (this.selected_folder == folder) {
                this.select_folder(null);
            }
            this.folders.Remove(folder);
        }

        //TODO: other main functionality

        public void select_folder(MailFolder sel) {
            if (sel == this.selected_folder) {
                return;
            }
            this.selected_folder = sel;
            if (sel is not null) {
                BindingOperations.EnableCollectionSynchronization(sel.messages, this.cache_lock);
            }
            this.window.message_list.ItemsSource = sel?.messages;
        }

        public void select_message(int idx) {
            if (idx == this.selected_message) {
                return;
            }
            MailMessage sel = null;
            if ((this.selected_folder is not null) && (idx >= 0) && (idx < this.selected_folder.messages.Count)) {
                sel = this.selected_folder.messages[idx];
            }
            //TODO: if sel.source is null: download
            this.window.message_ctrl.show_message(sel, false);
        }

        //TODO: other handlers
    }
}
