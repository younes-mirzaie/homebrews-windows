using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using RetroFiles.Core;
using Windows.Storage;

namespace RetroFiles
{
    public partial class MainPage : PhoneApplicationPage
    {
        enum SortBy { Name, Size, Date }

        static readonly string[][] BuiltInPlaces =
        {
            new[] { @"C:\Data\Users\Public", "your files (Documents, Pictures, Music, Videos, Downloads)" },
            new[] { @"C:\Data", "user data partition" },
            new[] { @"C:\Data\Users\DefApps\AppData", "app data" },
            new[] { @"C:\Data\SharedData", "shared data (RetroFiles helper lives here)" },
            new[] { @"C:\Data\Programs", "installed apps" },
            new[] { @"C:\Windows\System32", "system (careful)" },
            new[] { @"C:\EFIESP", "boot partition (careful)" },
        };

        string _path;                 // null = places
        bool _selectMode;
        SortBy _sort = SortBy.Name;
        bool _showHidden = true;
        bool _helperReady;
        CancellationTokenSource _longOp;
        List<FsItem> _items = new List<FsItem>();
        int _loadId;
        const int WebPort = 8095;
        Tuple<bool, int, string> _web;   // web access state: on, port, phone IP

        RfxClient Client { get { return AppState.Client; } }

        public MainPage()
        {
            InitializeComponent();
            var s = IsolatedStorageSettings.ApplicationSettings;
            object v;
            if (s.TryGetValue("sort", out v)) _sort = (SortBy)(int)v;
            if (s.TryGetValue("hidden", out v)) _showHidden = (bool)v;
            BuildAppBar();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Metro.TurnstileIn(LayoutRoot, e.NavigationMode == NavigationMode.Back);
            if (e.NavigationMode == NavigationMode.New)
            {
                await StartHelperAsync();
                if (State.ContainsKey("path")) await LoadAsync(State["path"] as string);
                else await LoadAsync(null);
            }
            else if (AppState.FolderChanged || !_helperReady)
            {
                AppState.FolderChanged = false;
                if (!_helperReady) await StartHelperAsync();
                await LoadAsync(_path, keepScroll: true);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            State["path"] = _path;
        }

        // ---------- helper ----------

        async Task StartHelperAsync()
        {
            ShowStatus("connecting to the helper...", busy: true);
            string problem = await HelperLauncher.EnsureRunningAsync(Client, msg => ShowStatus(msg, busy: true));
            _helperReady = problem == null;
            if (_helperReady)
            {
                HideStatus();
                try { _web = await Client.WebAsync(null); } catch { _web = null; }
                ShowWebStatus();
            }
            else
            {
                ShowStatus(problem, busy: false);
                RetryButton.Visibility = Visibility.Visible;
            }
        }

        async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            RetryButton.Visibility = Visibility.Collapsed;
            await StartHelperAsync();
            if (_helperReady) await LoadAsync(_path);
        }

        // ---------- status ----------

        void ShowStatus(string text, bool busy, bool determinate = false)
        {
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = text;
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Busy.IsIndeterminate = busy && !determinate;
        }

        void HideStatus()
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
        }

        void ShowError(Exception e)
        {
            var re = e as RfxException;
            if (re != null && re.Cancelled) { ShowStatus("canceled", false); return; }
            ShowStatus(e.Message, false);
            if (re != null && re.Code == 0)
            {
                _helperReady = false;
                RetryButton.Visibility = Visibility.Visible;
            }
        }

        // ---------- loading ----------

        async Task LoadAsync(string path, bool keepScroll = false)
        {
            int id = ++_loadId;
            ExitSelectMode();
            var oldPath = _path;
            try
            {
                List<FsItem> items;
                if (path == null) items = await LoadPlacesAsync();
                else
                {
                    if (!_helperReady) throw new RfxException(0, "the helper isn't running");
                    items = await Client.ListAsync(path);
                }
                if (id != _loadId) return;
                _path = path;
                _items = items;
                ShowItems(keepScroll && oldPath == path);
                // moving between folders turnstiles the list: back when going up, forward otherwise
                if (!string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase))
                    Metro.TurnstileIn(Items, path == null || (oldPath != null && oldPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)));
                if (StatusPanel.Visibility == Visibility.Visible && RetryButton.Visibility != Visibility.Visible && StatusText.Text != _webLine) HideStatus();
            }
            catch (Exception e)
            {
                if (id != _loadId) return;
                ShowError(e);
            }
            ShowTitle();
            BuildAppBar();
        }

        void ShowTitle()
        {
            PageTitle.Text = _path == null ? "places" : (_path.TrimEnd('\\').Length <= 2 ? _path.TrimEnd('\\') : FsItem.NameOf(_path));
            PathText.Text = _path ?? "";
            if (PathBox.Visibility != Visibility.Visible) PathText.Visibility = _path == null ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task<List<FsItem>> LoadPlacesAsync()
        {
            var list = new List<FsItem>();
            if (_helperReady)
            {
                try
                {
                    foreach (var d in await Client.DrivesAsync())
                    {
                        string name = d.Root.Length <= 3 ? d.Root : FsItem.NameOf(d.Root);
                        string label = string.IsNullOrEmpty(d.Label) ? (d.Root.StartsWith("D") ? "SD card" : "") : d.Label;
                        list.Add(new FsItem
                        {
                            Name = (label.Length > 0 ? label + " (" + d.Root.TrimEnd('\\') + ")" : d.Root.TrimEnd('\\')),
                            FullPath = d.Root, IsPlace = true, Attributes = FileAttr.Directory,
                            Detail = FsItem.FormatSize(d.Free) + " free of " + FsItem.FormatSize(d.Total) + "  " + d.FileSystem
                        });
                    }
                }
                catch (Exception e) { ShowError(e); }
            }
            foreach (var p in BuiltInPlaces)
                list.Add(new FsItem { Name = FsItem.NameOf(p[0]), FullPath = p[0], Detail = p[0] + "  " + p[1], IsPlace = true, Attributes = FileAttr.Directory });
            foreach (var b in Bookmarks)
                list.Add(new FsItem { Name = "\u2605 " + FsItem.NameOf(b), FullPath = b, Detail = b, IsPlace = true, Attributes = FileAttr.Directory });
            return list;
        }

        void ShowItems(bool keepScroll)
        {
            IEnumerable<FsItem> items = _items;
            if (_path != null)
            {
                if (!_showHidden) items = items.Where(i => (i.Attributes & (FileAttr.Hidden | FileAttr.System)) == 0);
                switch (_sort)
                {
                    case SortBy.Size: items = items.OrderByDescending(i => i.IsFolder).ThenByDescending(i => i.Size).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase); break;
                    case SortBy.Date: items = items.OrderByDescending(i => i.IsFolder).ThenByDescending(i => i.Modified); break;
                    default: items = items.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase); break;
                }
            }
            var list = items.ToList();
            Items.ItemsSource = list;
            if (!keepScroll && list.Count > 0) Items.ScrollIntoView(list[0]);
            if (_path != null && list.Count == 0) ShowStatus(_items.Count == 0 ? "empty folder" : "only hidden items here", false);
        }

        // ---------- navigation ----------

        async void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectMode) { BuildAppBar(); return; }
            var item = Items.SelectedItem as FsItem;
            if (item == null) return;
            Items.SelectedIndex = -1;
            await OpenAsync(item);
        }

        /// <summary>Types the built-in viewer handles well; everything else goes to the phone's own app for it.</summary>
        static readonly string[] ViewerTypes =
        {
            ".txt", ".log", ".ini", ".inf", ".cfg", ".conf", ".xml", ".json", ".csv", ".md", ".bat", ".cmd", ".reg",
            ".ps1", ".js", ".css", ".c", ".h", ".cpp", ".cs", ".py", ".sh", ".xaml", ".manifest", ".policy",
            ".jpg", ".jpeg", ".png", ".gif", ".bmp"
        };

        async Task OpenAsync(FsItem item)
        {
            if (item.IsFolder || item.IsPlace) { await LoadAsync(item.FullPath); return; }
            string ext = System.IO.Path.GetExtension(item.Name).ToLowerInvariant();
            if (ext.Length > 0 && !ViewerTypes.Contains(ext)) { await OpenWithAppAsync(item, fallbackToViewer: true); return; }
            ShowViewer(item.FullPath);
        }

        void ShowViewer(string path)
        {
            AppState.ViewerPath = path;
            NavigationService.Navigate(new Uri("/ViewerPage.xaml", UriKind.Relative));
        }

        protected override async void OnBackKeyPress(CancelEventArgs e)
        {
            base.OnBackKeyPress(e);
            if (e.Cancel) return;   // a prompt took it
            if (_longOp != null) { e.Cancel = true; return; }
            if (PathBox.Visibility == Visibility.Visible) { e.Cancel = true; HidePathBox(); return; }
            if (_selectMode) { e.Cancel = true; ExitSelectMode(); BuildAppBar(); return; }
            if (_path != null)
            {
                e.Cancel = true;
                string up = FsItem.ParentOf(_path);
                await LoadAsync(up);
            }
        }

        void PathText_Tap(object sender, GestureEventArgs e)
        {
            PathBox.Text = _path ?? @"C:\";
            PathBox.Visibility = Visibility.Visible;
            PathText.Visibility = Visibility.Collapsed;
            PageTitle.Visibility = Visibility.Collapsed;
            PathBox.Focus();
            PathBox.SelectAll();
        }

        void HidePathBox()
        {
            PathBox.Visibility = Visibility.Collapsed;
            PageTitle.Visibility = Visibility.Visible;
            ShowTitle();
        }

        async void PathBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            string p = PathBox.Text.Trim().Trim('"');
            HidePathBox();
            Focus();
            if (p.Length == 2 && p[1] == ':') p += "\\";
            if (p.Length > 0) await LoadAsync(p);
        }

        void PathBox_LostFocus(object sender, RoutedEventArgs e) { HidePathBox(); }

        // ---------- selection ----------

        void Item_Hold(object sender, GestureEventArgs e)
        {
            var item = (sender as FrameworkElement).DataContext as FsItem;
            if (item == null || _path == null || _selectMode) return;
            EnterSelectMode();
            Items.SelectedItems.Add(item);
            BuildAppBar();
        }

        void EnterSelectMode()
        {
            _selectMode = true;
            Items.SelectionMode = SelectionMode.Multiple;
            BuildAppBar();
        }

        void ExitSelectMode()
        {
            if (!_selectMode) return;
            _selectMode = false;
            Items.SelectedItems.Clear();
            Items.SelectionMode = SelectionMode.Single;
        }

        List<FsItem> Selected { get { return Items.SelectedItems.Cast<FsItem>().ToList(); } }

        // ---------- app bar ----------

        static ApplicationBarIconButton Btn(string icon, string text, EventHandler click)
        {
            var b = new ApplicationBarIconButton(new Uri("/Assets/AppBar/" + icon + ".png", UriKind.Relative)) { Text = text };
            b.Click += click;
            return b;
        }

        static ApplicationBarMenuItem Menu(string text, EventHandler click)
        {
            var m = new ApplicationBarMenuItem(text);
            m.Click += click;
            return m;
        }

        void BuildAppBar()
        {
            var bar = new ApplicationBar { Mode = ApplicationBarMode.Default };
            if (_longOp != null) { bar.IsVisible = false; ApplicationBar = bar; return; }

            if (_selectMode)
            {
                int n = Items.SelectedItems.Count;
                bar.Buttons.Add(Btn("copy", "copy", (s, e) => ToClipboard(false)));
                bar.Buttons.Add(Btn("cut", "cut", (s, e) => ToClipboard(true)));
                bar.Buttons.Add(Btn("delete", "delete", Delete_Click));
                bar.Buttons.Add(Btn("rename", "rename", Rename_Click));
                foreach (ApplicationBarIconButton b in bar.Buttons) b.IsEnabled = n > 0;
                ((ApplicationBarIconButton)bar.Buttons[3]).IsEnabled = n == 1;
                bar.MenuItems.Add(Menu("properties", Properties_Click));
                bar.MenuItems.Add(Menu("open with...", OpenWith_Click));
                bar.MenuItems.Add(Menu("view as text/hex", (s, e) => { var one = Selected.FirstOrDefault(); if (one != null && !one.IsFolder) { ExitSelectMode(); ShowViewer(one.FullPath); } }));
                bar.MenuItems.Add(Menu("select all", (s, e) => { Items.SelectAll(); BuildAppBar(); }));
                bar.MenuItems.Add(Menu("folder size", FolderSize_Click));
            }
            else if (_path == null)
            {
                bar.Buttons.Add(Btn("refresh", "refresh", async (s, e) => await LoadAsync(null)));
                bar.Buttons.Add(WebButton());
                bar.MenuItems.Add(Menu("go to path...", (s, e) => PathText_Tap(null, null)));
                bar.MenuItems.Add(Menu("new web password", NewWebPassword_Click));
                bar.MenuItems.Add(Menu(_showHidden ? "hide hidden items" : "show hidden items", Hidden_Click));
                bar.MenuItems.Add(Menu("restart helper", RestartHelper_Click));
                bar.MenuItems.Add(Menu("about", About_Click));
            }
            else
            {
                if (AppState.Clipboard.Count > 0)
                    bar.Buttons.Add(Btn("paste", "paste (" + AppState.Clipboard.Count + ")", Paste_Click));
                bar.Buttons.Add(Btn("newfolder", "new folder", NewFolder_Click));
                bar.Buttons.Add(Btn("select", "select", (s, e) => EnterSelectMode()));
                bar.Buttons.Add(WebButton());
                // at most 5 menu items, 20 characters or fewer (Metro app bar rules)
                bar.MenuItems.Add(Menu("refresh", async (s, e) => await LoadAsync(_path, keepScroll: true)));
                bar.MenuItems.Add(Menu("new text file", NewFile_Click));
                bar.MenuItems.Add(Menu(Bookmarks.Contains(_path) ? "remove favorite" : "add to favorites", Bookmark_Click));
                bar.MenuItems.Add(Menu("sort by " + (_sort == SortBy.Name ? "size" : _sort == SortBy.Size ? "date" : "name"), Sort_Click));
                bar.MenuItems.Add(Menu("folder properties", (s, e) => { AppState.PropertiesPath = _path; NavigationService.Navigate(new Uri("/PropertiesPage.xaml", UriKind.Relative)); }));
            }
            ApplicationBar = bar;
        }

        // ---------- web access over Wi-Fi ----------

        string _webLine;   // the status line for web access, kept while browsing

        bool WebOn { get { return _web != null && _web.Item1; } }

        ApplicationBarIconButton WebButton()
        {
            var b = Btn(WebOn ? "webon" : "web", WebOn ? "web: on" : "web: off", WebToggle_Click);
            b.IsEnabled = _helperReady;
            return b;
        }

        static string WebPassword
        {
            get
            {
                var s = IsolatedStorageSettings.ApplicationSettings;
                string p;
                if (!s.TryGetValue("webpass", out p) || string.IsNullOrEmpty(p))
                {
                    const string chars = "abcdefghjkmnpqrstuvwxyz23456789";   // no look-alikes
                    var bytes = new byte[8];
                    new System.Security.Cryptography.RNGCryptoServiceProvider().GetBytes(bytes);
                    p = new string(bytes.Select(x => chars[x % chars.Length]).ToArray());
                    s["webpass"] = p;
                    s.Save();
                }
                return p;
            }
        }

        void ShowWebStatus()
        {
            if (WebOn)
            {
                _webLine = "web access on: http://" + (_web.Item3.Length > 0 ? _web.Item3 : "<phone IP>") + ":" + _web.Item2 +
                           "\npassword: " + WebPassword + " (any user name)";
                ShowStatus(_webLine, false);
            }
            BuildAppBar();
        }

        async void WebToggle_Click(object sender, EventArgs e)
        {
            try
            {
                bool turnOn = !WebOn;
                _web = turnOn ? await Client.WebAsync(true, WebPort, WebPassword) : await Client.WebAsync(false);
                if (WebOn) ShowWebStatus();
                else { ShowStatus("Web access is off.", false); BuildAppBar(); }
            }
            catch (Exception ex) { ShowError(ex); }
        }

        async void NewWebPassword_Click(object sender, EventArgs e)
        {
            IsolatedStorageSettings.ApplicationSettings.Remove("webpass");
            var p = WebPassword;
            if (WebOn)
            {
                try { _web = await Client.WebAsync(true, WebPort, p); } catch (Exception ex) { ShowError(ex); return; }
                ShowWebStatus();
            }
            else ShowStatus("New web password: " + p, false);
        }

        // ---------- bookmarks / options ----------

        static List<string> Bookmarks
        {
            get
            {
                List<string> b;
                return IsolatedStorageSettings.ApplicationSettings.TryGetValue("bookmarks", out b) ? b : new List<string>();
            }
        }

        static void SaveBookmarks(List<string> b)
        {
            IsolatedStorageSettings.ApplicationSettings["bookmarks"] = b;
            IsolatedStorageSettings.ApplicationSettings.Save();
        }

        void Bookmark_Click(object sender, EventArgs e)
        {
            var b = Bookmarks;
            if (b.Contains(_path)) b.Remove(_path); else b.Add(_path);
            SaveBookmarks(b);
            BuildAppBar();
        }

        void Sort_Click(object sender, EventArgs e)
        {
            _sort = (SortBy)(((int)_sort + 1) % 3);
            IsolatedStorageSettings.ApplicationSettings["sort"] = (int)_sort;
            ShowItems(false);
            BuildAppBar();
        }

        void Hidden_Click(object sender, EventArgs e)
        {
            _showHidden = !_showHidden;
            IsolatedStorageSettings.ApplicationSettings["hidden"] = _showHidden;
            ShowItems(false);
            BuildAppBar();
        }

        async void RestartHelper_Click(object sender, EventArgs e)
        {
            try { await Client.QuitServerAsync(); } catch { }
            Client.Close();
            await Task.Delay(500);
            await StartHelperAsync();
            await LoadAsync(_path);
        }

        void About_Click(object sender, EventArgs e)
        {
            MessageBox.Show("File Explorer browses the whole phone through rfxfsd, a small helper started as SYSTEM via CMD Injector. " +
                            "It listens on 127.0.0.1:" + RfxClient.Port + " only and needs this app's key.\n\n" +
                            "Helper: " + (_helperReady ? "running, version " + Client.ServerVersion : "not running") +
                            "\nHelper folder: " + HelperLauncher.HelperDir +
                            "\n\nTips: tap the folder name to type a path; press and hold an item to select.", "About File Explorer", MessageBoxButton.OK);
        }

        // ---------- file operations ----------

        void ToClipboard(bool cut)
        {
            AppState.Clipboard.Clear();
            AppState.Clipboard.AddRange(Selected.Select(i => i.FullPath));
            AppState.ClipboardIsCut = cut;
            int n = AppState.Clipboard.Count;
            ExitSelectMode();
            BuildAppBar();
            ShowStatus((cut ? "cut " : "copied ") + n + " item" + (n == 1 ? "" : "s") + ". Open a folder and tap paste.", false);
        }

        bool ConfirmSystem(string what, IEnumerable<string> paths)
        {
            if (!paths.Any(AppState.IsSystemArea)) return true;
            return MessageBox.Show(what + " touches a system area (Windows, EFIESP, DPP, Programs). A mistake here can stop the phone from booting. Continue?",
                                   "System area", MessageBoxButton.OKCancel) == MessageBoxResult.OK;
        }

        /// <summary>Runs a cancellable operation with a progress line. Returns false if it failed or was cancelled
        /// (the error is already on screen).</summary>
        async Task<bool> RunLongAsync(string title, Func<IProgress<OpProgress>, CancellationToken, Task> work)
        {
            bool ok = false;
            _longOp = new CancellationTokenSource();
            BuildAppBar();
            ShowStatus(title, busy: true);
            CancelButton.Visibility = Visibility.Visible;
            var progress = new Progress<OpProgress>(p =>
                StatusText.Text = title + "\n" + p.Files + " items, " + FsItem.FormatSize(p.Bytes) + "\n" + p.Current);
            try
            {
                await work(progress, _longOp.Token);
                HideStatus();
                ok = true;
            }
            catch (Exception e) { ShowError(e); }
            finally
            {
                CancelButton.Visibility = Visibility.Collapsed;
                _longOp = null;
            }
            await LoadAsync(_path, keepScroll: true);
            return ok;
        }

        void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_longOp != null) _longOp.Cancel();
        }

        async Task<bool> ExistsAsync(string path)
        {
            try { await Client.StatAsync(path); return true; }
            catch (RfxException e) { if (e.Code == 0) throw; return false; }
        }

        async void Paste_Click(object sender, EventArgs e)
        {
            if (_path == null || AppState.Clipboard.Count == 0) return;
            var sources = AppState.Clipboard.ToList();
            bool cut = AppState.ClipboardIsCut;
            var targets = new List<string>(sources);
            targets.Add(_path);
            if (!ConfirmSystem(cut ? "This move" : "This copy", targets)) return;

            await RunLongAsync(cut ? "moving..." : "copying...", async (progress, cancel) =>
            {
                bool? overwriteAll = null;
                foreach (var src in sources)
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = FsItem.NameOf(src);
                    string dst = FsItem.Combine(_path, name);
                    bool overwrite = false;
                    if (string.Equals(dst, src, StringComparison.OrdinalIgnoreCase))
                    {
                        if (cut) continue;                       // moving onto itself: nothing to do
                        dst = await FreeNameAsync(_path, name);  // copy in place: "name (2).ext"
                    }
                    else if (await ExistsAsync(dst))
                    {
                        if (overwriteAll == null)
                            overwriteAll = MessageBox.Show("\"" + name + "\" already exists here. Replace it (and any other clashes)? Cancel keeps both by renaming the new copies.",
                                                           "Replace?", MessageBoxButton.OKCancel) == MessageBoxResult.OK;
                        if (overwriteAll == true) overwrite = true;
                        else dst = await FreeNameAsync(_path, name);
                    }
                    await Client.LongAsync(progress, cancel, cut ? "MOVE" : "COPY", src, dst, overwrite ? "1" : "0");
                }
                if (cut) AppState.Clipboard.Clear();
            });
        }

        async Task<string> FreeNameAsync(string dir, string name)
        {
            int dot = name.LastIndexOf('.');
            string stem = dot > 0 ? name.Substring(0, dot) : name, ext = dot > 0 ? name.Substring(dot) : "";
            for (int i = 2; ; i++)
            {
                string p = FsItem.Combine(dir, stem + " (" + i + ")" + ext);
                if (!await ExistsAsync(p)) return p;
            }
        }

        async void Delete_Click(object sender, EventArgs e)
        {
            var sel = Selected;
            if (sel.Count == 0) return;
            string what = sel.Count == 1 ? "\"" + sel[0].Name + "\"" + (sel[0].IsFolder && !sel[0].IsLink ? " and everything in it" : "") : sel.Count + " items";
            if (MessageBox.Show("Delete " + what + "? This can't be undone.", "Delete", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (!ConfirmSystem("This delete", sel.Select(i => i.FullPath))) return;
            await RunLongAsync("deleting...", async (progress, cancel) =>
            {
                foreach (var i in sel)
                {
                    cancel.ThrowIfCancellationRequested();
                    await Client.LongAsync(progress, cancel, "DELETE", i.FullPath);
                }
            });
        }

        async void FolderSize_Click(object sender, EventArgs e)
        {
            var sel = Selected;
            if (sel.Count == 0) return;
            ulong bytes = 0, files = 0, dirs = 0;
            bool ok = await RunLongAsync("measuring...", async (progress, cancel) =>
            {
                foreach (var i in sel)
                {
                    if (!i.IsFolder) { bytes += i.Size; files++; continue; }
                    var r = await Client.LongAsync(progress, cancel, "DU", i.FullPath);
                    bytes += ulong.Parse(r[1]); files += ulong.Parse(r[2]); dirs += ulong.Parse(r[3]) + 1;
                }
            });
            if (!ok) return;
            MessageBox.Show(FsItem.FormatSize(bytes) + " (" + bytes.ToString("N0") + " bytes)\n" + files + " files, " + dirs + " folders", "Size", MessageBoxButton.OK);
        }

        void Rename_Click(object sender, EventArgs e)
        {
            var item = Selected.FirstOrDefault();
            if (item == null) return;
            Prompt.Show(this, "rename", item.Name, async name =>
            {
                if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
                if (!ConfirmSystem("This rename", new[] { item.FullPath })) return;
                try
                {
                    await Client.RenameAsync(item.FullPath, FsItem.Combine(FsItem.ParentOf(item.FullPath), name.Trim()));
                    await LoadAsync(_path, keepScroll: true);
                }
                catch (Exception ex) { ShowError(ex); }
            });
        }

        void NewFolder_Click(object sender, EventArgs e)
        {
            Prompt.Show(this, "new folder", "New folder", async name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                try { await Client.MkdirAsync(FsItem.Combine(_path, name.Trim())); await LoadAsync(_path, keepScroll: true); }
                catch (Exception ex) { ShowError(ex); }
            });
        }

        void NewFile_Click(object sender, EventArgs e)
        {
            Prompt.Show(this, "new text file", "new.txt", async name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                try { await Client.NewFileAsync(FsItem.Combine(_path, name.Trim())); await LoadAsync(_path, keepScroll: true); }
                catch (Exception ex) { ShowError(ex); }
            });
        }

        void Properties_Click(object sender, EventArgs e)
        {
            var item = Selected.FirstOrDefault();
            if (item == null) return;
            ExitSelectMode();
            AppState.PropertiesPath = item.FullPath;
            NavigationService.Navigate(new Uri("/PropertiesPage.xaml", UriKind.Relative));
        }

        /// <summary>Copies the file into this app's own folder (the helper can write there), then
        /// asks Windows Phone to open it with whatever app handles the type.</summary>
        async void OpenWith_Click(object sender, EventArgs e)
        {
            var item = Selected.FirstOrDefault();
            if (item == null || item.IsFolder) return;
            ExitSelectMode();
            await OpenWithAppAsync(item, fallbackToViewer: false);
        }

        async Task OpenWithAppAsync(FsItem item, bool fallbackToViewer)
        {
            string dir = FsItem.Combine(ApplicationData.Current.LocalFolder.Path, "open");
            string dst = FsItem.Combine(dir, item.Name);
            bool ok = await RunLongAsync("preparing " + item.Name + "...", async (progress, cancel) =>
            {
                // only the file being opened is kept, so copies don't pile up
                try { await Client.LongAsync(null, cancel, "DELETE", dir); } catch (RfxException) { }
                try { await Client.MkdirAsync(dir); } catch (RfxException) { }
                await Client.LongAsync(progress, cancel, "COPY", item.FullPath, dst, "1");
            });
            if (!ok) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(dst);
                if (await Windows.System.Launcher.LaunchFileAsync(file)) return;
                if (fallbackToViewer) { ShowViewer(item.FullPath); return; }
                ShowStatus("No app on the phone opens ." + System.IO.Path.GetExtension(item.Name).TrimStart('.') + " files.", false);
            }
            catch (Exception ex)
            {
                if (fallbackToViewer) ShowViewer(item.FullPath); else ShowError(ex);
            }
        }
    }
}
