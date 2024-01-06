using System;
using System.Collections.ObjectModel;

namespace Mailzeug {
    public class MailManager {
        private readonly MainWindow window;
        public object cache_lock;
        public ObservableCollection<MailFolder> folders;
        public MailFolder selected_folder = null;
        public int selected_message = -1;
        //TODO: imap client, cached folders, cached messages
        //TODO: load, save, sync thread

        private Config config => this.window.config;
        private string data_dir => this.config.data_dir;

        public MailManager(MainWindow window) {
            this.window = window;
            this.cache_lock = new object();
            this.load_cache();
            //TODO: display cached folders and messages
        }

        private void load_cache() {
            ObservableCollection<MailFolder> folders = new ObservableCollection<MailFolder>();
            //TODO: load cached folders and messages
            lock (this.cache_lock) {
                this.folders = folders;
                this.selected_folder = null;
                this.selected_message = -1;
            }
        }

        //TODO: save_cache, ...

        public void select_folder(MailFolder sel) {
            if (sel == this.selected_folder) {
                return;
            }
            this.selected_folder = sel;
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
            this.window.message_ctrl.show_message(sel, false);
        }

        //TODO: other handlers
    }
}
