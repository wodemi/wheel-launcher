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
        public static void W(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.Root);
                    string file = System.IO.Path.Combine(Paths.Root, "log.txt");
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Exists && fi.Length > 1048576)
                        {
                            string old = System.IO.Path.Combine(Paths.Root, "log.1.txt");
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(file, old);
                        }
                    }
                    catch { }
                    File.AppendAllText(file,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
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

        public static ImageSource Get(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            ImageSource src;
            if (Cache.TryGetValue(exePath, out src)) return src;
            try
            {
                if (!File.Exists(exePath)) { Cache[exePath] = null; return null; }
                src = FromShell(exePath);
                if (src == null)
                {
                    using (System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico == null) { Cache[exePath] = null; return null; }
                        var bmp = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        bmp.Freeze();
                        src = bmp;
                    }
                }
            }
            catch { src = null; }
            Cache[exePath] = src;
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
                    if (p.Length < 2 || !File.Exists(p[1])) continue;
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
                    if (seen.Contains(name)) continue;
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
                    seen.Add(name);
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

    public static class Launcher
    {
        public static void Run(AppEntry e)
        {
            try
            {
                Log.W("launch name=" + e.Name + " path=" + e.Path + " args=" + e.Args);
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

        public static bool TabHotkey
        {
            get { return GetInt("tabhotkey", 1) != 0; }
            set { Set("tabhotkey", value ? "1" : "0"); }
        }

        public static bool FirstRunDone
        {
            get { return GetInt("firstrun", 0) != 0; }
            set { Set("firstrun", value ? "1" : "0"); }
        }
    }

    public class MouseHook
    {
        public static bool AcceptInjected = false;
        public static bool TabDown = false;
        public static bool ChordFired = false;
        public static Action<int> Quick;
        public static Action CommitKey;
        public static Func<bool> WheelOpen;
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
            try
            {
                if (on && keyHook == IntPtr.Zero)
                {
                    keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, Native.GetModuleHandle(null), 0);
                    Log.W("keyboard hook on=" + keyHook);
                }
                else if (!on && keyHook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(keyHook);
                    keyHook = IntPtr.Zero;
                    Log.W("keyboard hook off");
                }
            }
            catch (Exception ex) { Log.W("keyboard hook failed: " + ex.Message); }
        }

        public void Install(bool keyboardHotkey)
        {
            mouseProc = MouseCallback;
            keyProc = KeyCallback;
            IntPtr mod = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            if (mouseHook == IntPtr.Zero)
            {
                System.Threading.Thread.Sleep(300);
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            }
            if (keyboardHotkey) keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            if (keyboardHotkey && keyHook == IntPtr.Zero)
            {
                System.Threading.Thread.Sleep(300);
                keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            }
            Log.W("hooks installed mouse=" + mouseHook + " key=" + keyHook);
        }

        IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (AcceptInjected || (info.flags & Native.LLMHF_INJECTED) == 0)
                    {
                        int msg = wParam.ToInt32();
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
                int msg = wParam.ToInt32();
                if (nCode >= 0 && (msg == 0x0100 || msg == 0x0104))
                {
                    var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                    if (AcceptInjected || !injected)
                    {
                        uint vk = k.vkCode;
                        bool open = WheelOpen != null && WheelOpen();
                        if (vk == 0x09)
                        {
                            if (TabDown)
                            {
                                if (!AcceptInjected) Native.KeyTap(0x09);
                                return (IntPtr)1;
                            }
                            TabDown = true;
                            ChordFired = false;
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
                    if (k2.vkCode == 0x09 && TabDown)
                    {
                        TabDown = false;
                        if (!ChordFired && !AcceptInjected) Native.KeyTap(0x09);
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

    public class WheelWindow : Window
    {
        public static readonly double IconGap = 4;
        public static double RingOffset(int i) { return i * 137.508 * Math.PI / 180.0; }

        readonly Canvas root = new Canvas();
        Ellipse highlight;
        TextBlock centerText;
        TextBlock subText;
        public double cx, cy;
        public double hubRadius;
        Border bottomBar;
        TextBlock bottomText;
        List<Ring> rings = new List<Ring>();
        double iconSize, hubR = 60, r0, spacing, winSize;
        string hint = "侧键拖动";
        AppEntry hovered;

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

        public void SetLayout(List<Ring> layout, double icon, double hubR, double r0v, string hintText)
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
                Fill = new RadialGradientBrush(Color.FromArgb(250, 12, 10, 22), Color.FromArgb(252, 2, 2, 6))
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
                    Stroke = new SolidColorBrush(Color.FromArgb((byte)(14 + d * 3), 168, 152, 255)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(dring, cx - rd); Canvas.SetTop(dring, cy - rd);
                root.Children.Add(dring);
            }

            double cr = hubRad;
            var hubGlow = new Ellipse
            {
                Width = cr * 2.9, Height = cr * 2.9,
                Fill = new RadialGradientBrush(Color.FromArgb(120, 120, 90, 255), Color.FromArgb(0, 90, 70, 200))
            };
            Canvas.SetLeft(hubGlow, cx - cr * 1.45); Canvas.SetTop(hubGlow, cy - cr * 1.45);
            root.Children.Add(hubGlow);

            var hub = new Ellipse
            {
                Width = cr * 2, Height = cr * 2,
                Fill = new RadialGradientBrush(Color.FromArgb(245, 46, 38, 74), Color.FromArgb(250, 16, 14, 30)),
                Stroke = new SolidColorBrush(Color.FromArgb(225, 168, 148, 255)),
                StrokeThickness = 1.8
            };
            Canvas.SetLeft(hub, cx - cr); Canvas.SetTop(hub, cy - cr);
            root.Children.Add(hub);

            centerText = new TextBlock
            {
                Text = "+",
                Foreground = new SolidColorBrush(Color.FromArgb(238, 226, 216, 255)),
                FontSize = Math.Max(20, cr * 0.9),
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
                Foreground = new SolidColorBrush(Color.FromArgb(170, 208, 198, 255)),
                FontSize = 10.5,
                TextAlignment = TextAlignment.Center,
                Width = cr * 3.0
            };
            Canvas.SetLeft(subText, cx - cr * 1.5);
            Canvas.SetTop(subText, cy + cr * 0.72);
            root.Children.Add(subText);

            foreach (var ring in rings)
            {
                var band = new Ellipse
                {
                    Width = ring.Radius * 2, Height = ring.Radius * 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(44, 170, 158, 255)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(band, cx - ring.Radius); Canvas.SetTop(band, cy - ring.Radius);
                root.Children.Add(band);

                var letterSet = new List<char>();
                foreach (var it in ring.Items) if (!letterSet.Contains(it.Initial)) letterSet.Add(it.Initial);
                var lbl = new TextBlock
                {
                    Text = new string(letterSet.ToArray()),
                    Foreground = new SolidColorBrush(Color.FromArgb(205, 226, 216, 255)),
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
                        Stroke = new SolidColorBrush(Color.FromArgb(95, 200, 190, 255)),
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
                        Fill = new SolidColorBrush(Color.FromArgb(86, 26, 24, 40)),
                        Stroke = new SolidColorBrush(Color.FromArgb(80, 150, 140, 210)),
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
                            Foreground = new SolidColorBrush(Color.FromArgb(230, 230, 225, 255)),
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
                Fill = new SolidColorBrush(Color.FromArgb(130, 130, 104, 255)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 205, 192, 255)),
                StrokeThickness = 3,
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(highlight);

            bottomText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 250, 225)),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold
            };
            bottomBar = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(216, 40, 32, 68)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(230, 152, 128, 255)),
                BorderThickness = new Thickness(1.4),
                Padding = new Thickness(18, 6, 18, 6),
                Visibility = Visibility.Collapsed,
                Child = bottomText
            };
            root.Children.Add(bottomBar);

            var tip = new TextBlock
            {
                Text = "Esc 取消 · 空白处松手不启动 · Tab+A / Tab+B 呼出",
                Foreground = new SolidColorBrush(Color.FromArgb(140, 220, 215, 240)),
                FontSize = 11
            };
            Canvas.SetLeft(tip, 10); Canvas.SetTop(tip, winSize - 22);
            root.Children.Add(tip);
        }

        public void PlaceAt(int screenX, int screenY, double scale)
        {
            var scr = WF.Screen.FromPoint(new System.Drawing.Point(screenX, screenY));
            double w = Width * scale, h = Height * scale;
            double left = screenX - w / 2, top = screenY - h / 2;
            left = Math.Max(scr.WorkingArea.Left, Math.Min(left, scr.WorkingArea.Right - w));
            top = Math.Max(scr.WorkingArea.Top, Math.Min(top, scr.WorkingArea.Bottom - h));
            Left = left / scale; Top = top / scale;
            Log.W("place sx=" + screenX + " sy=" + screenY + " scale=" + scale + " wpx=" + w + " leftDIP=" + Left + " topDIP=" + Top);
        }

        public void UpdateHover(int screenX, int screenY, double scale)
        {
            double x = (screenX / scale) - Left - cx;
            double y = (screenY / scale) - Top - cy;
            double d = Math.Sqrt(x * x + y * y);
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
                if (hit != null) Click.Tick(ringIdx);
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
                    centerText.Text = "+";
                    centerText.FontSize = Math.Max(20, hubRadius * 0.9);
                    centerText.Foreground = new SolidColorBrush(Color.FromArgb(238, 226, 216, 255));
                    Canvas.SetTop(centerText, cy - centerText.FontSize * 0.62);
                }
            }
        }

        public AppEntry Hovered { get { return hovered; } }
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

        public Action<AppEntry> Pick;

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            try { var a = Commit(); if (Pick != null) Pick(a); } catch { }
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
            Background = new SolidColorBrush(Color.FromRgb(24, 22, 32));
            Foreground = new SolidColorBrush(Color.FromRgb(232, 228, 245));
            FontSize = 13;

            var grid = new Grid();
            grid.Margin = new Thickness(18);
            for (int i = 0; i < 4; i++) { var rd = new RowDefinition(); rd.Height = i == 3 ? GridLength.Auto : new GridLength(34); grid.RowDefinitions.Add(rd); }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var tbName = new TextBox { VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(38, 34, 50)), Foreground = new SolidColorBrush(Color.FromRgb(240, 236, 250)), Padding = new Thickness(6, 4, 6, 4) };
            var tbPath = new TextBox { VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(38, 34, 50)), Foreground = new SolidColorBrush(Color.FromRgb(240, 236, 250)), Padding = new Thickness(6, 4, 6, 4) };
            var tbArgs = new TextBox { VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(38, 34, 50)), Foreground = new SolidColorBrush(Color.FromRgb(240, 236, 250)), Padding = new Thickness(6, 4, 6, 4) };

            var lName = new TextBlock { Text = "显示名称", VerticalAlignment = VerticalAlignment.Center };
            var lPath = new TextBlock { Text = "程序路径", VerticalAlignment = VerticalAlignment.Center };
            var lArgs = new TextBlock { Text = "启动参数", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lName, 0); Grid.SetRow(tbName, 0); Grid.SetColumn(tbName, 1);
            Grid.SetRow(lPath, 1); Grid.SetRow(tbPath, 1); Grid.SetColumn(tbPath, 1);
            Grid.SetRow(lArgs, 2); Grid.SetRow(tbArgs, 2); Grid.SetColumn(tbArgs, 1);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var browse = new Button { Content = "浏览…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(52, 46, 70)), Foreground = new SolidColorBrush(Color.FromRgb(235, 230, 250)) };
            var okB = new Button { Content = "添加", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(96, 74, 200)), Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)) };
            var cancel = new Button { Content = "取消", Padding = new Thickness(16, 5, 16, 5), Background = new SolidColorBrush(Color.FromRgb(52, 46, 70)), Foreground = new SolidColorBrush(Color.FromRgb(235, 230, 250)) };
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
        List<AppEntry> apps;

        double scale = 1;
        int downX, downY, downBtn, showX, showY;
        DateTime pressAt;
        bool hadWheel;
        static int LongPressMs { get { return Settings.LongPress; } }
        bool pressed, dragged;
        string logPath;
        static System.Threading.Mutex appMutex;
        static System.Threading.EventWaitHandle showEvent;

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
            Click.Enabled = Settings.SoundOn;
            Click.SetVolume(Settings.Volume);
            Log.W("=== start === injectedOK=" + MouseHook.AcceptInjected);
            logPath = System.IO.Path.Combine(Paths.Root, "log.txt");

            // 单实例：已经在跑就通知它弹轮盘，然后退出
            string instName = "WheelLauncher.SingleInstance." + System.IO.Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
            bool created;
            appMutex = new System.Threading.Mutex(true, instName, out created);
            showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, instName + ".show");
            if (!created)
            {
                Log.W("another instance running, signal show and exit");
                try { showEvent.Set(); } catch { }
                Shutdown();
                return;
            }
            var listen = new System.Threading.Thread(delegate ()
            {
                while (true)
                {
                    try
                    {
                        showEvent.WaitOne();
                        Dispatcher.BeginInvoke(new Action(delegate { try { ShowAtCursor(); } catch (Exception ex) { Log.W("relay show failed: " + ex.Message); } }));
                    }
                    catch (Exception ex) { Log.W("relay loop: " + ex.Message); return; }
                }
            });
            listen.IsBackground = true;
            listen.Start();

            scale = GetScale();
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
            MouseHook.Quick += delegate(int b) { Native.POINT cp; Native.GetCursorPos(out cp); ShowWheel(b, cp.x, cp.y, cp.x, cp.y); };
            MouseHook.CommitKey += delegate
            {
                if (wheel == null) return;
                var a2 = wheel.Commit();
                CloseWheel();
                if (a2 != null) Launcher.Run(a2);
            };
            hook.Escape += OnEscape;
            MouseHook.WheelOpen = delegate { return wheel != null; };
            hook.Install(Settings.TabHotkey);
            if (!hook.Ok) Log.W("HOOK INSTALL FAILED - hotkeys will not work");

            tray = new WF.NotifyIcon();
            tray.Icon = LoadExeIcon() ?? MakeIcon();
            tray.Text = "轮盘启动器";
            tray.Visible = true;
            tray.DoubleClick += (s, a) => ShowAtCursor();
            tray.MouseClick += delegate (object s, WF.MouseEventArgs a) { if (a.Button == WF.MouseButtons.Left) ShowAtCursor(); };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("显示轮盘 (测试)", null, (s, a) => ShowAtCursor());
            menu.Items.Add("重新扫描应用", null, (s, a) => { apps = Scanner.FromDesktop(); });
            menu.Items.Add("添加应用…", null, (s, a) => OpenAddDialog());
            menu.Items.Add("设置…", null, (s, a) => OpenSettings());
            var snd = new WF.ToolStripMenuItem("选中音效") { CheckOnClick = true, Checked = Settings.SoundOn };
            snd.CheckedChanged += (s, a) => { Settings.SoundOn = snd.Checked; Log.W("sound=" + snd.Checked); };
            menu.Items.Add(snd);
            menu.Items.Add("测试音效", null, (s, a) => Click.Test());
            var auto = new WF.ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsAutoStart() };
            auto.CheckedChanged += (s, a) => SetAutoStart(auto.Checked);
            menu.Items.Add(auto);
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
            if (e.Args.Contains("--show1")) { Dispatcher.BeginInvoke(new Action(() => { var w = WF.Screen.PrimaryScreen.WorkingArea; ShowWheel(1, w.Width / 2, w.Height / 2, w.Width / 2 - 200, w.Height / 2); }), DispatcherPriority.Background); }
            if (e.Args.Contains("--show2")) { Dispatcher.BeginInvoke(new Action(() => { var w = WF.Screen.PrimaryScreen.WorkingArea; ShowWheel(2, w.Width / 2, w.Height / 2, w.Width / 2 - 200, w.Height / 2); }), DispatcherPriority.Background); }
            if (e.Args.Contains("--show")) { Dispatcher.BeginInvoke(new Action(() => { var scr = WF.Screen.PrimaryScreen.WorkingArea; ShowWheel(0, scr.Width / 2, scr.Height / 2, scr.Width / 2, scr.Height / 2); }), DispatcherPriority.Background); }
        }

        void OpenSettings()
        {
            var fg = new SolidColorBrush(Color.FromRgb(236, 232, 250));
            var dim = new SolidColorBrush(Color.FromRgb(158, 150, 188));
            var win = new Window();
            win.Title = "轮盘启动器 · 设置";
            win.Width = 500;
            win.SizeToContent = SizeToContent.Height;
            win.ResizeMode = ResizeMode.NoResize;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Background = new SolidColorBrush(Color.FromRgb(24, 22, 32));
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
            slVol.ValueChanged += (s, a) => { lbVol.Text = ((int)slVol.Value) + " %"; Click.SetVolume((int)slVol.Value); Click.Test(); };
            p.Children.Add(slVol);
            p.Children.Add(lbVol);

            var cbSound = new CheckBox { Content = "选中应用时播放哒哒声", IsChecked = Settings.SoundOn, Foreground = fg, Margin = new Thickness(0, 4, 0, 4) };
            p.Children.Add(cbSound);
            var cbTab = new CheckBox { Content = "启用 Tab+A / Tab+B 快捷键", IsChecked = Settings.TabHotkey, Foreground = fg, Margin = new Thickness(0, 0, 0, 12) };
            p.Children.Add(cbTab);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var testBtn = new Button { Content = "测试音效", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(52, 46, 70)), Foreground = fg };
            testBtn.Click += (s, a) => Click.Test();
            var ok = new Button { Content = "保存", Padding = new Thickness(22, 5, 22, 5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(96, 74, 200)), Foreground = Brushes.White };
            var cancel = new Button { Content = "取消", Padding = new Thickness(18, 5, 18, 5), Background = new SolidColorBrush(Color.FromRgb(52, 46, 70)), Foreground = fg };
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
            var fg = new SolidColorBrush(Color.FromRgb(236, 232, 250));
            var dim = new SolidColorBrush(Color.FromRgb(158, 150, 188));
            var win = new Window();
            win.Title = "欢迎使用轮盘启动器";
            win.Width = 560;
            win.SizeToContent = SizeToContent.Height;
            win.ResizeMode = ResizeMode.NoResize;
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Background = new SolidColorBrush(Color.FromRgb(24, 22, 32));
            var p = new StackPanel { Margin = new Thickness(26, 22, 26, 18) };
            p.Children.Add(new TextBlock { Text = "轮盘启动器", Foreground = fg, FontSize = 20, FontWeight = FontWeights.SemiBold });
            p.Children.Add(new TextBlock
            {
                Foreground = dim,
                FontSize = 12.5,
                Margin = new Thickness(0, 4, 0, 14),
                TextWrapping = TextWrapping.Wrap,
                Text = "按首字母把桌面上的程序排成几个圆环，选中松手就启动。"
            });
            var steps = new StackPanel();
            Action<string> add = delegate (string s)
            {
                steps.Children.Add(new TextBlock { Text = s, Foreground = fg, FontSize = 13, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap });
            };
            add("1. 按住鼠标侧键约 0.65 秒，轮盘出现；继续按住不放。");
            add("2. 移动鼠标指到图标上，松手即启动；按 Esc 取消。");
            add("3. 侧键 1 = A–M 的应用，侧键 2 = N–Z 的应用。");
            add("4. 没有侧键也能用：按住 Tab 再按 A 或 B。");
            add("5. 圆环中心的「+」可以添加桌面上没有的程序。");
            add("6. 托盘图标：左键单击呼出轮盘，右键打开菜单（设置 / 开机自启 / 退出）。");
            p.Children.Add(steps);
            if (noSideButtons)
            {
                var warn = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(64, 42, 42)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 12, 0, 4)
                };
                var wt = new TextBlock
                {
                    Text = "没有检测到鼠标侧键。请使用 Tab+A / Tab+B 呼出，或换一只带侧键的鼠标。",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 190)),
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
                Background = new SolidColorBrush(Color.FromRgb(96, 74, 200)),
                Foreground = Brushes.White
            };
            ok.Click += (s, a) => win.Close();
            p.Children.Add(ok);
            win.Content = p;
            Log.W("first run guide shown noSide=" + noSideButtons);
            win.ShowDialog();
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
                showX = x; showY = y;
                ShowWheel(downBtn, x, y, x, y);
                Log.W("longpress show btn=" + downBtn);
            }
            if (wheel != null) { wheel.UpdateHover(x, y, scale); lastAct = DateTime.Now; }
        }

        void OnUp(int btn, int x, int y)
        {
            if (!pressed) return;
            pressed = false;
            if (wheel != null)
            {
                double mv = Math.Sqrt((x - showX) * (double)(x - showX) + (y - showY) * (double)(y - showY));
                if (mv >= 24 * scale && wheel.HitHub(x, y, scale))
                {
                    CloseWheel();
                    OpenAddDialog();
                    return;
                }
                var hitSel = wheel.Commit();
                CloseWheel();
                if (hitSel != null && mv >= 14 * scale) Launcher.Run(hitSel);
                return;
            }
            if (!hadWheel && !MouseHook.AcceptInjected) Native.XClick((uint)downBtn);
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

        void OnEscape()
        {
            if (wheel != null) CloseWheel();
        }

        DateTime lastAct = DateTime.Now;
        void ShowWheel(int btn, int sx, int sy, int curX, int curY)
        {
            try
            {
                var scr = WF.Screen.FromPoint(new System.Drawing.Point(sx, sy)).WorkingArea;
                double budget = Math.Min(scr.Width, scr.Height) / scale / 2 - 8;
                double hub = 44;

                List<AppEntry> sub;
                string tag;
                if (btn == 1) { sub = apps.Where(a => a.Initial <= 'M').ToList(); tag = "A-M"; }
                else if (btn == 2) { sub = apps.Where(a => a.Initial >= 'N' || a.Initial == '#').ToList(); tag = "N-Z"; }
                else { sub = apps; tag = "ALL"; }
                if (sub.Count == 0) { Log.W("empty subset " + tag); return; }

                var ordered = sub.OrderBy(a => a.Initial == '#' ? '[' : a.Initial).ThenBy(a => a.Name, StringComparer.CurrentCulture).ToList();

                var groups = new List<List<AppEntry>>();
                foreach (var g in ordered.GroupBy(a => a.Initial == '#' ? '[' : a.Initial).OrderBy(g2 => g2.Key))
                {
                    var members = g.ToList();
                    if (groups.Count > 0 && members.Count <= 2 && groups[groups.Count - 1].Count <= 2)
                        groups[groups.Count - 1].AddRange(members);
                    else
                        groups.Add(members);
                }
                while (groups.Count > 4)
                {
                    int bi = 0, bs = int.MaxValue;
                    for (int i2 = 0; i2 < groups.Count - 1; i2++)
                    {
                        int s2 = groups[i2].Count + groups[i2 + 1].Count;
                        if (s2 < bs) { bs = s2; bi = i2; }
                    }
                    groups[bi].AddRange(groups[bi + 1]);
                    groups.RemoveAt(bi + 1);
                }

                int ringsN = groups.Count;
                double icon = Math.Min(42, Math.Max(20, (budget - hub - 10) / (1.95 + (ringsN - 1) * 1.55)));
                double step = icon * 1.55;
                var layout = new List<Ring>();
                for (int k = 0; k < ringsN; k++)
                {
                    var items = groups[k];
                    layout.Add(new Ring
                    {
                        Letter = items[0].Initial,
                        Part = 1,
                        Index = k,
                        Radius = hub + icon * 1.15 + k * step,
                        Capacity = items.Count,
                        Items = items
                    });
                }
                if (layout.Count == 0) { Log.W("no layout"); return; }

                double maxOuter = 0;
                foreach (var r0 in layout) maxOuter = Math.Max(maxOuter, r0.Radius + icon * 0.72);
                if (maxOuter > budget)
                {
                    double f0 = budget / maxOuter;
                    icon *= f0;
                    foreach (var r0 in layout) r0.Radius *= f0;
                }

                CloseWheel();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { Click.Warm(); });
                wheel = new WheelWindow();
                wheel.Pick = delegate (AppEntry a) { CloseWheel(); if (a != null) Launcher.Run(a); else OpenAddDialog(); };
                wheel.SetLayout(layout, icon, hub, 0, btn == 1 ? "侧键1 · A-M" : (btn == 2 ? "侧键2 · N-Z" : "全部应用"));
                wheel.PlaceAt(sx, sy, scale);
                wheel.Show();
                wheel.UpdateLayout();
                wheel.UpdateHover(curX, curY, scale);
                lastAct = DateTime.Now;
                Log.W("set=" + tag + " rings=" + ringsN + " icon=" + icon.ToString("0.0") + " names=" + string.Join("|", ordered.Select(a => a.Name + ":" + a.Initial).ToArray()));
            }
            catch (Exception ex) { Log.W("show failed: " + ex); }
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

        void CloseWheel()
        {
            if (wheel != null)
            {
                try { wheel.Close(); } catch { }
                wheel = null;
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
            Native.POINT p;
            Native.GetCursorPos(out p);
            ShowWheel(0, p.x, p.y, p.x, p.y);
        }
    }
}
