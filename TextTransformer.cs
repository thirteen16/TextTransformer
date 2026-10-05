using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TextTransformer
{
    enum ActionKind { ToggleCase, LettersOnly, SentenceCase }

    static class Transform
    {
        public static string Apply(string text, ActionKind kind)
        {
            if (kind == ActionKind.LettersOnly) {
                var letters = new StringBuilder();
                foreach (char c in text) if (IsLetter(c)) letters.Append(c);
                return letters.ToString();
            }
            bool allUpper = true;
            foreach (char c in text) if (c >= 'a' && c <= 'z') allUpper = false;
            var result = new StringBuilder(text.Length);
            bool sentenceStart = true;
            foreach (char c in text) {
                char next = c;
                if (IsLetter(c)) {
                    if (kind == ActionKind.ToggleCase) next = allUpper ? Char.ToLowerInvariant(c) : Char.ToUpperInvariant(c);
                    else { next = sentenceStart ? Char.ToUpperInvariant(c) : Char.ToLowerInvariant(c); sentenceStart = false; }
                }
                if (kind == ActionKind.SentenceCase) result.Append(EnglishPunctuation(next));
                else result.Append(next);
                if (kind == ActionKind.SentenceCase && ".!?。！？．…\r\n".IndexOf(c) >= 0) sentenceStart = true;
            }
            return result.ToString();
        }
        static bool IsLetter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
        static string EnglishPunctuation(char c)
        {
            switch (c) {
                case '。': return ".";
                case '、': return ",";
                case '“': case '”': case '「': case '」': case '『': case '』': return "\"";
                case '‘': case '’': return "'";
                case '【': case '〔': return "[";
                case '】': case '〕': return "]";
                case '《': case '〈': return "<";
                case '》': case '〉': return ">";
                case '…': return "...";
                case '—': return "-";
                case '·': return ".";
            }
            // Convert full-width ASCII punctuation, keeping letters and digits intact.
            if (c >= '\uFF01' && c <= '\uFF5E' && (Char.IsPunctuation(c) || Char.IsSymbol(c)))
                return ((char)(c - 0xFEE0)).ToString();
            return c.ToString();
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "TextTransformer";
        internal static string Command(string executable) { return "\"" + executable + "\" --startup"; }
        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) {
                return key != null && String.Equals(key.GetValue(ValueName) as string, Command(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
            }
        }
        public static void SetEnabled(bool enabled)
        {
            if (enabled) {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) {
                    if (key == null) throw new InvalidOperationException("无法打开开机自启设置。");
                    key.SetValue(ValueName, Command(Application.ExecutablePath), RegistryValueKind.String);
                }
            } else {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
                    if (key != null) key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    // A tap must be short, isolated, and followed by another tap of the same key.
    sealed class TapDetector
    {
        readonly HashSet<int> held = new HashSet<int>();
        int current, previous;
        long started, released;
        bool valid;
        public ActionKind? Feed(int key, bool down, long now, bool modifierHeld)
        {
            bool target = key == 0x14 || key == 0xA0 || key == 0xA1;
            if (!target) { previous = 0; valid = false; return null; }
            if (down) {
                if (!held.Add(key)) return null;
                if (current != 0 || modifierHeld) { valid = false; previous = 0; current = key; return null; }
                current = key; started = now; valid = true;
                if (previous != key) previous = 0;
                return null;
            }
            held.Remove(key);
            bool tap = current == key && valid && now - started <= 250;
            current = 0; valid = false;
            if (!tap) { previous = 0; return null; }
            if (previous == key && now - released <= 400) {
                previous = 0;
                return key == 0x14 ? ActionKind.ToggleCase : key == 0xA0 ? ActionKind.LettersOnly : ActionKind.SentenceCase;
            }
            previous = key; released = now; return null;
        }
        public void Reset() { held.Clear(); current = previous = 0; valid = false; }
    }

    static class Native
    {
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct Keyboard { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KeyboardInput keyboard; [FieldOffset(0)] public MouseInput mouse; }
        [StructLayout(LayoutKind.Sequential)] public struct Input { public uint type; public InputUnion data; }
        [StructLayout(LayoutKind.Sequential)] public struct GuiThreadInfo {
            public uint size, flags;
            public IntPtr active, focus, capture, menuOwner, moveSize, caret;
            public int left, top, right, bottom;
        }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        public static IntPtr FocusedControl(IntPtr window)
        {
            var info = new GuiThreadInfo { size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            uint thread = GetWindowThreadProcessId(window, IntPtr.Zero);
            return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.focus : IntPtr.Zero;
        }
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        public static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        public static void Shortcut(ushort key)
        {
            var inputs = new Input[4];
            ushort[] keys = { 0x11, key, key, 0x11 };
            for (int i = 0; i < 4; i++) { inputs[i].type = 1; inputs[i].data.keyboard.vk = keys[i]; inputs[i].data.keyboard.flags = i >= 2 ? 2u : 0u; }
            if (SendInput(4, inputs, Marshal.SizeOf(typeof(Input))) != 4) throw new InvalidOperationException("无法向目标窗口发送按键（Windows 错误 " + Marshal.GetLastWin32Error() + "）；请检查目标程序权限。");
        }
        public static void DeleteSelection()
        {
            var inputs = new Input[2];
            for (int i = 0; i < 2; i++) { inputs[i].type = 1; inputs[i].data.keyboard.vk = 0x2E; inputs[i].data.keyboard.flags = i == 1 ? 2u : 0u; }
            if (SendInput(2, inputs, Marshal.SizeOf(typeof(Input))) != 2) throw new InvalidOperationException("无法删除选区。");
        }
    }

    sealed class MainForm : Form
    {
        readonly TapDetector detector = new TapDetector();
        readonly Native.HookProc hookProc;
        readonly NotifyIcon tray;
        bool enabled = true;
        readonly Icon appIcon;
        bool startHidden;
        IntPtr hook;
        bool busy, quitting;
        bool interrupted;
        IntPtr expectedFocus;
        public MainForm(bool startupLaunch = false)
        {
            startHidden = startupLaunch;
            using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("TextTransformer.AppIcon")) {
                if (stream == null) throw new InvalidOperationException("程序图标资源缺失，请重新编译。");
                using (var icon = new Icon(stream)) appIcon = (Icon)icon.Clone();
            }
            Icon = appIcon;
            Text = "使用说明 — 文本转换助手"; ClientSize = new Size(540, 145); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10);
            var help = new Label { Location = new Point(24, 20), Size = new Size(492, 110), Text =
                "双击 CapsLock：英文字母全大写；已全大写则转为小写。\r\n双击左 Shift：只保留英文字母，删除其他字符。\r\n双击右 Shift：句首大写，其余英文字母小写，\r\n                       中文标点转为英文标点。" };
            Controls.Add(help);
            var menu = new ContextMenuStrip();
            menu.Items.Add("使用说明", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            var startupItem = new ToolStripMenuItem("开机自启");
            string startupError = null;
            try { startupItem.Checked = Startup.IsEnabled(); }
            catch (Exception ex) { startupItem.Enabled = false; startupError = "无法读取开机自启设置：" + ex.Message; }
            startupItem.Click += delegate {
                bool requested = !startupItem.Checked;
                try { Startup.SetEnabled(requested); startupItem.Checked = requested; }
                catch (Exception ex) { ReportError("开机自启设置失败：" + ex.Message); }
            };
            menu.Items.Add(startupItem);
            var shortcutItem = new ToolStripMenuItem("使用快捷键") { Checked = enabled };
            shortcutItem.Click += delegate { enabled = !enabled; shortcutItem.Checked = enabled; detector.Reset(); };
            menu.Items.Add(shortcutItem);
            menu.Items.Add("退出", null, delegate { quitting = true; Close(); });
            tray = new NotifyIcon { Icon = appIcon, Text = "文本转换助手", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { Show(); Activate(); };
            if (startupError != null) ReportError(startupError);
            hookProc = OnKey;
            hook = Native.SetWindowsHookEx(13, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new InvalidOperationException("注册全局键盘监听失败。");
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }
        void ReportError(string message)
        {
            tray.ShowBalloonTip(3000, "文本转换助手", message, ToolTipIcon.Warning);
        }
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && value) {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }
        IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && busy) {
                var input = (Native.Keyboard)Marshal.PtrToStructure(lParam, typeof(Native.Keyboard));
                int message = wParam.ToInt32();
                if ((input.flags & 0x10) == 0 && (message == 0x100 || message == 0x104)) interrupted = true;
            }
            if (code >= 0 && enabled && !busy) {
                var data = (Native.Keyboard)Marshal.PtrToStructure(lParam, typeof(Native.Keyboard));
                int msg = wParam.ToInt32();
                if ((data.flags & 0x10) == 0 && (msg == 0x100 || msg == 0x104 || msg == 0x101 || msg == 0x105)) {
                    bool down = msg == 0x100 || msg == 0x104;
                    int key = (int)data.vkCode;
                    bool modifiers = Native.Down(0x11) || Native.Down(0x12) || Native.Down(0x5B) || Native.Down(0x5C)
                        || (key != 0xA0 && Native.Down(0xA0)) || (key != 0xA1 && Native.Down(0xA1));
                    var action = detector.Feed(key, down, Environment.TickCount & 0xFFFFFFFFL, modifiers);
                    if (action.HasValue) {
                        IntPtr target = Native.GetForegroundWindow();
                        IntPtr control = Native.FocusedControl(target);
                        busy = true; interrupted = false;
                        BeginInvoke(new Action(async delegate { await ConvertTarget(target, action.Value, control); }));
                    }
                }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }
        static bool EditableFocus()
        {
            try {
                var focus = AutomationElement.FocusedElement;
                if (focus == null || focus.Current.IsPassword) return false;
                object pattern;
                if (focus.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) return !((ValuePattern)pattern).Current.IsReadOnly;
                if (focus.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) {
                    object readOnly = ((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                    return readOnly is bool && !(bool)readOnly;
                }
                return false;
            } catch { return false; }
        }
        static DataObject BackupClipboard()
        {
            IDataObject original = Clipboard.GetDataObject();
            if (original == null) return null;
            var backup = new DataObject();
            foreach (string format in original.GetFormats(false)) {
                object value = original.GetData(format, false);
                var stream = value as MemoryStream;
                if (stream != null) value = new MemoryStream(stream.ToArray());
                var image = value as Image;
                if (image != null) value = image.Clone();
                if (value != null) backup.SetData(format, false, value);
            }
            return backup;
        }
        async Task<string> CopyText(IntPtr target)
        {
            uint before = Native.GetClipboardSequenceNumber();
            CheckTarget(target); Native.Shortcut(0x43);
            for (int i = 0; i < 12; i++) {
                await Task.Delay(40);
                CheckTarget(target);
                if (Native.GetClipboardSequenceNumber() != before) {
                    try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
                    catch (ExternalException) { }
                }
            }
            return null;
        }
        void CheckTarget(IntPtr target)
        {
            if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("焦点已切换，已取消转换。");
            if (expectedFocus != IntPtr.Zero && Native.FocusedControl(target) != expectedFocus) throw new InvalidOperationException("输入控件已切换，已取消转换。");
            if (interrupted) throw new InvalidOperationException("检测到新的键盘输入，已取消转换，请重试。");
        }
        async Task ConvertTarget(IntPtr target, ActionKind kind, IntPtr control = default(IntPtr))
        {
            expectedFocus = control == IntPtr.Zero ? Native.FocusedControl(target) : control;
            DataObject backup = null;
            bool saved = false;
            uint ownedSequence = 0;
            try {
                await Task.Delay(40); CheckTarget(target);
                if (target == Handle) return;
                if (Native.Down(0x10) || Native.Down(0x11) || Native.Down(0x12)) return;
                backup = BackupClipboard(); saved = true;
                string original = await CopyText(target);
                ownedSequence = Native.GetClipboardSequenceNumber();
                if (String.IsNullOrEmpty(original)) {
                    if (!EditableFocus()) throw new InvalidOperationException("未发现文本选区或可编辑输入框，请先选中文字。");
                    CheckTarget(target); Native.Shortcut(0x41); await Task.Delay(70);
                    original = await CopyText(target); ownedSequence = Native.GetClipboardSequenceNumber();
                }
                if (String.IsNullOrEmpty(original)) return;
                string result = Transform.Apply(original, kind);
                if (result == original) return;
                await Task.Delay(80); CheckTarget(target);
                // Empty text cannot be pasted; Delete removes exactly the selected text.
                if (result.Length == 0) Native.DeleteSelection();
                else {
                    Clipboard.SetText(result); ownedSequence = Native.GetClipboardSequenceNumber();
                    Native.Shortcut(0x56);
                }
                await Task.Delay(600);
            } catch (Exception ex) { ReportError(ex.Message); }
            finally {
                try {
                    if (saved && ownedSequence != 0 && Native.GetClipboardSequenceNumber() == ownedSequence) {
                        if (backup == null) Clipboard.Clear(); else Clipboard.SetDataObject(backup, true, 5, 40);
                    }
                } catch { ReportError("转换已结束，但剪贴板恢复失败。"); }
                detector.Reset(); busy = false;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (disposing) { tray.ContextMenuStrip.Dispose(); tray.Dispose(); appIcon.Dispose(); }
            base.Dispose(disposing);
        }
    }

    static class Program
    {
        [STAThread] static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\TextTransformer.Desktop", out created)) {
                if (!created) { MessageBox.Show("程序已运行，请在系统托盘中打开。", "文本转换助手"); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new MainForm(Array.IndexOf(args, "--startup") >= 0)); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败"); }
            }
        }
    }
}
