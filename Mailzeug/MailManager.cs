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
using Microsoft.Toolkit.Uwp.Notifications;
using MimeKit;

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

        private static readonly TimeSpan SAVE_INTERVAL = new TimeSpan(0, 0, 10);

        private const int SYNC_BATCH_SIZE = 100;
        private const int UID_LOOKUP_RANGE = 10;

        private static readonly TimeSpan FORCE_SYNC_INTERVAL = new TimeSpan(8, 0, 0);
        private static readonly TimeSpan FULL_SYNC_INTERVAL = new TimeSpan(1, 0, 0);
        private static readonly TimeSpan IDLE_SYNC_INTERVAL = new TimeSpan(0, 9, 0);

        private static readonly TimeSpan NOTIFY_MAX_AGE = new TimeSpan(7, 0, 0, 0);

        private const string NOTIFY_ARG_FOLDER = "folder";
        private const string NOTIFY_ARG_MESSAGE = "message";
        private const string NOTIFY_ARG_ACTION = "action";
        private const string NOTIFY_ACTION_DELETE = "delete";

        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly MainWindow window;
        private readonly NLog.Logger sync_log;
        private readonly NLog.Logger event_log;
        private readonly NLog.Logger user_log;
        private bool running = true;
        private ManualResetEvent shutdown_event;
        private ImapClient client;
        public object folders_lock;
        public BindingList<MailFolder> folders;
        public MailFolder selected_folder = null;
        public int selected_message = -1;
        private LinkedList<SyncTask> sync_tasks;
        private DateTimeOffset last_force_sync;
        private DateTimeOffset last_full_sync;
        private object token_lock;
        private CancellationTokenSource shutdown_token;
        private CancellationTokenSource idle_token;
        private Thread save_thread;
        private Thread sync_thread;

        private Config config => this.window.config;
        private string data_dir => this.config.data_dir;
        private string messages_dir => Path.Join(this.data_dir, "messages");
        private string folders_path => Path.Join(this.messages_dir, "folders.xml");

        private abstract class SyncTask {
            public readonly bool priority;

            public SyncTask(bool priority = false) {
                this.priority = priority;
            }
        }

        private class FullFetchTask : SyncTask {
            // perform a full fetch on all folders
            // if force is true, check all messages, even if heuristics indicate nothing has changed
            public readonly bool force;

            public FullFetchTask(bool force = false, bool priority = false) : base(priority) {
                this.force = force;
            }
        }

        private class AddedFolderFetchTask : SyncTask {
            // fetch a newly-added folder
            public readonly IMailFolder folder;

            public AddedFolderFetchTask(IMailFolder folder, bool priority = false) : base(priority) {
                this.folder = folder;
            }
        }

        private abstract class FolderSyncTask : SyncTask {
            // sync task which operates on a specific folder
            public readonly MailFolder folder;

            public FolderSyncTask(MailFolder folder, bool priority = false) : base(priority) {
                this.folder = folder;
            }
        }

        private class FolderPurgeTask : FolderSyncTask {
            // purge and resync folder
            public FolderPurgeTask(MailFolder folder, bool priority = false) : base(folder, priority) { }
        }

        private class FolderFetchTask : FolderSyncTask {
            // fetch the entire contents of a folder, starting at the specified offset
            public int offset;
            public HashSet<uint> unseen_ids;

            public FolderFetchTask(MailFolder folder, int offset = 0, bool priority = false) : base(folder, priority) {
                this.offset = offset;
                this.unseen_ids = null;
            }
        }

        private class MessageStatusFetchTask : FolderSyncTask {
            // fetch the status of specific messages
            public HashSet<uint> ids;

            public MessageStatusFetchTask(MailFolder folder, bool priority = false) : base(folder, priority) {
                this.ids = new HashSet<uint>();
            }
        }

        private class MessageContentsFetchTask : FolderSyncTask {
            // fetch the contents of a single message
            public readonly uint id;
            public ManualResetEvent completed;

            public MessageContentsFetchTask(MailFolder folder, uint id, bool priority = false) : base(folder, priority) {
                this.id = id;
                this.completed = new ManualResetEvent(false);
            }
        }

        //TODO: action tasks (push flags, push spam status, move message, expunge deleted, etc.)

        public MailManager(MainWindow window) {
            this.window = window;
            this.sync_log = Logger.WithProperty("source", "SYNC");
            this.event_log = Logger.WithProperty("source", "EVENT");
            this.user_log = Logger.WithProperty("source", "USER");
            this.shutdown_event = new ManualResetEvent(false);
            this.client = new ImapClient();
            this.folders_lock = new object();
            this.load_folders();  //TODO: maybe catch MailStoreError and try to repair/delete corrupted cache
            this.sync_tasks = new LinkedList<SyncTask>();
            this.last_force_sync = DateTimeOffset.MinValue;
            this.last_full_sync = DateTimeOffset.MinValue;
            this.token_lock = new object();
            this.shutdown_token = new CancellationTokenSource();
            this.shutdown_token.Token.ThrowIfCancellationRequested();
            this.idle_token = new CancellationTokenSource();
            this.idle_token.Token.ThrowIfCancellationRequested();
            ToastNotificationManagerCompat.OnActivated += this.activated;
            this.save_thread = new Thread(this.save_loop);
            this.save_thread.Start();
            this.sync_thread = new Thread(this.sync_loop);
            this.sync_thread.Start();
        }

        private void load_folders() {
            string messagesDir = this.messages_dir;
            string foldersPath = this.folders_path;
            BindingList<MailFolder> folders = new BindingList<MailFolder>();
            if (File.Exists(foldersPath)) {
                List<MailFolder> folderList;
                DataContractSerializer serializer = new DataContractSerializer(typeof(List<MailFolder>));
                try {
                    using (FileStream f = new FileStream(foldersPath, FileMode.Open)) {
                        XmlDictionaryReader xmlReader = XmlDictionaryReader.CreateTextReader(f, new XmlDictionaryReaderQuotas());
                        folderList = (List<MailFolder>)(serializer.ReadObject(xmlReader, true));
                    }
                }
                catch (Exception e) when ((e is IOException) || (e is System.Security.SecurityException) || (e is XmlException)) {
                    throw new MailLoadError($"Failed to load {foldersPath}: {e.Message}", e);
                }
                foreach (MailFolder folder in folderList) {
                    folder.load_messages(messagesDir);
                    folders.Add(folder);
                }
            }
            lock (this.folders_lock) {
                this.folders = folders;
                this.selected_folder = null;
                this.selected_message = -1;
            }
            BindingOperations.EnableCollectionSynchronization(this.folders, this.folders_lock);
        }

        private void save_folders() {
            string messagesDir = this.messages_dir;
            string foldersPath = this.folders_path;
            Directory.CreateDirectory(messagesDir);
            List<MailFolder> folderList;
            lock (this.folders_lock) {
                folderList = new List<MailFolder>(this.folders);
            }
            DataContractSerializer serializer = new DataContractSerializer(typeof(List<MailFolder>));
            using (FileStream f = new FileStream(foldersPath, FileMode.Create)) {
                serializer.WriteObject(f, folderList);
            }
        }

        public void shutdown() {
            this.user_log.Info("Shutdown requested; shutting down");
            this.running = false;
            lock (this.token_lock) {
                this.idle_token.Cancel();
                this.shutdown_token.Cancel();
            }
            this.sync_thread.Join();
            this.shutdown_event.Set();
            this.client.Disconnect(true);
            this.save_thread.Join();
        }

        public void save_loop() {
            while (this.running) {
                // save dirty folders which haven't been changed for at least SAVE_INTERVAL (to batch changes together into one save)
                List<MailFolder> folders = new List<MailFolder>();
                lock (this.folders_lock) {
                    foreach (MailFolder folder in this.folders) {
                        if (folder.dirty) {
                            folders.Add(folder);
                        }
                    }
                }
                TimeSpan sleepTime = SAVE_INTERVAL;
                folders.Sort((x, y) => x.last_changed.CompareTo(y.last_changed));
                DateTimeOffset curTime = DateTimeOffset.Now;
                foreach (MailFolder folder in folders) {
                    DateTimeOffset dueTime = folder.last_changed + SAVE_INTERVAL;
                    if (dueTime <= curTime) {
                        // folder is due to be saved; do so
                        folder.save_messages();
                    }
                    else {
                        // folder isn't due to be saved yet, so we'll sleep until it's due (to a maximum of SAVE_INTERVAL)
                        TimeSpan folderSleep = dueTime - curTime;
                        if (folderSleep < sleepTime) {
                            sleepTime = folderSleep;
                        }
                        break;
                    }
                }
                // use shutdown_event to sleep so we'll break early if we're shutting down
                this.shutdown_event.WaitOne(sleepTime);
            }
            // shutting down; make sure every dirty folder is saved
            lock (this.folders_lock) {
                foreach (MailFolder folder in this.folders) {
                    folder.save_messages();
                }
            }
        }

        private void sync_loop() {
            this.client.Disconnected += (obj, args) => this.connect_client();
            this.client.FolderCreated += (obj, args) => this.handle_folder_created_event(args);
            this.connect_client();
            while (this.running) {
                SyncTask task = null;
                DateTimeOffset curTime = DateTimeOffset.Now;
                lock (this.sync_tasks) {
                    if (this.sync_tasks.First?.Value?.priority != true) {
                        // if we don't have any high-priority tasks, see if it's time for a full sync
                        if (curTime >= this.last_force_sync + FORCE_SYNC_INTERVAL) {
                            task = new FullFetchTask(true);
                        }
                        else if (curTime >= this.last_full_sync + FULL_SYNC_INTERVAL) {
                            task = new FullFetchTask();
                        }
                    }
                    if ((task is null) && (this.sync_tasks.First?.Value is not null)) {
                        task = this.sync_tasks.First.Value;
                        this.sync_tasks.RemoveFirst();
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

        private void handle_sync_task(SyncTask task) {
            if (task is FullFetchTask fullFetchTask) {
                this.handle_full_fetch_task(fullFetchTask);
                if (fullFetchTask.force) {
                    this.last_force_sync = DateTimeOffset.Now;
                }
                this.last_full_sync = DateTimeOffset.Now;
                return;
            }
            if (task is AddedFolderFetchTask addedFolderFetchTask) {
                this.handle_added_folder_fetch_task(addedFolderFetchTask);
                return;
            }
            if (task is FolderPurgeTask folderPurgeTask) {
                this.handle_folder_purge_task(folderPurgeTask);
                return;
            }
            if (task is FolderFetchTask folderFetchTask) {
                this.handle_folder_fetch_task(folderFetchTask);
                return;
            }
            if (task is MessageStatusFetchTask messageStatusFetchTask) {
                this.handle_message_status_fetch_task(messageStatusFetchTask);
                return;
            }
            if (task is MessageContentsFetchTask messageContentsFetchTask) {
                this.handle_message_contents_fetch_task(messageContentsFetchTask);
                return;
            }
            //TODO: action tasks (push flags, push spam status, move message, expunge deleted, etc.)
        }

        private void handle_full_fetch_task(FullFetchTask task) {
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
            string messagesDir = this.messages_dir;
            lock (this.folders_lock) {
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
                        mzFolder = new MailFolder(messagesDir, imapFolder.FullName, weight);
                        this.folders.Insert(idx, mzFolder);
                        dirty = true;
                    }
                    this.set_imap_folder(mzFolder, imapFolder);
                    if (
                        (task.force) ||
                        (imapFolder.UidValidity != mzFolder.uid_validity) ||
                        (imapFolder.Count != mzFolder.count) ||
                        (imapFolder.UidNext.Value.Id != mzFolder.uid_next)
                    ) {
                        // folder needs sync
                        this.sync_log.Info("Scheduling sync of folder {name}", imapFolder.FullName);
                        if (imapFolder.UidValidity != mzFolder.uid_validity) {
                            // UID validity differs; invalidate whole folder cache
                            mzFolder.purge();
                        }
                        lock (this.sync_tasks) {
                            bool needTask = true;
                            foreach (SyncTask t in this.sync_tasks) {
                                if ((t is FolderFetchTask folderTask) && (folderTask.folder.name == mzFolder.name)) {
                                    // there's already a pending sync task for this folder; just reset its offset back to 0
                                    if (folderTask.offset > 0) {
                                        folderTask.offset = 0;
                                    }
                                    needTask = false;
                                    break;
                                }
                            }
                            if (needTask) {
                                // no pending sync task on this folder; add one
                                this.sync_tasks.AddLast(new FolderFetchTask(mzFolder));
                            }
                        }
                        mzFolder.uid_validity = imapFolder.UidValidity;
                        mzFolder.uid_next = imapFolder.UidNext.Value.Id;
                        dirty = true;
                    }
                }
                foreach (MailFolder folder in deleted) {
                    this.sync_log.Info("Deleting folder {name}", folder.name);
                    this.delete_folder(folder);
                    dirty = true;
                }
            }
            if (dirty) {
                this.save_folders();
                this.window.fix_folder_list_column_sizes();
            }
            this.sync_log.Info("Done syncing all folders");
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
            // this function assumes caller holds folders_lock
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

        private void handle_added_folder_fetch_task(AddedFolderFetchTask task) {
            this.sync_log.Info("Syncing new folder {name}", task.folder.FullName);
            //TODO: error handling
            try {
                task.folder.Status(StatusItems.Count | StatusItems.UidNext | StatusItems.UidValidity, this.shutdown_token.Token);
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
            int weight = get_weight(task.folder);
            lock (this.folders_lock) {
                int idx = this.get_insertion_index(task.folder, weight);
                if ((idx < this.folders.Count) && (this.folders[idx].name == task.folder.FullName)) {
                    // folder already tracked; no need to sync
                    return;
                }
                MailFolder mzFolder = new MailFolder(this.messages_dir, task.folder.FullName, weight);
                this.folders.Insert(idx, mzFolder);
                this.set_imap_folder(mzFolder, task.folder);
                this.sync_log.Info("Scheduling sync of folder {name}", task.folder.FullName);
                lock (this.sync_tasks) {
                    this.sync_tasks.AddLast(new FolderFetchTask(mzFolder));
                }
                mzFolder.uid_validity = task.folder.UidValidity;
                mzFolder.uid_next = task.folder.UidNext.Value.Id;
            }
            this.save_folders();
            this.window.fix_folder_list_column_sizes();
            this.sync_log.Info("Done syncing new folder {name}", task.folder.FullName);
        }

        private void handle_folder_purge_task(FolderPurgeTask task) {
            MailFolder mzFolder = task.folder;
            IMailFolder imapFolder = mzFolder.imap_folder;
            this.sync_log.Info("Purging and resyncing messages in {name}", mzFolder.name);
            //TODO: error handling
            try {
                imapFolder.Status(StatusItems.Count | StatusItems.UidNext | StatusItems.UidValidity, this.shutdown_token.Token);
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
            lock (this.folders_lock) {
                mzFolder.uid_validity = imapFolder.UidValidity;
                mzFolder.uid_next = imapFolder.UidNext.Value.Id;
                mzFolder.purge();
            }
            lock (this.sync_tasks) {
                this.sync_tasks.AddFirst(new FolderFetchTask(mzFolder));
            }
        }

        private void handle_folder_fetch_task(FolderFetchTask task) {
            MailFolder mzFolder = task.folder;
            IMailFolder imapFolder = mzFolder.imap_folder;
            this.sync_log.Info("Syncing messages in {name}: {offset} / {count}", mzFolder.name, task.offset, imapFolder.Count);
            if (task.unseen_ids is null) {
                task.unseen_ids = new HashSet<uint>(mzFolder.message_ids);
            }
            int startIdx = task.offset;
            if (startIdx >= imapFolder.Count) {
                this.finalize_folder_fetch_task(task);
                this.sync_log.Info("No more new messages to sync");
                return;
            }
            int endIdx = startIdx + SYNC_BATCH_SIZE;
            if (endIdx >= imapFolder.Count) {
                endIdx = -1;
            }
            FetchRequest req = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Envelope);
            IList<IMessageSummary> summaries;
            //TODO: error handling
            imapFolder.Open(FolderAccess.ReadOnly);
            try {
                summaries = imapFolder.Fetch(startIdx, endIdx, req, this.shutdown_token.Token);
            }
            catch (OperationCanceledException) {
                this.sync_log.Info("Terminating sync due to shutdown");
                return;
            }
            finally {
                if (this.running) {
                    imapFolder.Close();
                }
            }
            lock (this.token_lock) {
                this.shutdown_token.Dispose();
                this.shutdown_token = new CancellationTokenSource();
                this.shutdown_token.Token.ThrowIfCancellationRequested();
            }
            DateTimeOffset minNotify = DateTimeOffset.Now - NOTIFY_MAX_AGE;
            List<MailMessage> notifyMessages = new List<MailMessage>();
            foreach (IMessageSummary sum in summaries) {
                task.unseen_ids.Remove(sum.UniqueId.Id);
                MailMessage msg = mzFolder.add_message(sum);
                if (msg is not null) {
                    if (msg.id == mzFolder.uid_next) {
                        // predict next uid after this one was assigned; this should avoid unnecessary syncs
                        mzFolder.uid_next += 1;
                    }
                    if ((msg.unread) && (msg.timestamp >= minNotify) && (imapFolder.Attributes.HasFlag(FolderAttributes.Inbox))) {
                        notifyMessages.Add(msg);
                    }
                }
            }
            this.window.fix_folder_list_column_sizes();
            foreach (MailMessage msg in notifyMessages) {
                this.notify_message(mzFolder, msg);
            }
            if ((endIdx < 0) || (summaries.Count <= 0)) {
                this.sync_log.Info("Synced all new messages in {name}", mzFolder.name);
                this.finalize_folder_fetch_task(task);
                return;
            }
            task.offset += summaries.Count;
            lock (this.sync_tasks) {
                //TODO: put this after priority tasks
                this.sync_tasks.AddFirst(task);
            }
            this.sync_log.Info("Synced {count} new messages in {name}", summaries.Count, mzFolder.name);
        }

        private void finalize_folder_fetch_task(FolderFetchTask task) {
            // remove messages which weren't seen during the sync
            foreach (uint uid in task.unseen_ids) {
                task.folder.remove_message(uid);
            }
        }

        private void handle_message_status_fetch_task(MessageStatusFetchTask task) {
            MailFolder mzFolder = task.folder;
            IMailFolder imapFolder = mzFolder.imap_folder;
            string idStr = format_ids(task.ids);
            this.sync_log.Info("Syncing message{s:l} {uids} in {name}", (task.ids.Count > 0 ? "s" : ""), idStr, mzFolder.name);
            List<MailKit.UniqueId> uids = new List<MailKit.UniqueId>(task.ids.Count);
            foreach (uint uid in task.ids) {
                uids.Add(new MailKit.UniqueId(uid));
            }
            FetchRequest req = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags);
            IList<IMessageSummary> summaries;
            //TODO: error handling
            imapFolder.Open(FolderAccess.ReadOnly);
            try {
                summaries = imapFolder.Fetch(uids, req, this.shutdown_token.Token);
            }
            catch (OperationCanceledException) {
                this.sync_log.Info("Terminating sync due to shutdown");
                return;
            }
            finally {
                if (this.running) {
                    imapFolder.Close();
                }
            }
            lock (this.token_lock) {
                this.shutdown_token.Dispose();
                this.shutdown_token = new CancellationTokenSource();
                this.shutdown_token.Token.ThrowIfCancellationRequested();
            }
            HashSet<uint> unseen = new HashSet<uint>(task.ids);
            DateTimeOffset minNotify = DateTimeOffset.Now - NOTIFY_MAX_AGE;
            List<MailMessage> notifyMessages = new List<MailMessage>();
            foreach (IMessageSummary sum in summaries) {
                uint uid = sum.UniqueId.Id;
                unseen.Remove(uid);
                MailMessage msg = mzFolder.add_message(sum);
                if (msg is not null) {
                    if (msg.id == mzFolder.uid_next) {
                        // predict next uid after this one was assigned; this should avoid unnecessary syncs
                        mzFolder.uid_next += 1;
                    }
                    if ((msg.unread) && (msg.timestamp >= minNotify) && (imapFolder.Attributes.HasFlag(FolderAttributes.Inbox))) {
                        notifyMessages.Add(msg);
                    }
                }
            }
            foreach (uint uid in unseen) {
                mzFolder.remove_message(uid);
            }
            this.window.fix_folder_list_column_sizes();
            foreach (MailMessage msg in notifyMessages) {
                this.notify_message(mzFolder, msg);
            }
            this.sync_log.Info("Synced message{s:l} {uids} in {name}", (task.ids.Count > 0 ? "s" : ""), idStr, mzFolder.name);
        }

        private static string format_ids(ICollection<uint> ids) {
            if (ids.Count <= 0) {
                return "";
            }
            List<uint> idList = new List<uint>(ids);
            idList.Sort();
            return string.Join(", ", idList);
        }

        private void handle_message_contents_fetch_task(MessageContentsFetchTask task) {
            MailFolder mzFolder = task.folder;
            IMailFolder imapFolder = mzFolder.imap_folder;
            this.sync_log.Info("Downloading message {uid} body in {name}", task.id, mzFolder.name);
            MimeMessage imapMsg;
            //TODO: error handling
            imapFolder.Open(FolderAccess.ReadOnly);
            try {
                imapMsg = imapFolder.GetMessage(new MailKit.UniqueId(task.id), this.shutdown_token.Token);
            }
            catch (OperationCanceledException) {
                this.sync_log.Info("Terminating download due to shutdown");
                return;
            }
            finally {
                if (this.running) {
                    imapFolder.Close();
                }
            }
            lock (this.token_lock) {
                this.shutdown_token.Dispose();
                this.shutdown_token = new CancellationTokenSource();
                this.shutdown_token.Token.ThrowIfCancellationRequested();
            }
            mzFolder.load_message(task.id, imapMsg);
            task.completed.Set();
            this.sync_log.Info("Downloaded message {uid} body in {name}", task.id, mzFolder.name);
        }

        //TODO: handlers for action tasks (push flags, push spam status, move message, expunge deleted, etc.)

        private void delete_folder(MailFolder folder) {
            // delete any pending sync tasks on this folder
            // NOTE: this assumes caller holds folders_lock
            lock (this.sync_tasks) {
                LinkedListNode<SyncTask> taskNode = this.sync_tasks.First;
                while (taskNode is not null) {
                    if ((taskNode.Value is FolderSyncTask folderTask) && (folderTask.folder == folder)) {
                        this.sync_tasks.Remove(taskNode);
                    }
                    taskNode = taskNode.Next;
                }
            }
            folder.remove();
            if (this.selected_folder == folder) {
                this.select_folder(null);
            }
            this.folders.Remove(folder);
        }

        private void notify_message(MailFolder folder, MailMessage msg) {
            ToastArguments args = new ToastArguments().Add(NOTIFY_ARG_FOLDER, folder.name).Add(NOTIFY_ARG_MESSAGE, (int)(msg.id));
            ToastContentBuilder toast = new ToastContentBuilder().AddText(msg.from).AddText(msg.subject);
            // add args to toast before adding delete arg
            foreach (KeyValuePair<string, string> arg in args) {
                toast = toast.AddArgument(arg.Key, arg.Value);
            }
            // add delete arg for button
            args = args.Add(NOTIFY_ARG_ACTION, NOTIFY_ACTION_DELETE);
            toast.AddCustomTimeStamp(msg.timestamp.LocalDateTime).AddButton("Delete", ToastActivationType.Foreground, args.ToString()).AddAudio(
                new ToastAudio() { Silent = true }
            ).Show();
        }

        private void set_imap_folder(MailFolder mzFolder, IMailFolder imapFolder) {
            if (imapFolder == mzFolder.imap_folder) {
                return;
            }
            //TODO: do we need to unregister previous event handlers?
            mzFolder.imap_folder = imapFolder;
            imapFolder.Deleted += (obj, args) => this.handle_folder_deleted_event(mzFolder);
            imapFolder.Renamed += (obj, args) => this.handle_folder_renamed_event(mzFolder, args);
            imapFolder.UidValidityChanged += (obj, args) => this.handle_uid_validity_changed_event(mzFolder);
            imapFolder.CountChanged += (obj, args) => this.handle_count_changed_event(mzFolder);
            imapFolder.MessageExpunged += (obj, args) => this.handle_message_expunged_event(mzFolder, args);
            imapFolder.MessageFlagsChanged += (obj, args) => this.handle_message_flags_changed_event(mzFolder, args);
        }

        private void handle_folder_created_event(FolderCreatedEventArgs args) {
            this.event_log.Info("Folder created: {name}", args.Folder.FullName);
            lock (this.sync_tasks) {
                this.sync_tasks.AddLast(new AddedFolderFetchTask(args.Folder));
            }
        }

        private void handle_folder_deleted_event(MailFolder folder) {
            this.event_log.Info("Folder deleted: {name}", folder.name);
            lock (this.folders_lock) {
                this.delete_folder(folder);
            }
            this.save_folders();
            this.window.fix_folder_list_column_sizes();
            //TODO: remove outstanding notifications on folder
        }

        private void handle_folder_renamed_event(MailFolder folder, FolderRenamedEventArgs args) {
            this.event_log.Info("Folder renamed from {oldName} to {newName}", args.OldName, args.NewName);
            lock (this.folders_lock) {
                folder.name = args.NewName;
            }
            this.save_folders();
            this.window.fix_folder_list_column_sizes();
            //TODO: modify/invalidate outstanding notifications on folder
        }

        private void handle_uid_validity_changed_event(MailFolder folder) {
            this.event_log.Info("Folder UID validity changed: {name}", folder.name);
            lock (this.sync_tasks) {
                // remove any outstanding sync tasks on folder
                LinkedListNode<SyncTask> taskNode = this.sync_tasks.First;
                while (taskNode is not null) {
                    if ((taskNode.Value is FolderSyncTask folderTask) && (folderTask.folder == folder)) {
                        this.sync_tasks.Remove(taskNode);
                    }
                    taskNode = taskNode.Next;
                }
                // add purge task
                this.sync_tasks.AddLast(new FolderPurgeTask(folder));
            }
        }

        private void handle_count_changed_event(MailFolder folder) {
            // NOTE: deleted messages are handled on the MessageExpunged event; we only need to look for new messages here
            this.event_log.Info("Folder message count changed: {name}", folder.name);
            lock (this.sync_tasks) {
                foreach (SyncTask t in this.sync_tasks) {
                    if ((t is FolderFetchTask folderTask) && (folderTask.folder.name == folder.name)) {
                        // there's already a fetch in progress on this folder; it'll pick up any new messages
                        return;
                    }
                }
                // no fetch task in progress; add a new one for any messages past those we already have
                this.sync_tasks.AddLast(new FolderFetchTask(folder, folder.count) { unseen_ids = new HashSet<uint>() });
            }
        }

        private void handle_message_expunged_event(MailFolder folder, MessageEventArgs args) {
            // we can't guarantee our indices are in sync with args.Index, so rescan a neighborhood around our best guess
            this.event_log.Info("Folder {name} message {idx} deleted", folder.name, args.Index);
            this.rescan_message_index(folder, args.Index);
        }

        private void rescan_message_index(MailFolder folder, int index) {
            // schedule fetch of messages in a neighborhood around the specified index
            List<uint> ids = new List<uint>(folder.message_ids);
            ids.Sort();
            int minIdx = index - UID_LOOKUP_RANGE;
            if (minIdx < 0) {
                minIdx = 0;
            }
            int maxIdx = index + UID_LOOKUP_RANGE;
            if (maxIdx > ids.Count) {
                maxIdx = ids.Count;
            }
            lock (this.sync_tasks) {
                bool newTask = false;
                MessageStatusFetchTask task = null;
                // see if there's an existing task to extend
                foreach (SyncTask t in this.sync_tasks) {
                    if ((t is MessageStatusFetchTask fetchTask) && (fetchTask.folder.name == folder.name)) {
                        task = fetchTask;
                        break;
                    }
                }
                if (task is null) {
                    // no existing task; create a new one
                    task = new MessageStatusFetchTask(folder);
                    newTask = true;
                }
                for (int i = minIdx; i < maxIdx; i++) {
                    task.ids.Add(ids[i]);
                }
                if (newTask) {
                    this.sync_tasks.AddLast(task);
                }
            }
        }

        private void handle_message_flags_changed_event(MailFolder folder, MessageFlagsChangedEventArgs args) {
            // we can't guarantee our indices are in sync with args.Index, so rescan a neighborhood around our best guess
            this.event_log.Info("Folder {name} message {idx} flags changed", folder.name, args.Index);
            this.rescan_message_index(folder, args.Index);
        }

        public void select_folder(MailFolder sel) {
            if (sel == this.selected_folder) {
                return;
            }
            this.selected_folder = sel;
            this.window.message_list.ItemsSource = sel?.message_display;
        }

        async public void select_message(int idx) {
            if (idx == this.selected_message) {
                return;
            }
            MailFolder oldSelFolder = this.selected_folder;
            int oldSelMsg = this.selected_message;
            MailMessage sel = null;
            if ((this.selected_folder is not null) && (idx >= 0) && (idx < this.selected_folder.message_display.Count)) {
                sel = this.selected_folder.message_display[idx];
            }
            if ((sel is not null) && (!sel.loaded) && (this.client.IsConnected)) {
                this.user_log.Info("Downloading message {uid} in {folder}", sel.id, this.selected_folder.name);
                MessageContentsFetchTask downloadTask = new MessageContentsFetchTask(this.selected_folder, sel.id, true);
                lock (this.sync_tasks) {
                    this.sync_tasks.AddFirst(downloadTask);
                }
                lock (this.token_lock) {
                    this.idle_token.Cancel();
                }
                // do this async so we don't block events in main thread; otherwise sync thread and main thread may block on each other
                await System.Threading.Tasks.Task.Run(() => downloadTask.completed.WaitOne());
                lock (this.token_lock) {
                    this.idle_token.Dispose();
                    this.idle_token = new CancellationTokenSource();
                    this.idle_token.Token.ThrowIfCancellationRequested();
                }
            }
            // make sure selection hasn't changed before we show the downloaded message
            if ((this.selected_folder == oldSelFolder) && (this.selected_message == oldSelMsg)) {
                this.window.message_ctrl.show_message(sel, false);
            }
        }

        public void select_message(string folder, uint id) {
            MailFolder selFolder = null;
            int msgIdx = -1;
            lock (this.folders_lock) {
                foreach (MailFolder fld in this.folders) {
                    if (fld.name == folder) {
                        selFolder = fld;
                        break;
                    }
                }
                if (selFolder is null) {
                    return;
                }
                for (int i = 0; i < selFolder.message_display.Count; i++) {
                    if (selFolder.message_display[i].id == id) {
                        msgIdx = i;
                        break;
                    }
                }
            }
            if (msgIdx < 0) {
                return;
            }
            this.window.folder_list.SelectedItem = selFolder;
            this.window.message_list.SelectedIndex = msgIdx;
            this.window.Show();
            this.window.Activate();
        }

        public void activated(ToastNotificationActivatedEventArgsCompat e) {
            ToastArguments args = ToastArguments.Parse(e.Argument);
            if ((!args.Contains(NOTIFY_ARG_FOLDER)) || (!args.Contains(NOTIFY_ARG_MESSAGE))) {
                return;
            }
            string folder = args[NOTIFY_ARG_FOLDER];
            uint msgId = (uint)(args.GetInt(NOTIFY_ARG_MESSAGE));
            if ((args.Contains(NOTIFY_ARG_ACTION)) && (args[NOTIFY_ARG_ACTION] == NOTIFY_ACTION_DELETE)) {
                //TODO: delete message
                return;
            }
            this.window.Dispatcher.Invoke(() => this.select_message(folder, msgId));
        }

        //TODO: other handlers
    }
}
