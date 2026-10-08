using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TextTransformer
{
    enum ActionKind { ToggleCase, LettersOnly, SentenceCase, RestorePinyin }

    static class Transform
    {
        public static string Apply(string text, ActionKind kind)
        {
            if (kind == ActionKind.LettersOnly) {
                // Keep delimited content intact; otherwise keep ASCII letters only.
                var quoted = Regex.Match(text, @"""(?<middle>[^""]*)""|“(?<middle>[^”]*)”|‘(?<middle>[^’]*)’|'(?<middle>[^']*)'|「(?<middle>[^」]*)」|『(?<middle>[^』]*)』");
                if (quoted.Success) return quoted.Groups["middle"].Value;
                int firstSpace = text.IndexOf(' ');
                int lastSpace = text.LastIndexOf(' ');
                if (firstSpace >= 0 && lastSpace > firstSpace) {
                    return text.Substring(firstSpace + 1, lastSpace - firstSpace - 1);
                }
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
            bool target = key == 0x14 || key == 0xA0 || key == 0xA1 || key == 0xA4;
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
                return key == 0x14 ? ActionKind.ToggleCase : key == 0xA0 ? ActionKind.LettersOnly : key == 0xA1 ? ActionKind.SentenceCase : ActionKind.RestorePinyin;
            }
            previous = key; released = now; return null;
        }
        public void Reset() { held.Clear(); current = previous = 0; valid = false; }
    }

    static class Compatibility
    {
        // Readiness probes only. Never retry an operation that already typed text.
        public static async Task<bool> WaitUntil(Func<bool> ready, Action checkTarget, int attempts, int delay)
        {
            for (int i = 0; i < attempts; i++) {
                checkTarget();
                if (ready()) return true;
                if (i + 1 < attempts) await Task.Delay(delay);
            }
            return false;
        }
    }

    static class PinyinText
    {
        public static bool Valid(string text) { return text != null && text.Length <= 64 && Regex.IsMatch(text, @"\A[a-zA-Z]+(?:'[a-zA-Z]+)*'?\z"); }
        public static string Suffix(string prefix)
        {
            var match = Regex.Match(prefix ?? "", @"[a-zA-Z']+\z");
            return match.Success && Valid(match.Value) ? match.Value : "";
        }
    }

    // Use actual text ranges. Never treat an editor's copy-whole-line behavior
    // as a selection, and never select the entire input for pinyin recovery.
    sealed class PinyinSelection
    {
        readonly TextPattern pattern;
        readonly TextPatternRange original;
        readonly Native.EditSnapshot native;
        readonly ValuePattern keyboardValue;
        public readonly bool KeyboardOnly;
        public readonly AutomationElement Element;
        public PinyinSelection(IntPtr control)
        {
            Element = AutomationElement.FocusedElement;
            object value;
            if (Element != null && Element.Current.IsPassword)
                throw new InvalidOperationException("不在密码输入框中恢复拼音。");
            if (Element == null || !Element.TryGetCurrentPattern(TextPattern.Pattern, out value)) {
                if (Native.TryReadEdit(control, out native)) { Element = null; return; }
                if (Element != null && Native.IsQtWindow(control) && (Element.Current.ControlType == ControlType.Edit || Element.Current.ControlType == ControlType.ComboBox)
                    && Element.TryGetCurrentPattern(ValuePattern.Pattern, out value) && !((ValuePattern)value).Current.IsReadOnly) {
                    keyboardValue = (ValuePattern)value; KeyboardOnly = true; return;
                }
                throw new InvalidOperationException("此输入框不支持文本选区读取，已取消拼音恢复。");
            }
            pattern = (TextPattern)value;
            var ranges = pattern.GetSelection();
            if (ranges.Length != 1) throw new InvalidOperationException("不支持此输入框的选区类型。");
            original = ranges[0].Clone();
        }
        public string KeyboardValue { get { return keyboardValue.Current.Value; } }
        public string SelectedText { get { return native != null ? native.Text.Substring(native.Start, native.End - native.Start) : original.GetText(-1); } }
        public string AutomaticText()
        {
            if (native != null) return PinyinText.Suffix(native.Text.Substring(0, native.Start));
            var prefix = original.Clone();
            prefix.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -65);
            string text = prefix.GetText(-1);
            return PinyinText.Suffix(text);
        }
    }

    static class Native
    {
        public sealed class EditSnapshot { public string Text; public int Start, End; }
        public static bool IsQtWindow(IntPtr window)
        {
            var name = new StringBuilder(256);
            return window != IntPtr.Zero && GetClassName(window, name, name.Capacity) != 0 && name.ToString().StartsWith("Qt", StringComparison.Ordinal);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] static extern IntPtr SendPointerTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        public static bool IsEditableTextControl(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            var name = new StringBuilder(256);
            if (GetClassName(window, name, name.Capacity) == 0) return false;
            string className = name.ToString();
            bool edit = className.Equals("Edit", StringComparison.OrdinalIgnoreCase) || className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || className.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase)
                || className.StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase);
            return edit && (GetWindowStyle(window, -16) & (0x800 | 0x20)) == 0;
        }
        static UIntPtr ReadEditMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            UIntPtr result;
            if (SendPointerTimeout(window, message, wParam, lParam, 2, 250, out result) == IntPtr.Zero)
                throw new InvalidOperationException("原生输入框读取超时，未修改文字。");
            return result;
        }
        public static bool TryReadEdit(IntPtr window, out EditSnapshot snapshot)
        {
            snapshot = null;
            if (!IsEditableTextControl(window)) return false;
            ulong length = ReadEditMessage(window, 0xE, IntPtr.Zero, IntPtr.Zero).ToUInt64();
            if (length > 1048576) throw new InvalidOperationException("输入框文本过长，已取消恢复。");
            IntPtr textBuffer = Marshal.AllocHGlobal(((int)length + 1) * 2);
            IntPtr offsets = Marshal.AllocHGlobal(8);
            try {
                Marshal.WriteInt32(offsets, 0, -1); Marshal.WriteInt32(offsets, 4, -1);
                int copied = (int)ReadEditMessage(window, 0xD, new IntPtr((long)length + 1), textBuffer).ToUInt64();
                string text = Marshal.PtrToStringUni(textBuffer, copied);
                ReadEditMessage(window, 0xB0, offsets, IntPtr.Add(offsets, 4)); // EM_GETSEL
                int start = Marshal.ReadInt32(offsets), end = Marshal.ReadInt32(offsets, 4);
                if (start < 0 || end < start || end > text.Length) throw new InvalidOperationException("无法核对原生输入框的选区，未修改文字。");
                snapshot = new EditSnapshot { Text = text, Start = start, End = end };
                return true;
            } finally { Marshal.FreeHGlobal(textBuffer); Marshal.FreeHGlobal(offsets); }
        }
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential)] public struct Keyboard { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MouseHookData { public int x, y; public uint data, flags, time; public UIntPtr extra; }
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
        [DllImport("user32.dll")] public static extern short GetKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr command, IntPtr value, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        public static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        public static void Key(ushort key) { SendKeys(new ushort[] { key, key }, new uint[] { 0, 2 }); }
        public static void SelectPrevious(int count)
        {
            var keys = new List<ushort>(); var flags = new List<uint>();
            keys.Add(0x10); flags.Add(0);
            for (int i = 0; i < count; i++) { keys.Add(0x25); flags.Add(0); keys.Add(0x25); flags.Add(2); }
            keys.Add(0x10); flags.Add(2);
            SendKeys(keys.ToArray(), flags.ToArray());
        }
        public static void SelectPrefix()
        {
            SendKeys(new ushort[] { 0x10, 0x24, 0x24, 0x10 }, new uint[] { 0, 0, 2, 2 });
        }
        static void SendKeys(ushort[] keys, uint[] flags)
        {
            var inputs = new Input[keys.Length];
            for (int i = 0; i < keys.Length; i++) { inputs[i].type = 1; inputs[i].data.keyboard.vk = keys[i]; inputs[i].data.keyboard.flags = flags[i]; }
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
                throw new InvalidOperationException("无法发送按键，请检查目标程序权限。");
        }
        static ulong ImeControl(IntPtr window, uint command, int value)
        {
            UIntPtr result;
            if (SendMessageTimeout(window, 0x283, new UIntPtr(command), new IntPtr(value), 2, 250, out result) == IntPtr.Zero)
                throw new InvalidOperationException("无法读取输入法状态，请切回微软拼音中文模式再试。");
            return result.ToUInt64();
        }
        public static async Task EnsureChinese(IntPtr focus, Action checkTarget)
        {
            uint thread = GetWindowThreadProcessId(focus, IntPtr.Zero);
            if (focus == IntPtr.Zero || thread == 0 || (GetKeyboardLayout(thread).ToInt64() & 0xFFFF) != 0x0804)
                throw new InvalidOperationException("请先用 Win+空格切换到微软拼音。");
            IntPtr ime = ImmGetDefaultIMEWnd(focus);
            if (ime == IntPtr.Zero) throw new InvalidOperationException("无法控制此输入框的输入法，请先切到中文模式。");
            bool ready = await Compatibility.WaitUntil(delegate {
                try {
                    int mode = (int)ImeControl(ime, 1, 0);
                    if ((mode & 1) != 0 && ImeControl(ime, 5, 0) != 0) return true;
                    ImeControl(ime, 6, 1);
                    ImeControl(ime, 2, mode | 1);
                } catch (InvalidOperationException) { }
                return false;
            }, checkTarget, 10, 60);
            if (!ready)
                throw new InvalidOperationException("未能切到中文模式；请手动切到“中”，必要时启用旧版微软拼音兼容性后重试。");
        }
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
        readonly Native.HookProc mouseProc;
        readonly NotifyIcon tray;
        bool enabled = true;
        readonly Icon appIcon;
        readonly Icon trayIcon;
        bool startHidden;
        IntPtr hook, mouseHook;
        bool busy, quitting;
        bool interrupted;
        IntPtr expectedFocus;
        AutomationElement expectedElement;
        bool altAlone;
        public MainForm(bool startupLaunch = false)
        {
            startHidden = startupLaunch;
            using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("TextTransformer.AppIcon")) {
                if (stream == null) throw new InvalidOperationException("程序图标资源缺失，请重新编译。");
                using (var icon = new Icon(stream)) appIcon = (Icon)icon.Clone();
            }
            trayIcon = new Icon(appIcon, SystemInformation.SmallIconSize);
            Icon = appIcon;
            Text = "文本转换助手"; FormBorderStyle = FormBorderStyle.FixedDialog;
            AutoScaleMode = AutoScaleMode.None; AutoSize = false;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.Manual;
            Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Color.FromArgb(247, 247, 249);
            float scale;
            using (var graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
            Func<int, int> px = value => (int)Math.Round(value * scale);
            ClientSize = new Size(px(480), px(280));
            var heading = new Label { Text = "双击快捷键", Font = new Font(Font.FontFamily, 14, FontStyle.Bold), ForeColor = Color.FromArgb(40, 43, 49), Bounds = new Rectangle(px(24), px(20), px(432), px(32)) };
            Controls.Add(heading);
            string[] keyNames = { "左 Alt", "CapsLock", "左 Shift", "右 Shift" };
            string[] actions = { "恢复拼音候选", "切换英文大小写", "提取中间内容 / 查词", "句首大写 / 标点转换" };
            for (int i = 0; i < keyNames.Length; i++) {
                var row = new Panel { BackColor = Color.White, Bounds = new Rectangle(px(24), px(66 + i * 48), px(432), px(42)) };
                var keyLabel = new Label { Text = keyNames[i], TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(255, 239, 220), ForeColor = Color.FromArgb(182, 78, 8), Font = new Font(Font, FontStyle.Bold), Bounds = new Rectangle(px(10), px(7), px(112), px(28)) };
                var actionLabel = new Label { Text = actions[i], TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(55, 58, 64), Bounds = new Rectangle(px(140), 0, px(280), px(42)) };
                row.Controls.Add(keyLabel); row.Controls.Add(actionLabel); Controls.Add(row);
            }
            var menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Point);
            menu.Items.Add("使用说明", null, delegate { ShowHelp(); });
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
            shortcutItem.Click += delegate { enabled = !enabled; shortcutItem.Checked = enabled; detector.Reset(); altAlone = false; };
            menu.Items.Add(shortcutItem);
            menu.Items.Add("退出", null, delegate { quitting = true; Close(); });
            tray = new NotifyIcon { Icon = trayIcon, Text = "TextTransformer", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { ShowHelp(); };
            if (startupError != null) ReportError(startupError);
            hookProc = OnKey;
            hook = Native.SetWindowsHookEx(13, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new InvalidOperationException("注册全局键盘监听失败。");
            mouseProc = OnMouse;
            mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
            if (mouseHook == IntPtr.Zero) {
                Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero;
                throw new InvalidOperationException("注册鼠标监听失败，无法保护拼音恢复范围。");
            }
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }
        void ReportError(string message)
        {
            tray.ShowBalloonTip(3000, "文本转换助手", message, ToolTipIcon.Warning);
        }
        void CenterHelp()
        {
            PerformLayout();
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
        }
        void ShowHelp()
        {
            WindowState = FormWindowState.Normal;
            CenterHelp();
            Show();
            Activate();
        }
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && value) {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            if (value) CenterHelp();
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
                    if (down) {
                        if (key == 0xA4) altAlone = !Native.Down(0x11) && !Native.Down(0x10) && !Native.Down(0x5B) && !Native.Down(0x5C);
                        else altAlone = false;
                    }
                    if (!down && key == 0xA4 && altAlone) {
                        try { Native.Key(0xE8); } catch (Exception ex) { ReportError(ex.Message); }
                        altAlone = false;
                    }
                    bool modifiers = Native.Down(0x11) || Native.Down(0xA5) || (key != 0xA4 && Native.Down(0xA4)) || Native.Down(0x5B) || Native.Down(0x5C)
                        || (key != 0xA0 && Native.Down(0xA0)) || (key != 0xA1 && Native.Down(0xA1));
                    var action = detector.Feed(key, down, Environment.TickCount & 0xFFFFFFFFL, modifiers);
                    if (action.HasValue) {
                        IntPtr target = Native.GetForegroundWindow();
                        IntPtr control = Native.FocusedControl(target);
                        busy = true; interrupted = false;
                        BeginInvoke(new Action(async delegate {
                            if (action.Value == ActionKind.RestorePinyin) await RestorePinyin(target, control);
                            else await ConvertTarget(target, action.Value, control);
                        }));
                    }
                }
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }
        IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            if (code >= 0 && (message == 0x201 || message == 0x204 || message == 0x207 || message == 0x20A || message == 0x20E || message == 0x20B)) {
                var data = (Native.MouseHookData)Marshal.PtrToStructure(lParam, typeof(Native.MouseHookData));
                if ((data.flags & 1) == 0) { detector.Reset(); altAlone = false; if (busy) interrupted = true; }
            }
            return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
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
        async Task<string> CopyText(IntPtr target, int attempts = 1, Func<string, bool> accept = null, Action<uint> copied = null, int polls = 12)
        {
            for (int attempt = 0; attempt < attempts; attempt++) {
                uint before = Native.GetClipboardSequenceNumber();
                CheckTarget(target); Native.Shortcut(0x43);
                for (int i = 0; i < polls; i++) {
                    await Task.Delay(40);
                    uint sequence = Native.GetClipboardSequenceNumber();
                    if (sequence != before && copied != null) copied(sequence);
                    CheckTarget(target);
                    if (sequence != before) {
                        try {
                            string text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                            if (accept == null || accept(text)) return text;
                        } catch (ExternalException) { }
                    }
                }
            }
            return null;
        }
        async Task<DataObject> BackupForPinyin(IntPtr target)
        {
            DataObject result = null;
            bool ready = await Compatibility.WaitUntil(delegate {
                try { result = BackupClipboard(); return true; }
                catch (ExternalException) { return false; }
            }, () => CheckTarget(target), 6, 50);
            if (!ready) throw new InvalidOperationException("剪贴板持续被占用，未修改文字，请稍后再试。");
            return result;
        }
        async Task<PinyinSelection> AcquireSelection(IntPtr target, IntPtr control)
        {
            PinyinSelection result = null;
            Exception last = null;
            bool ready = await Compatibility.WaitUntil(delegate {
                try {
                    if (!EditableFocus() && !Native.IsEditableTextControl(control)) return false;
                    result = new PinyinSelection(control);
                    // An accessibility provider can be present but publish empty
                    // text until its first query has activated it.
                    return result.KeyboardOnly || result.SelectedText.Length != 0 || result.AutomaticText().Length != 0;
                } catch (ElementNotAvailableException ex) { last = ex; }
                  catch (InvalidOperationException ex) { last = ex; }
                  catch (ExternalException ex) { last = ex; }
                return false;
            }, () => CheckTarget(target), 4, 100);
            if (ready || (last == null && result != null)) return result;
            throw last ?? new InvalidOperationException("请在可编辑输入框中恢复拼音。");
        }
        void CheckTarget(IntPtr target)
        {
            if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("焦点已切换，已取消转换。");
            if (expectedFocus != IntPtr.Zero && Native.FocusedControl(target) != expectedFocus) throw new InvalidOperationException("输入控件已切换，已取消转换。");
            if (interrupted) throw new InvalidOperationException("检测到新的键盘输入，已取消转换，请重试。");
            if (expectedElement != null && !Automation.Compare(expectedElement, AutomationElement.FocusedElement)) throw new InvalidOperationException("输入框已切换，已取消拼音恢复。");
        }
        async Task RestorePinyin(IntPtr target, IntPtr control)
        {
            expectedFocus = control;
            PinyinSelection selection = null;
            bool temporarySelection = false;
            DataObject backup = null;
            bool saved = false;
            uint ownedSequence = 0;
            try {
                await Task.Delay(40); CheckTarget(target);
                if (target == Handle) return;
                if (Native.Down(0x10) || Native.Down(0x11) || Native.Down(0x12)) return;
                if ((Native.GetKeyState(0x14) & 1) != 0) throw new InvalidOperationException("请先关闭 CapsLock 再恢复拼音。");
                selection = await AcquireSelection(target, control); expectedElement = selection.Element;
                backup = await BackupForPinyin(target); saved = true;
                bool automatic;
                string pinyin;
                if (selection.KeyboardOnly) {
                    // Qt QLineEdit's Copy is a no-op without a selection.
                    string selected = await CopyText(target, 2, text => !String.IsNullOrEmpty(text), sequence => ownedSequence = sequence, 6);
                    ownedSequence = Native.GetClipboardSequenceNumber();
                    automatic = String.IsNullOrEmpty(selected);
                    if (automatic) {
                        CheckTarget(target); Native.SelectPrefix(); await Task.Delay(60);
                        string prefix = await CopyText(target, 3, text => !String.IsNullOrEmpty(text) && selection.KeyboardValue.StartsWith(text, StringComparison.Ordinal), sequence => ownedSequence = sequence);
                        ownedSequence = Native.GetClipboardSequenceNumber();
                        if (String.IsNullOrEmpty(prefix)) throw new InvalidOperationException("光标前没有可读取的文字，未修改输入内容。");
                        temporarySelection = true;
                        if (!selection.KeyboardValue.StartsWith(prefix, StringComparison.Ordinal))
                            throw new InvalidOperationException("无法核对输入框中的光标位置，未修改文字。");
                        pinyin = PinyinText.Suffix(prefix);
                        CheckTarget(target); Native.Key(0x27); temporarySelection = false;
                    } else pinyin = selected;
                } else {
                    string selected = selection.SelectedText;
                    automatic = selected.Length == 0;
                    pinyin = automatic ? selection.AutomaticText() : selected;
                }
                if (automatic && pinyin.Length == 0)
                    throw new InvalidOperationException("光标前没有可恢复的拼音字母；可以选中拼音后双击左 Alt。");
                if (!PinyinText.Valid(pinyin)) throw new InvalidOperationException("请仅选中拼音字母和英文单引号，每次最多 64 个字符。");
                CheckTarget(target); await Native.EnsureChinese(control, () => CheckTarget(target));
                await Task.Delay(80); CheckTarget(target);
                if (automatic) { Native.SelectPrevious(pinyin.Length); temporarySelection = true; await Task.Delay(60); }
                string actual = await CopyText(target, 3, text => String.Equals(text, pinyin, StringComparison.Ordinal), sequence => ownedSequence = sequence);
                ownedSequence = Native.GetClipboardSequenceNumber();
                if (!String.Equals(actual, pinyin, StringComparison.Ordinal))
                    throw new InvalidOperationException("实际选中的文字与待恢复拼音不一致，未替换任何文字。");
                foreach (char letter in pinyin.ToLowerInvariant()) {
                    CheckTarget(target);
                    Native.Key(letter == '\'' ? (ushort)0xDE : (ushort)Char.ToUpperInvariant(letter));
                    temporarySelection = false;
                    await Task.Delay(35);
                }
                // Send virtual keys only: no Unicode paste, Space or Enter.
            } catch (Exception ex) {
                if (temporarySelection && Native.GetForegroundWindow() == target && Native.FocusedControl(target) == control) {
                    try { if (expectedElement == null || Automation.Compare(expectedElement, AutomationElement.FocusedElement)) Native.Key(0x27); } catch { }
                }
                ReportError(ex.Message);
            } finally {
                try {
                    if (saved && ownedSequence != 0 && Native.GetClipboardSequenceNumber() == ownedSequence) {
                        if (backup == null) Clipboard.Clear(); else Clipboard.SetDataObject(backup, true, 5, 40);
                    }
                } catch { ReportError("拼音恢复已结束，但剪贴板恢复失败。"); }
                expectedElement = null; detector.Reset(); busy = false;
            }
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
                if (kind == ActionKind.LettersOnly && !EditableFocus() && result.Length > 0) {
                    CheckTarget(target);
                    Clipboard.SetText(result);
                    saved = false;
                    tray.ShowBalloonTip(2000, "文本转换助手", "已复制提取结果，可以直接粘贴。", ToolTipIcon.Info);
                    return;
                }
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
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (disposing) { tray.ContextMenuStrip.Dispose(); tray.Dispose(); trayIcon.Dispose(); appIcon.Dispose(); }
            base.Dispose(disposing);
        }
    }

    static class Program
    {
        [STAThread] static void Main(string[] args)
        {
            if (Array.IndexOf(args, "--self-test") >= 0) {
                try { SelfTest(); Console.WriteLine("All TextTransformer checks passed."); Environment.Exit(0); }
                catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); Environment.Exit(1); }
                return;
            }
            bool created;
            Native.SetProcessDPIAware();
            using (var mutex = new Mutex(true, "Local\\TextTransformer.Desktop", out created)) {
                if (!created) { MessageBox.Show("程序已运行，请在系统托盘中打开。", "文本转换助手"); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new MainForm(Array.IndexOf(args, "--startup") >= 0)); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败"); }
            }
        }
        static void Require(bool condition, string description) { if (!condition) throw new Exception(description); }
        static void SelfTest()
        {
            int probes = 0, guards = 0;
            Require(Compatibility.WaitUntil(() => ++probes == 3, () => guards++, 4, 0).GetAwaiter().GetResult() && probes == 3 && guards == 3,
                "A cold provider must settle within a single invocation");
            probes = 0;
            Require(!Compatibility.WaitUntil(() => { probes++; return false; }, () => { }, 3, 0).GetAwaiter().GetResult() && probes == 3,
                "An unavailable provider must have bounded retries");
            probes = 0;
            bool cancelled = false;
            try { Compatibility.WaitUntil(() => { probes++; return false; }, () => { throw new InvalidOperationException("Target changed"); }, 3, 0).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { cancelled = true; }
            Require(cancelled && probes == 0, "Target changes must abort without retrying or typing");
            Require(Transform.Apply("Hello，世界!", ActionKind.ToggleCase) == "HELLO，世界!", "Case conversion regression");
            Require(Transform.Apply("ABC", ActionKind.ToggleCase) == "abc", "Lowercase conversion regression");
            Require(Transform.Apply("a中B!123", ActionKind.LettersOnly) == "aB", "Letters-only regression");
            Require(Transform.Apply("hELLO。wORLD！", ActionKind.SentenceCase) == "Hello.World!", "Sentence/punctuation regression");
            foreach (string value in new[] { "nihao", "NIHAO", "xi'an", "nihao'" }) Require(PinyinText.Valid(value), "Valid pinyin rejected");
            foreach (string value in new[] { "", "你好nihao", "hello nihao", "nihao\n", "abc123", "'abc", "a''b", new string('a', 65) }) Require(!PinyinText.Valid(value), "Unsafe pinyin accepted");
            Require(PinyinText.Suffix("已有中文，English!nihaoshijie") == "nihaoshijie", "Recover only the ASCII suffix of mixed content");
            Require(PinyinText.Suffix("前文\r\nxi'an") == "xi'an", "Multiline/apostrophe suffix");
            Require(PinyinText.Suffix("nihao，") == "" && PinyinText.Suffix("nihao ") == "", "Do not recover letters behind a boundary");
            Require(PinyinText.Suffix(new string('a', 65)) == "", "Do not truncate an oversized suffix");
            Require(PinyinText.Suffix("hello nihao") == "nihao", "Preserve an existing English word");
            Require(PinyinText.Suffix("abc''def") == "", "Reject malformed apostrophe runs");
            using (var edit = new TextBox()) {
                string text = "前文，English!nihao后文";
                edit.Text = text;
                IntPtr handle = edit.Handle;
                int start = text.IndexOf("nihao", StringComparison.Ordinal);
                edit.Select(start, 5);
                Native.EditSnapshot snapshot;
                Require(Native.TryReadEdit(handle, out snapshot), "Native Edit must work without UIA TextPattern");
                Require(snapshot.Text.Substring(snapshot.Start, snapshot.End - snapshot.Start) == "nihao" && edit.Text == text, "Native selection must preserve surrounding content");
                edit.Select(start + 5, 0);
                Require(Native.TryReadEdit(handle, out snapshot) && snapshot.Start == snapshot.End && PinyinText.Suffix(snapshot.Text.Substring(0, snapshot.Start)) == "nihao", "Read the caret in the middle of mixed text");
                edit.ReadOnly = true;
                Require(!Native.TryReadEdit(edit.Handle, out snapshot), "Reject read-only Edit controls");
                edit.ReadOnly = false; edit.UseSystemPasswordChar = true;
                Require(!Native.TryReadEdit(edit.Handle, out snapshot), "Reject password Edit controls");
            }
            using (var edit = new RichTextBox()) {
                edit.Text = "中文!xi'an";
                IntPtr handle = edit.Handle;
                edit.Select(edit.Text.Length, 0);
                Native.EditSnapshot snapshot;
                Require(Native.TryReadEdit(handle, out snapshot) && PinyinText.Suffix(snapshot.Text.Substring(0, snapshot.Start)) == "xi'an", "RichEdit caret and pinyin separator");
            }
            var taps = new TapDetector();
            Require(!taps.Feed(0xA4, true, 0, false).HasValue, "First Alt down");
            Require(!taps.Feed(0xA4, false, 60, false).HasValue, "Single Alt tap");
            taps.Feed(0xA4, true, 150, false);
            Require(taps.Feed(0xA4, false, 210, false) == ActionKind.RestorePinyin, "Double left Alt");
            taps.Reset(); taps.Feed(0xA4, true, 0, false); taps.Feed(0x09, true, 30, true); taps.Feed(0xA4, false, 50, false);
            taps.Feed(0xA4, true, 100, false);
            Require(!taps.Feed(0xA4, false, 160, false).HasValue, "Alt+Tab must not trigger recovery");
            taps.Reset(); taps.Feed(0xA4, true, 0, false); taps.Feed(0xA4, false, 300, false); taps.Feed(0xA4, true, 350, false);
            Require(!taps.Feed(0xA4, false, 400, false).HasValue, "Long Alt hold must not count as a tap");
            foreach (int key in new[] { 0x14, 0xA0, 0xA1 }) {
                taps.Reset(); taps.Feed(key, true, 0, false); taps.Feed(key, false, 50, false); taps.Feed(key, true, 100, false);
                Require(taps.Feed(key, false, 150, false) == (key == 0x14 ? ActionKind.ToggleCase : key == 0xA0 ? ActionKind.LettersOnly : ActionKind.SentenceCase), "Existing tap shortcut regression");
            }
            Require(Marshal.SizeOf(typeof(Native.Input)) == (IntPtr.Size == 8 ? 40 : 28), "Native SendInput layout");
        }
    }
}

