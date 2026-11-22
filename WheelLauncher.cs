using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace WheelLauncher
{
    public class AppEntry
    {
        public string Name;
        public string Path;
        public string Args;
        public char Initial;
        public ImageSource Icon;
        public override string ToString() { return Name; }
    }

    public static class Log
    {
        static readonly object Gate = new object();
        static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending = new System.Collections.Concurrent.ConcurrentQueue<string>();
        static System.Threading.Thread writer;

        static void Ensure()
        {
            if (writer != null) return;
            lock (Gate)
            {
                if (writer != null) return;
                writer = new System.Threading.Thread(WriterLoop);
                writer.IsBackground = true;
                writer.Start();
            }
        }

        static void WriterLoop()
        {
            while (true)
            {
                try { Flush(); } catch { }
                System.Threading.Thread.Sleep(200);
            }
        }

        static void Flush()
        {
            if (Pending.IsEmpty) return;
            var sb = new StringBuilder();
            string s;
            while (Pending.TryDequeue(out s))
            {
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(s).Append("\r\n");
                if (sb.Length > 8192) break;
            }
            if (sb.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Paths.Root);
                string file = System.IO.Path.Combine(Paths.Root, "log.txt");
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 1048576)
                    {
                        string old = System.IO.Path.Combine(Paths.Root, "log.1.txt");
                        try { if (File.Exists(old)) File.Delete(old); } catch { }
                        try { File.Move(file, old); } catch { try { File.WriteAllText(file, ""); } catch { } }
                    }
                }
                catch { }
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static void W(string msg)
        {
            Ensure();
            try { Pending.Enqueue(msg); } catch { }
        }
    }

    public static class Paths
    {
        public static readonly string Root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WheelLauncher");
    }

    public static class Pinyin
    {
        // GB2312 一级汉字按拼音排序，用区间反查首字母
        static readonly string[] Table = {
            "B0A1B0C4A","B0C5B2C0B","B2C1B4EDC","B4EEB6E9D","B6EAB7A1E","B7A2B8C0F",
            "B8C1B9FDG","B9FEBBF6H","BBF7BFA5J","BFA6C0ABK","C0ACC2E7L","C2E8C4C2M",
            "C4C3C5B5N","C5B6C5BDO","C5BEC6D9P","C6DAC8BAQ","C8BBC8F5R","C8F6CBF9S",
            "CB FACDD9T".Replace(" ",""), "CDDACEF3W","CEF4D1B8X","D1B9D4D0Y","D4D1D7F9Z"
        };

        public static char Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return '#';
            char c = name.Trim()[0];
            if (c >= 'a' && c <= 'z') return char.ToUpper(c);
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return '#';
            try
            {
                byte[] bs = Encoding.GetEncoding(936).GetBytes(c.ToString());
                if (bs.Length != 2) return '#';
                int code = (bs[0] << 8) | bs[1];
                foreach (var row in Table)
                {
                    if (row.Length < 5) continue;
                    int lo = Convert.ToInt32(row.Substring(0, 4), 16);
                    int hi = Convert.ToInt32(row.Substring(4, 4), 16);
                    if (code >= lo && code <= hi) return row[8];
                }
            }
            catch { }
            return '#';
        }
    }

    public static class Icons
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        static ImageSource FromShell(string exePath)
        {
            try
            {
                var ph = new IntPtr[1];
                var ids = new uint[1];
                uint n = PrivateExtractIcons(exePath, 0, 64, 64, ph, ids, 1, 0);
                if (n > 0 && ph[0] != IntPtr.Zero)
                {
                    try
                    {
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(ph[0], Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        return bmp;
                    }
                    finally { DestroyIcon(ph[0]); }
                }
            }
            catch { }
            return null;
        }

        static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static void Put(string key, ImageSource img) { if (Cache.Count > 400) Cache.Clear(); Cache[key] = img; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        static ImageSource FromShellPath(string anyPath)
        {
            try
            {
                var info = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(anyPath, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    0x000000100 /*SHGFI_ICON*/ | 0x000000000 /*SHGFI_LARGEICON*/);
                if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        return bmp;
                    }
                    finally { DestroyIcon(info.hIcon); }
                }
            }
            catch { }
            return null;
        }

        public static ImageSource Get(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            ImageSource src;
            if (Cache.TryGetValue(exePath, out src)) return src;
            try
            {
                if (Directory.Exists(exePath))
                {
                    src = FromShellPath(exePath);
                    Put(exePath, src);
                    return src;
                }
                if (!File.Exists(exePath)) { Put(exePath, null); return null; }
                src = FromShell(exePath);
                if (src == null)
                {
                    using (System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico == null) { Put(exePath, null); return null; }
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        src = bmp;
                    }
                }
            }
            catch { src = null; }
            Put(exePath, src);
            return src;
        }
    }

    public static class Scanner
    {
        static readonly string SelfExe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        public static string CustomFile { get { return System.IO.Path.Combine(Paths.Root, "custom.txt"); } }

        public static List<AppEntry> LoadCustom()
        {
            var list = new List<AppEntry>();
            try
            {
                if (!File.Exists(CustomFile)) return list;
                foreach (var raw in File.ReadAllLines(CustomFile, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var p = line.Split('|');
                    if (p.Length < 2 || (!File.Exists(p[1]) && !Directory.Exists(p[1]))) continue;
                    list.Add(new AppEntry { Name = p[0], Path = p[1], Args = p.Length > 2 ? p[2] : "", Initial = Pinyin.Get(p[0]) });
                }
            }
            catch { }
            return list;
        }

        public static void AddCustom(string name, string path, string args)
        {
            Directory.CreateDirectory(Paths.Root);
            File.AppendAllText(CustomFile, name.Replace("|", " ") + "|" + path + "|" + (args ?? "").Replace("|", " ") + "\r\n", Encoding.UTF8);
        }

        public static bool RemoveCustom(string path)
        {
            try
            {
                if (!File.Exists(CustomFile)) return false;
                var keep = new List<string>();
                bool removed = false;
                foreach (var raw in File.ReadAllLines(CustomFile, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var p = line.Split('|');
                    if (p.Length >= 2 && string.Equals(p[1], path, StringComparison.OrdinalIgnoreCase)) { removed = true; continue; }
                    keep.Add(line);
                }
                if (removed) File.WriteAllLines(CustomFile, keep.ToArray(), Encoding.UTF8);
                return removed;
            }
            catch { return false; }
        }

        public static List<AppEntry> FromDesktop()
        {
            var list = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            object shell = null;
            try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")); }
            catch { }

            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(lnk);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    string target = "", args = "";
                    try
                    {
                        if (shell != null)
                        {
                            dynamic sc = shell.GetType().InvokeMember("CreateShortcut",
                                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                            target = sc.TargetPath;
                            args = sc.Arguments;
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                    if (string.Equals(target, SelfExe, StringComparison.OrdinalIgnoreCase)) continue;
                    // 按目标路径去重（原来按显示名去重，同名不同程序会被吞掉）
                    string key = target.ToLowerInvariant();
                    if (seen.Contains(key)) continue;
                    seen.Add(key);
                    if (list.Exists(delegate (AppEntry q) { return string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase); }))
                    {
                        int n2 = 2;
                        string cand = name;
                        while (list.Exists(delegate (AppEntry q) { return string.Equals(q.Name, cand, StringComparison.OrdinalIgnoreCase); })) cand = name + " " + (n2++);
                        name = cand;
                    }
                    list.Add(new AppEntry { Name = name, Path = target, Args = args, Initial = Pinyin.Get(name) });
                }
            }
            if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch { } }

            foreach (var c in LoadCustom())
            {
                bool dup = false;
                foreach (var e2 in list) if (string.Equals(e2.Path, c.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(e2.Name, c.Name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) list.Add(c);
            }

            foreach (var e in list) e.Icon = Icons.Get(e.Path);
            Log.W("scanned apps=" + list.Count + " letters=" + string.Join("", list.GroupBy(a => a.Initial).OrderBy(g => g.Key == '#' ? 999 : g.Key).Select(g => g.Key.ToString()).ToArray()));
            return list.OrderBy(a => a.Initial == '#' ? 999 : a.Initial).ThenBy(a => a.Name, StringComparer.CurrentCulture).ToList();
        }
    }

    /// <summary>自动识别常用文件夹，作为候选加入圆盘。</summary>
    public static class AutoFolders
    {
        public class Candidate
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public string Where { get; set; }
            public bool Checked { get; set; }
        }

        static string UserHome { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }

        static readonly string[] SkipNames = { "$recycle.bin", "$sysreset", "$winreagent", "$windows.~bt", "$windows.~ws",
            "system volume information", "windows", "program files", "program files (x86)", "programdata", "recovery",
            "msocache", "node_modules", ".git", ".vs", ".idea", "bin", "obj", "packages", "appdata", "temp", "tmp",
            "config.msi", "intel", "amd", "nvidia", "drivers", "perflogs", "onedrivetemp", "users", "documents and settings",
            "found.000", "found.001", "OEM", "lenovo", "boot", "efi", "sources", "installer", "assembly", "servicing",
            "winsxs", "syswow64", "system32", "xboxgames", "hyper-v", "python27", "python310", "nodejs", "git" };

        public static bool IsSkipped(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n.Length == 0 || n.StartsWith(".") || n.StartsWith("$")) return true;
            for (int i = 0; i < SkipNames.Length; i++) if (n == SkipNames[i]) return true;
            return false;
        }

        static void AddKnown(List<Candidate> list, string name, string path, bool check)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                list.Add(new Candidate { Name = name, Path = path, Where = "系统文件夹", Checked = check });
        }

        public static List<Candidate> Known()
        {
            var list = new List<Candidate>();
            AddKnown(list, "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false);
            AddKnown(list, "文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true);
            AddKnown(list, "下载", System.IO.Path.Combine(UserHome, "Downloads"), true);
            AddKnown(list, "图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), false);
            AddKnown(list, "视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), false);
            AddKnown(list, "音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), false);
            return list;
        }

        /// <summary>扫描候选：常用文件夹 + 桌面里的文件夹 + 固定磁盘根目录一层。</summary>
        public static List<Candidate> Scan()
        {
            var list = Known();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in list) seen.Add(c.Path);

            try
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (Directory.Exists(desk))
                    foreach (var d in Directory.GetDirectories(desk))
                    {
                        string n = System.IO.Path.GetFileName(d);
                        if (IsSkipped(n) || !seen.Add(d)) continue;
                        list.Add(new Candidate { Name = n, Path = d, Where = "桌面", Checked = false });
                    }
            }
            catch { }

            try
            {
                foreach (var dr in DriveInfo.GetDrives())
                {
                    if (dr.DriveType != DriveType.Fixed || !dr.IsReady) continue;
                    string[] dirs;
                    try { dirs = Directory.GetDirectories(dr.RootDirectory.FullName); } catch { continue; }
                    int n = 0;
                    foreach (var d in dirs)
                    {
                        if (n >= 24) break;
                        string name = System.IO.Path.GetFileName(d);
                        if (IsSkipped(name) || !seen.Add(d)) continue;
                        list.Add(new Candidate { Name = name, Path = d, Where = dr.Name.TrimEnd('\\'), Checked = false });
                        n++;
                    }
                }
            }
            catch { }

            var onWheel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Scanner.FromDesktop()) onWheel.Add(e.Path);
            list.RemoveAll(delegate (Candidate c) { return onWheel.Contains(c.Path); });
            return list;
        }

        /// <summary>把系统常用文件夹加进圆盘，返回新增数量（已存在的不重复加）。</summary>
        public static int EnsureDefaults()
        {
            int added = 0;
            var existing = Scanner.LoadCustom();
            foreach (var c in Known())
            {
                if (!c.Checked) continue;
                bool has = false;
                foreach (var e in existing) if (string.Equals(e.Path, c.Path, StringComparison.OrdinalIgnoreCase)) { has = true; break; }
                if (has) continue;
                Scanner.AddCustom(c.Name, c.Path, "");
                added++;
            }
            if (added > 0) Log.W("autofolders: added " + added + " known folders");
            return added;
        }
    }

    /// <summary>自动收录文件夹的选择窗口。</summary>
    public class AutoFoldersWindow : Window
    {
        public class Row
        {
            public bool Checked { get; set; }
            public string Name { get; set; }
            public string Where { get; set; }
            public string Path { get; set; }
        }

        ListView lv;
        TextBlock tip;
        List<Row> rows = new List<Row>();
        public int Added;

        public AutoFoldersWindow()
        {
            Title = "自动收录文件夹";
            Width = 820; Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Ui.BgCol);
            Foreground = new SolidColorBrush(Ui.InkCol);
            FontSize = 13;

            var root = new Grid { Background = new SolidColorBrush(Ui.BgCol) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            Ui.Crisp(this);

            var head = new StackPanel { Margin = new Thickness(18, 16, 18, 4) };
            head.Children.Add(new TextBlock { Text = "自动收录文件夹", FontSize = 19, FontWeight = FontWeights.SemiBold });
            head.Children.Add(new TextBlock
            {
                Text = "勾选后会加到圆盘上（按名字首字母分环）。已经在圆盘上的不会重复出现。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Ui.SubCol),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            DockPanel.SetDock(head, Dock.Top);
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 8, 18, 10) };
            var bAll = Ui.MkButton("全选", false);
            var bNone = Ui.MkButton("全不选", false);
            var bRe = Ui.MkButton("重新扫描", false);
            bAll.Margin = new Thickness(0, 0, 8, 0); bNone.Margin = new Thickness(0, 0, 8, 0);
            bar.Children.Add(bAll); bar.Children.Add(bNone); bar.Children.Add(bRe);
            DockPanel.SetDock(bar, Dock.Top);
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            var foot = new DockPanel { Margin = new Thickness(18, 8, 18, 14) };
            tip = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Ui.SubCol), FontSize = 12 };
            var add = Ui.MkButton("加入圆盘", true);
            var cancel = Ui.MkButton("取消", false);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(add); right.Children.Add(cancel);
            DockPanel.SetDock(right, Dock.Right);
            foot.Children.Add(tip); foot.Children.Add(right);
            DockPanel.SetDock(foot, Dock.Bottom);
            Grid.SetRow(foot, 3);
            root.Children.Add(foot);

            lv = new ListView { Margin = new Thickness(18, 0, 18, 0), Background = new SolidColorBrush(Ui.CardCol), BorderThickness = new Thickness(0) };
            var gv = new GridView();
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding("Checked") { Mode = System.Windows.Data.BindingMode.TwoWay });
            cb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var tpl = new DataTemplate();
            tpl.VisualTree = cb;
            gv.Columns.Add(new GridViewColumn { Header = "", Width = 42, CellTemplate = tpl });
            gv.Columns.Add(new GridViewColumn { Header = "名称", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            gv.Columns.Add(new GridViewColumn { Header = "位置", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Where") });
            gv.Columns.Add(new GridViewColumn { Header = "路径", Width = 360, DisplayMemberBinding = new System.Windows.Data.Binding("Path") });
            lv.View = gv;
            Ui.Skin(lv, gv);
            Grid.SetRow(lv, 2);
            lv.Margin = new Thickness(18, 0, 18, 0);
            root.Children.Add(lv);

            bAll.Click += delegate { foreach (var r in rows) r.Checked = true; Refresh(); };
            bNone.Click += delegate { foreach (var r in rows) r.Checked = false; Refresh(); };
            bRe.Click += delegate { Load(); };
            add.Click += delegate { AddSelected(); };
            cancel.Click += delegate { Close(); };
            Loaded += delegate { Load(); };
        }

        void Load()
        {
            rows = new List<Row>();
            foreach (var c in AutoFolders.Scan())
                rows.Add(new Row { Checked = c.Checked, Name = c.Name, Where = c.Where, Path = c.Path });
            Refresh();
        }

        void Refresh()
        {
            lv.Items.Clear();
            foreach (var r in rows) lv.Items.Add(r);
            int n = 0;
            foreach (var r in rows) if (r.Checked) n++;
            tip.Text = "共 " + rows.Count + " 个候选，已勾选 " + n + " 个";
        }

        void AddSelected()
        {
            int n = 0;
            foreach (var r in rows)
            {
                if (!r.Checked) continue;
                Scanner.AddCustom(r.Name, r.Path, "");
                n++;
            }
            Added = n;
            Log.W("autofolders add: " + n);
            Close();
        }
    }

    static class Ui
    {
        // 两套配色同时设计好，切换时整窗口重建，不残留旧色
        public static bool Dark { get { return Settings.DarkUI; } }

        public static Color BgCol { get { return Dark ? Color.FromRgb(38, 36, 46) : Color.FromRgb(226, 224, 216); } }
        public static Color CardCol { get { return Dark ? Color.FromRgb(47, 45, 57) : Color.FromRgb(243, 242, 236); } }
        public static Color InkCol { get { return Dark ? Color.FromRgb(217, 213, 227) : Color.FromRgb(43, 41, 51); } }
        public static Color SubCol { get { return Dark ? Color.FromRgb(156, 150, 174) : Color.FromRgb(107, 102, 117); } }
        public static Color MutedCol { get { return Dark ? Color.FromRgb(126, 120, 144) : Color.FromRgb(139, 134, 152); } }
        public static Color LineCol { get { return Dark ? Color.FromRgb(60, 57, 71) : Color.FromRgb(213, 210, 201); } }
        public static Color HeadCol { get { return Dark ? Color.FromRgb(43, 41, 52) : Color.FromRgb(233, 231, 223); } }
        public static Color AccentCol { get { return Dark ? Color.FromRgb(124, 107, 224) : Color.FromRgb(110, 91, 200); } }
        public static Color AccentHiCol { get { return Dark ? Color.FromRgb(143, 127, 240) : Color.FromRgb(127, 109, 217); } }
        public static Color ChipCol { get { return Dark ? Color.FromRgb(53, 50, 63) : Color.FromRgb(223, 220, 211); } }
        public static Color ChipHiCol { get { return Dark ? Color.FromRgb(64, 60, 77) : Color.FromRgb(211, 207, 196); } }
        public static Color TabBgCol { get { return Dark ? Color.FromRgb(48, 46, 58) : Color.FromRgb(225, 222, 214); } }
        public static Color TabFgCol { get { return Dark ? Color.FromRgb(169, 163, 187) : Color.FromRgb(85, 80, 95); } }
        public static Color SelBgCol { get { return Dark ? Color.FromRgb(59, 53, 82) : Color.FromRgb(214, 206, 240); } }
        public static Color WarnBgCol { get { return Dark ? Color.FromRgb(74, 58, 56) : Color.FromRgb(246, 227, 221); } }
        public static Color WarnFgCol { get { return Dark ? Color.FromRgb(232, 180, 166) : Color.FromRgb(168, 64, 44); } }

        public static SolidColorBrush Br(Color c) { return new SolidColorBrush(c); }

        // 文字清晰化：ClearType + 设备像素对齐 + Display 排版模式（避免 150% 缩放下发虚）
        public static void Crisp(FrameworkElement fe)
        {
            fe.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(fe, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(fe, TextRenderingMode.ClearType);
            var win = fe as Window;
            if (win != null) win.UseLayoutRounding = true;
        }

        // 轮盘窗口是透明底层，ClearType 会被系统关掉 → 用灰度抗锯齿并加粗，避免发虚
        public static void CrispWheel(FrameworkElement fe)
        {
            fe.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(fe, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(fe, TextRenderingMode.Grayscale);
            var win = fe as Window;
            if (win != null) win.UseLayoutRounding = true;
        }

        public static Button MkButton(string text, bool accent)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 6, 14, 6),
                FontSize = 12.5,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Br(accent ? AccentCol : ChipCol),
                Foreground = Br(accent ? Colors.White : InkCol)
            };
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            b.Template = tpl;
            b.MouseEnter += delegate { b.Background = Br(accent ? AccentHiCol : ChipHiCol); };
            b.MouseLeave += delegate { b.Background = Br(accent ? AccentCol : ChipCol); };
            return b;
        }

        public static RadioButton MkRadio(string text, string group, bool isChecked)
        {
            var rb = new RadioButton
            {
                Content = text,
                GroupName = group,
                IsChecked = isChecked,
                Foreground = Br(InkCol),
                FontSize = 12.5,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 9)
            };
            var tpl = new ControlTemplate(typeof(RadioButton));
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(FrameworkElement.WidthProperty, 17.0);
            grid.SetValue(FrameworkElement.HeightProperty, 17.0);
            grid.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var ring = new FrameworkElementFactory(typeof(Ellipse));
            ring.Name = "ring";
            ring.SetValue(Ellipse.StrokeProperty, Br(MutedCol));
            ring.SetValue(Ellipse.StrokeThicknessProperty, 1.5);
            ring.SetValue(Ellipse.FillProperty, Brushes.Transparent);
            ring.SetValue(FrameworkElement.WidthProperty, 14.0);
            ring.SetValue(FrameworkElement.HeightProperty, 14.0);
            ring.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            ring.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var dot = new FrameworkElementFactory(typeof(Ellipse));
            dot.Name = "dot";
            dot.SetValue(Ellipse.FillProperty, Br(AccentHiCol));
            dot.SetValue(FrameworkElement.WidthProperty, 7.0);
            dot.SetValue(FrameworkElement.HeightProperty, 7.0);
            dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetValue(FrameworkElement.VisibilityProperty, Visibility.Collapsed);
            grid.AppendChild(ring);
            grid.AppendChild(dot);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(grid);
            root.AppendChild(cp);
            tpl.VisualTree = root;
            var trOn = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
            trOn.Setters.Add(new Setter(FrameworkElement.VisibilityProperty, Visibility.Visible) { TargetName = "dot" });
            trOn.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Br(AccentHiCol)) { TargetName = "ring" });
            tpl.Triggers.Add(trOn);
            var trHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trHover.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Br(AccentCol)) { TargetName = "ring" });
            tpl.Triggers.Add(trHover);
            rb.Template = tpl;
            return rb;
        }

        public static void StyleCheck(CheckBox cb)
        {
            cb.Foreground = Br(InkCol);
            cb.Cursor = Cursors.Hand;
            var tpl = new ControlTemplate(typeof(CheckBox));
            var root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var box = new FrameworkElementFactory(typeof(Border));
            box.Name = "box";
            box.SetValue(FrameworkElement.WidthProperty, 16.0);
            box.SetValue(FrameworkElement.HeightProperty, 16.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1.4));
            box.SetValue(Border.BorderBrushProperty, Br(MutedCol));
            box.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var mark = new FrameworkElementFactory(typeof(TextBlock));
            mark.Name = "mark";
            mark.SetValue(TextBlock.TextProperty, "\u2713");
            mark.SetValue(TextBlock.FontSizeProperty, 11.0);
            mark.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            mark.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(FrameworkElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(mark);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(box);
            root.AppendChild(cp);
            tpl.VisualTree = root;
            var trOn = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
            trOn.Setters.Add(new Setter(FrameworkElement.VisibilityProperty, Visibility.Visible) { TargetName = "mark" });
            trOn.Setters.Add(new Setter(Border.BackgroundProperty, Br(AccentCol)) { TargetName = "box" });
            trOn.Setters.Add(new Setter(Border.BorderBrushProperty, Br(AccentCol)) { TargetName = "box" });
            tpl.Triggers.Add(trOn);
            cb.Template = tpl;
        }

        public static TextBlock Head(string text)
        {
            return new TextBlock { Text = text, FontSize = 12, Foreground = Br(SubCol), Margin = new Thickness(0, 0, 0, 6) };
        }

        public static Color Mix(Color a, Color b, double k) { return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k)); }

        public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public static void Skin(ListView lv, GridView gv)
        {
            var headTpl = new Style(typeof(GridViewColumnHeader));
            headTpl.Setters.Add(new Setter(GridViewColumnHeader.BackgroundProperty, Br(HeadCol)));
            headTpl.Setters.Add(new Setter(GridViewColumnHeader.ForegroundProperty, Br(SubCol)));
            headTpl.Setters.Add(new Setter(GridViewColumnHeader.BorderBrushProperty, Br(LineCol)));
            headTpl.Setters.Add(new Setter(GridViewColumnHeader.PaddingProperty, new Thickness(10, 8, 10, 8)));
            headTpl.Setters.Add(new Setter(Control.FontSizeProperty, 11.5));
            headTpl.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Normal));
            headTpl.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Arrow));
            var headView = new ControlTemplate(typeof(GridViewColumnHeader));
            var hb = new FrameworkElementFactory(typeof(Border));
            hb.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            hb.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            hb.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
            hb.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            hb.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var hcp = new FrameworkElementFactory(typeof(ContentPresenter));
            hcp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            hcp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            hb.AppendChild(hcp);
            headView.VisualTree = hb;
            headTpl.Setters.Add(new Setter(Control.TemplateProperty, headView));
            gv.ColumnHeaderContainerStyle = headTpl;

            lv.Background = Br(CardCol);
            lv.Foreground = Br(InkCol);
            lv.BorderBrush = Br(LineCol);
            lv.BorderThickness = new Thickness(1);
            lv.FontSize = 12.5;

            // 行模板：默认模板选中时会画一圈系统白边，很扎眼 → 自绘圆角底，选中只用配色区分
            string selHex = Hex(Mix(CardCol, AccentCol, Dark ? 0.34 : 0.30));
            string hovHex = Hex(Mix(CardCol, AccentCol, Dark ? 0.14 : 0.12));
            string rowXaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListViewItem'>"
                + "<Border x:Name='bd' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' CornerRadius='4' SnapsToDevicePixels='True'>"
                + "<GridViewRowPresenter VerticalAlignment='Center' Columns='{TemplateBinding GridView.ColumnCollection}'/>"
                + "</Border>"
                + "<ControlTemplate.Triggers>"
                + "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='" + hovHex + "'/></Trigger>"
                + "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='" + selHex + "'/></Trigger>"
                + "</ControlTemplate.Triggers></ControlTemplate>";
            var item = new Style(typeof(ListViewItem));
            item.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(2, 5, 2, 5)));
            item.Setters.Add(new Setter(Control.ForegroundProperty, Br(InkCol)));
            item.Setters.Add(new Setter(Control.BackgroundProperty, Br(CardCol)));
            item.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            item.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            item.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            item.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse(rowXaml)));
            lv.ItemContainerStyle = item;

            // 滚动条：默认模板是浅色系统主题（深色下发白、看不清）→ 用 XAML 模板换成调色板色
            string thumbHex = Dark ? "#6E6880" : "#8E8898";
            string trackHex = Dark ? "#2A2833" : "#D6D3CA";
            string vertXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>"
                + "<Grid Width='12' Background='" + trackHex + "'>"
                + "<Track x:Name='PART_Track' Focusable='False' Orientation='Vertical' IsDirectionReversed='True' "
                + "Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' "
                + "Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>"
                + "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>"
                + "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>"
                + "<Track.Thumb><Thumb Width='8' Margin='2' MinHeight='24'>"
                + "<Thumb.Template><ControlTemplate TargetType='Thumb'>"
                + "<Border CornerRadius='4' Background='" + thumbHex + "'/>"
                + "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
                + "</Track></Grid></ControlTemplate>";
            string horizXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>"
                + "<Grid Height='12' Background='" + trackHex + "'>"
                + "<Track x:Name='PART_Track' Focusable='False' Orientation='Horizontal' IsDirectionReversed='False' "
                + "Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' "
                + "Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>"
                + "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageLeftCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>"
                + "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageRightCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>"
                + "<Track.Thumb><Thumb Height='8' Margin='2' MinWidth='24'>"
                + "<Thumb.Template><ControlTemplate TargetType='Thumb'>"
                + "<Border CornerRadius='4' Background='" + thumbHex + "'/>"
                + "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
                + "</Track></Grid></ControlTemplate>";
            var sbStyle = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
            sbStyle.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse(vertXaml)));
            var trigH = new Trigger { Property = System.Windows.Controls.Primitives.ScrollBar.OrientationProperty, Value = Orientation.Horizontal };
            trigH.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse(horizXaml)));
            sbStyle.Triggers.Add(trigH);
            // ScrollViewer 模板：横竖滚动条交叉处默认是系统白块，染成卡片色

            // 妯珫婊氬姩鏉′氦鍙夎榛樿鐢?SystemColors.ControlBrush锛堢櫧鍧楋級鈫?鎹㈡垚鍗＄墖搴曡壊
            lv.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), sbStyle);
        }
        // 滑块模板：默认模板在深/浅两套底色上都看不清，改成细轨 + 圆点
        public static Style SliderStyle()
        {
            string trackHex = Dark ? "#3C3947" : "#CFCBC0";
            string thumbHex = Dark ? "#D9D5E3" : "#FFFFFF";
            string accentHex = Dark ? "#7C6BE0" : "#6E5BC8";
            string xaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                + "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>"
                + "<Grid VerticalAlignment='Center' Height='24' Background='Transparent'>"
                + "<Border Height='4' CornerRadius='2' Background='" + trackHex + "' VerticalAlignment='Center'/>"
                + "<Track x:Name='PART_Track' VerticalAlignment='Center' Focusable='False' Orientation='Horizontal' "
                + "Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' "
                + "Value='{TemplateBinding Value}'>"
                + "<Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Focusable='False' Opacity='0'/></Track.DecreaseRepeatButton>"
                + "<Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Focusable='False' Opacity='0'/></Track.IncreaseRepeatButton>"
                + "<Track.Thumb><Thumb Width='16' Height='16' Cursor='Hand'>"
                + "<Thumb.Template><ControlTemplate TargetType='Thumb'>"
                + "<Ellipse Fill='" + thumbHex + "' Stroke='" + accentHex + "' StrokeThickness='2'/>"
                + "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
                + "</Track></Grid></ControlTemplate>";
            var st = new Style(typeof(Slider));
            st.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse(xaml)));
            st.Setters.Add(new Setter(FrameworkElement.HeightProperty, 24.0));
            return st;
        }
        public static void SkinTabs(TabControl tc)
        {
            var s = new Style(typeof(TabItem));
            s.Setters.Add(new Setter(TabItem.BackgroundProperty, Br(TabBgCol)));
            s.Setters.Add(new Setter(TabItem.ForegroundProperty, Br(TabFgCol)));
            s.Setters.Add(new Setter(TabItem.BorderThicknessProperty, new Thickness(0)));
            s.Setters.Add(new Setter(TabItem.FontSizeProperty, 13.0));
            s.Setters.Add(new Setter(TabItem.MarginProperty, new Thickness(0, 0, 6, 0)));
            s.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));

            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "b";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(9, 9, 0, 0));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.PaddingProperty, new Thickness(16, 8, 16, 8));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            var tpl = new ControlTemplate(typeof(TabItem));
            tpl.VisualTree = bd;
            s.Setters.Add(new Setter(Control.TemplateProperty, tpl));

            var sel = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(TabItem.BackgroundProperty, Br(AccentCol)));
            sel.Setters.Add(new Setter(TabItem.ForegroundProperty, Br(Colors.White)));
            sel.Setters.Add(new Setter(TabItem.FontWeightProperty, FontWeights.SemiBold));
            s.Triggers.Add(sel);
            tc.ItemContainerStyle = s;
        }
    }

    /// <summary>手动维护的「游戏」清单（只影响自动暂停，不影响圆盘显示）。</summary>
    public static class GameList
    {
        static List<string> cache;
        static DateTime cacheAt;

        public static string FilePath { get { return System.IO.Path.Combine(Paths.Root, "games.txt"); } }

        public static List<string> Load()
        {
            if (cache != null && (DateTime.Now - cacheAt).TotalSeconds < 2) return cache;
            var list = new List<string>();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        if (!list.Exists(delegate (string q) { return string.Equals(q, line, StringComparison.OrdinalIgnoreCase); })) list.Add(line);
                    }
                }
            }
            catch { }
            cache = list;
            cacheAt = DateTime.Now;
            return list;
        }

        public static void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Directory.CreateDirectory(Paths.Root);
                var all = Load();
                foreach (var q in all) if (string.Equals(q, path, StringComparison.OrdinalIgnoreCase)) return;
                File.AppendAllText(FilePath, path + "\r\n", Encoding.UTF8);
                cache = null;
            }
            catch (Exception ex) { Log.W("gamelist add failed: " + ex.Message); }
        }

        public static bool Remove(string path)
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var keep = new List<string>();
                bool removed = false;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (string.Equals(line, path, StringComparison.OrdinalIgnoreCase)) { removed = true; continue; }
                    keep.Add(line);
                }
                if (removed) { File.WriteAllLines(FilePath, keep.ToArray(), Encoding.UTF8); cache = null; }
                return removed;
            }
            catch { return false; }
        }

        /// <summary>命中：路径完全相等，或位于某个手动游戏文件夹之下。</summary>
        public static bool Match(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var g in Load())
            {
                if (string.Equals(g, path, StringComparison.OrdinalIgnoreCase)) return true;
                string prefix = g.TrimEnd('\\') + "\\";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    public static class GameWatch
    {
        static readonly string[] NotGame = { "explorer", "chrome", "msedge", "firefox", "code", "discord", "wechat", "weixin",
            "qq", "notepad", "powershell", "pwsh", "cmd", "windowsterminal", "conhost", "applicationframehost",
            "searchhost", "startmenuexperiencehost", "shellexperiencehost", "taskmgr", "devenv", "sublime_text",
            "spotify", "vlc", "wmplayer", "musicbee", "obs64", "obs32", "doubao", "bandizip", "winrar", "7zfm",
            "lockapp", "lockapphost", "logonui", "textinputhost", "ctfmon", "widgets", "searchapp", "searchui",
            "sihost", "useroobebroker", "nahimic3", "nahimicsvc", "audiodg", "rundll32", "dllhost", "sppsvc",
            "sticky notes", "stickynotes", "calculatorapp", "photos", "photoshop", "illustrator", "word", "excel",
            "powerpnt", "wps", "wpp", "et", "acrobat", "acrord32", "foxitreader", "thunderbird", "outlook" };

        static readonly string[] GameDirs = { "\\steamapps\\common\\", "\\steam library\\", "\\epic games\\", "\\gog games\\",
            "\\gog galaxy\\games\\", "\\wegameapps\\", "\\riot games\\", "\\battle.net\\", "\\ea games\\", "\\origin games\\",
            "\\xboxgames\\", "\\ubisoft\\", "\\ubisoft game launcher\\games\\", "\\my games\\", "\\game\\", "\\games\\" };

        static readonly string[] StoreExes = { "steam", "epicgameslauncher", "wegame", "wegames", "riotclientservices",
            "battle.net", "origin", "eaapp", "eadesktop", "ubisoftconnect", "ubisoftgamelauncher", "upc",
            "galaxylauncher", "gamingservices", "gamingservicesnet", "itch", "goggalaxy" };

        static readonly string[] AccelExes = { "biubiu", "biubiu_nets_proxy", "biubiuacc", "glacc", "uugame", "neteaseuu",
            "xunyou", "leigod", "ghelper" };

        public static volatile bool Suspended;
        public static volatile bool AccelFg;
        public static volatile string FgExe = "";
        public static bool Paused { get { return Suspended || AccelFg; } }
        public static Action<bool> Changed;

        static GameWatch()
        {
            var t = new System.Threading.Thread(Loop);
            t.IsBackground = true;
            t.Name = "gamewatch";
            t.Start();
            Log.W("gamewatch started");
        }

        static void Loop()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(1200);
                try { Scan(); } catch (Exception ex) { Log.W("gamewatch error: " + ex.Message); }
            }
        }

        public static string ExeName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return System.IO.Path.GetFileName(path).ToLowerInvariant(); } catch { return ""; }
        }

        public static string Bare(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return "";
            string s = exe.Trim().ToLowerInvariant();
            if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
            return s;
        }

        public static bool IsStoreExe(string exe) { return Array.IndexOf(StoreExes, Bare(exe)) >= 0; }
        public static bool IsAccelExe(string exe) { return Array.IndexOf(AccelExes, Bare(exe)) >= 0; }

        public static string PidPath(int pid)
        {
            IntPtr h = Native.OpenProcess(0x1000, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new System.Text.StringBuilder(1024);
                    int size = 1024;
                    if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
                }
                finally { Native.CloseHandle(h); }
            }
            try { return System.Diagnostics.Process.GetProcessById(pid).MainModule.FileName; }
            catch { return ""; }
        }

        static System.Collections.Generic.Dictionary<int, int> ParentMap()
        {
            var map = new System.Collections.Generic.Dictionary<int, int>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var pe = new Native.PROCESSENTRY32();
                pe.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.PROCESSENTRY32));
                if (Native.Process32First(snap, ref pe))
                {
                    do
                    {
                        map[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID;
                        pe.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.PROCESSENTRY32));
                    } while (Native.Process32Next(snap, ref pe));
                }
            }
            finally { Native.CloseHandle(snap); }
            return map;
        }

        public static bool IsGame(IntPtr h, out string why)
        {
            why = "-";
            if (h == IntPtr.Zero) return false;
            int pid;
            Native.GetWindowThreadProcessId(h, out pid);
            string path = PidPath(pid);
            string exe = ExeName(path);
            if (exe.Length == 0) return false;
            string bare = exe.EndsWith(".exe") ? exe.Substring(0, exe.Length - 4) : exe;
            if (Array.IndexOf(NotGame, bare) >= 0) return false;

            if (IsStoreExe(bare))
            {
                if (Settings.PauseOnStore) { why = "storeui:" + bare; return true; }
                return false;
            }
            if (path.Length > 0 && GameList.Match(path)) { why = "manual:" + exe; return true; }
            if (path.Length > 0)
            {
                string low = path.ToLowerInvariant();
                foreach (var d in GameDirs) { if (low.Contains(d)) { why = "path:" + d.Trim('\\'); return true; } }
            }
            try
            {
                var map = ParentMap();
                int cur = (int)pid;
                for (int depth = 0; depth < 3; depth++)
                {
                    int parent;
                    if (!map.TryGetValue(cur, out parent) || parent <= 0) break;
                    string pp = PidPath(parent);
                    string pe = ExeName(pp);
                    string pbare = pe.EndsWith(".exe") ? pe.Substring(0, pe.Length - 4) : pe;
                    if (IsStoreExe(pbare)) { why = "store:" + pbare + ">" + bare; return true; }
                    cur = parent;
                }
            }
            catch { }

            int quns = 0;
            if (Native.SHQueryUserNotificationState(out quns) >= 0 && quns == 3) { why = "d3d-fullscreen:" + bare; return true; }

            try
            {
                var r = new Native.RECT();
                Native.GetWindowRect(h, out r);
                var scr = WF.Screen.FromHandle(h).Bounds;
                bool covers = Math.Abs(r.Left - scr.Left) <= 2 && Math.Abs(r.Top - scr.Top) <= 2 &&
                               Math.Abs(r.Right - scr.Right) <= 2 && Math.Abs(r.Bottom - scr.Bottom) <= 2;
                long style = Native.GetWindowLongPtr(h, -16).ToInt64();
                bool caption = (style & 0x00C00000) == 0x00C00000;
                bool popup = (style & 0x80000000L) != 0;
                if (covers && (!caption || popup)) { why = "fullscreen:" + bare; return true; }
            }
            catch { }
            return false;
        }

        public static bool IsGamePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (GameList.Match(path)) return true;
            string low = path.ToLowerInvariant();
            foreach (var d in GameDirs) { if (low.Contains(d)) return true; }
            return false;
        }

        static void Scan()
        {
            IntPtr fg = Native.GetForegroundWindow();
            string fex = "";
            if (fg != IntPtr.Zero)
            {
                int pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                fex = ExeName(PidPath(pid));
            }
            FgExe = fex;
            string bare = fex.EndsWith(".exe") ? fex.Substring(0, fex.Length - 4) : fex;

            bool accel = Settings.PauseAccel && !WheelApp.IsElevated && IsAccelExe(bare);
            if (accel != AccelFg)
            {
                AccelFg = accel;
                Log.W(accel ? ("accel pause (" + bare + ")") : "accel resume");
            }

            string why; bool game = IsGame(fg, out why);
            if (game != Suspended)
            {
                Suspended = game;
                if (game) Log.W("gamewatch: suspend (" + why + ")");
                else Log.W("gamewatch: resume");
                var ch = Changed;
                if (ch != null) ch(game);
            }
        }

        public static void Start() { }
        public static void Probe(int seconds)
        {
            var t = new System.Threading.Thread(delegate ()
            {
                int end = Environment.TickCount + seconds * 1000;
                while (Environment.TickCount < end)
                {
                    IntPtr h = Native.GetForegroundWindow();
                    int pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    int quns = 0;
                    Native.SHQueryUserNotificationState(out quns);
                    string why; bool g = IsGame(h, out why);
                    Log.W("gameprobe fg=" + ExeName(PidPath((int)pid)) + " game=" + g + " why=" + why + " quns=" + quns);
                    System.Threading.Thread.Sleep(1500);
                }
                Log.W("gameprobe done");
            });
            t.IsBackground = true;
            t.Start();
        }

        public static void TestPid(int pid)
        {
            string ppath = PidPath(pid);
            IntPtr hwnd = IntPtr.Zero;
            try { hwnd = System.Diagnostics.Process.GetProcessById(pid).MainWindowHandle; } catch { }
            string why; bool g = IsGame(hwnd, out why);
            Log.W("gametest pid=" + pid + " hwnd=" + hwnd + " game=" + g + " why=" + why + " path=" + ppath);
        }
    }

    // ===== 自检（--selftest）：纯逻辑断言，不开窗、不影响常驻运行 =====
    public static class SelfTest
    {
        static int pass, fail;
        static System.Text.StringBuilder sb;

        static void C(string name, bool ok)
        {
            if (ok) pass++;
            else { fail++; sb.AppendLine("FAIL  " + name); }
        }

        public static int Run()
        {
            sb = new System.Text.StringBuilder();
            pass = 0; fail = 0;
            try
            {
                C("pinyin 微信=W", Pinyin.Get("微信") == 'W');
                C("pinyin 文档=W", Pinyin.Get("文档") == 'W');
                C("pinyin 下载=X", Pinyin.Get("下载") == 'X');
                C("pinyin QQ=Q", Pinyin.Get("QQ") == 'Q');
                C("pinyin DeepSeek=D", Pinyin.Get("DeepSeek Harness") == 'D');
                C("pinyin 数字=#", Pinyin.Get("360安全") == '#');

                C("hex", Ui.Hex(System.Windows.Media.Color.FromRgb(1, 2, 3)) == "#010203");
                System.Windows.Media.Color mid = Ui.Mix(System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White, 0.5);
                C("mix 50%", mid.R == 127 && mid.G == 127 && mid.B == 127);

                C("skin names=6", WheelSkin.Names.Length == 6);
                int wi = WheelSkin.Index;
                C("skin index in range", wi >= 0 && wi < WheelSkin.Names.Length);

                C("store steam", GameWatch.IsStoreExe("steam"));
                C("store epic", GameWatch.IsStoreExe("epicgameslauncher"));
                C("exename steam.exe", GameWatch.ExeName(@"C:\Program Files (x86)\Steam\steam.exe") == "steam.exe");
                C("store steam.exe", GameWatch.IsStoreExe("Steam.EXE"));
                C("accel biubiu", GameWatch.IsAccelExe("biubiu"));
                C("accel glacc.exe", GameWatch.IsAccelExe("glacc.exe"));
                C("not accel chrome", !GameWatch.IsAccelExe("chrome.exe"));

                C("gamepath steamlib", GameWatch.IsGamePath(@"D:\SteamLibrary\steamapps\common\Stardew Valley\Stardew Valley.exe"));
                C("gamepath explorer=false", !GameWatch.IsGamePath(@"C:\Windows\explorer.exe"));

                int ov = Settings.Volume;
                Settings.Volume = 37;
                C("settings volume write", Settings.Volume == 37);
                Settings.Volume = ov;
                C("settings volume restore", Settings.Volume == ov);
                C("settings longpress 250..2000", Settings.LongPress >= 250 && Settings.LongPress <= 2000);
                C("settings wheelsize 70..140", Settings.WheelSize >= 70 && Settings.WheelSize <= 140);

                string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wl_selftest_dir");
                string customPath = System.IO.Path.Combine(Paths.Root, "custom.txt");
                Scanner.AddCustom("__selftest__", tmpDir, "");
                string raw = System.IO.File.Exists(customPath) ? System.IO.File.ReadAllText(customPath, System.Text.Encoding.UTF8) : "";
                C("custom add", raw.Contains("__selftest__"));
                C("custom remove", Scanner.RemoveCustom(tmpDir));
                raw = System.IO.File.Exists(customPath) ? System.IO.File.ReadAllText(customPath, System.Text.Encoding.UTF8) : "";
                C("custom removed", raw.IndexOf("__selftest__") < 0);

                string gp = System.IO.Path.Combine(tmpDir, "a.exe");
                GameList.Add(tmpDir);
                C("gamelist match", GameList.Match(gp));
                C("gamelist remove", GameList.Remove(tmpDir));
                C("gamelist unmatched", !GameList.Match(gp));

                C("icon notepad", Icons.Get(@"C:\Windows\System32\notepad.exe") != null);
                C("paths root exists", System.IO.Directory.Exists(Paths.Root));
                C("hotkey vk F7", 0x76 == 118 && 0x77 == 119);
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("FAIL  exception: " + ex.GetType().Name + ": " + ex.Message);
            }
            string head = "SELFTEST pass=" + pass + " fail=" + fail;
            sb.Insert(0, head + Environment.NewLine);
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Root, "selftest.txt"), sb.ToString(), System.Text.Encoding.UTF8); }
            catch { }
            try { Log.W(head); } catch { }
            return fail == 0 ? 0 : 1;
        }
    }
    // ===== 手柄：L3+Start 呼出，左摇杆选方向，A 确认，B 取消 =====
    public static class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_GAMEPAD
        {
            public ushort wButtons; public byte bLeftTrigger; public byte bRightTrigger;
            public short sThumbLX; public short sThumbLY; public short sThumbRX; public short sThumbRY;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern int XInputGetState14(int dwUserIndex, out XINPUT_STATE pState);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern int XInputGetState91(int dwUserIndex, out XINPUT_STATE pState);

        const ushort BTN_LB = 0x0100, BTN_RB = 0x0200;
        const ushort BTN_DPAD_UP = 0x0001, BTN_DPAD_DOWN = 0x0002, BTN_DPAD_LEFT = 0x0004, BTN_DPAD_RIGHT = 0x0008;
        const ushort BTN_START = 0x0010, BTN_VIEW = 0x0020, BTN_L3 = 0x0040, BTN_A = 0x1000, BTN_B = 0x2000;

        public static Action<int, int> Nav;
        public static Action<double, double> Stick;   // 左摇杆连续向量，用于丝滑跟随
        public static Action<int> Trigger;   // 1=A-M（L3+视图键）, 2=N-Z（L3+菜单键）
        public static Action<int> Page;      // 肩键翻页
        public static Action Commit;
        public static Action Cancel;
        public static volatile bool Connected;

        static int GetState(int i, out XINPUT_STATE st)
        {
            try { return XInputGetState14(i, out st); }
            catch { try { return XInputGetState91(i, out st); } catch { st = new XINPUT_STATE(); return 1; } }
        }

        public static void Start()
        {
            var th = new System.Threading.Thread(Loop);
            th.IsBackground = true; th.Name = "gamepad"; th.Start();
            Log.W("gamepad thread started");
        }

        static void Loop()
        {
            int lastDx = 0, lastDy = 0, nextDirAt = 0;
            bool comboViewArm = true, comboStartArm = true, aWas = false, bWas = false, wasOn = false, lbWas = false, rbWas = false;
            while (true)
            {
                System.Threading.Thread.Sleep(60);
                try
                {
                    XINPUT_STATE st = new XINPUT_STATE(); bool any = false;
                    for (int i = 0; i < 4; i++) { if (GetState(i, out st) == 0) { any = true; break; } }
                    if (any != wasOn) { wasOn = any; Log.W("gamepad " + (any ? "connected" : "disconnected")); }
                    Connected = any;
                    if (!any) { lastDx = 0; lastDy = 0; continue; }

                    ushort b = st.Gamepad.wButtons;
                    bool l3 = (b & BTN_L3) != 0;
                    bool comboView = l3 && (b & BTN_VIEW) != 0;
                    bool comboStart = l3 && (b & BTN_START) != 0;
                    if (comboView)
                    {
                        if (comboViewArm) { comboViewArm = false; var tr = Trigger; if (tr != null) tr(1); }
                    }
                    else comboViewArm = true;
                    if (comboStart)
                    {
                        if (comboStartArm) { comboStartArm = false; var tr = Trigger; if (tr != null) tr(2); }
                    }
                    else comboStartArm = true;

                    // 左摇杆：输出连续向量给 UI 做丝滑跟随
                    double sxv = st.Gamepad.sThumbLX / 32767.0, syv = st.Gamepad.sThumbLY / 32767.0;
                    double mag2 = Math.Sqrt(sxv * sxv + syv * syv);
                    if (mag2 > 0.22)
                    {
                        var sk = Stick;
                        if (sk != null) sk(sxv, -syv);
                    }

                    // 十字键 = 方向，原理与 WASD 一致（组合键出斜向）
                    int ddx = 0, ddy = 0;
                    if ((b & BTN_DPAD_RIGHT) != 0) ddx += 1;
                    if ((b & BTN_DPAD_LEFT) != 0) ddx -= 1;
                    if ((b & BTN_DPAD_DOWN) != 0) ddy += 1;
                    if ((b & BTN_DPAD_UP) != 0) ddy -= 1;
                    if (ddx != 0 || ddy != 0)
                    {
                        bool changed = ddx != lastDx || ddy != lastDy;
                        if (changed || Environment.TickCount >= nextDirAt)
                        {
                            lastDx = ddx; lastDy = ddy; nextDirAt = Environment.TickCount + 260;
                            var n = Nav;
                            if (n != null) n(ddx, ddy);
                        }
                    }
                    else { lastDx = 0; lastDy = 0; }

                    bool lb = (b & BTN_LB) != 0, rb = (b & BTN_RB) != 0;
                    if (lb && !lbWas) { var pg = Page; if (pg != null) pg(-1); }
                    if (rb && !rbWas) { var pg = Page; if (pg != null) pg(1); }
                    lbWas = lb; rbWas = rb;
                    bool a = (b & BTN_A) != 0, bb = (b & BTN_B) != 0;
                    if (a && !aWas) { var c = Commit; if (c != null) c(); }
                    if (bb && !bWas) { var x = Cancel; if (x != null) x(); }
                    aWas = a; bWas = bb;
                }
                catch { }
            }
        }
    }
    // ===== 方格面板：按首字母分行，适合“看得清、点得准” =====
    public class GridWindow : Window
    {
        public List<AppEntry> Items = new List<AppEntry>();
        public List<Ring> Rings { get { return new List<Ring>(); } }
        public double MaxRadius { get { return 0; } }
        public AppEntry Hovered;
        public bool HubSelected { get { return gearSel; } }
        public double WinScale { get { return winScale; } }

        internal bool gearSel;

        double winScale = 1;
        Canvas cv;
        TextBlock tip;
        Border tipBar;
        readonly List<Border> tiles = new List<Border>();
        readonly List<TextBlock> caps = new List<TextBlock>();
        readonly List<double> tcx = new List<double>();
        readonly List<double> tcy = new List<double>();
        List<List<KeyValuePair<char, List<AppEntry>>>> gPages = new List<List<KeyValuePair<char, List<AppEntry>>>>();
        int gPage = 0;
        double lastMaxW = 900, lastMaxH = 800;
        double tW = 82, tH = 96, gapX = 10, gapY = 10, chipW = 26, padL = 18, padT = 16;
        double contentW, contentH;
        double curX, curY;
        string caption = "";

        public GridWindow(string title)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            caption = title;
        }

        public void Build(List<AppEntry> list, double maxW, double maxH)
        {
            Items = list;
            lastMaxW = maxW; lastMaxH = maxH;
            var groups = new List<KeyValuePair<char, List<AppEntry>>>();
            foreach (var g in list.GroupBy(a => a.Initial == '#' ? '[' : a.Initial).OrderBy(g2 => g2.Key))
                groups.Add(new KeyValuePair<char, List<AppEntry>>(g.Key, g.ToList()));

            int rowsPerCol0 = Math.Max(1, (int)Math.Floor((maxH - 90) / (88 + 10)));
            int maxTiles = Math.Max(1, (int)Math.Floor((maxW - padL * 2 - 24) / (74 + 8)));
            var rows = new List<KeyValuePair<char, List<AppEntry>>>();
            foreach (var kv in groups)
            {
                for (int si = 0; si < kv.Value.Count; si += maxTiles)
                {
                    int cn = Math.Min(maxTiles, kv.Value.Count - si);
                    rows.Add(new KeyValuePair<char, List<AppEntry>>(kv.Key, kv.Value.GetRange(si, cn)));
                }
            }
            if (rows.Count == 0) rows.Add(new KeyValuePair<char, List<AppEntry>>('#', new List<AppEntry>()));
            int perPage = Math.Max(1, rowsPerCol0 * 3);
            gPages.Clear();
            for (int i = 0; i < rows.Count; i += perPage)
                gPages.Add(rows.GetRange(i, Math.Min(perPage, rows.Count - i)));
            gPage = 0;
            DrawGroups(gPages[0], maxW, maxH);
        }

        public int PageCount { get { return gPages.Count; } }
        public int PageIndex { get { return gPage; } }

        public void SetPage(int p)
        {
            if (p < 0 || p >= gPages.Count) return;
            gPage = p;
            DrawGroups(gPages[p], lastMaxW, lastMaxH);
        }

        void DrawGroups(List<KeyValuePair<char, List<AppEntry>>> groups, double maxW, double maxH)
        {
            tiles.Clear(); caps.Clear(); tcx.Clear(); tcy.Clear();
            cv = new Canvas();
            Content = cv;

            tW = 74; tH = 88; gapX = 8; gapY = 10; chipW = 18;
            int rowsPerCol = Math.Max(1, (int)Math.Floor((maxH - 70) / (tH + gapY)));
            var cols = new List<List<KeyValuePair<char, List<AppEntry>>>>();
            var curCol = new List<KeyValuePair<char, List<AppEntry>>>();
            foreach (var kv in groups)
            {
                if (curCol.Count >= rowsPerCol) { cols.Add(curCol); curCol = new List<KeyValuePair<char, List<AppEntry>>>(); }
                curCol.Add(kv);
            }
            if (curCol.Count > 0) cols.Add(curCol);

            var colWs = new List<double>();
            double totalW = 0;
            foreach (var c0 in cols)
            {
                double w0 = 0;
                foreach (var kv in c0) w0 = Math.Max(w0, chipW + kv.Value.Count * (tW + gapX));
                colWs.Add(w0);
                totalW += w0 + 18;
            }
            double maxRows = 0;
            foreach (var c0 in cols) if (c0.Count > maxRows) maxRows = c0.Count;
            contentW = padL * 2 + totalW;
            contentH = padT * 2 + 26 + maxRows * (tH + gapY);
            if (contentW > maxW)
            {
                double f = (maxW - padL * 2) / totalW;
                tW *= f; tH *= f; chipW *= f;
                contentW = maxW;
                contentH = padT * 2 + 26 + maxRows * (tH + gapY);
            }
            Width = contentW;
            Height = contentH;

            var bg = new Border();
            bg.Width = contentW; bg.Height = contentH;
            bg.CornerRadius = new CornerRadius(16);
            bg.Background = new LinearGradientBrush(Ui.BgCol, Ui.Mix(Ui.BgCol, Ui.CardCol, 0.9), 90);
            bg.BorderBrush = new SolidColorBrush(Ui.LineCol);
            bg.BorderThickness = new Thickness(1);
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0);
            cv.Children.Add(bg);

            var head = new TextBlock();
            head.Text = caption + (gPages.Count > 1 ? "   " + (gPage + 1) + "/" + gPages.Count : "");
            head.FontSize = 14.5;
            head.Foreground = new SolidColorBrush(Ui.InkCol);
            head.FontWeight = FontWeights.SemiBold;
            Canvas.SetLeft(head, padL); Canvas.SetTop(head, 8);
            cv.Children.Add(head);

            double baseX = padL;
            for (int ci = 0; ci < cols.Count; ci++)
            {
                double y = padT + 24;
                foreach (var kv in cols[ci])
                {
                    double x = baseX;
                    var lt = new TextBlock();
                    lt.Text = kv.Key == '[' ? "#" : kv.Key.ToString();
                    lt.FontSize = 11.5;
                    lt.Foreground = new SolidColorBrush(Ui.SubCol);
                    Canvas.SetLeft(lt, x + 1); Canvas.SetTop(lt, y + tH / 2 - 8);
                    cv.Children.Add(lt);
                    x += chipW;
                    foreach (var it in kv.Value)
                    {
                        var bd = new Border();
                        bd.Width = tW; bd.Height = tH;
                        bd.CornerRadius = new CornerRadius(10);
                        bd.Background = new SolidColorBrush(Ui.Mix(Ui.CardCol, Ui.BgCol, 0.35));
                        bd.BorderThickness = new Thickness(1);
                        bd.BorderBrush = new SolidColorBrush(Ui.LineCol);
                        var sp = new StackPanel();
                        sp.VerticalAlignment = VerticalAlignment.Center;
                        var img = new Image();
                        double isz = Math.Min(40, tW - 18);
                        img.Width = isz; img.Height = isz;
                        img.Source = Icons.Get(it.Path);
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        var nm = new TextBlock();
                        nm.Text = it.Name;
                        nm.FontSize = 10.5;
                        nm.Margin = new Thickness(3, 3, 3, 0);
                        nm.TextAlignment = TextAlignment.Center;
                        nm.TextTrimming = TextTrimming.CharacterEllipsis;
                        nm.Foreground = new SolidColorBrush(Ui.InkCol);
                        sp.Children.Add(img);
                        sp.Children.Add(nm);
                        bd.Child = sp;
                        bd.Tag = it;
                        Canvas.SetLeft(bd, x); Canvas.SetTop(bd, y);
                        cv.Children.Add(bd);
                        tiles.Add(bd);
                        tcx.Add(x + tW / 2); tcy.Add(y + tH / 2);
                        x += tW + gapX;
                    }
                    y += tH + gapY;
                }
                baseX += colWs[ci] + 18;
            }

            tipBar = new Border();
            tipBar.CornerRadius = new CornerRadius(8);
            tipBar.Background = new SolidColorBrush(Ui.Mix(Ui.LineCol, Ui.BgCol, 0.55));
            tipBar.Padding = new Thickness(10, 4, 10, 4);
            tipBar.Visibility = Visibility.Collapsed;
            tip = new TextBlock();
            tip.FontSize = 12.5;
            tip.Foreground = new SolidColorBrush(Ui.InkCol);
            tipBar.Child = tip;
            cv.Children.Add(tipBar);

            gear = new Border();
            gear.Width = 36; gear.Height = 36;
            gear.CornerRadius = new CornerRadius(9);
            gear.Background = new SolidColorBrush(Ui.Mix(Ui.CardCol, Ui.BgCol, 0.35));
            gear.BorderThickness = new Thickness(1);
            gear.BorderBrush = new SolidColorBrush(Ui.LineCol);
            var gt = new TextBlock();
            gt.Text = "\u2699";
            gt.FontSize = 18;
            gt.HorizontalAlignment = HorizontalAlignment.Center;
            gt.VerticalAlignment = VerticalAlignment.Center;
            gt.Foreground = new SolidColorBrush(Ui.SubCol);
            gear.Child = gt;
            Canvas.SetLeft(gear, contentW - 50); Canvas.SetTop(gear, contentH - 50);
            cv.Children.Add(gear);

            curX = contentW / 2; curY = contentH / 2;
        }


        public void PlaceAt(int sx, int sy, double scale)
        {
            winScale = scale;
            Left = (sx / scale) - contentW / 2;
            Top = (sy / scale) - contentH / 2;
            var wa = WF.Screen.FromPoint(new System.Drawing.Point(sx, sy)).WorkingArea;
            double l = Left * scale, t = Top * scale, w = contentW * scale, h = contentH * scale;
            if (l < wa.Left + 4) Left = (wa.Left + 4) / scale;
            if (t < wa.Top + 4) Top = (wa.Top + 4) / scale;
            if (l + w > wa.Right - 4) Left = (wa.Right - 4 - w) / scale;
            if (t + h > wa.Bottom - 4) Top = (wa.Bottom - 4 - h) / scale;
        }

        public bool HitGear(int sx, int sy, double scale)
        {
            double x = sx / scale - Left;
            double y = sy / scale - Top;
            return x >= contentW - 56 && y >= contentH - 56;
        }

        void GearHot(bool on)
        {
            if (gear == null) return;
            gear.Background = new SolidColorBrush(on ? Ui.Mix(Ui.CardCol, Ui.AccentCol, 0.30) : Ui.Mix(Ui.CardCol, Ui.BgCol, 0.35));
            gear.BorderBrush = new SolidColorBrush(on ? Ui.AccentCol : Ui.LineCol);
        }

        // 键盘/手柄把「设置」齿轮当普通目标选中
        public void GearSelect()
        {
            gearSel = true;
            GearHot(true);
            ApplyHit(-1);
            Hovered = null;
            tip.Text = "设置";
            tipBar.Visibility = Visibility.Visible;
            tipBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tipBar, contentW / 2 - tipBar.DesiredSize.Width / 2);
            Canvas.SetTop(tipBar, contentH - tipBar.DesiredSize.Height - 48);
            try { Click.Tick(0); } catch { }
        }

        public void UpdateHover(int screenX, int screenY, double scale)
        {
            if (HitGear(screenX, screenY, scale)) { gearSel = true; GearHot(true); ApplyHit(-1); tip.Text = "设置"; tipBar.Visibility = Visibility.Visible; tipBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); Canvas.SetLeft(tipBar, contentW / 2 - tipBar.DesiredSize.Width / 2); Canvas.SetTop(tipBar, contentH - tipBar.DesiredSize.Height - 48); return; }
            gearSel = false;
            GearHot(false);
            double x = screenX / scale - Left;
            double y = screenY / scale - Top;
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < tiles.Count; i++)
            {
                double dx = Math.Abs(x - tcx[i]), dy = Math.Abs(y - tcy[i]);
                if (dx <= tW / 2 + 6 && dy <= tH / 2 + 6)
                {
                    double d = dx + dy;
                    if (d < bd) { bd = d; best = i; }
                }
            }
            if (best < 0 && clampMode && tiles.Count > 0) { double nb = double.MaxValue; for (int qi = 0; qi < tiles.Count; qi++) { double qx = x - tcx[qi], qy = y - tcy[qi]; double qd = qx * qx + qy * qy; if (qd < nb) { nb = qd; best = qi; } } }
            ApplyHit(best);
        }

        int lastIdx = -1;
        internal bool clampMode;
        Border gear;
        void ApplyHit(int idx)
        {
            if (idx >= 0) { gearSel = false; GearHot(false); }
            if (idx == lastIdx) return;
            lastIdx = idx;
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].Background = new SolidColorBrush(i == idx ? Ui.Mix(Ui.CardCol, Ui.AccentCol, 0.30) : Ui.Mix(Ui.CardCol, Ui.BgCol, 0.35));
                tiles[i].BorderBrush = new SolidColorBrush(i == idx ? Ui.AccentCol : Ui.LineCol);
            }
            if (idx >= 0 && idx < tiles.Count)
            {
                Hovered = (AppEntry)tiles[idx].Tag;
                tip.Text = Hovered.Name;
                tipBar.Visibility = Visibility.Visible;
                tipBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tipBar, contentW / 2 - tipBar.DesiredSize.Width / 2);
                Canvas.SetTop(tipBar, contentH - tipBar.DesiredSize.Height - 6);
                var c = tcx[idx]; var cyy = tcy[idx];
                curX = c; curY = cyy;
                try { Click.Tick(0); } catch { }
            }
            else
            {
                Hovered = null;
                tipBar.Visibility = Visibility.Collapsed;
            }
        }

        public void HoverItem(AppEntry e) { int i = Items.IndexOf(e); if (i >= 0) ApplyHit(i); }

        public void Move(int dx, int dy)
        {
            int cur = Hovered == null ? -1 : Items.IndexOf(Hovered);
            bool fromGear = gearSel;
            bool fresh = cur < 0 && !fromGear;
            double ox, oy;
            if (fromGear) { ox = contentW - 32; oy = contentH - 32; }
            else if (cur < 0) { ox = contentW / 2; oy = contentH / 2; }
            else { ox = tcx[cur]; oy = tcy[cur]; }


            double minDot = fresh ? 0.30 : 0.72;
            double gearDot = fresh ? 0.30 : 0.60;
            double bs = double.MaxValue; int bi = -1; bool bGear = false;

            for (int pass = 0; pass < 2; pass++)
            {
                double md = pass == 0 ? minDot : 0.45;
                double gd = pass == 0 ? gearDot : 0.40;
                for (int i = 0; i < Items.Count; i++)
                {
                    if (i == cur) continue;
                    double ux = tcx[i] - ox, uy = tcy[i] - oy;
                    double l = Math.Sqrt(ux * ux + uy * uy); if (l < 1) continue;
                    double dot = (ux * dx + uy * dy) / l;
                    if (dot < md) continue;
                    double s = fresh ? l / (dot * dot) : l / (dot * dot * dot);
                    if (s < bs) { bs = s; bi = i; bGear = false; }
                }
                if (!fromGear)
                {
                    double ux = (contentW - 32) - ox, uy = (contentH - 32) - oy;
                    double l = Math.Sqrt(ux * ux + uy * uy);
                    if (l > 1)
                    {
                        double dot = (ux * dx + uy * dy) / l;
                        if (dot >= gd)
                        {
                            double s = fresh ? l / (dot * dot) : l / (dot * dot * dot);
                            if (s < bs) { bs = s; bGear = true; bi = -1; }
                        }
                    }
                }
                if (bGear || bi >= 0) break;
            }
            if (!bGear && bi < 0 && dx > 0 && dy > 0) { GearSelect(); return; }   // 右下方向且无其它候选 -> 选中设置

            if (bGear) { GearSelect(); return; }
            if (bi >= 0) ApplyHit(bi);
        }

        public void CursorMove(double nx, double ny)
        {
            double mag = Math.Sqrt(nx * nx + ny * ny);
            if (mag > 1) { nx /= mag; ny /= mag; mag = 1; }
            double step = 14 + 52 * mag;
            curX += nx * step; curY += ny * step;
            clampMode = true;
            if (tiles.Count > 0)
            {
                double mnX = double.MaxValue, mxX = double.MinValue, mnY = double.MaxValue, mxY = double.MinValue;
                for (int ci = 0; ci < tiles.Count; ci++)
                {
                    if (tcx[ci] < mnX) mnX = tcx[ci];
                    if (tcx[ci] > mxX) mxX = tcx[ci];
                    if (tcy[ci] < mnY) mnY = tcy[ci];
                    if (tcy[ci] > mxY) mxY = tcy[ci];
                }
                if (curX < mnX) curX = mnX; if (curX > mxX) curX = mxX;
                if (curY < mnY) curY = mnY; if (curY > mxY) curY = mxY;
            }
            else
            {
                if (curX < 4) curX = 4; if (curX > contentW - 4) curX = contentW - 4;
                if (curY < 4) curY = 4; if (curY > contentH - 4) curY = contentH - 4;
            }
            UpdateHover((int)Math.Round((Left + curX) * winScale), (int)Math.Round((Top + curY) * winScale), winScale);
        }

        public AppEntry Commit() { var h = Hovered; Hovered = null; return h; }

        public AppEntry CommitAt(int sx, int sy, double scale) { UpdateHover(sx, sy, scale); return Commit(); }
    }

    public static class Launcher
    {
        public static void Run(AppEntry e)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { RunNow(e); });
        }

        static void RunNow(AppEntry e)
        {
            try
            {
                Log.W("launch name=" + e.Name + " path=" + e.Path + " args=" + e.Args);
                if (Directory.Exists(e.Path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + e.Path + "\"") { UseShellExecute = true });
                    return;
                }
                var psi = new ProcessStartInfo(e.Path);
                if (!string.IsNullOrEmpty(e.Args)) psi.Arguments = e.Args;
                psi.WorkingDirectory = System.IO.Path.GetDirectoryName(e.Path);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Log.W("launch failed: " + ex.Message); }
        }
    }

    public static class Native
    {
        public const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
        public const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;
        public const uint LLMHF_INJECTED = 0x1, LLKHF_INJECTED = 0x20;
        public const int VK_ESCAPE = 0x1B;
        public const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
        public const uint XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;
        public const uint MARK = 0x57574545;

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; public uint lPrivate; }

        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public static void KeyTap(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr extraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extraInfo; }

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public static void XClick(uint button)
        {
            uint data = button == XBUTTON2 ? 0x0002u : 0x0001u;
            mouse_event(MOUSEEVENTF_XDOWN, 0, 0, data, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_XUP, 0, 0, data, UIntPtr.Zero);
        }
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;
    }

    public static class Click
    {
        public static bool Enabled = true;

        const int Rate = 48000, Ch = 2, BytesPerSample = 2;
        const int BufMs = 20, BufCount = 4;
        const int BufBytes = Rate * BufMs / 1000 * Ch * BytesPerSample;

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, IntPtr uDeviceID, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);

        static IntPtr hwo = IntPtr.Zero;
        static IntPtr[] hdr = new IntPtr[BufCount];
        static GCHandle[] pin = new GCHandle[BufCount];
        static byte[][] buf = new byte[BufCount][];
        static bool[] written = new bool[BufCount];
        static bool started;
        static int flagsOff = -1;
        static System.Threading.Thread feeder;
        static readonly object Life = new object();
        static int lastUse;

        static readonly object Q = new object();
        static readonly Queue<short[]> tones = new Queue<short[]>();
        static short[] cur;
        static int curPos;

        static short[] tLow, tMid, tHigh;
        static int volPct = 100;

        public static void SetVolume(int pct)
        {
            volPct = Math.Max(0, Math.Min(100, pct));
            tLow = tMid = tHigh = null;
        }

        static short[] Tone(int freq, int ms, int amp)
        {
            int frames = Rate * ms / 1000;
            var s = new short[frames * Ch];
            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / Rate;
                double env = Math.Exp(-t * 80.0);
                double body = Math.Sin(2 * Math.PI * freq * t) + 0.30 * Math.Sin(2 * Math.PI * freq * 2.01 * t);
                double val = body / 1.30 * amp * env;
                if (val > 32000) val = 32000; else if (val < -32000) val = -32000;
                short v = (short)val;
                s[i * 2] = v;
                s[i * 2 + 1] = v;
            }
            return s;
        }

        static void MakeTones()
        {
            int amp = (int)(26000.0 * volPct / 100.0);
            if (amp < 1500) amp = 1500;
            tLow = Tone(720, 45, amp);
            tMid = Tone(980, 38, amp);
            tHigh = Tone(1280, 32, amp);
        }

        public static void Close()
        {
            try { ShutdownStream(); } catch { }
        }

        public static void Warm()
        {
            try { Ensure(); } catch (Exception ex) { Log.W("audio warm failed: " + ex.Message); }
        }

        static void Ensure()
        {
            if (started) return;
            lock (Life)
            {
                if (started) return;
                MakeTones();
                var fmt = new WAVEFORMATEX();
                fmt.wFormatTag = 1;
                fmt.nChannels = Ch;
                fmt.nSamplesPerSec = Rate;
                fmt.nAvgBytesPerSec = (uint)(Rate * Ch * BytesPerSample);
                fmt.nBlockAlign = (ushort)(Ch * BytesPerSample);
                fmt.wBitsPerSample = 16;
                fmt.cbSize = 0;
                int rc = waveOutOpen(out hwo, new IntPtr(-1), ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                if (rc != 0) { Log.W("audio open failed rc=" + rc); hwo = IntPtr.Zero; return; }
                int sz = Marshal.SizeOf(typeof(WAVEHDR));
                flagsOff = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
                for (int i = 0; i < BufCount; i++)
                {
                    buf[i] = new byte[BufBytes];
                    pin[i] = GCHandle.Alloc(buf[i], GCHandleType.Pinned);
                    var h = new WAVEHDR();
                    h.lpData = pin[i].AddrOfPinnedObject();
                    h.dwBufferLength = (uint)BufBytes;
                    hdr[i] = Marshal.AllocHGlobal(sz);
                    Marshal.StructureToPtr(h, hdr[i], false);
                    waveOutPrepareHeader(hwo, hdr[i], sz);
                }
                lastUse = Environment.TickCount;
                started = true;
                feeder = new System.Threading.Thread(Feed);
                feeder.IsBackground = true;
                feeder.Priority = System.Threading.ThreadPriority.AboveNormal;
                feeder.Start();
                Log.W("audio stream open ok rate=" + Rate + " buf=" + BufMs + "ms x" + BufCount);
            }
        }

        static void Feed()
        {
            int sz = Marshal.SizeOf(typeof(WAVEHDR));
            while (started)
            {
                if (Environment.TickCount - lastUse > 20000 && Idle())
                {
                    Log.W("audio stream closed (idle)");
                    ShutdownStream();
                    return;
                }
                for (int i = 0; i < BufCount; i++)
                {
                    if (hwo == IntPtr.Zero) break;
                    int fl = Marshal.ReadInt32(hdr[i], flagsOff);
                    bool done = (fl & 1) != 0;
                    if (!written[i] || done)
                    {
                        lock (Q) { Fill(buf[i]); }
                        Marshal.WriteInt32(hdr[i], flagsOff, fl & ~1);
                        waveOutWrite(hwo, hdr[i], sz);
                        written[i] = true;
                    }
                }
                System.Threading.Thread.Sleep(4);
            }
        }

        static bool Idle()
        {
            lock (Q) { return tones.Count == 0 && cur == null; }
        }

        static void ShutdownStream()
        {
            lock (Life)
            {
                if (hwo == IntPtr.Zero) { started = false; return; }
                try { waveOutReset(hwo); } catch { }
                int sz = Marshal.SizeOf(typeof(WAVEHDR));
                for (int i = 0; i < BufCount; i++)
                {
                    if (hdr[i] != IntPtr.Zero)
                    {
                        try { waveOutUnprepareHeader(hwo, hdr[i], sz); } catch { }
                        Marshal.FreeHGlobal(hdr[i]);
                        hdr[i] = IntPtr.Zero;
                    }
                    if (pin[i].IsAllocated) pin[i].Free();
                    written[i] = false;
                }
                try { waveOutClose(hwo); } catch { }
                hwo = IntPtr.Zero;
                started = false;
                lock (Q) { tones.Clear(); cur = null; curPos = 0; }
            }
        }

        static void Fill(byte[] dst)
        {
            int p = 0;
            while (p < dst.Length)
            {
                if (cur == null || curPos >= cur.Length)
                {
                    if (tones.Count == 0) { Array.Clear(dst, p, dst.Length - p); return; }
                    cur = tones.Dequeue();
                    curPos = 0;
                }
                int n = Math.Min(dst.Length - p, (cur.Length - curPos) * 2);
                Buffer.BlockCopy(cur, curPos * 2, dst, p, n);
                curPos += n / 2;
                p += n;
            }
        }

        public static void Tick(int ring)
        {
            if (!Enabled) return;
            try
            {
                lastUse = Environment.TickCount;
                Ensure();
                if (!started) return;
                if (tLow == null) MakeTones();
                short[] tone = ring <= 0 ? tHigh : (ring == 1 ? tMid : tLow);
                lock (Q)
                {
                    while (tones.Count > 2) tones.Dequeue();
                    tones.Enqueue(tone);
                }
            }
            catch { }
        }

        public static void Test()
        {
            if (!Enabled) { Log.W("click test skipped (sound off)"); return; }
            try
            {
                lastUse = Environment.TickCount;
                Ensure();
                if (!started) { Log.W("click test failed (no audio stream)"); return; }
                var th = new System.Threading.Thread(delegate ()
                {
                    try
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            lastUse = Environment.TickCount;
                            lock (Q) { tones.Enqueue(i == 0 ? tHigh : (i == 1 ? tMid : tLow)); }
                            if (i < 2) System.Threading.Thread.Sleep(170);
                        }
                        Log.W("click test played queued=3");
                    }
                    catch (Exception ex2) { Log.W("click test failed: " + ex2.Message); }
                });
                th.IsBackground = true;
                th.Start();
            }
            catch (Exception ex) { Log.W("click test failed: " + ex.Message); }
        }
    }

    public static class Settings
    {
        public static string IniFile { get { return System.IO.Path.Combine(Paths.Root, "settings.ini"); } }

        static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(IniFile))
                    foreach (var l in File.ReadAllLines(IniFile))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) d[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
                    }
            }
            catch { }
            return d;
        }

        static int GetInt(string key, int def)
        {
            var d = Read();
            string v;
            int n;
            if (d.TryGetValue(key, out v) && int.TryParse(v, out n)) return n;
            return def;
        }

        static void Set(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                var d = Read();
                d[key] = value;
                var sb = new StringBuilder();
                foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(IniFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static bool SoundOn
        {
            get { return GetInt("sound", 1) != 0; }
            set { Set("sound", value ? "1" : "0"); Click.Enabled = value; }
        }

        public static int Volume
        {
            get { return Math.Max(0, Math.Min(100, GetInt("volume", 100))); }
            set { Set("volume", Math.Max(0, Math.Min(100, value)).ToString()); }
        }

        public static int LongPress
        {
            get { return Math.Max(250, Math.Min(2000, GetInt("longpress", 650))); }
            set { Set("longpress", Math.Max(250, Math.Min(2000, value)).ToString()); }
        }

        public static bool AutoPause
        {
            get { return GetInt("autopause", 1) != 0; }
            set { Set("autopause", value ? "1" : "0"); }
        }

        public static bool PauseOnStore
        {
            get { return GetInt("pausestore", 0) != 0; }
            set { Set("pausestore", value ? "1" : "0"); }
        }

        public static bool PauseAccel
        {
            get { return GetInt("pauseaccel", 1) != 0; }
            set { Set("pauseaccel", value ? "1" : "0"); }
        }

        public static bool TabHotkey
        {
            get { return GetInt("tabhotkey", 1) != 0; }
            set { Set("tabhotkey", value ? "1" : "0"); }
        }

        public static bool AutoFolders
        {
            get { return GetInt("autofolders", 1) != 0; }
            set { Set("autofolders", value ? "1" : "0"); }
        }

        /// <summary>常用文件夹只在首次运行时播种一次，用户删掉后不再自动加回</summary>
        public static bool FoldersSeeded
        {
            get { return GetInt("foldersseed", 0) != 0; }
            set { Set("foldersseed", value ? "1" : "0"); }
        }

        public static bool FirstRunDone
        {
            get { return GetInt("firstrun", 0) != 0; }
            set { Set("firstrun", value ? "1" : "0"); }
        }

        public static bool DarkUI
        {
            get { return GetInt("theme", 1) != 0; }
            set { Set("theme", value ? "1" : "0"); }
        }

        public static int WheelTheme
        {
            get { return GetInt("wheeltheme", 0); }
            set { Set("wheeltheme", value.ToString()); }
        }

        // 0 = 松开侧键即启动滑到的应用；1 = 只呼出圆盘，需左键点选才启动
        public static int ReleaseMode
        {
            get { return GetInt("release", 0) == 1 ? 1 : 0; }
            set { Set("release", value.ToString()); }
        }

        public static int WheelStyle
        {
            get { return GetInt("wheelstyle", 0); }
            set { Set("wheelstyle", value == 1 ? "1" : "0"); }
        }

        public static int WheelSize
        {
            get
            {
                int v = GetInt("wheelsize", 100);
                if (v < 70) v = 70;
                if (v > 140) v = 140;
                return v;
            }
            set
            {
                int v = value;
                if (v < 70) v = 70;
                if (v > 140) v = 140;
                Set("wheelsize", v.ToString());
            }
        }
    }

    public class MouseHook
    {
        public static bool AcceptInjected = false;
        public static bool TabDown = false;
        public static bool ChordFired = false;
        public static bool ChordUsed = false;
        public static Action<int> Quick;
        public static Action CommitKey;
        public static Action<int, int> KbMove;
        static readonly System.Collections.Generic.HashSet<uint> WasdDown = new System.Collections.Generic.HashSet<uint>();
        public static Action ChordRelease;
        public static Func<bool> WheelOpen;
        public static Func<int, int, bool> LeftClick;
        public static Action<int> PageFlip;      // 滚轮 / 方括号 / 肩键翻页
        public static Action<int> HotkeyFire;
        public static System.Windows.Threading.Dispatcher UiDispatcher;
        static volatile bool wheelShown;
        static int wl, wt, wr, wb;
        System.Threading.Thread pump;
        volatile bool wantKeyboard;
        uint pumpTid;
        const uint WM_APP_APPLY_KEYS = 0x8001;

        public static void SetWheelRect(bool shown, int l, int t, int r, int b)
        {
            wl = l; wt = t; wr = r; wb = b; wheelShown = shown;
        }
        int _mvCount = 0;

        Native.HookProc mouseProc, keyProc;
        IntPtr mouseHook = IntPtr.Zero, keyHook = IntPtr.Zero;
        public event Action<int, int, int> SideDown;   // button, x, y
        public event Action<int, int, int> SideUp;     // button, x, y
        public event Action<int, int> Move;
        public event Action Escape;

        public bool Ok { get { return mouseHook != IntPtr.Zero; } }

        public void SetKeyboard(bool on)
        {
            wantKeyboard = on;
            try { if (pumpTid != 0) Native.PostThreadMessage(pumpTid, WM_APP_APPLY_KEYS, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        void ApplyKeyboard()
        {
            try
            {
                IntPtr mod = Native.GetModuleHandle(null);
                if (wantKeyboard && keyHook == IntPtr.Zero)
                {
                    keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
                    Log.W("keyboard hook on=" + keyHook);
                }
                else if (!wantKeyboard && keyHook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(keyHook);
                    keyHook = IntPtr.Zero;
                    Log.W("keyboard hook off");
                }
            }
            catch (Exception ex) { Log.W("keyboard hook failed: " + ex.Message); }
        }

        void PumpLoop()
        {
            mouseProc = MouseCallback;
            keyProc = KeyCallback;
            IntPtr mod = Native.GetModuleHandle(null);
            pumpTid = Native.GetCurrentThreadId();
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            if (mouseHook == IntPtr.Zero)
            {
                System.Threading.Thread.Sleep(300);
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            }
            if (wantKeyboard) keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            Log.W("hooks installed on pump thread mouse=" + mouseHook + " key=" + keyHook);
            if (mouseHook == IntPtr.Zero) Log.W("HOOK INSTALL FAILED - side button will not work");
            while (true)
            {
                Native.MSG m;
                int r = Native.GetMessage(out m, IntPtr.Zero, 0, 0);
                if (r == 0 || r == -1) break;
                if (m.message == WM_APP_APPLY_KEYS) { ApplyKeyboard(); continue; }
                Native.TranslateMessage(ref m);
                Native.DispatchMessage(ref m);
            }
            Log.W("pump loop exited");
        }

        public void Start(bool keyboardHotkey)
        {
            wantKeyboard = keyboardHotkey;
            if (pump != null) { SetKeyboard(keyboardHotkey); return; }
            pump = new System.Threading.Thread(PumpLoop);
            pump.IsBackground = true;
            pump.Start();
            for (int i = 0; i < 120 && mouseHook == IntPtr.Zero; i++) System.Threading.Thread.Sleep(20);
        }

        public void Install(bool keyboardHotkey)
        {
            Start(keyboardHotkey);
        }


        IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (GameWatch.Suspended) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                if (nCode >= 0)
                {
                    var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (AcceptInjected || (info.flags & Native.LLMHF_INJECTED) == 0)
                    {
                        int msg = wParam.ToInt32();
                        if (msg == 0x020A && wheelShown && PageFlip != null && info.pt.x >= wl && info.pt.x <= wr && info.pt.y >= wt && info.pt.y <= wb)
                        {
                            int wd = (short)((info.mouseData >> 16) & 0xFFFF);
                            int dir = wd > 0 ? 1 : -1;
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { var pf = PageFlip; if (pf != null) { try { pf(dir); } catch { } } }));
                            return (IntPtr)1;
                        }
                        if (msg == 0x0201 && LeftClick != null && wheelShown && info.pt.x >= wl && info.pt.x <= wr && info.pt.y >= wt && info.pt.y <= wb)
                        {
                            int lx = info.pt.x, ly = info.pt.y;
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { try { LeftClick(lx, ly); } catch { } }));
                            return (IntPtr)1;
                        }
                        if (msg == Native.WM_XBUTTONDOWN)
                        {
                            int btn = (int)((info.mouseData >> 16) & 0xFFFF);
                            int px = info.pt.x, py = info.pt.y, bb = btn;
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { var h = SideDown; if (h != null) h(bb, px, py); }));
                            return (IntPtr)1;
                        }
                        if (msg == Native.WM_XBUTTONUP)
                        {
                            int btn = (int)((info.mouseData >> 16) & 0xFFFF);
                            int px2 = info.pt.x, py2 = info.pt.y, bb2 = btn;
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { var h = SideUp; if (h != null) h(bb2, px2, py2); }));
                            return (IntPtr)1;
                        }

                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (GameWatch.Suspended)
                {
                    TabDown = false; ChordFired = false; ChordUsed = false;
                    return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }
                int msg = wParam.ToInt32();
                if (nCode >= 0 && (msg == 0x0100 || msg == 0x0104))
                {
                    var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                    if (AcceptInjected || !injected)
                    {
                        uint vk = k.vkCode;
                        if ((vk == 0x41 || vk == 0x42)
                            && (Native.GetAsyncKeyState(0x11) & 0x8000) != 0
                            && (Native.GetAsyncKeyState(0x12) & 0x8000) != 0
                            && (Native.GetAsyncKeyState(0x10) & 0x8000) == 0)
                        {
                            var hf = HotkeyFire;
                            if (hf != null)
                            {
                                int which = vk == 0x41 ? 1 : 2;
                                Log.W("hotkey hook ctrl+alt+" + ((char)vk));
                                UiDispatcher.BeginInvoke(new Action(delegate { try { hf(which); } catch (Exception ex) { Log.W("hook hotkey failed: " + ex.Message); } }));
                                return (IntPtr)1;
                            }
                        }
                        bool open = WheelOpen != null && WheelOpen();
                        if (vk == 0x09)
                        {
                            // Alt/Ctrl/Shift/Win + Tab 一律放行，否则 Alt+Tab 切窗口会被打断
                            bool mods = (Native.GetAsyncKeyState(0x12) & 0x8000) != 0
                                     || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0
                                     || (Native.GetAsyncKeyState(0x10) & 0x8000) != 0
                                     || (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0
                                     || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0;
                            if (mods)
                            {
                                TabDown = false; ChordFired = false;
                                return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                            }
                            if (TabDown)
                            {
                                if (!AcceptInjected) Native.KeyTap(0x09);
                                return (IntPtr)1;
                            }
                            TabDown = true;
                            ChordFired = false;
                            ChordUsed = false;
                            return (IntPtr)1;
                        }
                        else if (open && (vk == 0x57 || vk == 0x53 || vk == 0x44 || vk == 0x41))
                        {
                            WasdDown.Add(vk);
                            int ndx = 0, ndy = 0;
                            if (WasdDown.Contains(0x44)) ndx += 1;
                            if (WasdDown.Contains(0x41)) ndx -= 1;
                            if (WasdDown.Contains(0x53)) ndy += 1;
                            if (WasdDown.Contains(0x57)) ndy -= 1;
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { var kb = KbMove; if (kb != null) kb(ndx, ndy); }));
                            return (IntPtr)1;
                        }
                        else if (open && vk == 0x42)
                        {
                            // 方格/圆形面板通用：B 键取消
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { var h = Escape; if (h != null) h(); }));
                            return (IntPtr)1;
                        }
                        else if (open && (vk == 0xDB || vk == 0xDD))
                        {
                            int d3 = vk == 0xDD ? 1 : -1;
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate { var pf = PageFlip; if (pf != null) { try { pf(d3); } catch { } } }));
                            return (IntPtr)1;
                        }
                        else if (vk == Native.VK_ESCAPE)
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { var h = Escape; if (h != null) h(); }));
                            if (open) return (IntPtr)1;
                        }
                        else if (TabDown && (vk == 0x41 || vk == 0x42))
                        {
                            int which = vk == 0x41 ? 1 : 2;
                            ChordFired = true;
                            ChordUsed = true;
                            TabDown = false;   // 用掉这次 Tab，避免 TabDown 卡死导致日后误触发
                            if (open) { Log.W("hotkey tab+" + (which == 1 ? "A" : "B") + " ignored (wheel already open)"); return (IntPtr)1; }
                            Log.W("hotkey tab+" + (which == 1 ? "A" : "B"));
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { var q = Quick; if (q != null) q(which); }));
                            return (IntPtr)1;
                        }
                        else if (vk == 0x0D && open)
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { var c = CommitKey; if (c != null) c(); }));
                            return (IntPtr)1;
                        }
                    }
                }
                else if (nCode >= 0 && msg == 0x0101)
                {
                    var k2 = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    WasdDown.Remove(k2.vkCode);
                    if ((k2.vkCode == 0x41 || k2.vkCode == 0x42) && ChordFired)
                    {
                        // 松开 A/B 就相当于「松手」：启动当前悬停的应用
                        ChordFired = false;
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => { var c = ChordRelease; if (c != null) c(); }));
                        return (IntPtr)1;
                    }
                    if (k2.vkCode == 0x09 && TabDown)
                    {
                        TabDown = false;
                        if (!ChordFired && !ChordUsed && !AcceptInjected) Native.KeyTap(0x09);
                        ChordUsed = false;
                        return (IntPtr)1;
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    public class Ring
    {
        public char Letter;
        public int Part;
        public int Index;
        public double Radius;
        public int Capacity;
        public List<AppEntry> Items = new List<AppEntry>();
    }

    public static class WheelSkin
    {
        public static readonly string[] Names = { "轮回眼 · 紫", "鎏金 · 琥珀", "青霜 · 冰蓝", "极简 · 雾白", "赛博 · 品红", "松影 · 青绿" };
        public static int Index
        {
            get
            {
                int v = Settings.WheelTheme;
                if (v < 0 || v >= Names.Length) v = 0;
                return v;
            }
        }

        static byte[] Acc
        {
            get
            {
                switch (Index)
                {
                    case 1: return new byte[] { 224, 176, 88 };
                    case 2: return new byte[] { 92, 190, 218 };
                    case 3: return new byte[] { 214, 214, 222 };
                    case 4: return new byte[] { 236, 96, 180 };
                    case 5: return new byte[] { 104, 198, 150 };
                    default: return new byte[] { 132, 112, 236 };
                }
            }
        }

        static byte[] Hi
        {
            get
            {
                switch (Index)
                {
                    case 1: return new byte[] { 250, 216, 150 };
                    case 2: return new byte[] { 156, 228, 246 };
                    case 3: return new byte[] { 255, 255, 255 };
                    case 4: return new byte[] { 255, 158, 216 };
                    case 5: return new byte[] { 158, 232, 196 };
                    default: return new byte[] { 186, 170, 255 };
                }
            }
        }

        static byte[] HubBg
        {
            get
            {
                switch (Index)
                {
                    case 1: return new byte[] { 44, 34, 18 };
                    case 2: return new byte[] { 14, 32, 44 };
                    case 3: return new byte[] { 30, 30, 36 };
                    case 4: return new byte[] { 42, 16, 34 };
                    case 5: return new byte[] { 14, 36, 28 };
                    default: return new byte[] { 28, 22, 46 };
                }
            }
        }

        public static Color A(int a) { var c = Acc; return Color.FromArgb((byte)a, c[0], c[1], c[2]); }
        public static Color H(int a) { var c = Hi; return Color.FromArgb((byte)a, c[0], c[1], c[2]); }
        public static Color Disc(int a, int lift)
        {
            var c = HubBg;
            int r = c[0] + lift, g = c[1] + lift, b = c[2] + lift;
            if (r > 255) r = 255; if (g > 255) g = 255; if (b > 255) b = 255;
            return Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
        }

        public static string Describe { get { return Names[Index]; } }
    }

    public class WheelWindow : Window
    {
        public static bool ShotMode;

        public static readonly double IconGap = 4;
        public static double RingOffset(int i) { return i * 137.508 * Math.PI / 180.0; }

        readonly Canvas root = new Canvas();
        Ellipse highlight;
        TextBlock centerText;
        TextBlock subText;
        public double cx, cy;
        public double hubRadius; double winScale = 1; Ellipse hubHotRing;         public bool HubSelected { get; private set; }
        public double AngleDegRing(Ring ring, int idx)
        {
            double a = -90 + RingOffset(ring.Index) * 180.0 / Math.PI + 360.0 * idx / ring.Capacity;
            a = a % 360; if (a < 0) a += 360;
            return a;
        }
        public bool TryPos(AppEntry hit, out double px, out double py)
        {
            px = 0; py = 0;
            if (hit == null) return false;
            for (int i = 0; i < rings.Count; i++)
            {
                int idx = rings[i].Items.IndexOf(hit);
                if (idx < 0) continue;
                double a = -Math.PI / 2 + RingOffset(rings[i].Index) + 2 * Math.PI * idx / rings[i].Capacity;
                px = rings[i].Radius * Math.Cos(a);
                py = rings[i].Radius * Math.Sin(a);
                return true;
            }
            return false;
        }
        public void HoverItem(AppEntry hit)
        {
            double px, py;
            if (!TryPos(hit, out px, out py)) return;
            UpdateHover((int)Math.Round((Left + cx + px) * winScale), (int)Math.Round((Top + cy + py) * winScale), winScale);
        }

        // 键盘/手柄把中心「设置」选中
        public void HoverHub()
        {
            UpdateHover((int)Math.Round((Left + cx) * winScale), (int)Math.Round((Top + cy) * winScale), winScale);
        }
        public List<AppEntry> AllItems()
        {
            var list = new List<AppEntry>();
            foreach (var ring in rings) list.AddRange(ring.Items);
            return list;
        }public double WinScale { get { return winScale; } }
        public double MaxRadius { get { double m = 0; foreach (var r in rings) if (r.Radius > m) m = r.Radius; return m + iconSize * 0.6; } }
        Border bottomBar;
        TextBlock bottomText;
        List<Ring> rings = new List<Ring>();
        double iconSize, hubR = 60, r0, spacing, winSize;
        string hint = "侧键拖动";
        AppEntry hovered; AppEntry lastHit; DateTime lastHitAt; double reach;

        public WheelWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Cursor = Cursors.Arrow;
            root.IsHitTestVisible = false;
            Content = root;
            Ui.CrispWheel(this);
        }

        public void SetHint(string s) { hint = s; centerText.Text = s; }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(h, -20);
                Native.SetWindowLong(h, -20, ex | 0x08000000 | 0x00000080);
            }
            catch { }
        }

        public void SetLayout(List<Ring> layout, double icon, double hubR, double r0v, string hintText, string tipText = null)
        {
            rings = layout; iconSize = icon; spacing = 0; hubRadius = hubR;
            double maxR = r0v;
            foreach (var rr in rings) if (rr.Radius > maxR) maxR = rr.Radius;
            r0 = maxR;
            winSize = (maxR + icon + 14) * 2;
            cx = winSize / 2; cy = winSize / 2;
            Width = winSize; Height = winSize;
            root.Width = winSize; root.Height = winSize;
            root.Children.Clear();
            hovered = null; hint = hintText;

            var dim = new Ellipse
            {
                Width = winSize, Height = winSize,
                Fill = new RadialGradientBrush(WheelSkin.Disc(238, 6), WheelSkin.Disc(250, 0))
            };
            root.Children.Add(dim);

            double hubRad = hubR;
            double decoR = 0;
            foreach (var rr in rings) if (rr.Radius > decoR) decoR = rr.Radius;
            int decoN = 9;
            for (int d = 1; d <= decoN; d++)
            {
                double rd = hubRad * 0.85 + (decoR - hubRad * 0.85) * d / (decoN + 1.0);
                var dring = new Ellipse
                {
                    Width = rd * 2, Height = rd * 2,
                    Stroke = new SolidColorBrush(WheelSkin.A(14 + d * 3)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(dring, cx - rd); Canvas.SetTop(dring, cy - rd);
                root.Children.Add(dring);
            }

            double cr = hubRad;
            var hubGlow = new Ellipse
            {
                Width = cr * 2.9, Height = cr * 2.9,
                Fill = new RadialGradientBrush(WheelSkin.A(110), WheelSkin.A(0))
            };
            Canvas.SetLeft(hubGlow, cx - cr * 1.45); Canvas.SetTop(hubGlow, cy - cr * 1.45);
            root.Children.Add(hubGlow);

            var hub = new Ellipse
            {
                Width = cr * 2, Height = cr * 2,
                Fill = new RadialGradientBrush(WheelSkin.Disc(240, 22), WheelSkin.Disc(252, 0)),
                Stroke = new SolidColorBrush(WheelSkin.H(220)),
                StrokeThickness = 1.8
            };
            Canvas.SetLeft(hub, cx - cr); Canvas.SetTop(hub, cy - cr);
            root.Children.Add(hub);

            hubHotRing = new Ellipse
            {
                Width = cr * 2 + 12,
                Height = cr * 2 + 12,
                Stroke = new SolidColorBrush(WheelSkin.H(255)),
                StrokeThickness = 3,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(hubHotRing, cx - cr - 6);
            Canvas.SetTop(hubHotRing, cy - cr - 6);
            root.Children.Add(hubHotRing);

            centerText = new TextBlock
            {
                Text = "⚙",
                Foreground = new SolidColorBrush(WheelSkin.H(240)),
                FontSize = Math.Max(18, cr * 0.66),
                FontWeight = FontWeights.Light,
                TextAlignment = TextAlignment.Center,
                Width = cr * 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Canvas.SetLeft(centerText, cx - cr);
            Canvas.SetTop(centerText, cy - cr * 0.62);
            root.Children.Add(centerText);

            subText = new TextBlock
            {
                Text = hint,
                Foreground = new SolidColorBrush(WheelSkin.H(232)),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Width = cr * 3.0
            };
            var subBg = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(150, 10, 8, 18)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 10, 3)
            };
            Canvas.SetLeft(subBg, cx - cr * 1.5 - 10);
            Canvas.SetTop(subBg, cy + cr * 1.06);
            root.Children.Add(subBg);
            Canvas.SetLeft(subText, cx - cr * 1.5);
            Canvas.SetTop(subText, cy + cr * 1.06 + 3);
            root.Children.Add(subText);

            foreach (var ring in rings)
            {
                var band = new Ellipse
                {
                    Width = ring.Radius * 2, Height = ring.Radius * 2,
                    Stroke = new SolidColorBrush(WheelSkin.A(46)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(band, cx - ring.Radius); Canvas.SetTop(band, cy - ring.Radius);
                root.Children.Add(band);

                var letterSet = new List<char>();
                foreach (var it in ring.Items) if (!letterSet.Contains(it.Initial)) letterSet.Add(it.Initial);
                var lbl = new TextBlock
                {
                    Text = new string(letterSet.ToArray()),
                    Foreground = new SolidColorBrush(WheelSkin.H(210)),
                    FontSize = Math.Max(9, Math.Min(13, iconSize * 0.30)),
                    FontWeight = FontWeights.Bold
                };
                lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lAng = -Math.PI / 2 + RingOffset(ring.Index) - 0.34;
                double lR = ring.Radius + iconSize * 0.80;
                Canvas.SetLeft(lbl, cx + lR * Math.Cos(lAng) - lbl.DesiredSize.Width / 2);
                Canvas.SetTop(lbl, cy + lR * Math.Sin(lAng) - lbl.DesiredSize.Height / 2);
                root.Children.Add(lbl);

                for (int j = 0; j < ring.Capacity; j++)
                {
                    double a = -Math.PI / 2 + RingOffset(ring.Index) + 2 * Math.PI * j / ring.Capacity;
                    double inn = ring.Radius - iconSize * 0.64, outr = ring.Radius + iconSize * 0.64;
                    var ln = new Line
                    {
                        X1 = cx + inn * Math.Cos(a), Y1 = cy + inn * Math.Sin(a),
                        X2 = cx + outr * Math.Cos(a), Y2 = cy + outr * Math.Sin(a),
                        Stroke = new SolidColorBrush(WheelSkin.A(100)),
                        StrokeThickness = 1.2
                    };
                    root.Children.Add(ln);
                }

                for (int j = 0; j < ring.Items.Count; j++)
                {
                    double ang = -Math.PI / 2 + RingOffset(ring.Index) + 2 * Math.PI * j / ring.Capacity;
                    double x = cx + ring.Radius * Math.Cos(ang);
                    double y = cy + ring.Radius * Math.Sin(ang);

                    var slot = new Ellipse
                    {
                        Width = iconSize + 12, Height = iconSize + 12,
                        Fill = new SolidColorBrush(WheelSkin.Disc(96, 14)),
                        Stroke = new SolidColorBrush(WheelSkin.A(90)),
                        StrokeThickness = 1
                    };
                    Canvas.SetLeft(slot, x - (iconSize + 12) / 2); Canvas.SetTop(slot, y - (iconSize + 12) / 2);
                    root.Children.Add(slot);

                    var app = ring.Items[j];
                    if (app.Icon != null)
                    {
                        var img = new Image { Source = app.Icon, Width = iconSize, Height = iconSize };
                        Canvas.SetLeft(img, x - iconSize / 2); Canvas.SetTop(img, y - iconSize / 2);
                        root.Children.Add(img);
                    }
                    else
                    {
                        var tb = new TextBlock
                        {
                            Text = app.Name.Length > 1 ? app.Name.Substring(0, 1) : app.Name,
                            Foreground = new SolidColorBrush(WheelSkin.H(232)),
                            FontSize = iconSize * 0.55,
                            Width = iconSize,
                            TextAlignment = TextAlignment.Center
                        };
                        Canvas.SetLeft(tb, x - iconSize / 2); Canvas.SetTop(tb, y - iconSize * 0.36);
                        root.Children.Add(tb);
                    }
                }
            }

            highlight = new Ellipse
            {
                Width = iconSize + 24, Height = iconSize + 24,
                Fill = new SolidColorBrush(WheelSkin.A(140)),
                Stroke = new SolidColorBrush(WheelSkin.H(255)),
                StrokeThickness = 3,
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(highlight);

            bottomText = new TextBlock
            {
                Foreground = new SolidColorBrush(WheelSkin.H(255)),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold
            };
            bottomBar = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(WheelSkin.Disc(225, 20)),
                BorderBrush = new SolidColorBrush(WheelSkin.H(230)),
                BorderThickness = new Thickness(1.4),
                Padding = new Thickness(18, 6, 18, 6),
                Visibility = Visibility.Collapsed,
                Child = bottomText
            };
            root.Children.Add(bottomBar);

            if (!ShotMode)
            {
                var tip = new TextBlock
                {
                    Text = tipText ?? "Esc 取消 · 空白处松手不启动 · Tab+A / Tab+B 呼出",
                    Foreground = new SolidColorBrush(WheelSkin.H(228)),
                    FontSize = 11.5
                };
                var tipBg = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(160, 10, 8, 18)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 4, 12, 4),
                    Child = tip
                };
                tipBg.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tipBg, (winSize - tipBg.DesiredSize.Width) / 2);
                Canvas.SetTop(tipBg, 10);
                root.Children.Add(tipBg);
            }
        }

        public void PlaceAt(int screenX, int screenY, double scale)
        {
            var scr = WF.Screen.FromPoint(new System.Drawing.Point(screenX, screenY));
            double w = Width * scale, h = Height * scale;
            double left = screenX - w / 2, top = screenY - h / 2;
            left = Math.Max(scr.WorkingArea.Left, Math.Min(left, scr.WorkingArea.Right - w));
            top = Math.Max(scr.WorkingArea.Top, Math.Min(top, scr.WorkingArea.Bottom - h));
            Left = left / scale; Top = top / scale; winScale = scale;
            Log.W("place sx=" + screenX + " sy=" + screenY + " scale=" + scale + " wpx=" + w + " leftDIP=" + Left + " topDIP=" + Top);
        }


        public void UpdateHover(int screenX, int screenY, double scale)
        {
            double x = (screenX / scale) - Left - cx;
            double y = (screenY / scale) - Top - cy;
            double d = Math.Sqrt(x * x + y * y);
            if (d > reach) reach = d;
            if (d <= hubRadius * 1.05)
            {
                if (hovered != null) { hovered = null; highlight.Visibility = Visibility.Collapsed; }
                centerText.Text = "⚙";
                centerText.FontSize = Math.Max(20, hubRadius * 0.66);
                centerText.Foreground = new SolidColorBrush(WheelSkin.H(255));
                if (hubHotRing != null) hubHotRing.Visibility = Visibility.Visible;
                HubSelected = true;
                Canvas.SetTop(centerText, cy - centerText.FontSize * 0.62);
                bottomText.Text = "圆盘设置";
                bottomBar.Visibility = Visibility.Visible;
                bottomBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(bottomBar, cx - bottomBar.DesiredSize.Width / 2);
                Canvas.SetTop(bottomBar, cy + r0 + iconSize * 0.55 - bottomBar.DesiredSize.Height / 2);
                return;
            }
            AppEntry hit = null; int ringIdx = -1;

            double best = double.MaxValue;
            for (int i = 0; i < rings.Count; i++)
            {
                double diff = Math.Abs(d - rings[i].Radius);
                if (diff < best && diff <= iconSize * 0.80) { best = diff; ringIdx = i; }
            }
            if (ringIdx >= 0)
            {
                var ring = rings[ringIdx];
                double ang = Math.Atan2(y, x) + Math.PI / 2 - RingOffset(ringIdx);
                double twoPi = 2 * Math.PI;
                ang = ang % twoPi;
                if (ang < 0) ang += twoPi;
                int idx = (int)Math.Round(ang / (twoPi / ring.Capacity));
                idx %= ring.Capacity;
                if (idx < 0) idx += ring.Capacity;
                if (idx < ring.Items.Count) hit = ring.Items[idx];
            }
            if (hit != hovered)
            {
                hovered = hit;
                HubSelected = false;
                if (hit != null) { Click.Tick(ringIdx); lastHit = hit; lastHitAt = DateTime.Now; }
                if (hubHotRing != null) hubHotRing.Visibility = Visibility.Collapsed;
                if (hit != null)
                {
                    var ring = rings[ringIdx];
                    int idx = ring.Items.IndexOf(hit);
                    double ang = -Math.PI / 2 + RingOffset(ring.Index) + 2 * Math.PI * idx / ring.Capacity;
                    double px = cx + ring.Radius * Math.Cos(ang);
                    double py = cy + ring.Radius * Math.Sin(ang);
                    Canvas.SetLeft(highlight, px - highlight.Width / 2);
                    Canvas.SetTop(highlight, py - highlight.Height / 2);
                    highlight.Visibility = Visibility.Visible;
                    centerText.Text = hit.Name;
                    centerText.FontSize = Math.Max(12, Math.Min(17, hubRadius * 0.42));
                    centerText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 246, 205));
                    Canvas.SetTop(centerText, cy - centerText.FontSize * 0.62);
                    bottomText.Text = hit.Name;
                    bottomBar.Visibility = Visibility.Visible;
                    bottomBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double bw = bottomBar.DesiredSize.Width, bh = bottomBar.DesiredSize.Height;
                    Canvas.SetLeft(bottomBar, cx - bw / 2);
                    Canvas.SetTop(bottomBar, cy + r0 + iconSize * 0.55 - bh / 2);
                }
                else
                {
                    highlight.Visibility = Visibility.Collapsed;
                    bottomBar.Visibility = Visibility.Collapsed;
                    centerText.Text = "⚙";
                    centerText.FontSize = Math.Max(16, hubRadius * 0.55);
                    centerText.Foreground = new SolidColorBrush(WheelSkin.H(240));
                    Canvas.SetTop(centerText, cy - centerText.FontSize * 0.62);
                }
            }
        }

        public AppEntry Hovered { get { return hovered; } }
        public double Reach { get { return reach; } }
        public void ResetReach() { reach = 0; }
        public double HubRadius { get { return hubRadius; } }
        public List<Ring> Rings { get { return rings; } }

        public void Probe(int scaleI)
        {
            double sc = scaleI;
            Log.W("probe: cx=" + cx + " cy=" + cy + " left=" + Left + " top=" + Top + " icon=" + iconSize + " scale=" + sc);
            foreach (var ring in rings)
            {
                for (int j = 0; j < ring.Items.Count; j++)
                {
                    double ang = -Math.PI / 2 + RingOffset(ring.Index) + 2 * Math.PI * j / ring.Capacity;
                    double dx = cx + ring.Radius * Math.Cos(ang);
                    double dy = cy + ring.Radius * Math.Sin(ang);
                    int px = (int)Math.Round((Left + dx) * sc);
                    int py = (int)Math.Round((Top + dy) * sc);
                    Native.SetCursorPos(px, py);
                    UpdateHover(px, py, sc);
                    string got = hovered == null ? "(none)" : hovered.Name;
                    bool bad = !object.ReferenceEquals(hovered, ring.Items[j]);
                    Log.W("probe r=" + ring.Index + " j=" + j + " rad=" + ring.Radius.ToString("F1") + " expect=" + ring.Items[j].Name + " got=" + got + (bad ? " BAD" : ""));
                }
            }
        }

        public bool HitHub(int screenX, int screenY, double scale)
        {
            double x = (screenX / scale) - Left - cx;
            double y = (screenY / scale) - Top - cy;
            return Math.Sqrt(x * x + y * y) <= hubRadius * 1.12;
        }

        public AppEntry Commit()
        {
            var h = hovered;
            hovered = null;
            return h;
        }

        // 松手时按真实光标位置重算一次（钩子回调里 25ms 轮询可能还没刷新），
        // 再退一步取最近图标（半径容差 1.15 个图标），避免"滑到图标上但差几像素"漏启动。
        public AppEntry CommitAt(int screenX, int screenY, double scale)
        {
            try
            {
                UpdateHover(screenX, screenY, scale);
                if (hovered == null) hovered = NearestIcon(screenX, screenY, scale);
                if (hovered == null && lastHit != null && (DateTime.Now - lastHitAt).TotalMilliseconds < 450) hovered = lastHit;
                var h = hovered;
                hovered = null;
                return h;
            }
            catch
            {
                var h2 = hovered;
                hovered = null;
                return h2;
            }
        }

        AppEntry NearestIcon(int screenX, int screenY, double scale)
        {
            double x = (screenX / scale) - Left - cx;
            double y = (screenY / scale) - Top - cy;
            if (Math.Sqrt(x * x + y * y) <= hubRadius * 1.05) return null;   // 松手在中心圈 = 取消
            AppEntry best = null;
            double bestD2 = double.MaxValue;
            foreach (var ring in rings)
            {
                for (int j = 0; j < ring.Items.Count; j++)
                {
                    double ang = -Math.PI / 2 + RingOffset(ring.Index) + 2 * Math.PI * j / ring.Capacity;
                    double px = ring.Radius * Math.Cos(ang);
                    double py = ring.Radius * Math.Sin(ang);
                    double dd = (x - px) * (x - px) + (y - py) * (y - py);
                    if (dd < bestD2) { bestD2 = dd; best = ring.Items[j]; }
                }
            }
            if (best == null) return null;
            return Math.Sqrt(bestD2) <= iconSize * 2.2 ? best : null;
        }

        public Action<AppEntry> Pick;
        public Action HubPick;

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            try
            {
                var p = PointToScreen(System.Windows.Input.Mouse.GetPosition(this));
                if (HitHub((int)Math.Round(p.X), (int)Math.Round(p.Y), winScale))
                {
                    var hp = HubPick;
                    if (hp != null) hp();
                    return;
                }
                var a = Commit();
                if (Pick != null) Pick(a);
            }
            catch { }
        }
    }
    public class WheelSettingsWindow : Window
    {
        public class WheelRow
        {
            public string Name { get; set; }
            public string Kind { get; set; }
            public string Source { get; set; }
            public string Path { get; set; }
        }

        public class GameRow
        {
            public string Name { get; set; }
            public string Source { get; set; }
            public string Path { get; set; }
        }

        ListView lvWheel, lvGame;
        TabControl tabs;
        TextBox tbSearch;
        TextBlock tipWheel, tipGame, emptyWheel, emptyGame;
        List<WheelRow> wheelRows = new List<WheelRow>();
        public bool Changed;
        public bool Reopen;
        public Action Reloaded;

        public WheelSettingsWindow() : this(0) { }

        public WheelSettingsWindow(int startTab)
        {
            Title = "轮盘启动器 · 设置";
            Width = 980; Height = 700; MinWidth = 880; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Ui.Crisp(this);
            BuildUi(startTab);
            // 列表刷新放到 BuildUi 末尾（切主题重建界面时也要刷新）
        }

        void BuildUi(int startPage)
        {
            Background = new SolidColorBrush(Ui.BgCol);
            Foreground = new SolidColorBrush(Ui.InkCol);
            FontSize = 13;

            var root = new Grid { Background = new SolidColorBrush(Ui.BgCol) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var side = new Border
            {
                Background = new SolidColorBrush(Ui.CardCol),
                BorderBrush = new SolidColorBrush(Ui.LineCol),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };
            var sideDock = new DockPanel { Margin = new Thickness(14, 22, 14, 16) };
            side.Child = sideDock;

            var brand = new StackPanel { Margin = new Thickness(6, 0, 6, 26) };
            brand.Children.Add(new TextBlock { Text = "轮盘启动器", FontSize = 16.5, FontWeight = FontWeights.SemiBold });
            brand.Children.Add(new TextBlock { Text = "WHEEL LAUNCHER", FontSize = 9.5, Foreground = new SolidColorBrush(Ui.MutedCol), Margin = new Thickness(0, 4, 0, 0) });
            DockPanel.SetDock(brand, Dock.Top);
            sideDock.Children.Add(brand);

            var nav = new StackPanel();
            DockPanel.SetDock(nav, Dock.Top);
            sideDock.Children.Add(nav);

            var pageWheel = PageHost("圆盘内容", "圆盘上出现什么，顺序按首字母自动排。", BuildWheelPage());
            var pageGame = PageHost("游戏与暂停", "什么时候自动让开，不打扰你玩。", BuildGamePage());
            var pageLook = PageHost("外观与行为", "配色、大小、松手之后做什么。", BuildLookPage());

            var host = new Grid();
            var pages = new FrameworkElement[] { pageWheel, pageGame, pageLook };
            foreach (var pg in pages) host.Children.Add(pg);

            var navButtons = new List<Button>();
            string[] navText = { "圆盘内容", "游戏与暂停", "外观与行为" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var nb = Ui.MkButton(navText[i], false);
                nb.Height = 38;
                nb.Margin = new Thickness(0, 0, 0, 4);
                nb.Padding = new Thickness(14, 0, 14, 0);
                nb.HorizontalContentAlignment = HorizontalAlignment.Left;
                nb.FontSize = 13.5;
                nb.Click += delegate { ShowPage(idx); };
                navButtons.Add(nb);
                nav.Children.Add(nb);
            }

            foreach (var nbx in navButtons) { nbx.MouseLeave += delegate { if (showPage != null) showPage(curIdx); }; }

            var bTheme = Ui.MkButton(Ui.Dark ? "切换到浅色界面" : "切换到深色界面", false);
            bTheme.Height = 36;
            bTheme.Margin = new Thickness(0, 22, 0, 0);
            bTheme.HorizontalContentAlignment = HorizontalAlignment.Left;
            bTheme.Padding = new Thickness(14, 0, 14, 0);
            bTheme.Click += delegate { Settings.DarkUI = !Settings.DarkUI; BuildUi(curIdx); };
            nav.Children.Add(bTheme);

            var ver = new TextBlock
            {
                Text = "本地版本 · Wheel Launcher",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Ui.MutedCol),
                Margin = new Thickness(6, 14, 6, 2)
            };
            nav.Children.Add(ver);

            Grid.SetColumn(side, 0);
            root.Children.Add(side);

            var right = new DockPanel { LastChildFill = true };
            var foot = new DockPanel { Margin = new Thickness(28, 8, 24, 16) };
            var hint = new TextBlock
            {
                Text = "改动即时生效 · 配置保存在 %APPDATA%\\WheelLauncher",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Ui.MutedCol)
            };
            var bClose = Ui.MkButton("关闭", false);
            bClose.MinWidth = 92; bClose.Height = 34;
            bClose.VerticalAlignment = VerticalAlignment.Center;
            bClose.Margin = new Thickness(12, 0, 0, 0);
            bClose.Click += delegate { Close(); };
            DockPanel.SetDock(bClose, Dock.Right);
            foot.Children.Add(bClose);
            foot.Children.Add(hint);
            DockPanel.SetDock(foot, Dock.Bottom);
            right.Children.Add(foot);
            right.Children.Add(host);
            Grid.SetColumn(right, 1);
            root.Children.Add(right);
            Content = root;

            showPage = delegate (int i)
            {
                curIdx = i;
                for (int k = 0; k < pages.Length; k++)
                    pages[k].Visibility = (k == i) ? Visibility.Visible : Visibility.Collapsed;
                for (int k = 0; k < navButtons.Count; k++)
                {
                    bool sel = (k == i);
                    var b = navButtons[k];
                    b.Background = new SolidColorBrush(sel ? Ui.SelBgCol : Ui.CardCol);
                    b.Foreground = new SolidColorBrush(sel ? Ui.InkCol : Ui.SubCol);
                    b.FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
                }
            };
            ShowPage(startPage < 0 || startPage > 2 ? 0 : startPage);
Dispatcher.BeginInvoke(new Action(delegate
            {
                ReloadWheel();
                ReloadGames();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        Action<int> showPage;
        int curIdx;

        public void ShowPage(int i)
        {
            if (showPage != null) showPage(i);
        }

        FrameworkElement PageHost(string title, string desc, FrameworkElement body)
        {
            var d = new DockPanel { LastChildFill = true };
            var hd = new StackPanel { Margin = new Thickness(26, 20, 26, 12) };
            hd.Children.Add(new TextBlock { Text = title, FontSize = 19.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Ui.InkCol) });
            hd.Children.Add(new TextBlock { Text = desc, FontSize = 12.5, Foreground = new SolidColorBrush(Ui.SubCol), Margin = new Thickness(0, 5, 0, 0) });
            DockPanel.SetDock(hd, Dock.Top);
            var card = new Border
            {
                Margin = new Thickness(24, 0, 24, 14),
                Background = new SolidColorBrush(Ui.CardCol),
                BorderBrush = new SolidColorBrush(Ui.LineCol),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = body
            };
            d.Children.Add(hd);
            d.Children.Add(card);
            return d;
        }

        // ================= 圆盘外观 =================
        FrameworkElement BuildLookPage()
        {
            var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 10) };

            panel.Children.Add(new TextBlock { Text = "轮盘配色", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Ui.InkCol), Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Text = "呼出圆盘时使用的配色方案，改完下一次呼出就生效。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Ui.SubCol),
                Margin = new Thickness(0, 0, 0, 12)
            });

            var wrap = new System.Windows.Controls.WrapPanel();
            var radios = new List<RadioButton>();
            for (int i = 0; i < WheelSkin.Names.Length; i++)
            {
                int idx = i;
                var rb = Ui.MkRadio(WheelSkin.Names[i], "wheeltheme", Settings.WheelTheme == i);
                rb.Margin = new Thickness(0, 0, 22, 10);
                rb.Checked += delegate
                {
                    if (!rb.IsChecked.HasValue || !rb.IsChecked.Value) return;
                    Settings.WheelTheme = idx;
                    Changed = true;
                };
                radios.Add(rb);
                wrap.Children.Add(rb);
            }
            panel.Children.Add(wrap);

            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Ui.LineCol),
                Margin = new Thickness(0, 8, 0, 18)
            });

            panel.Children.Add(new TextBlock { Text = "轮盘大小", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Ui.InkCol), Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Text = "按屏幕短边的比例缩放圆盘，太小看不清、太大容易顶到屏幕边缘。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Ui.SubCol),
                Margin = new Thickness(0, 0, 0, 12)
            });

            var sizeRow = new DockPanel();
            var slSize = new Slider
            {
                Style = Ui.SliderStyle(),
                Minimum = 70,
                Maximum = 140,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = Settings.WheelSize,
                Width = 360,
                VerticalAlignment = VerticalAlignment.Center
            };
            var lblSize = new TextBlock
            {
                Text = Settings.WheelSize + "%",
                Width = 56,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Ui.InkCol)
            };
            DockPanel.SetDock(lblSize, Dock.Right);
            slSize.ValueChanged += delegate
            {
                Settings.WheelSize = (int)slSize.Value;
                lblSize.Text = Settings.WheelSize + "%";
                Changed = true;
            };
            sizeRow.Children.Add(lblSize);
            sizeRow.Children.Add(slSize);
            panel.Children.Add(sizeRow);

            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Ui.LineCol),
                Margin = new Thickness(0, 20, 0, 18)
            });

            panel.Children.Add(new TextBlock { Text = "侧键松手后", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Ui.InkCol), Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Text = "决定「松开侧键」这一下是直接启动应用，还是只把圆盘留在屏幕上等你点。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Ui.SubCol),
                Margin = new Thickness(0, 0, 0, 12)
            });

            var rbRelease = Ui.MkRadio("滑到就启动（松开侧键立即打开高亮的应用）", "releasemode", Settings.ReleaseMode == 0);
            rbRelease.Margin = new Thickness(0, 0, 0, 10);
            rbRelease.Checked += delegate { if (rbRelease.IsChecked == true) { Settings.ReleaseMode = 0; Changed = true; } };

            var rbClick = Ui.MkRadio("只呼出圆盘（松开侧键不启动，左键点图标才打开；Esc / 点空白关闭）", "releasemode", Settings.ReleaseMode == 1);
            rbClick.Checked += delegate { if (rbClick.IsChecked == true) { Settings.ReleaseMode = 1; Changed = true; } };

            panel.Children.Add(rbRelease);
            panel.Children.Add(rbClick);

            var sep2 = new Border(); sep2.Height = 1; sep2.Margin = new Thickness(0, 14, 0, 14);
            sep2.Background = new SolidColorBrush(Ui.LineCol);
            panel.Children.Add(sep2);
            var lblStyle = new TextBlock();
            lblStyle.Text = "轮盘样式";
            lblStyle.FontSize = 13; lblStyle.Margin = new Thickness(0, 0, 0, 8);
            lblStyle.Foreground = new SolidColorBrush(Ui.InkCol);
            panel.Children.Add(lblStyle);
            var rbRound = Ui.MkRadio("圆形轮盘（按字母成环，看着更漂亮）", "wheelstyle", Settings.WheelStyle == 0);
            rbRound.Margin = new Thickness(0, 0, 0, 10);
            rbRound.Checked += delegate { if (rbRound.IsChecked == true) { Settings.WheelStyle = 0; Changed = true; } };
            panel.Children.Add(rbRound);
            var rbGrid = Ui.MkRadio("方格面板（按字母分行，标注更清楚、更容易点）", "wheelstyle", Settings.WheelStyle == 1);
            rbGrid.Checked += delegate { if (rbGrid.IsChecked == true) { Settings.WheelStyle = 1; Changed = true; } };
            panel.Children.Add(rbGrid);
            panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Ui.LineCol), Margin = new Thickness(0, 20, 0, 18) });
            var bMore = Ui.MkButton("长按时间与音效…", false);
            bMore.HorizontalAlignment = HorizontalAlignment.Left;
            bMore.Click += delegate { var wa = Application.Current as WheelApp; if (wa != null) wa.OpenSettings(); };
            panel.Children.Add(bMore);
            return panel;
        }
        FrameworkElement BuildWheelPage()
        {
            var panel = new DockPanel();

            var bar = new Grid { Margin = new Thickness(12, 12, 12, 10) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var bAuto = Ui.MkButton("自动收录文件夹…", true);
            bAuto.Margin = new Thickness(0, 0, 8, 0);
            var bFile = Ui.MkButton("添加文件…", false);
            bFile.Margin = new Thickness(0, 0, 8, 0);
            var bDir = Ui.MkButton("添加文件夹…", false);
            bDir.Margin = new Thickness(0, 0, 8, 0);
            var bDel = Ui.MkButton("移除", false);
            bDel.Margin = new Thickness(0, 0, 8, 0);
            var bRel = Ui.MkButton("重新扫描", false);
            left.Children.Add(bAuto); left.Children.Add(bFile); left.Children.Add(bDir); left.Children.Add(bDel); left.Children.Add(bRel);

            tbSearch = new TextBox
            {
                Width = 166,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Ui.InkCol),
                FontSize = 12.5,
                ToolTip = "按名称或路径筛选"
            };
            var searchWrap = new Border
            {
                Background = new SolidColorBrush(Ui.ChipCol),
                BorderBrush = new SolidColorBrush(Ui.LineCol),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Height = 32,
                Width = 218,
                Padding = new Thickness(10, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var searchInner = new DockPanel { LastChildFill = true };
            var mag = new TextBlock { Text = "\u2315", FontSize = 15, Foreground = new SolidColorBrush(Ui.MutedCol), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(mag, Dock.Left);
            searchInner.Children.Add(mag);
            searchInner.Children.Add(tbSearch);
            searchWrap.Child = searchInner;
            DockPanel.SetDock(searchWrap, Dock.Right);
            Grid.SetColumn(searchWrap, 2);
            bar.Children.Add(searchWrap);
            Grid.SetColumn(left, 0);
            bar.Children.Add(left);
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);

            var footRow = new DockPanel { Margin = new Thickness(12, 8, 12, 12) };
            var cbAuto = new CheckBox
            {
                Content = "启动时自动收录常用文件夹（文档 / 下载）",
                IsChecked = Settings.AutoFolders,
                Foreground = new SolidColorBrush(Ui.InkCol),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Ui.StyleCheck(cbAuto);
            cbAuto.Checked += delegate { Settings.AutoFolders = true; };
            cbAuto.Unchecked += delegate { Settings.AutoFolders = false; };
            tipWheel = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = new SolidColorBrush(Ui.SubCol) };
            DockPanel.SetDock(tipWheel, Dock.Right);
            footRow.Children.Add(tipWheel);
            footRow.Children.Add(cbAuto);
            DockPanel.SetDock(footRow, Dock.Bottom);
            panel.Children.Add(footRow);

            lvWheel = new ListView { Margin = new Thickness(12, 0, 12, 0), Background = new SolidColorBrush(Ui.CardCol), BorderThickness = new Thickness(0), FontSize = 12.5, Padding = new Thickness(2, 2, 2, 2) };
            ScrollViewer.SetHorizontalScrollBarVisibility(lvWheel, ScrollBarVisibility.Auto);
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn { Header = "名称", Width = 218, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            gv.Columns.Add(new GridViewColumn { Header = "类型", Width = 58, DisplayMemberBinding = new System.Windows.Data.Binding("Kind") });
            gv.Columns.Add(new GridViewColumn { Header = "来源", Width = 96, DisplayMemberBinding = new System.Windows.Data.Binding("Source") });
            var pathCol = new GridViewColumn { Header = "路径", Width = 470 };
            var ptpl = new DataTemplate();
            var pfef = new FrameworkElementFactory(typeof(TextBlock));
            pfef.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding("Path"));
            pfef.SetValue(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Path"));
            pfef.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            pfef.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            pfef.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            ptpl.VisualTree = pfef;
            pathCol.CellTemplate = ptpl;
            gv.Columns.Add(pathCol);
            lvWheel.View = gv;
            Ui.Skin(lvWheel, gv);
            lvWheel.BorderThickness = new Thickness(0);

            emptyWheel = new TextBlock
            {
                Text = "还没有任何项。点左上角「自动收录文件夹…」，或者添加一个程序。",
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Ui.MutedCol),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(40),
                Visibility = Visibility.Collapsed
            };
            var gridW = new Grid();
            gridW.Children.Add(lvWheel);
var cornerPatch = new System.Windows.Shapes.Rectangle
            {
                Width = 44, Height = 44,
                Fill = new SolidColorBrush(Ui.CardCol),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 0),
                IsHitTestVisible = false
            };
            gridW.Children.Add(cornerPatch);
            gridW.Children.Add(emptyWheel);
            panel.Children.Add(gridW);

            tbSearch.TextChanged += delegate { ApplyFilter(); };
            bRel.Click += delegate { ReloadWheel(); };
            bFile.Click += delegate { PickWheel(false); };
            bDir.Click += delegate { PickWheel(true); };
            bDel.Click += delegate { RemoveWheelSelected(); };
            bAuto.Click += delegate
            {
                var w = new AutoFoldersWindow();
                w.ShowDialog();
                if (w.Added > 0) { Changed = true; ReloadWheel(); Notify(); }
            };
            return panel;
        }

        public void ReloadWheel()
        {
            try
            {
                wheelRows = new List<WheelRow>();
                var custom = Scanner.LoadCustom();
                foreach (var a in Scanner.FromDesktop())
                {
                    bool isDir = Directory.Exists(a.Path);
                    bool isCustom = custom.Exists(delegate (AppEntry c) { return string.Equals(c.Path, a.Path, StringComparison.OrdinalIgnoreCase); });
                    wheelRows.Add(new WheelRow
                    {
                        Name = a.Name,
                        Path = a.Path,
                        Kind = isDir ? "文件夹" : (a.Path.ToLowerInvariant().EndsWith(".exe") ? "应用" : "文件"),
                        Source = isCustom ? "手动添加" : "桌面快捷方式"
                    });
                }
                ApplyFilter();
                int manual = 0;
                foreach (var r in wheelRows) if (r.Source == "手动添加") manual++;
                tipWheel.Text = "共 " + wheelRows.Count + " 项（手动 " + manual + " 项）";
            }
            catch (Exception ex) { tipWheel.Text = "读取失败：" + ex.Message; }
        }

        void ApplyFilter()
        {
            string q = tbSearch == null ? "" : (tbSearch.Text ?? "").Trim();
            lvWheel.Items.Clear();
            int n = 0;
            foreach (var r in wheelRows)
            {
                if (q.Length > 0)
                {
                    bool hit = r.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                        || r.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                lvWheel.Items.Add(r); n++;
            }
            if (emptyWheel != null)
                emptyWheel.Visibility = (lvWheel.Items.Count == 0 && (tbSearch.Text ?? "").Length == 0) ? Visibility.Visible : Visibility.Collapsed;
            if (tipWheel != null && q.Length > 0) tipWheel.Text = "筛选出 " + n + " 项";
        }

        void PickWheel(bool folder)
        {
            try
            {
                string path = folder ? PickFolder("选择要加到圆盘的文件夹") : PickFile("选择要加到圆盘的文件");
                if (string.IsNullOrEmpty(path)) return;
                Scanner.AddCustom(NameOf(path), path, "");
                Changed = true;
                Log.W("settings add wheel: " + path);
                ReloadWheel();
                Notify();
            }
            catch (Exception ex) { tipWheel.Text = "添加失败：" + ex.Message; }
        }

        void RemoveWheelSelected()
        {
            var r = lvWheel.SelectedItem as WheelRow;
            if (r == null) { tipWheel.Text = "先在列表里选中一项"; return; }
            if (Scanner.RemoveCustom(r.Path))
            {
                Changed = true;
                Log.W("settings remove wheel: " + r.Path);
                ReloadWheel();
                Notify();
            }
            else tipWheel.Text = "这项来自桌面快捷方式，程序里删不掉（到桌面删）；这里只能移除手动添加的项。";
        }

        // ================= 游戏（自动暂停） =================
        FrameworkElement BuildGamePage()
        {
            var panel = new DockPanel();

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 12, 12, 10) };
            var bDir = Ui.MkButton("添加游戏文件夹…", true);
            var bFile = Ui.MkButton("添加游戏程序…", false);
            var bDel = Ui.MkButton("取消标为游戏", false);
            var bRel = Ui.MkButton("刷新", false);
            bDir.Margin = new Thickness(0, 0, 8, 0); bFile.Margin = new Thickness(0, 0, 8, 0); bDel.Margin = new Thickness(0, 0, 8, 0);
            bar.Children.Add(bDir); bar.Children.Add(bFile); bar.Children.Add(bDel); bar.Children.Add(bRel);
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar);

            var footRow = new StackPanel { Margin = new Thickness(12, 8, 12, 10) };
            var cbRow = new WrapPanel();
            var cbPause = new CheckBox
            {
                Content = "检测到游戏时自动暂停轮盘（侧键与 Tab 快捷键全部放行）",
                IsChecked = Settings.AutoPause,
                Foreground = new SolidColorBrush(Ui.InkCol),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Ui.StyleCheck(cbPause);
            cbPause.Checked += delegate { Settings.AutoPause = true; ReloadGames(); };
            cbPause.Unchecked += delegate { Settings.AutoPause = false; ReloadGames(); };
            var cbStore = new CheckBox
            {
                Content = "打开 Steam / Epic 等游戏平台窗口时也暂停",
                IsChecked = Settings.PauseOnStore,
                Foreground = new SolidColorBrush(Ui.InkCol),
                FontSize = 12,
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Ui.StyleCheck(cbStore);
            cbStore.Checked += delegate { Settings.PauseOnStore = true; ReloadGames(); };
            cbStore.Unchecked += delegate { Settings.PauseOnStore = false; ReloadGames(); };
            var cbAccel = new CheckBox
            {
                Content = "加速器窗口在前台时也放行（biubiu 等）",
                IsChecked = Settings.PauseAccel,
                Foreground = new SolidColorBrush(Ui.InkCol),
                FontSize = 12,
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Ui.StyleCheck(cbAccel);
            cbAccel.Checked += delegate { Settings.PauseAccel = true; };
            cbAccel.Unchecked += delegate { Settings.PauseAccel = false; };
            tipGame = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Foreground = new SolidColorBrush(Ui.SubCol), Margin = new Thickness(2, 7, 0, 0) };
            cbPause.Margin = new Thickness(0, 4, 24, 0);
            cbStore.Margin = new Thickness(0, 4, 24, 0);
            cbAccel.Margin = new Thickness(0, 4, 24, 0);
            cbRow.Children.Add(cbPause);
            cbRow.Children.Add(cbStore);
            cbRow.Children.Add(cbAccel);
            footRow.Children.Add(cbRow);
            footRow.Children.Add(tipGame);
            DockPanel.SetDock(footRow, Dock.Bottom);
            panel.Children.Add(footRow);

            lvGame = new ListView { Margin = new Thickness(12, 0, 12, 0), Background = new SolidColorBrush(Ui.CardCol), BorderThickness = new Thickness(0), FontSize = 12.5, Padding = new Thickness(2, 2, 2, 2) };
            ScrollViewer.SetHorizontalScrollBarVisibility(lvGame, ScrollBarVisibility.Auto);
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn { Header = "名称", Width = 218, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            gv.Columns.Add(new GridViewColumn { Header = "判定方式", Width = 112, DisplayMemberBinding = new System.Windows.Data.Binding("Source") });
            var gpathCol = new GridViewColumn { Header = "路径", Width = 470 };
            var gtpl = new DataTemplate();
            var gfef = new FrameworkElementFactory(typeof(TextBlock));
            gfef.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding("Path"));
            gfef.SetValue(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Path"));
            gfef.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            gfef.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
            gfef.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            gtpl.VisualTree = gfef;
            gpathCol.CellTemplate = gtpl;
            gv.Columns.Add(gpathCol);
            lvGame.View = gv;
            Ui.Skin(lvGame, gv);
            lvGame.BorderThickness = new Thickness(0);

            emptyGame = new TextBlock
            {
                Text = "还没有标记任何游戏。\n把游戏文件夹加进来，或者等它在 Steam / Epic 目录里被自动识别。",
                FontSize = 12.5,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Ui.MutedCol),
                Visibility = Visibility.Collapsed
            };
            var gridG = new Grid();
            gridG.Children.Add(lvGame);
            gridG.Children.Add(emptyGame);
            panel.Children.Add(gridG);

            bRel.Click += delegate { ReloadGames(); };
            bDir.Click += delegate { PickGame(true); };
            bFile.Click += delegate { PickGame(false); };
            bDel.Click += delegate { RemoveGameSelected(); };
            return panel;
        }

        public void ReloadGames()
        {
            try
            {
                lvGame.Items.Clear();
                var manual = GameList.Load();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in Scanner.FromDesktop())
                {
                    if (!GameWatch.IsGamePath(a.Path)) continue;
                    if (!Settings.PauseOnStore && GameWatch.IsStoreExe(GameWatch.ExeName(a.Path))) continue;
                    bool isManual = GameList.Match(a.Path);
                    seen.Add(a.Path);
                    lvGame.Items.Add(new GameRow { Name = a.Name, Path = a.Path, Source = isManual ? "手动指定" : "游戏目录识别" });
                }
                foreach (var g in manual)
                {
                    if (seen.Contains(g)) continue;
                    seen.Add(g);
                    lvGame.Items.Add(new GameRow { Name = NameOf(g) + (Directory.Exists(g) ? "（文件夹）" : ""), Path = g, Source = "手动指定" });
                }
                if (emptyGame != null) emptyGame.Visibility = lvGame.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                tipGame.Text = "共 " + lvGame.Items.Count + " 项" + (Settings.AutoPause ? "" : " · 自动暂停已关闭");
            }
            catch (Exception ex) { tipGame.Text = "读取失败：" + ex.Message; }
        }

        void PickGame(bool folder)
        {
            try
            {
                string path = folder ? PickFolder("选择游戏所在的文件夹") : PickFile("选择游戏主程序");
                if (string.IsNullOrEmpty(path)) return;
                GameList.Add(path);
                Changed = true;
                Log.W("settings add game: " + path);
                ReloadGames();
                Notify();
            }
            catch (Exception ex) { tipGame.Text = "添加失败：" + ex.Message; }
        }

        void RemoveGameSelected()
        {
            var r = lvGame.SelectedItem as GameRow;
            if (r == null) { tipGame.Text = "先在列表里选中一项"; return; }
            if (GameList.Remove(r.Path))
            {
                Changed = true;
                Log.W("settings remove game: " + r.Path);
                ReloadGames();
                Notify();
            }
            else tipGame.Text = "这项是按游戏目录自动识别的，不是手动加的，取消不了。";
        }

        // ================= 工具 =================
        static string NameOf(string path)
        {
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = path;
            if (name.ToLowerInvariant().EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
            return name;
        }

        static string PickFolder(string desc)
        {
            var d = new WF.FolderBrowserDialog();
            d.Description = desc;
            d.ShowNewFolderButton = false;
            return d.ShowDialog() == WF.DialogResult.OK ? d.SelectedPath : null;
        }

        static string PickFile(string title)
        {
            var d = new WF.OpenFileDialog();
            d.Title = title;
            d.Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
            return d.ShowDialog() == WF.DialogResult.OK ? d.FileName : null;
        }

        void Notify()
        {
            var h = Reloaded;
            if (h != null) { try { h(); } catch { } }
        }
    }

    public class AddAppDialog : Window
    {
        public string ResultName, ResultPath, ResultArgs;

        public AddAppDialog()
        {
            Title = "添加应用到轮盘";
            Width = 520; Height = 250; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Ui.BgCol);
            Foreground = new SolidColorBrush(Ui.InkCol);
            FontSize = 13;
            Ui.Crisp(this);

            var grid = new Grid();
            grid.Margin = new Thickness(18);
            for (int i = 0; i < 4; i++) { var rd = new RowDefinition(); rd.Height = i == 3 ? GridLength.Auto : new GridLength(34); grid.RowDefinitions.Add(rd); }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var tbName = new TextBox { VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Ui.CardCol), Foreground = new SolidColorBrush(Ui.InkCol), Padding = new Thickness(6, 4, 6, 4) };
            var tbPath = new TextBox { VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Ui.CardCol), Foreground = new SolidColorBrush(Ui.InkCol), Padding = new Thickness(6, 4, 6, 4) };
            var tbArgs = new TextBox { VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Ui.CardCol), Foreground = new SolidColorBrush(Ui.InkCol), Padding = new Thickness(6, 4, 6, 4) };

            var lName = new TextBlock { Text = "显示名称", VerticalAlignment = VerticalAlignment.Center };
            var lPath = new TextBlock { Text = "程序路径", VerticalAlignment = VerticalAlignment.Center };
            var lArgs = new TextBlock { Text = "启动参数", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lName, 0); Grid.SetRow(tbName, 0); Grid.SetColumn(tbName, 1);
            Grid.SetRow(lPath, 1); Grid.SetRow(tbPath, 1); Grid.SetColumn(tbPath, 1);
            Grid.SetRow(lArgs, 2); Grid.SetRow(tbArgs, 2); Grid.SetColumn(tbArgs, 1);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var browse = Ui.MkButton("浏览…", false); browse.Margin = new Thickness(0, 0, 8, 0);
            var okB = Ui.MkButton("添加", true); okB.Margin = new Thickness(0, 0, 8, 0);
            var cancel = Ui.MkButton("取消", false);
            panel.Children.Add(browse); panel.Children.Add(okB); panel.Children.Add(cancel);
            Grid.SetRow(panel, 3); Grid.SetColumnSpan(panel, 2);

            grid.Children.Add(lName); grid.Children.Add(tbName);
            grid.Children.Add(lPath); grid.Children.Add(tbPath);
            grid.Children.Add(lArgs); grid.Children.Add(tbArgs);
            grid.Children.Add(panel);
            Content = grid;

            browse.Click += (s, e) =>
            {
                var dlg = new WF.OpenFileDialog { Filter = "程序 (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|所有文件 (*.*)|*.*" };
                if (dlg.ShowDialog() == WF.DialogResult.OK)
                {
                    tbPath.Text = dlg.FileName;
                    if (string.IsNullOrWhiteSpace(tbName.Text)) tbName.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            };
            okB.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(tbName.Text) || string.IsNullOrWhiteSpace(tbPath.Text)) return;
                ResultName = tbName.Text.Trim();
                ResultPath = tbPath.Text.Trim();
                ResultArgs = tbArgs.Text.Trim();
                DialogResult = true;
            };
            cancel.Click += (s, e) => { DialogResult = false; };
        }
    }

class WheelApp : Application
    {
        public static bool IsElevated
        {
            get
            {
                try { var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }
        public WheelApp()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += delegate (object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs a)
            {
                Log.W("UNHANDLED: " + a.Exception);
                a.Handled = true;
            };
        }
        WF.NotifyIcon tray;
        bool startGameTab;
        bool startLookTab;

        static System.Drawing.Icon LoadExeIcon()
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return (System.Drawing.Icon)ico.Clone();
            }
            catch { }
            return null;
        }
        MouseHook hook;
        WheelWindow wheel;
        GridWindow grid;
        List<AppEntry> apps;

        double scale = 1;
        int downX, downY, downBtn, showX, showY;
        DateTime pressAt;
        bool hadWheel;
        static int LongPressMs { get { return Settings.LongPress; } }
        bool pressed, dragged; bool hotkeyWheel;
        string logPath;
        static System.Threading.Mutex appMutex;
        static System.Threading.EventWaitHandle showEvent;
        static System.Threading.EventWaitHandle quitEvent;
        System.Windows.Interop.HwndSource hkSrc;
        const int HK_QUICK_A = 0xB10, HK_QUICK_B = 0xB11, HK_ESC = 0xB12;
        int lastHotkeyTick;
        public static Action<int> HotkeyFire;
        public static System.Windows.Threading.Dispatcher UiDispatcher;
        static readonly string showReqFile = System.IO.Path.Combine(Paths.Root, "show.req");

        [STAThread]
        public static void Main()
        {
            var app = new WheelApp();
            app.DispatcherUnhandledException += (s, e) => { Log.W("crash: " + e.Exception); };
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            MouseHook.AcceptInjected = e.Args.Contains("--accept-injected");
            if (e.Args.Contains("--selftest")) { int rc = SelfTest.Run(); Shutdown(rc); return; }
            foreach (string ga in e.Args)
            {
                if (ga.StartsWith("--gridtest="))
                {
                    int gn;
                    if (!int.TryParse(ga.Substring(11), out gn)) gn = 60;
                    var fake = new List<AppEntry>();
                    string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                    for (int gi = 0; gi < gn; gi++)
                    {
                        var fe = new AppEntry();
                        fe.Name = "App" + gi.ToString("00");
                        fe.Path = @"C:\Windows\System32\notepad.exe";
                        fe.Initial = letters[gi % letters.Length];
                        fake.Add(fe);
                    }
                    apps = fake;
                    Log.W("gridtest items=" + gn);
                    Dispatcher.BeginInvoke(new Action(delegate { ShowWheel(1, 1280, 800, 1280, 800); }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            Click.Enabled = Settings.SoundOn;
            Click.SetVolume(Settings.Volume);
            Log.W("=== start === injectedOK=" + MouseHook.AcceptInjected);
            logPath = System.IO.Path.Combine(Paths.Root, "log.txt");

            // 单实例：已经在跑就通知它弹轮盘，然后退出
            string instName = "WheelLauncher.SingleInstance." + System.IO.Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
            bool created;
            appMutex = new System.Threading.Mutex(true, instName, out created);
            showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, instName + ".show");
            quitEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, instName + ".quit");
            if (!created)
            {
                Log.W("another instance running, signal show and exit");
                try { showEvent.Set(); } catch { }
            try { System.IO.File.WriteAllText(showReqFile, "1"); } catch { }
                Shutdown();
                return;
            }
            var listen = new System.Threading.Thread(delegate ()
            {
                while (true)
                {
                    try
                    {
                        int sig = System.Threading.WaitHandle.WaitAny(new System.Threading.WaitHandle[] { showEvent, quitEvent }, 700);
                        if (sig == 1) { Log.W("quit requested"); Dispatcher.BeginInvoke(new Action(delegate { Shutdown(); })); return; }
                        bool relay = sig == 0;
                        try { if (System.IO.File.Exists(showReqFile)) { System.IO.File.Delete(showReqFile); relay = true; } } catch { }
                        if (!relay) continue;
                        Dispatcher.BeginInvoke(new Action(delegate { try { ShowAtCursor(); } catch (Exception ex) { Log.W("relay show failed: " + ex.Message); } }));
                    }
                    catch (Exception ex) { Log.W("relay loop: " + ex.Message); return; }
                }
            });
            listen.IsBackground = true;
            listen.Start();

            scale = GetScale();
            if (Settings.AutoFolders)
            {
                try { if (!Settings.FoldersSeeded) { AutoFolders.EnsureDefaults(); Settings.FoldersSeeded = true; } } catch { }
            }
            apps = Scanner.FromDesktop();

            hook = new MouseHook();
            hook.SideDown += OnDown;
            hook.SideUp += OnUp;
            hook.Move += OnMove;
            var tick = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input);
            tick.Interval = TimeSpan.FromMilliseconds(25);
            tick.Tick += delegate { Native.POINT cp; Native.GetCursorPos(out cp); OnMove(cp.x, cp.y); };
            tick.Start();
            var wd = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
            wd.Interval = TimeSpan.FromMilliseconds(300);
            wd.Tick += delegate
            {
                if (wheel != null && (DateTime.Now - lastAct).TotalSeconds > 30) { Log.W("watchdog close (idle)"); CloseWheel(); }
            };
            wd.Start();
            MouseHook.Quick += delegate(int b) { hotkeyWheel = true; Native.POINT cp; Native.GetCursorPos(out cp); ShowWheel(b, cp.x, cp.y, cp.x, cp.y); };
            MouseHook.CommitKey += delegate { KbCommit(); };
            MouseHook.ChordRelease = delegate
            {
                // 键盘方式没有「滑动」语义：松开 A/B 不启动也不关闭，轮盘留在屏幕上等左键点击，Esc 才退出
                if (wheel == null) return;
                Log.W("chord release keep wheel (hotkey mode)");
            };
            hook.Escape += OnEscape;
            MouseHook.WheelOpen = delegate { return wheel != null || grid != null; };
            if (e.Args.Contains("--fliptest"))
            {
                DispatcherTimerOneShot(300, delegate { var p = System.Windows.Forms.Cursor.Position; ShowWheel(1, p.X, p.Y, p.X, p.Y); });
                DispatcherTimerOneShot(1200, delegate { Log.W("fliptest next"); WheelFlip(1); });
                DispatcherTimerOneShot(2000, delegate { Log.W("fliptest next2"); WheelFlip(1); });
                DispatcherTimerOneShot(2800, delegate { Log.W("fliptest prev"); WheelFlip(-1); });
                DispatcherTimerOneShot(3600, delegate { Log.W("fliptest done"); CloseWheel(); });
            }

            MouseHook.KbMove += delegate (int kx, int ky) { KbMove(kx, ky); };
            Gamepad.Trigger = delegate (int which)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (wheel != null) { CloseWheel(); return; }
                    hotkeyWheel = true;
                    Native.POINT cp; Native.GetCursorPos(out cp);
                    ShowWheel(which, cp.x, cp.y, cp.x, cp.y);
                    Log.W("gamepad trigger set=" + (which == 2 ? "N-Z" : "A-M"));
                }));
            };
            Gamepad.Nav = delegate (int gx, int gy) { Dispatcher.BeginInvoke(new Action(delegate { KbMove(gx, gy); })); };
            Gamepad.Stick = delegate (double snx, double sny) { Dispatcher.BeginInvoke(new Action(delegate { StickMove(snx, sny); })); };
            Gamepad.Commit = delegate { Dispatcher.BeginInvoke(new Action(delegate { KbCommit(); })); };
            Gamepad.Cancel = delegate { Dispatcher.BeginInvoke(new Action(delegate { OnEscape(); })); };
        MouseHook.PageFlip = delegate (int d) { PageFlip(d); };
        Gamepad.Page = delegate (int d) { Dispatcher.BeginInvoke(new Action(delegate { PageFlip(d); })); };
            Gamepad.Start();
        MouseHook.LeftClick = delegate (int lx, int ly)
        {
            if (grid != null) { if (grid.HitGear(lx, ly, scale)) { CloseWheel(); Log.W("grid click gear -> settings"); OpenWheelSettings(); return true; } var gg = grid.CommitAt(lx, ly, scale); CloseWheel(); if (gg != null) { Log.W("grid click launch " + gg.Name); Launcher.Run(gg); } return true; }
            if (wheel == null) return false;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                var w2 = wheel;
                if (w2 == null) return;
                double s2 = w2.WinScale;
                try
                {
                    bool hubHit = w2.HitHub(lx, ly, s2);
                    if (hubHit) { CloseWheel(); Log.W("left click hub -> settings"); OpenWheelSettings(); return; }
                    var a2 = w2.CommitAt(lx, ly, s2);
                    CloseWheel();
                    if (a2 != null) { Log.W("left click launch " + a2.Name); Launcher.Run(a2); }
                    else Log.W("left click blank -> close");
                }
                catch (Exception ex) { Log.W("left click err " + ex.Message); CloseWheel(); }
            }));
            return true;
        };
            hook.Install(Settings.TabHotkey);
            if (!hook.Ok) Log.W("HOOK INSTALL FAILED - hotkeys will not work");

            GameWatch.Changed = delegate (bool on)
            {
                try
                {
                    if (on) CloseWheel();
                    if (tray != null) tray.Text = on ? "轮盘启动器（游戏中已暂停）" : "轮盘启动器";
                }
                catch { }
            };
            GameWatch.Start();

            tray = new WF.NotifyIcon();
            tray.Icon = LoadExeIcon() ?? MakeIcon();
            tray.Text = "轮盘启动器";
            tray.Visible = true;
            tray.DoubleClick += (s, a) => { };
            tray.MouseClick += delegate (object s, WF.MouseEventArgs a) { if (a.Button == WF.MouseButtons.Left) ShowAtCursor(); };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("显示轮盘", null, (s, a) => ShowAtCursor());
            menu.Items.Add("重新扫描应用", null, (s, a) => RescanAsync());
            menu.Items.Add("添加应用…", null, (s, a) => OpenAddDialog());
            menu.Items.Add("圆盘设置（内容 / 游戏 / 外观）…", null, (s, a) => OpenWheelSettings());
            menu.Items.Add("长按与音效…", null, (s, a) => OpenSettings());
            var gap = new WF.ToolStripMenuItem("游戏自动暂停（全屏/游戏目录时）") { CheckOnClick = true, Checked = Settings.AutoPause };
            gap.CheckedChanged += (s, a) => { Settings.AutoPause = gap.Checked; Log.W("autopause=" + gap.Checked); };
            menu.Items.Add(gap);
            var snd = new WF.ToolStripMenuItem("选中音效") { CheckOnClick = true, Checked = Settings.SoundOn };
            snd.CheckedChanged += (s, a) => { Settings.SoundOn = snd.Checked; Log.W("sound=" + snd.Checked); };
            menu.Items.Add(snd);
            menu.Items.Add("测试音效", null, (s, a) => Click.Test());
            var auto = new WF.ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsAutoStart() };
            auto.CheckedChanged += (s, a) => SetAutoStart(auto.Checked);
            menu.Items.Add(auto);
            var logonTask = new WF.ToolStripMenuItem("开机自动启动（管理员权限，无 UAC 提示）") { CheckOnClick = true, Checked = TaskInstalled() };
            logonTask.CheckedChanged += (s, a) => { ToggleLogonTask(logonTask.Checked); tray.ShowBalloonTip(1500, "轮盘启动器", logonTask.Checked ? "已设置开机自动启动" : "已取消开机自动启动", WF.ToolTipIcon.Info); };
            menu.Items.Add(logonTask);
            if (!WheelApp.IsElevated)
                menu.Items.Add("以管理员身份重启（加速器/管理员程序下也能呼出）…", null, (s, a) => RestartElevated());
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, a) => Shutdown());
            tray.ContextMenuStrip = menu;

            var rs = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
            rs.Interval = TimeSpan.FromMinutes(3);
            rs.Tick += delegate { RescanAsync(); };
            rs.Start();

            Log.W("ready apps=" + apps.Count + " scale=" + scale);
            if (!Settings.FirstRunDone || e.Args.Contains("--firstrun"))
                Dispatcher.BeginInvoke(new Action(delegate { ShowFirstRun(WF.SystemInformation.MouseButtons < 5); }), DispatcherPriority.Background);
            Click.Warm();
            if (e.Args.Contains("--ticktest")) { Log.W("ticktest"); Click.Test(); }
            if (e.Args.Contains("--probe2")) { Dispatcher.BeginInvoke(new Action(() => { ShowWheel(0, 1280, 700, 1280, 700); DispatcherTimerOneShot(700, delegate { if (wheel != null) wheel.Probe((int)Math.Round(scale)); }); })); }
            if (e.Args.Contains("--addtest")) { Dispatcher.BeginInvoke(new Action(() => OpenAddDialog())); }
            if (e.Args.Contains("--wheelsettings")) { startGameTab = e.Args.Contains("--gametab"); startLookTab = e.Args.Contains("--looktab"); Dispatcher.BeginInvoke(new Action(() => OpenWheelSettings())); }
            if (e.Args.Contains("--autofolders")) { Dispatcher.BeginInvoke(new Action(delegate { var w = new AutoFoldersWindow(); w.ShowDialog(); })); }
            if (e.Args.Contains("--shot")) WheelWindow.ShotMode = true;
            if (e.Args.Contains("--show1")) { Dispatcher.BeginInvoke(new Action(() => { var w = WF.Screen.PrimaryScreen.WorkingArea; ShowWheel(1, w.Width / 2, w.Height / 2, w.Width / 2 - 200, w.Height / 2);
            if (e.Args.Contains("--clicktest")) DispatcherTimerOneShot(900, delegate { Log.W("clicktest fire"); if (MouseHook.LeftClick != null) MouseHook.LeftClick(w.Width / 2, w.Height / 2); });
            if (e.Args.Contains("--clickicon")) DispatcherTimerOneShot(1100, delegate {
                var ww = wheel; if (ww == null || MouseHook.LeftClick == null) { Log.W("clickicon skip"); return; }
                var rg = ww.Rings[0]; double ang = -Math.PI / 2;
                int ix = (int)((ww.Left + ww.cx + Math.Cos(ang) * rg.Radius) * ww.WinScale);
                int iy = (int)((ww.Top + ww.cy + Math.Sin(ang) * rg.Radius) * ww.WinScale);
                Log.W("clickicon at " + ix + "," + iy + " ring=" + rg.Radius.ToString("0.0") + " item=" + rg.Items[0].Name);
                MouseHook.LeftClick(ix, iy);
            }); }), DispatcherPriority.Background); }
            if (e.Args.Contains("--show2")) { Dispatcher.BeginInvoke(new Action(() => { var w = WF.Screen.PrimaryScreen.WorkingArea; ShowWheel(2, w.Width / 2, w.Height / 2, w.Width / 2 - 200, w.Height / 2); }), DispatcherPriority.Background); }
            if (e.Args.Contains("--show")) { Dispatcher.BeginInvoke(new Action(() => { var scr = WF.Screen.PrimaryScreen.WorkingArea; ShowWheel(0, scr.Width / 2, scr.Height / 2, scr.Width / 2, scr.Height / 2); }), DispatcherPriority.Background); }

            try
            {
                var hp = new System.Windows.Interop.HwndSourceParameters("WheelLauncherHotkeys");
                hp.Width = 1; hp.Height = 1;
                hp.PositionX = -32000; hp.PositionY = -32000;
                hp.WindowStyle = unchecked((int)0x80000000);
                hp.ExtendedWindowStyle = 0x00000080 | 0x08000000;
                MouseHook.HotkeyFire = QuickWheel;
                MouseHook.UiDispatcher = Dispatcher;
                hkSrc = new System.Windows.Interop.HwndSource(hp);
                hkSrc.AddHook(HotkeyHook);
                TryRegisterHotkeys();
                if (!hotkeysOk)
                {
                    // 启动瞬间常与旧实例/其它程序抢注册，失败就每 3 秒重试，直到拿到为止
                    var hkRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    hkRetry.Tick += delegate { TryRegisterHotkeys(); if (hotkeysOk) hkRetry.Stop(); };
                    hkRetry.Start();
                }
            }
            catch (Exception ex) { Log.W("hotkeys failed: " + ex.Message); }

            if (e.Args.Contains("--elevated-restart"))
            {
                Log.W("elevated restart handover");
                System.Threading.Thread.Sleep(1800);
                Log.W("elevated restart continuing");
            }
        }

        public void OpenSettings()
        {
            var fg = new SolidColorBrush(Ui.InkCol);
            var dim = new SolidColorBrush(Ui.SubCol);
            var win = new Window();
            win.Title = "轮盘启动器 · 设置";
            win.Width = 500;
            win.SizeToContent = SizeToContent.Height;
            win.ResizeMode = ResizeMode.NoResize;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Background = new SolidColorBrush(Ui.BgCol);
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };

            p.Children.Add(new TextBlock { Text = "呼出判定时间", Foreground = fg, FontSize = 15, FontWeight = FontWeights.SemiBold });
            p.Children.Add(new TextBlock { Text = "按住鼠标侧键超过这个时间才会弹出轮盘；短按不会触发。", Foreground = dim, FontSize = 12, Margin = new Thickness(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap });
            var lbLp = new TextBlock { Text = Settings.LongPress + " 毫秒", Foreground = fg, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
            var slLp = new Slider { Minimum = 250, Maximum = 2000, Value = Settings.LongPress, TickFrequency = 50, IsSnapToTickEnabled = true };
            slLp.ValueChanged += (s, a) => { lbLp.Text = ((int)slLp.Value) + " 毫秒"; };
            p.Children.Add(slLp);
            p.Children.Add(lbLp);

            p.Children.Add(new TextBlock { Text = "选中音效音量", Foreground = fg, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            var lbVol = new TextBlock { Text = Settings.Volume + " %", Foreground = fg, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
            var slVol = new Slider { Minimum = 0, Maximum = 100, Value = Settings.Volume, TickFrequency = 5, IsSnapToTickEnabled = true };
            slVol.ValueChanged += (s, a) => { lbVol.Text = ((int)slVol.Value) + " %"; Click.SetVolume((int)slVol.Value); };
            p.Children.Add(slVol);
            p.Children.Add(lbVol);

            var cbSound = new CheckBox { Content = "选中应用时播放哒哒声", IsChecked = Settings.SoundOn, Foreground = fg, Margin = new Thickness(0, 4, 0, 4) };
            Ui.StyleCheck(cbSound);
            p.Children.Add(cbSound);
            var cbTab = new CheckBox { Content = "启用 Tab+A / Tab+B 快捷键", IsChecked = Settings.TabHotkey, Foreground = fg, Margin = new Thickness(0, 0, 0, 12) };
            Ui.StyleCheck(cbTab);
            p.Children.Add(cbTab);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var testBtn = new Button { Content = "测试音效", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Ui.LineCol), Foreground = fg };
            testBtn.Click += (s, a) => Click.Test();
            var ok = new Button { Content = "保存", Padding = new Thickness(22, 5, 22, 5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Ui.AccentCol), Foreground = Brushes.White };
            var cancel = new Button { Content = "取消", Padding = new Thickness(18, 5, 18, 5), Background = new SolidColorBrush(Ui.LineCol), Foreground = fg };
            row.Children.Add(testBtn);
            row.Children.Add(ok);
            row.Children.Add(cancel);
            p.Children.Add(row);

            ok.Click += (s, a) =>
            {
                Settings.LongPress = (int)slLp.Value;
                Settings.Volume = (int)slVol.Value;
                Settings.SoundOn = cbSound.IsChecked == true;
                Settings.TabHotkey = cbTab.IsChecked == true;
                Click.SetVolume(Settings.Volume);
                if (hook != null) hook.SetKeyboard(Settings.TabHotkey);
                Log.W("settings saved longpress=" + Settings.LongPress + " volume=" + Settings.Volume + " sound=" + Settings.SoundOn + " tab=" + Settings.TabHotkey);
                win.Close();
            };
            cancel.Click += (s, a) => win.Close();

            win.Content = p;
            win.ShowDialog();
        }

        void ShowFirstRun(bool noSideButtons)
        {
            Settings.FirstRunDone = true;
            var fg = new SolidColorBrush(Ui.InkCol);
            var dim = new SolidColorBrush(Ui.SubCol);
            var win = new Window();
            win.Title = "欢迎使用轮盘启动器";
            win.Width = 560;
            win.SizeToContent = SizeToContent.Height;
            win.ResizeMode = ResizeMode.NoResize;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Background = new SolidColorBrush(Ui.BgCol);
            var p = new StackPanel { Margin = new Thickness(26, 22, 26, 18) };
            p.Children.Add(new TextBlock { Text = "轮盘启动器", Foreground = fg, FontSize = 20, FontWeight = FontWeights.SemiBold });
            p.Children.Add(new TextBlock
            {
                Foreground = dim,
                FontSize = 12.5,
                Margin = new Thickness(0, 4, 0, 14),
                TextWrapping = TextWrapping.Wrap,
                Text = "按首字母把桌面上的程序排成几个圆环，滑到目标上就能打开。"
            });
            var steps = new StackPanel();
            Action<string> add = delegate (string s)
            {
                steps.Children.Add(new TextBlock { Text = s, Foreground = fg, FontSize = 13, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap });
            };
            add("1. 按住鼠标侧键约 0.65 秒呼出轮盘，滑到图标上松开即启动。");
            add("2. 松开在空白处＝取消，不启动任何程序；Esc 随时退出。");
            add("3. 侧键 1 = A–M 的应用，侧键 2 = N–Z 的应用。");
            add("4. 没有侧键：按 Tab+A / Tab+B 呼出，再点图标启动，Esc 关闭。");
            add("5. 圆环中心的齿轮：点一下打开设置（应用 / 游戏 / 外观）。");
            add("6. 托盘图标：左键单击呼出轮盘，右键打开菜单（设置 / 开机自启 / 退出）。");
            p.Children.Add(steps);
            if (noSideButtons)
            {
                var warn = new Border
                {
                    Background = new SolidColorBrush(Ui.WarnBgCol),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 12, 0, 4)
                };
                var wt = new TextBlock
                {
                    Text = "没有检测到鼠标侧键。请使用 Tab+A / Tab+B 呼出，或换一只带侧键的鼠标。",
                    Foreground = new SolidColorBrush(Ui.WarnFgCol),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap
                };
                warn.Child = wt;
                p.Children.Add(warn);
            }
            var ok = new Button
            {
                Content = "开始使用",
                Padding = new Thickness(24, 6, 24, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                Background = new SolidColorBrush(Ui.AccentCol),
                Foreground = Brushes.White
            };
            ok.Click += (s, a) => win.Close();
            p.Children.Add(ok);
            win.Content = p;
            Log.W("first run guide shown noSide=" + noSideButtons);
            win.ShowDialog();
        }

        static double GetScaleAt(int x, int y)
        {
            try
            {
                Native.POINT pt; pt.x = x; pt.y = y;
                IntPtr mon = Native.MonitorFromPoint(pt, 2);
                uint dx, dy;
                if (mon != IntPtr.Zero && Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
            }
            catch { }
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
        }

        static double GetScale()
        {
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
        }

        static System.Drawing.Icon MakeIcon()
        {
            var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                var colors = new[] {
                    System.Drawing.Color.FromArgb(255, 108, 70, 232),
                    System.Drawing.Color.FromArgb(255, 60, 130, 240),
                    System.Drawing.Color.FromArgb(255, 70, 200, 160) };
                for (int i = 0; i < 6; i++)
                {
                    using (var b = new System.Drawing.SolidBrush(colors[i % colors.Length]))
                        g.FillPie(b, 2, 2, 28, 28, i * 60, 54);
                }
                using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 20, 20, 26)))
                    g.FillEllipse(b, 12, 12, 8, 8);
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        public static bool IsAutoStart()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return k != null && k.GetValue("WheelLauncher") != null;
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (on) k.SetValue("WheelLauncher", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue("WheelLauncher", false);
                }
            }
            catch (Exception ex) { Log.W("autostart failed: " + ex.Message); }
        }

        void OnDown(int btn, int x, int y)
        {
            pressed = true; dragged = false; hadWheel = false;
            downBtn = btn; downX = x; downY = y; pressAt = DateTime.Now; showX = x; showY = y;
        }

        void OnMove(int x, int y)
        {
            if (pressed && !dragged && wheel == null && (DateTime.Now - pressAt).TotalMilliseconds >= LongPressMs)
            {
                dragged = true; hadWheel = true;
                hotkeyWheel = false;
            kbGearHold = false;
                showX = x; showY = y;
                ShowWheel(downBtn, x, y, x, y);
                Log.W("longpress show btn=" + downBtn);
            }
            if (grid != null)
            {
            bool gkbHold = (kbSel != null || kbGearHold) && (DateTime.Now - kbAt).TotalSeconds < 20 && Math.Abs(x - kbCurX) < 6 && Math.Abs(y - kbCurY) < 6;
                bool gstickHold = stickOn && Math.Abs(x - stickCurX) < 8 && Math.Abs(y - stickCurY) < 8;
                grid.clampMode = false;
                if (!gkbHold && !gstickHold) { kbSel = null; stickOn = false; grid.UpdateHover(x, y, scale); }
                lastAct = DateTime.Now;
                return;
            }
            if (wheel != null)
            {
            bool kbHold = (kbSel != null || kbGearHold) && (DateTime.Now - kbAt).TotalSeconds < 20 && Math.Abs(x - kbCurX) < 6 && Math.Abs(y - kbCurY) < 6;
                bool stickHold = stickOn && Math.Abs(x - stickCurX) < 8 && Math.Abs(y - stickCurY) < 8;
                if (!kbHold && !stickHold) { kbSel = null; stickOn = false; wheel.UpdateHover(x, y, scale); }
                lastAct = DateTime.Now;
            }
        }

        void OnUp(int btn, int x, int y)
        {
            if (!pressed) return;
            if (wheel != null && hotkeyWheel) { pressed = false; Log.W("side release closes hotkey wheel"); CloseWheel(); return; }
            if (wheel != null && btn != downBtn) return;   // 另一个侧键的抬起不参与提交
            if (grid != null)
            {
                pressed = false;
                var gh = grid.CommitAt(x, y, scale);
                CloseWheel();
                if (gh != null) { Log.W("grid release launch " + gh.Name); Launcher.Run(gh); }
                return;
            }
            pressed = false;
            if (wheel != null)
            {
                double mv = Math.Sqrt((x - showX) * (double)(x - showX) + (y - showY) * (double)(y - showY));
                if (wheel.HitHub(x, y, scale))
                {
                    if (Settings.ReleaseMode == 0) CloseWheel();   // 中心键只认左键点击，松手不触发
                    Log.W("hub release ignored");
                    return;
                }
                var hitSel = wheel.CommitAt(x, y, scale);
                if (Settings.ReleaseMode == 1)
                {
                    // 只呼出轮盘：松开侧键不启动，轮盘留着等左键点选（Esc / 点空白 / 再按一次侧键关闭）
                    if (wheel.Reach < wheel.HubRadius * 0.9) { CloseWheel(); }   // 原地按了又松，视作取消
                    else Log.W("release mode=click keep wheel shown reach=" + (int)wheel.Reach);
                    return;
                }
                CloseWheel();
                if (hitSel != null) Launcher.Run(hitSel);
                else Log.W("commit null x=" + x + " y=" + y + " (no icon within reach)");
                return;
            }
            // 长按已经超过阈值、只是轮盘还没来得及弹（钩子回调与计时器有 25ms 级延迟）：
            // 这次松手不要再补发"后退"点击，否则用户会看到浏览器/资源管理器后退一下。
            double held = (DateTime.Now - pressAt).TotalMilliseconds;
            if (!hadWheel && held >= LongPressMs - 80) Log.W("longpress missed at release held=" + (int)held);
            if (!hadWheel && !MouseHook.AcceptInjected && held < LongPressMs - 80) Native.XClick((uint)downBtn);
        }

        void DispatcherTimerOneShot(int ms, Action a)
        {
            var t2 = new System.Windows.Threading.DispatcherTimer();
            t2.Interval = TimeSpan.FromMilliseconds(ms);
            t2.Tick += delegate { t2.Stop(); a(); };
            t2.Start();
        }

        void OpenAddDialog()
        {
            try
            {
                var dlg = new AddAppDialog();
                if (dlg.ShowDialog() == true)
                {
                    Scanner.AddCustom(dlg.ResultName, dlg.ResultPath, dlg.ResultArgs);
                    apps = Scanner.FromDesktop();
                    Log.W("custom added: " + dlg.ResultName + " -> " + dlg.ResultPath + " apps=" + apps.Count);
                }
            }
            catch (Exception ex) { Log.W("OpenAddDialog failed: " + ex.Message); }
        }

        void OpenWheelSettings()
        {
            try
            {
                for (int guard = 0; guard < 4; guard++)
                {
                    var w = new WheelSettingsWindow(startLookTab ? 2 : (startGameTab ? 1 : 0));
                    w.Reloaded = delegate { apps = Scanner.FromDesktop(); };
                    w.ShowDialog();
                    if (w.Changed) apps = Scanner.FromDesktop();
                    Log.W("wheel settings closed changed=" + w.Changed + " reopen=" + w.Reopen + " apps=" + apps.Count);
                    if (!w.Reopen) break;   // 切换主题后重建窗口，用新配色重开
                    startGameTab = false; startLookTab = false;
                }
            }
            catch (Exception ex) { Log.W("OpenWheelSettings failed: " + ex.Message); }
        }

        bool hotkeysOk;
        void TryRegisterHotkeys()
        {
            if (hkSrc == null) return;
            try
            {
                Native.UnregisterHotKey(hkSrc.Handle, HK_QUICK_A);
                Native.UnregisterHotKey(hkSrc.Handle, HK_QUICK_B);
                Native.UnregisterHotKey(hkSrc.Handle, 0xB13);
                Native.UnregisterHotKey(hkSrc.Handle, 0xB14);
            }
            catch { }
            bool a1 = Native.RegisterHotKey(hkSrc.Handle, HK_QUICK_A, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x41);
            bool a2 = Native.RegisterHotKey(hkSrc.Handle, HK_QUICK_B, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x42);
            bool a3 = Native.RegisterHotKey(hkSrc.Handle, 0xB13, Native.MOD_NOREPEAT, 0x76);
            bool a4 = Native.RegisterHotKey(hkSrc.Handle, 0xB14, Native.MOD_NOREPEAT, 0x77);
            hotkeysOk = a1 && a2 && a3 && a4;
            Log.W("hotkeys ctrl+alt+A=" + a1 + " ctrl+alt+B=" + a2 + " F7=" + a3 + " F8=" + a4);
        }

        IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int id = (int)wParam;
                if (id == HK_QUICK_A) { QuickWheel(1); handled = true; }
                else if (id == HK_QUICK_B) { QuickWheel(2); handled = true; }
                else if (id == 0xB13) { if ((Native.GetAsyncKeyState(0x76) & 0x8000) == 0 || wheel == null) QuickWheel(1); handled = true; }
                else if (id == 0xB14) { if ((Native.GetAsyncKeyState(0x77) & 0x8000) == 0 || wheel == null) QuickWheel(2); handled = true; }
                else if (id == HK_ESC) { CloseWheel(); handled = true; }
            }
            return IntPtr.Zero;
        }

        void QuickWheel(int which)
        {
            // 长按热键会连发：已经在显示就直接忽略，另加 350ms 去抖
            if (wheel != null) return;
            if ((Environment.TickCount - lastHotkeyTick) < 350) return;
            lastHotkeyTick = Environment.TickCount;
            Native.POINT p;
            Native.GetCursorPos(out p);
            hotkeyWheel = true;
            Log.W("hotkey quick show " + which);
            ShowWheel(which, p.x, p.y, p.x, p.y);
        }

        void RestartElevated()
        {
            try
            {
                if (quitEvent != null) { try { quitEvent.Set(); } catch { } }
                var psi = new System.Diagnostics.ProcessStartInfo(WF.Application.ExecutablePath, "--elevated-restart");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                Shutdown();
            }
            catch (Exception ex) { Log.W("elevate failed: " + ex.Message); }
        }

        static bool TaskInstalled()
        {
            try
            {
                var p = new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Query /TN WheelLauncherAutostart");
                p.UseShellExecute = false; p.RedirectStandardOutput = true; p.RedirectStandardError = true; p.CreateNoWindow = true;
                using (var pr = System.Diagnostics.Process.Start(p)) { pr.WaitForExit(4000); return pr.ExitCode == 0; }
            }
            catch { return false; }
        }

        void ToggleLogonTask(bool on)
        {
            try
            {
                string exe = WF.Application.ExecutablePath;
                string args = on
                    ? "/Create /F /SC ONLOGON /RL HIGHEST /TN WheelLauncherAutostart /TR \"\\\"" + exe + "\\\"\""
                    : "/Delete /F /TN WheelLauncherAutostart";
                var p = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args);
                p.UseShellExecute = false; p.CreateNoWindow = true; p.RedirectStandardOutput = true; p.RedirectStandardError = true;
                using (var pr = System.Diagnostics.Process.Start(p)) { pr.WaitForExit(8000); Log.W("logon task " + on + " exit=" + pr.ExitCode); }
            }
            catch (Exception ex) { Log.W("logon task failed: " + ex.Message); }
        }

        void OnEscape()
        {
            if (wheel != null || grid != null) CloseWheel();
        }

        DateTime lastAct = DateTime.Now;
        List<List<Ring>> BuildWheelPages(List<AppEntry> ordered, double hub, double budget)
        {
            var groups = new List<List<AppEntry>>();
            foreach (var g in ordered.GroupBy(a => a.Initial == '#' ? '[' : a.Initial).OrderBy(g2 => g2.Key))
            {
                var members = g.ToList();
                if (groups.Count > 0 && members.Count <= 2 && groups[groups.Count - 1].Count <= 2)
                    groups[groups.Count - 1].AddRange(members);
                else
                    groups.Add(members);
            }

            // 原逻辑：环数过多就合并相邻最小两组，最多 4 环
            while (groups.Count > 4)
            {
                int bi = 0; int bs = int.MaxValue;
                for (int i = 0; i + 1 < groups.Count; i++)
                {
                    int ss = groups[i].Count + groups[i + 1].Count;
                    if (ss < bs) { bs = ss; bi = i; }
                }
                groups[bi].AddRange(groups[bi + 1]);
                groups.RemoveAt(bi + 1);
            }

            // 拥挤检测：若某环的槽位间距小于图标 1.15 倍，才启用分页
            bool crowded = groups.Count > 4;
            if (!crowded)
            {
                double iconTry = Math.Min(42, Math.Max(20, (budget - hub - 10) / (1.95 + (Math.Max(1, groups.Count) - 1) * 1.55)));
                double stepTry = iconTry * 1.55;
                for (int i = 0; i < groups.Count; i++)
                {
                    double rr = hub + iconTry * 1.15 + i * stepTry;
                    double arc = 2 * Math.PI * rr / Math.Max(1, groups[i].Count);
                    if (arc < iconTry * 1.15) { crowded = true; break; }
                }
            }

            List<List<AppEntry>> segs = groups;
            if (crowded)
            {
                int capRing = 8;
                segs = new List<List<AppEntry>>();
                foreach (var grp in groups)
                    for (int s = 0; s < grp.Count; s += capRing)
                        segs.Add(grp.GetRange(s, Math.Min(capRing, grp.Count - s)));
            }
            if (segs.Count == 0) return new List<List<Ring>>();

            int ringsForSizing = Math.Min(4, segs.Count);
            wIcon = Math.Min(42, Math.Max(20, (budget - hub - 10) / (1.95 + (ringsForSizing - 1) * 1.55)));
            double step = wIcon * 1.55;
            wR0 = hub + wIcon * 1.15 + (ringsForSizing - 1) * step;

            var pages = new List<List<Ring>>();
            int perPage = crowded ? 4 : Math.Max(1, segs.Count);
            for (int p = 0; p * perPage < segs.Count; p++)
            {
                var page = new List<Ring>();
                for (int k = 0; k < perPage && p * perPage + k < segs.Count; k++)
                {
                    var items = segs[p * perPage + k];
                    page.Add(new Ring { Letter = items[0].Initial, Part = 1, Index = k, Radius = hub + wIcon * 1.15 + k * step, Capacity = items.Count, Items = items });
                }
                pages.Add(page);
            }

            double maxOuter = wR0 + wIcon * 0.72;
            if (maxOuter > budget)
            {
                double f0 = budget / maxOuter;
                wIcon *= f0; wR0 *= f0;
                foreach (var pg in pages) foreach (var r0 in pg) r0.Radius *= f0;
            }
            wHub = hub;
            return pages;
        }

        string HintText()
        {
            string h0 = wBtn == 1 ? "A-M" : (wBtn == 2 ? "N-Z" : "全部");
            if (wPages != null && wPages.Count > 1) h0 += "  " + (wPage + 1) + "/" + wPages.Count;
            return h0;
        }

        string TipText()
        {
            if (wPages != null && wPages.Count > 1) return "滚轮 / [ ] / 手柄 LB·RB 翻页 · " + (wPage + 1) + "/" + wPages.Count + " 页 · Esc 取消";
            return null;
        }

        void UpdateWheelRect()
        {
            try
            {
                if (wheel != null)
                {
                    IntPtr hw = new System.Windows.Interop.WindowInteropHelper(wheel).Handle;
                    Native.RECT rr;
                    if (Native.GetWindowRect(hw, out rr)) MouseHook.SetWheelRect(true, rr.Left, rr.Top, rr.Right, rr.Bottom);
                    return;
                }
                if (grid != null)
                {
                    IntPtr hg = new System.Windows.Interop.WindowInteropHelper(grid).Handle;
                    Native.RECT rg;
                    if (Native.GetWindowRect(hg, out rg)) MouseHook.SetWheelRect(true, rg.Left, rg.Top, rg.Right, rg.Bottom);
                    return;
                }
                MouseHook.SetWheelRect(false, 0, 0, 0, 0);
            }
            catch { }
        }

        void PageFlip(int d)
        {
            if (grid != null) { GridFlip(d); return; }
            WheelFlip(d);
        }

        void GridFlip(int d)
        {
            if (grid == null) { Log.W("grid flip ignored (no grid)"); return; }
            if (grid.PageCount < 2) { Log.W("grid flip ignored (pages=" + grid.PageCount + ")"); return; }
            int p = (grid.PageIndex + d) % grid.PageCount;
            if (p < 0) p += grid.PageCount;
            grid.SetPage(p);
            grid.UpdateLayout();
            UpdateWheelRect();
            Log.W("grid page " + (p + 1) + "/" + grid.PageCount);
        }

        void WheelFlip(int d)
        {
            if (wheel == null || wPages == null || wPages.Count < 2) return;
            wPage = (wPage + d) % wPages.Count; if (wPage < 0) wPage += wPages.Count;
            wheel.SetLayout(wPages[wPage], wIcon, wHub, wR0, HintText(), TipText());
            wheel.UpdateLayout();
            UpdateWheelRect();
            Log.W("wheel page " + (wPage + 1) + "/" + wPages.Count);
            lastAct = DateTime.Now;
        }

        void ShowWheel(int btn, int sx, int sy, int curX, int curY)
        {
            if (Settings.WheelStyle == 1) { ShowGrid(btn, sx, sy); return; }
            try
            {
                scale = GetScaleAt(sx, sy);
                kbSel = null;
                stickOn = false; stickX = 0; stickY = 0; kbSel = null;
                var scr = WF.Screen.FromPoint(new System.Drawing.Point(sx, sy)).WorkingArea;
                double budget = (Math.Min(scr.Width, scr.Height) / scale / 2 - 8) * (Settings.WheelSize / 100.0);
                double hub = 44 * (Settings.WheelSize / 100.0);

                List<AppEntry> sub;
                string tag;
                if (btn == 1) { sub = apps.Where(a => a.Initial <= 'M').ToList(); tag = "A-M"; }
                else if (btn == 2) { sub = apps.Where(a => a.Initial >= 'N' || a.Initial == '#').ToList(); tag = "N-Z"; }
                else { sub = apps; tag = "ALL"; }
                if (sub.Count == 0) { Log.W("empty subset " + tag); return; }

                var ordered = sub.OrderBy(a => a.Initial == '#' ? '[' : a.Initial).ThenBy(a => a.Name, StringComparer.CurrentCulture).ToList();

                wPages = BuildWheelPages(ordered, hub, budget);
                if (wPages.Count == 0) { Log.W("no layout"); return; }
                wPage = 0; wBtn = btn; wTag = tag;
                CloseWheel();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { Click.Warm(); });
                wheel = new WheelWindow();
                var selfW = wheel;
                wheel.Closed += delegate { if (ReferenceEquals(wheel, selfW)) wheel = null; };
            wheel.ResetReach();
                wheel.Pick = delegate (AppEntry a) { CloseWheel(); if (a != null) Launcher.Run(a); else Log.W("left click blank -> close wheel"); };
                wheel.HubPick = delegate { CloseWheel(); OpenWheelSettings(); };
                wheel.SetLayout(wPages[0], wIcon, wHub, wR0, HintText(), TipText());
                wheel.PlaceAt(sx, sy, scale);
                wheel.Show();
                if (hkSrc != null)
                {
                    Native.UnregisterHotKey(hkSrc.Handle, HK_ESC);
                    Native.RegisterHotKey(hkSrc.Handle, HK_ESC, Native.MOD_NOREPEAT, 0x1B);
                }
                UpdateWheelRect();
                wheel.UpdateLayout();
                wheel.UpdateHover(curX, curY, scale);
                lastAct = DateTime.Now;
                Log.W("set=" + tag + " rings=" + wPages[0].Count + " pages=" + wPages.Count + " icon=" + wIcon.ToString("0.0") + " names=" + string.Join("|", ordered.Select(a => a.Name + ":" + a.Initial).ToArray()));
            }
            catch (Exception ex) { Log.W("show failed: " + ex); }
            finally { if (wheel != null && !wheel.IsVisible) CloseWheel(); }
        }
        void ShowGrid(int btn, int sx, int sy)
        {
            try
            {
                scale = GetScaleAt(sx, sy);
                List<AppEntry> sub; string title;
                if (btn == 1) { sub = apps.Where(a => a.Initial <= 'M').ToList(); title = "A - M"; }
                else if (btn == 2) { sub = apps.Where(a => a.Initial >= 'N' || a.Initial == '#').ToList(); title = "N - Z"; }
                else { sub = apps; title = "全部应用"; }
                if (sub.Count == 0) { Log.W("empty subset grid"); return; }
                var ordered = sub.OrderBy(a => a.Initial == '#' ? '[' : a.Initial).ThenBy(a => a.Name, StringComparer.CurrentCulture).ToList();
                CloseWheel();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { Click.Warm(); });
                grid = new GridWindow(title);
                var selfG = grid;
                grid.Closed += delegate { if (ReferenceEquals(grid, selfG)) grid = null; };
                var scr = WF.Screen.FromPoint(new System.Drawing.Point(sx, sy)).WorkingArea;
                grid.Build(ordered, scr.Width / scale * 0.84, scr.Height / scale * 0.80);
                grid.PlaceAt(sx, sy, scale);
                grid.Show();
                if (hkSrc != null)
                {
                    Native.UnregisterHotKey(hkSrc.Handle, HK_ESC);
                    Native.RegisterHotKey(hkSrc.Handle, HK_ESC, Native.MOD_NOREPEAT, 0x1B);
                }
                try
                {
                    IntPtr hw = new System.Windows.Interop.WindowInteropHelper(grid).Handle;
                    Native.RECT rr;
                    if (Native.GetWindowRect(hw, out rr)) { MouseHook.SetWheelRect(true, rr.Left, rr.Top, rr.Right, rr.Bottom); Log.W("grid rect " + rr.Left + "," + rr.Top + " - " + rr.Right + "," + rr.Bottom); }
                }
                catch { }
                grid.UpdateLayout();
                grid.UpdateHover(sx, sy, scale);
                lastAct = DateTime.Now;
                Log.W("grid set=" + title + " items=" + ordered.Count);
            }
            catch (Exception ex) { Log.W("show grid failed: " + ex); }
            finally { if (grid != null && !grid.IsVisible) CloseWheel(); }
        }


static int Cap(double radius, double icon)
        {
            return Math.Max(1, (int)Math.Floor(2 * Math.PI * radius / (icon + 8)));
        }

        static List<Ring> BuildLayout(List<AppEntry> all, double r0, double spacing, double icon)
        {
            var rings = new List<Ring>();
            var groups = all.GroupBy(a => a.Initial)
                            .OrderBy(g => g.Key == '#' ? 999 : g.Key)
                            .ToList();
            foreach (var g in groups)
            {
                var items = g.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
                int i = 0, part = 1;
                while (i < items.Count)
                {
                    double radius = r0 + rings.Count * spacing;
                    int cap = Cap(radius, icon);
                    var ring = new Ring { Letter = g.Key, Part = part, Index = rings.Count, Radius = radius, Capacity = cap };
                    for (int k = 0; k < cap && i < items.Count; k++, i++) ring.Items.Add(items[i]);
                    rings.Add(ring);
                    part++;
                }
            }
            return rings;
        }

        AppEntry kbSel; int kbCurX, kbCurY; DateTime kbAt; bool kbGearHold;
        List<List<Ring>> wPages; int wPage, wBtn; double wIcon, wHub, wR0; string wTag;
        int kbLastDx, kbLastDy, kbLastTick;
        double stickX, stickY; bool stickOn; int stickCurX, stickCurY;
        void StickMove(double nx, double ny)
        {
            if (grid != null)
            {
                Native.POINT gp; Native.GetCursorPos(out gp); stickCurX = gp.x; stickCurY = gp.y;
                stickOn = true; kbSel = null;
                grid.CursorMove(nx, ny);
                lastAct = DateTime.Now;
                return;
            }
            if (wheel == null) { stickOn = false; return; }
            if (!stickOn) { stickX = 0; stickY = 0; stickOn = true; }
            double mag = Math.Sqrt(nx * nx + ny * ny);
            if (mag > 1) { nx /= mag; ny /= mag; mag = 1; }
            double step = 12 + 54 * mag;         // 每帧 DIP，越推越快
            stickX += nx * step; stickY += ny * step;
            double d = Math.Sqrt(stickX * stickX + stickY * stickY);
            double maxR = wheel.MaxRadius;
            if (d > maxR) { stickX = stickX / d * maxR; stickY = stickY / d * maxR; d = maxR; }
            // 贴到最近的环上：避免指针停在两环之间导致高亮反复闪烁
            double bestDiff = double.MaxValue, snapR = 0;
            foreach (var rg in wheel.Rings)
            {
                double diff = Math.Abs(d - rg.Radius);
                if (diff < bestDiff) { bestDiff = diff; snapR = rg.Radius; }
            }
            double hx = stickX, hy = stickY;
            if (snapR > 0 && d > 0.001)
            {
                double ux = stickX / d, uy = stickY / d;
                hx = ux * snapR; hy = uy * snapR;   // 只把「高亮落点」贴到环上，指针本身继续累加以便能往外走
            }
            kbSel = null;                         // 摇杆接管，取消键盘选中缓存
            Native.POINT sp; Native.GetCursorPos(out sp); stickCurX = sp.x; stickCurY = sp.y;
            int sx = (int)Math.Round((wheel.Left + wheel.cx + hx) * wheel.WinScale);
            int sy = (int)Math.Round((wheel.Top + wheel.cy + hy) * wheel.WinScale);
            wheel.UpdateHover(sx, sy, wheel.WinScale);
            lastAct = DateTime.Now;
        }
        void KbMove(int dx, int dy)
        {
            if (grid != null)
            {
                grid.Move(dx, dy);
                Native.POINT kp; Native.GetCursorPos(out kp); kbCurX = kp.x; kbCurY = kp.y; kbAt = DateTime.Now;
            kbGearHold = grid.HubSelected; kbSel = grid.Hovered; stickOn = false;
                lastAct = DateTime.Now;
                return;
            }
            if (wheel == null) return;
            double len = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (len < 0.5) return;
            if (dx == kbLastDx && dy == kbLastDy && Environment.TickCount - kbLastTick < 150) return;
            kbLastDx = dx; kbLastDy = dy; kbLastTick = Environment.TickCount;
            double vx = dx / len, vy = dy / len;

            double curX = 0, curY = 0;
            bool have = kbSel != null && wheel.TryPos(kbSel, out curX, out curY);
            // 朝圆心方向 → 选中中心「设置」齿轮
            double rl = Math.Sqrt(curX * curX + curY * curY);
            if (rl > 1 && have)
            {
                double inward = ((-curX) * vx + (-curY) * vy) / rl;
                if (inward >= 0.45)
                {
                    wheel.HoverHub();
                    kbGearHold = true; kbSel = null;
                    Native.POINT kh; Native.GetCursorPos(out kh); kbCurX = kh.x; kbCurY = kh.y; kbAt = DateTime.Now;
                    Log.W("kb nav -> hub");
                    lastAct = DateTime.Now;
                    return;
                }
            }
            double bestScore = double.MaxValue; AppEntry best = null;
            foreach (var it in wheel.AllItems())
            {
                if (have && ReferenceEquals(it, kbSel)) continue;
                double px, py;
                if (!wheel.TryPos(it, out px, out py)) continue;
                double ux = px - curX, uy = py - curY;
                double dist = Math.Sqrt(ux * ux + uy * uy);
                if (dist < 1) continue;
                double dot = (ux * vx + uy * vy) / dist;
                if (dot < 0.72) continue;
                double score = dist / (dot * dot * dot * dot);
                if (score < bestScore) { bestScore = score; best = it; }
            }
            if (best == null)
            {
                Log.W("kb nav none dir=" + dx + "," + dy);
                return;
            }
            Native.POINT kbCur; Native.GetCursorPos(out kbCur); kbCurX = kbCur.x; kbCurY = kbCur.y; kbAt = DateTime.Now;
            wheel.HoverItem(best);
            kbSel = best;
            kbSel = best; kbGearHold = false;
        }
        void KbCommit()
        {
            if (grid != null)
            {
                if (grid.HubSelected) { Log.W("grid kb commit gear -> settings"); CloseWheel(); OpenWheelSettings(); return; }
                var ga = grid.Hovered; CloseWheel();
                if (ga != null) { Log.W("grid kb launch " + ga.Name); Launcher.Run(ga); }
                return;
            }
            if (wheel == null) return;
            if (wheel.HubSelected && kbSel == null) { Log.W("kb commit hub -> settings"); CloseWheel(); OpenWheelSettings(); return; }
            var a = kbSel != null ? kbSel : wheel.Hovered;
            if (a == null) { Log.W("kb commit nothing -> close"); CloseWheel(); return; }
            CloseWheel();
            Log.W("kb launch " + a.Name);
            Launcher.Run(a);
        }
        void CloseWheel()
        {
            if (hkSrc != null) { try { Native.UnregisterHotKey(hkSrc.Handle, HK_ESC); } catch { } }
            if (grid != null) { Log.W("grid closed"); try { grid.Close(); } catch { } grid = null; }
            if (wheel != null) Log.W("wheel closed");
            MouseHook.SetWheelRect(false, 0, 0, 0, 0);
            hotkeyWheel = false;
            if (wheel != null)
            {
                try { wheel.Close(); } catch { }
                wheel = null;
            wPages = null;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            try { Click.Close(); } catch { }
            Log.W("exit");
            base.OnExit(e);
        }

        void RescanAsync()
        {
            var th = new System.Threading.Thread(delegate ()
            {
                try
                {
                    var fresh = Scanner.FromDesktop();
                    if (fresh.Count > 0)
                        Dispatcher.BeginInvoke(new Action(delegate { apps = fresh; Log.W("rescanned apps=" + fresh.Count); }));
                }
                catch (Exception ex) { Log.W("rescan failed: " + ex.Message); }
            });
            th.SetApartmentState(System.Threading.ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
        }

        void ShowAtCursor()
        {
            hotkeyWheel = false;
            if (wheel != null) { CloseWheel(); return; }   // 再点一次托盘就是收起
            Native.POINT p;
            Native.GetCursorPos(out p);
            ShowWheel(0, p.x, p.y, p.x, p.y);
        }
    }
}
