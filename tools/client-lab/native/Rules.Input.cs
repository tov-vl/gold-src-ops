using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GsoLab.Interaction
{
    internal sealed class WindowState
    {
        internal IntPtr Handle;
        internal bool IdentityMatches;
        internal bool Visible;
        internal bool Minimized;
        internal bool Foreground;
        internal bool DesktopUnlocked;
        internal bool SessionActive;
        internal bool PhysicalGeometry;
        internal int WindowCount;
        internal int Width;
        internal int Height;
    }

    internal interface IWindowPort
    {
        WindowState Read();
        void Post(IntPtr window, uint message, IntPtr word, IntPtr data);
        bool Activate(IntPtr window);
        void Delay(int milliseconds);
    }

    internal interface IDpiContextPort
    {
        IntPtr Set(IntPtr context);
        int LastError { get; }
    }

    internal interface IFrameCapturePort
    {
        byte[] Capture(IntPtr window, int width, int height);
    }

    internal sealed class NativeFrameCapturePort : IFrameCapturePort
    {
        public byte[] Capture(IntPtr window, int width, int height)
        {
            if (window == IntPtr.Zero || window == new IntPtr(-1) || window == new IntPtr(0xffff) || width != 1920 || height != 1200)
            {
                throw new InvalidOperationException("Only the fixed owned client window can be captured.");
            }
            using (new PhysicalDpiScope(new NativeDpiContextPort()))
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                // DCX_CACHE | DCX_CLIPSIBLINGS: a releasable client DC for this HWND only.
                var source = NativeMethods.GetDCEx(window, IntPtr.Zero, 0x12);
                if (source == IntPtr.Zero) { throw new InvalidOperationException("Owned client DC is unavailable."); }
                bool released;
                try
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        var target = graphics.GetHdc();
                        try
                        {
                            if (!NativeMethods.BitBlt(target, 0, 0, width, height, source, 0, 0, 0x00cc0020))
                            {
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned client copy failed.");
                            }
                        }
                        finally { graphics.ReleaseHdc(target); }
                    }
                }
                finally { released = NativeMethods.ReleaseDC(window, source) != 0; }
                if (!released) { throw new InvalidOperationException("Borrowed client DC release failed."); }
                var first = bitmap.GetPixel(16, 16).ToArgb();
                var varied = false;
                for (var y = 16; y < height && !varied; y += 128)
                {
                    for (var x = 16; x < width; x += 128)
                    {
                        if (bitmap.GetPixel(x, y).ToArgb() == first) { continue; }
                        varied = true;
                        break;
                    }
                }
                if (!varied) { throw new InvalidOperationException("Owned client copy is flat or unavailable; no fallback."); }
                using (var output = new MemoryStream())
                {
                    bitmap.Save(output, ImageFormat.Bmp);
                    return output.ToArray();
                }
            }
        }
    }

    internal sealed class NativeDpiContextPort : IDpiContextPort
    {
        public IntPtr Set(IntPtr context) { return NativeMethods.SetThreadDpiAwarenessContext(context); }
        public int LastError { get { return Marshal.GetLastWin32Error(); } }
    }

    internal sealed class PhysicalDpiScope : IDisposable
    {
        private readonly IDpiContextPort _port;
        private readonly int _threadId;
        private IntPtr _previous;

        internal PhysicalDpiScope(IDpiContextPort port)
        {
            if (port == null) { throw new ArgumentNullException("port"); }
            _port = port;
            _threadId = Environment.CurrentManagedThreadId;
            _previous = port.Set(new IntPtr(-4)); // PER_MONITOR_AWARE_V2, this thread only.
            if (_previous == IntPtr.Zero) { throw new Win32Exception(port.LastError, "Physical DPI context is unavailable."); }
        }

        public void Dispose()
        {
            if (_previous == IntPtr.Zero) { return; }
            if (Environment.CurrentManagedThreadId != _threadId) { throw new InvalidOperationException("DPI context cannot be restored on a different thread."); }
            var previous = _previous;
            _previous = IntPtr.Zero; // A failed restore is not retried with an uncertain context.
            if (_port.Set(previous) == IntPtr.Zero) { throw new Win32Exception(_port.LastError, "Previous DPI context could not be restored."); }
        }
    }

    // This source also compiles in Windows PowerShell 5.1; keep C# 5 syntax.
    public sealed class GameWindow
    {
        private readonly IWindowPort _port;
        private readonly IFrameCapturePort _frames;
        private IntPtr _pinnedWindow;
        private bool _preparationUsed;

        // Bounded diagnostic projection. No titles, handles, paths or other windows are exposed.
        public string GuardCode { get; private set; }
        public bool GuardObservationComplete { get; private set; }
        public int GuardFlags { get; private set; }
        public int ClientWidth { get; private set; }
        public int ClientHeight { get; private set; }
        public int VisibleWindowCount { get; private set; }
        public int? NativeErrorCode { get; private set; }
        public bool ActivationAttempted { get; private set; }
        public bool? ActivationAccepted { get; private set; }
        public FocusObservation FocusAttempt { get; private set; }

        public GameWindow(int processId, long startedUtcTicks, string executable, int sessionId)
            : this(new NativeWindowPort(processId, startedUtcTicks, executable, sessionId)) { }

        internal GameWindow(IWindowPort port)
            : this(port, new NativeFrameCapturePort()) { }

        internal GameWindow(IWindowPort port, IFrameCapturePort frames)
        {
            if (port == null) { throw new ArgumentNullException("port"); }
            if (frames == null) { throw new ArgumentNullException("frames"); }
            _port = port;
            _frames = frames;
        }

        private IntPtr Check(bool requireForeground = true)
        {
            GuardCode = "window_observation_failed";
            GuardObservationComplete = false;
            GuardFlags = ClientWidth = ClientHeight = VisibleWindowCount = 0;
            NativeErrorCode = null;
            WindowState state;
            try { state = _port.Read(); }
            catch (Win32Exception error) { NativeErrorCode = error.NativeErrorCode; throw; }
            if (state == null)
            {
                GuardCode = "window_state_unavailable";
                throw new InvalidOperationException("Owned game window state is unavailable.");
            }
            var validHandle = state.Handle != IntPtr.Zero && state.Handle != new IntPtr(-1) && state.Handle != new IntPtr(0xffff);
            GuardObservationComplete = true;
            GuardFlags = (state.IdentityMatches ? 1 : 0) | (state.Visible ? 2 : 0) | (state.Minimized ? 4 : 0) |
                (state.Foreground ? 8 : 0) | (state.DesktopUnlocked ? 16 : 0) | (state.SessionActive ? 32 : 0) | (validHandle ? 64 : 0) | (state.PhysicalGeometry ? 128 : 0);
            ClientWidth = state.Width;
            ClientHeight = state.Height;
            VisibleWindowCount = state.WindowCount;
            GuardCode = GetGuardCode(state, validHandle, requireForeground);
            if (!string.Equals(GuardCode, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Owned game window is not ready for input.");
            }
            if (_pinnedWindow == IntPtr.Zero) { _pinnedWindow = state.Handle; }
            if (state.Handle == _pinnedWindow) { return _pinnedWindow; }
            GuardCode = "window_identity_changed";
            throw new InvalidOperationException("Game window identity changed.");
        }

        private static string GetGuardCode(WindowState state, bool validHandle, bool requireForeground)
        {
            if (!state.IdentityMatches) { return "process_identity_changed"; }
            if (state.WindowCount != 1) { return "window_count_mismatch"; }
            if (!validHandle) { return "invalid_window_handle"; }
            if (!state.Visible) { return "window_not_visible"; }
            if (state.Minimized) { return "window_minimized"; }
            if (requireForeground && !state.Foreground) { return "window_not_foreground"; }
            if (!state.DesktopUnlocked) { return "desktop_not_default"; }
            if (!state.SessionActive) { return "session_not_active"; }
            if (!state.PhysicalGeometry) { return "physical_geometry_unverified"; }
            if (state.Width != 1920 || state.Height != 1200) { return "client_size_mismatch"; }
            return "ready";
        }

        private void Post(uint message, IntPtr word, IntPtr data)
        {
            var window = Check();
            try { _port.Post(window, message, word, data); }
            catch (Win32Exception error)
            {
                GuardCode = "input_delivery_uncertain";
                NativeErrorCode = error.NativeErrorCode;
                throw;
            }
        }

        private void Key(int key, int scan, int holdMilliseconds)
        {
            var down = 1 | (scan << 16);
            var up = unchecked(down | (int)0xc0000000);
            Post(0x0100, new IntPtr(key), new IntPtr(down));
            try { _port.Delay(holdMilliseconds); }
            finally { Post(0x0101, new IntPtr(key), new IntPtr(up)); }
        }

        public void CheckReady() { Check(); }

        public byte[] CaptureFrame()
        {
            var window = Check();
            byte[] bytes;
            try { bytes = _frames.Capture(window, 1920, 1200); }
            catch (Win32Exception error) { GuardCode = "owned_frame_capture_failed"; NativeErrorCode = error.NativeErrorCode; throw; }
            catch (InvalidOperationException) { GuardCode = "owned_frame_capture_unavailable"; throw; }
            Check(); // Discard pixels if process, focus, desktop, geometry or HWND changed.
            if (bytes != null && bytes.Length >= 54 && bytes.Length <= 16 * 1024 * 1024) { return bytes; }
            GuardCode = "owned_frame_size_mismatch";
            throw new InvalidOperationException("Owned client frame size differs.");
        }

        public void PrepareOwnWindow()
        {
            if (_preparationUsed)
            {
                GuardCode = "window_preparation_already_used";
                throw new InvalidOperationException("Window preparation is one-attempt only.");
            }
            _preparationUsed = true;
            var window = Check(requireForeground: false);
            if ((GuardFlags & 8) != 0) { return; }
            ActivationAttempted = true;
            try { ActivationAccepted = _port.Activate(window); }
            catch (Win32Exception error)
            {
                GuardCode = "window_activation_failed";
                NativeErrorCode = error.NativeErrorCode;
                throw;
            }
            catch (InvalidOperationException)
            {
                GuardCode = "window_activation_unavailable";
                throw;
            }
            finally
            {
                var diagnostics = _port as IFocusDiagnosticsPort;
                FocusAttempt = diagnostics == null ? null : diagnostics.ReadFocusObservation();
            }
            if (!ActivationAccepted.Value)
            {
                GuardCode = "window_activation_denied";
                throw new InvalidOperationException("Windows did not activate the owned game window; no fallback or retry.");
            }
            _port.Delay(200);
            Check(); // The API result alone never permits input to a background window.
        }

        public void ClickOk()
        {
            // The fixed 1920x1200 MOTD button must first be approved in a fresh native frame.
            var point = new IntPtr(516 | (936 << 16));
            Post(0x0200, IntPtr.Zero, point);
            _port.Delay(40);
            Post(0x0201, new IntPtr(1), point);
            try { _port.Delay(40); }
            finally { Post(0x0202, IntPtr.Zero, point); }
        }

        public void MoveControls()
        {
            Key(0x53, 0x1f, 250); // S / +back, checked separately in the existing game config.
            Key(0x41, 0x1e, 250); // A / +moveleft.
        }

        public void ReopenRules()
        {
            ReopenPanel("rules");
        }

        public void ReopenPanel(string panel)
        {
            if (!string.Equals(panel, "rules", StringComparison.Ordinal) &&
                !string.Equals(panel, "ranks", StringComparison.Ordinal) &&
                !string.Equals(panel, "profile", StringComparison.Ordinal) &&
                !string.Equals(panel, "topall", StringComparison.Ordinal))
            { throw new ArgumentException("Fixed panel required.", "panel"); }
            ChatFixed(panel);
        }

        public void ReopenGuns() { ChatFixed("guns"); }

        public void SelectGunsItem(string phase)
        {
            switch (phase)
            {
                case "exit-primary":
                case "exit-pistol-respawn": Key(0x30, 0x0b, 40); return;
                case "select-primary":
                case "repeat-primary": Key(0x32, 0x03, 40); return;
                case "select-pistol": Key(0x33, 0x04, 40); return;
                default: throw new ArgumentException("Fixed weapons phase required.", "phase");
            }
        }

        private void ChatFixed(string panel)
        {
            Key(0x55, 0x16, 40); // U / messagemode2.
            _port.Delay(180);
            foreach (var value in "/" + panel)
            {
                Post(0x0102, new IntPtr(value), new IntPtr(1));
                _port.Delay(25);
            }
            Key(0x0d, 0x1c, 40);
        }

        public void Snapshot()
        {
            Key(0x74, 0x3f, 40); // F5 / native snapshot; no console, bind or global input.
        }
    }

    internal sealed class NativeWindowPort : IWindowPort, IFocusDiagnosticsPort
    {
        private readonly int _processId;
        private readonly long _startedUtcTicks;
        private readonly string _executable;
        private readonly int _sessionId;

        internal NativeWindowPort(int processId, long startedUtcTicks, string executable, int sessionId)
        {
            if (processId <= 0) { throw new ArgumentOutOfRangeException("processId"); }
            if (startedUtcTicks <= 0) { throw new ArgumentOutOfRangeException("startedUtcTicks"); }
            if (sessionId <= 0) { throw new ArgumentOutOfRangeException("sessionId"); }
            if (string.IsNullOrWhiteSpace(executable)) { throw new ArgumentException("Executable path required.", "executable"); }
            _processId = processId;
            _startedUtcTicks = startedUtcTicks;
            _executable = Path.GetFullPath(executable);
            _sessionId = sessionId;
        }

        public WindowState Read()
        {
            using (new PhysicalDpiScope(new NativeDpiContextPort()))
            {
                var state = new WindowState();
                using (var process = Process.GetProcessById(_processId))
                {
                    var module = process.MainModule;
                    state.IdentityMatches = module != null && process.StartTime.ToUniversalTime().Ticks == _startedUtcTicks &&
                        process.SessionId == _sessionId &&
                        string.Equals(module.FileName, _executable, StringComparison.OrdinalIgnoreCase);
                }
                NativeMethods.WindowCallback callback = delegate (IntPtr window, IntPtr unused)
                {
                    uint owner;
                    var thread = NativeMethods.GetWindowThreadProcessId(window, out owner);
                    if (thread == 0 || owner != (uint)_processId || !NativeMethods.IsWindowVisible(window)) { return true; }
                    state.WindowCount++;
                    state.Handle = window;
                    return true;
                };
                if (!NativeMethods.EnumWindows(callback, IntPtr.Zero)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                GC.KeepAlive(callback);
                if (state.WindowCount != 1) { return state; }
                state.Visible = NativeMethods.IsWindowVisible(state.Handle);
                state.Minimized = NativeMethods.IsIconic(state.Handle);
                state.Foreground = NativeMethods.GetForegroundWindow() == state.Handle;
                NativeMethods.Rect rectangle;
                if (!NativeMethods.GetClientRect(state.Handle, out rectangle)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                state.Width = rectangle.Right - rectangle.Left;
                state.Height = rectangle.Bottom - rectangle.Top;
                state.PhysicalGeometry = true;
                state.DesktopUnlocked = IsInputDesktopDefault();
                state.SessionActive = IsSessionActive();
                return state;
            }
        }

        private bool IsSessionActive()
        {
            IntPtr buffer;
            uint size;
            if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero, _sessionId, 8, out buffer, out size)) { return false; }
            try { return size == 4 && buffer != IntPtr.Zero && Marshal.ReadInt32(buffer) == 0; }
            finally { NativeMethods.WTSFreeMemory(buffer); }
        }

        private static bool IsInputDesktopDefault()
        {
            var desktop = NativeMethods.OpenInputDesktop(flags: 0, inherit: false, access: 1);
            if (desktop == IntPtr.Zero) { return false; }
            try
            {
                var name = new char[64];
                uint needed;
                if (!NativeMethods.GetUserObjectInformationW(desktop, 2, name, (uint)(name.Length * 2), out needed)) { return false; }
                var end = Array.IndexOf(name, value: '\0');
                return needed <= name.Length * 2 && end >= 0 && string.Equals(new string(name, 0, end), "Default", StringComparison.Ordinal);
            }
            finally { NativeMethods.CloseDesktop(desktop); }
        }

        public void Post(IntPtr window, uint message, IntPtr word, IntPtr data)
        {
            using (new PhysicalDpiScope(new NativeDpiContextPort()))
            {
                // Only GameWindow's fixed actions call this implementation.
                if (!NativeMethods.PostMessageW(window, message, word, data))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Game input was not queued; do not retry automatically.");
                }
            }
        }

        private FocusObservation _lastFocus;

        public FocusObservation ReadFocusObservation() { return _lastFocus; }

        public bool Activate(IntPtr window)
        {
            var focus = new NativeFocusPort(_processId);
            try { return OwnedFocus.TryFocus(focus, window); }
            finally { _lastFocus = focus.ReadFocusObservation(); }
        }

        public void Delay(int milliseconds) { Thread.Sleep(milliseconds); }
    }

    internal static class NativeMethods
    {
        internal delegate bool WindowCallback(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr window, out Rect rectangle);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] char[] value, uint length, out uint needed);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(IntPtr window, uint message, IntPtr word, IntPtr data);

        [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int information, out IntPtr buffer, out uint bytes);

        [DllImport("wtsapi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void WTSFreeMemory(IntPtr buffer);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetDCEx(IntPtr window, IntPtr clippingRegion, uint flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(IntPtr target, int targetX, int targetY, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    }
}

namespace GsoLab.Interaction
{
    // Fixed visual profile for the retained 1920x1200 v2.99 fixture only.
    public sealed class FrameObservation
    {
        public string BodySha256 { get; internal set; }
        public bool PanelMatches { get; internal set; }
        public bool SceneCandidate { get; internal set; }
    }

    public static class RulesFrameGate
    {
        public static FrameObservation Analyze(byte[] bytes, string language)
        {
            return AnalyzePanel(bytes, "rules", language);
        }

        public static FrameObservation AnalyzePanel(byte[] bytes, string panel, string language)
        {
            if (bytes == null || bytes.Length < 54 || bytes.Length > 16 * 1024 * 1024)
            { throw new ArgumentException("Bounded frame required.", "bytes"); }
            if (!string.Equals(language, "ru", StringComparison.Ordinal) && !string.Equals(language, "en", StringComparison.Ordinal))
            { throw new ArgumentException("Fixed language required.", "language"); }
            if (bytes[0] != 66 || bytes[1] != 77 || BitConverter.ToInt32(bytes, 18) != 1920 ||
                BitConverter.ToInt32(bytes, 22) != 1200 || BitConverter.ToInt16(bytes, 28) != 24 ||
                BitConverter.ToInt32(bytes, 30) != 0)
            { throw new InvalidOperationException("Expected uncompressed 1920x1200 24-bit BMP."); }
            using (var input = new MemoryStream(bytes, writable: false))
            using (var bitmap = new Bitmap(input))
            {
                var pixels = ReadPixels(bitmap);
                var body = new byte[1200 * 600 * 3];
                var background = 0;
                for (var y = 0; y < 600; y++)
                {
                    Buffer.BlockCopy(pixels, ((y + 290) * 1920 + 350) * 3, body, y * 1200 * 3, 1200 * 3);
                }
                for (var index = 0; index < body.Length; index += 3)
                {
                    if (body[index] == 43 && body[index + 1] == 33 && body[index + 2] == 24) { background++; }
                }
                string hash;
                using (var algorithm = SHA256.Create())
                { hash = ToHex(algorithm.ComputeHash(body)); }
                var expected = ExpectedBody(panel, language);
                var buttonInk = 0;
                for (var y = 925; y < 947; y++)
                {
                    for (var x = 500; x < 531; x++)
                    {
                        var offset = (y * 1920 + x) * 3;
                        if (pixels[offset + 2] > 170 && pixels[offset + 1] > 100 && pixels[offset] < 90) { buttonInk++; }
                    }
                }
                var colors = new HashSet<int>();
                for (var y = 300; y < 880; y += 20)
                {
                    for (var x = 360; x < 1540; x += 20)
                    {
                        var offset = (y * 1920 + x) * 3;
                        colors.Add((pixels[offset] >> 4) | ((pixels[offset + 1] >> 4) << 4) | ((pixels[offset + 2] >> 4) << 8));
                    }
                }
                return new FrameObservation
                {
                    BodySha256 = hash,
                    PanelMatches = string.Equals(hash, expected, StringComparison.Ordinal) && buttonInk >= 20,
                    // Absence and scene variation are prerequisites, not proof of control or acceptance.
                    SceneCandidate = background < 7200 && colors.Count > 32
                };
            }
        }

        private static string ExpectedBody(string panel, string language)
        {
            switch (panel)
            {
                case "rules":
                    return string.Equals(language, "en", StringComparison.Ordinal) ?
                        "98ABF91AC6295CF2ECFAA9E4313D740D025C4F9B733732502E8476BDA431AC27" :
                        "7FBB711203E96B30136755219814B369E2BC7E521BCED8E67E8006F67DA5F505";
                case "ranks":
                    return string.Equals(language, "en", StringComparison.Ordinal) ?
                        "FD12421105BBE5FE38E4C5A3B1E218129FDE453DE5F037DAACEBF4A11C7D7DD0" :
                        "671EEE8068DB22686A7CB78219EC7ED62EA8C3BEF797BEF7948D636BB1DDAA98";
                case "profile":
                    return string.Equals(language, "en", StringComparison.Ordinal) ?
                        "787DE4A1C1C3EC8A337B76193917F253FB2FDE26CEDAE8C3612C255C6FF6FC07" :
                        "DAC7E72E928BA219A04F69221D02FCE5445F4211CFA4C63889C815246828925B";
                case "topall":
                    return string.Equals(language, "en", StringComparison.Ordinal) ?
                        "6095B3D59FB2C79C7F38A4D1BBC03FCCD57939E95852B3C0B03E9FFC2002E885" :
                        "C830ECFB036297BE9D5D4F9CFED6EF8F6DDB475C057C9D0B271247715E3BF5A1";
                default:
                    throw new ArgumentException("Fixed panel required.", "panel");
            }
        }

        internal static string ToHex(byte[] digest)
        {
            const string alphabet = "0123456789ABCDEF";
            var characters = new char[digest.Length * 2];
            for (var index = 0; index < digest.Length; index++)
            {
                characters[index * 2] = alphabet[digest[index] >> 4];
                characters[index * 2 + 1] = alphabet[digest[index] & 15];
            }
            return new string(characters);
        }

        internal static byte[] ReadPixels(Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, 1920, 1200), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var pixels = new byte[1920 * 1200 * 3];
                for (var y = 0; y < 1200; y++)
                { Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * 1920 * 3, 1920 * 3); }
                return pixels;
            }
            finally { bitmap.UnlockBits(data); }
        }
    }

    public static class GunsFrameGate
    {
        // Fixed fixture backdrop only; these pixels are not a production-scene profile.
        public static FrameObservation Analyze(byte[] bytes, string expectedSha256)
        {
            if (bytes == null || bytes.Length < 54 || bytes.Length > 16 * 1024 * 1024 ||
                bytes[0] != 66 || bytes[1] != 77 || BitConverter.ToInt32(bytes, 18) != 1920 ||
                BitConverter.ToInt32(bytes, 22) != 1200 || BitConverter.ToInt16(bytes, 28) != 24 ||
                BitConverter.ToInt32(bytes, 30) != 0)
            { throw new ArgumentException("Expected bounded uncompressed 1920x1200 24-bit BMP.", "bytes"); }
            using (var input = new MemoryStream(bytes, writable: false))
            using (var bitmap = new Bitmap(input))
            {
                var pixels = RulesFrameGate.ReadPixels(bitmap);
                var body = new byte[800 * 400 * 3];
                var black = 0;
                for (var y = 0; y < 400; y++)
                {
                    Buffer.BlockCopy(pixels, (y + 350) * 1920 * 3, body, y * 800 * 3, 800 * 3);
                    // The buy-zone HUD occupies the left margin in the gap below menu items.
                    if (y >= 180 && y < 300) { Array.Clear(body, y * 800 * 3, 44 * 3); }
                }
                for (var offset = 0; offset < body.Length; offset += 3)
                { if (body[offset] == 0 && body[offset + 1] == 0 && body[offset + 2] == 0) { black++; } }
                string hash;
                using (var algorithm = SHA256.Create())
                { hash = RulesFrameGate.ToHex(algorithm.ComputeHash(body)); }
                return new FrameObservation
                {
                    BodySha256 = hash,
                    PanelMatches = black > 288000 && black < 319500 &&
                        string.Equals(hash, expectedSha256, StringComparison.Ordinal),
                    SceneCandidate = false
                };
            }
        }
    }

    internal interface IFocusPort
    {
        bool TryForeground(IntPtr window);
        void FocusOwnedElement(IntPtr window);
    }

    internal static class OwnedFocus
    {
        internal static bool TryFocus(IFocusPort port, IntPtr window)
        {
            if (port == null) { throw new ArgumentNullException("port"); }
            if (window == IntPtr.Zero || window == new IntPtr(-1) || window == new IntPtr(0xffff))
            { throw new InvalidOperationException("A fixed owned HWND is required."); }
            if (port.TryForeground(window)) { return true; }
            // One scoped provider call, never an Alt key, desktop click, or global input.
            port.FocusOwnedElement(window);
            // The caller must independently recheck foreground, identity, desktop and dimensions.
            return true;
        }
    }

}

namespace GsoLab.Interaction
{
    public sealed class FocusObservation
    {
        public string Outcome { get; internal set; }
        public string Stage { get; internal set; }
        public bool ForegroundAttempted { get; internal set; }
        public bool? ForegroundAccepted { get; internal set; }
        public bool ProviderAttempted { get; internal set; }
        public string FailureCategory { get; internal set; }
        public int? FailureHResult { get; internal set; }
        public int? NativeFailureCode { get; internal set; }
        public int ElapsedMilliseconds { get; internal set; }
        public bool? Enabled { get; internal set; }
        public bool? KeyboardFocusable { get; internal set; }
        public bool? Offscreen { get; internal set; }
        public bool? WindowPatternAvailable { get; internal set; }
        public int? WindowVisualState { get; internal set; }
        public bool? WindowReady { get; internal set; }
        public string Method { get; internal set; }
    }

    internal interface IFocusDiagnosticsPort
    {
        FocusObservation ReadFocusObservation();
    }

    internal sealed class FocusRecorder
    {
        private readonly object _sync = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string _outcome = "not_attempted";
        private string _stage = "none";
        private bool _foregroundAttempted;
        private bool? _foregroundAccepted;
        private bool _providerAttempted;
        private string _failureCategory = "none";
        private int? _hresult;
        private int? _nativeCode;
        private FocusObservation _sealed;
        private FocusCapabilities _capabilities;
        private string _method = "none";

        internal void ForegroundStarted()
        { lock (_sync) { if (_sealed != null) { return; } _foregroundAttempted = true; _stage = "foreground"; } }
        internal void ForegroundReturned(bool accepted)
        { lock (_sync) { if (_sealed != null) { return; } _foregroundAccepted = accepted; _outcome = accepted ? "foreground_accepted" : "foreground_denied"; } }
        internal void ProviderStarted()
        { lock (_sync) { if (_sealed != null) { return; } _providerAttempted = true; _stage = "provider_dispatch"; _outcome = "provider_running"; } }
        internal void Stage(string stage)
        { lock (_sync) { if (_sealed != null) { return; } _stage = stage; } }
        internal void IdentityMismatch()
        { lock (_sync) { if (_sealed != null) { return; } _outcome = "provider_identity_mismatch"; } }
        internal void ProviderReturned()
        { lock (_sync) { if (_sealed != null) { return; } _outcome = "provider_returned"; _stage = "provider_completed"; } }
        internal void TimedOut()
        { lock (_sync) { if (_sealed != null) { return; } _outcome = "provider_timeout"; Seal(); } }
        internal void Capabilities(FocusCapabilities capabilities)
        { lock (_sync) { if (_sealed != null) { return; } _capabilities = capabilities; } }
        internal void Method(string method)
        { lock (_sync) { if (_sealed != null) { return; } _method = method; } }
        internal void NotActivatable()
        { lock (_sync) { if (_sealed != null) { return; } _outcome = "provider_not_activatable"; } }

        internal void Failure(Exception error)
        {
            lock (_sync)
            {
                if (_sealed != null) { return; }
                if (!string.Equals(_outcome, "provider_identity_mismatch", StringComparison.Ordinal) &&
                    !string.Equals(_outcome, "provider_not_activatable", StringComparison.Ordinal))
                { _outcome = _providerAttempted ? "provider_exception" : "foreground_exception"; }
                _failureCategory = Classify(error);
                _hresult = error.HResult;
                var native = error as Win32Exception;
                _nativeCode = native == null ? (int?)null : native.NativeErrorCode;
            }
        }

        internal FocusObservation Seal()
        {
            lock (_sync)
            {
                if (_sealed != null) { return _sealed; }
                _clock.Stop();
                _sealed = new FocusObservation
                {
                    Outcome = _outcome,
                    Stage = _stage,
                    ForegroundAttempted = _foregroundAttempted,
                    ForegroundAccepted = _foregroundAccepted,
                    ProviderAttempted = _providerAttempted,
                    FailureCategory = _failureCategory,
                    FailureHResult = _hresult,
                    NativeFailureCode = _nativeCode,
                    ElapsedMilliseconds = (int)Math.Min(_clock.ElapsedMilliseconds, int.MaxValue),
                    Enabled = _capabilities == null ? (bool?)null : _capabilities.Enabled,
                    KeyboardFocusable = _capabilities == null ? (bool?)null : _capabilities.KeyboardFocusable,
                    Offscreen = _capabilities == null ? (bool?)null : _capabilities.Offscreen,
                    WindowPatternAvailable = _capabilities == null ? (bool?)null : _capabilities.WindowPattern,
                    WindowVisualState = _capabilities == null ? null : _capabilities.VisualState,
                    WindowReady = _capabilities == null ? (bool?)null : _capabilities.WindowReady,
                    Method = _method
                };
                return _sealed;
            }
        }

        private static string Classify(Exception error)
        {
            // Never retain or serialize the message, stack, source, exception data, or arbitrary type name.
            var name = error.GetType().FullName;
            if (string.Equals(name, "System.Windows.Automation.ElementNotAvailableException", StringComparison.Ordinal)) { return "element_unavailable"; }
            if (string.Equals(name, "System.Windows.Automation.ElementNotEnabledException", StringComparison.Ordinal)) { return "element_not_enabled"; }
            if (error is Win32Exception) { return "win32"; }
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException) { return "access_denied"; }
            if (error is FileNotFoundException) { return "file_not_found"; }
            if (error is FileLoadException) { return "file_load"; }
            if (error is BadImageFormatException) { return "bad_image"; }
            if (error is TypeLoadException) { return "type_load"; }
            if (error is TypeInitializationException) { return "type_initialization"; }
            if (error is COMException) { return "com"; }
            if (error is InvalidOperationException) { return "invalid_operation"; }
            return error is ArgumentException ? "argument" : "unexpected";
        }
    }

    internal interface IFocusElement
    {
        int ProcessId { get; }
        IntPtr Window { get; }
        void SetFocus();
        FocusCapabilities Inspect();
        void Restore();
    }

    internal sealed class FocusCapabilities
    {
        internal bool Enabled;
        internal bool KeyboardFocusable;
        internal bool Offscreen;
        internal bool WindowPattern;
        internal bool WindowReady;
        internal int? VisualState;
    }

    internal interface IFocusPlatform
    {
        bool TryForeground(IntPtr window);
        IFocusElement Find(IntPtr window);
    }

    internal sealed class NativeFocusPort : IFocusPort, IFocusDiagnosticsPort
    {
        private readonly int _processId;
        private readonly IFocusPlatform _platform;
        private readonly int _waitMilliseconds;
        private readonly FocusRecorder _recorder = new FocusRecorder();

        internal NativeFocusPort(int processId) : this(processId, new NativeFocusPlatform(), 2000) { }
        internal NativeFocusPort(int processId, IFocusPlatform platform, int waitMilliseconds)
        {
            if (platform == null) { throw new ArgumentNullException("platform"); }
            if (waitMilliseconds < 1 || waitMilliseconds > 2000) { throw new ArgumentOutOfRangeException("waitMilliseconds"); }
            _processId = processId; _platform = platform; _waitMilliseconds = waitMilliseconds;
        }

        public bool TryForeground(IntPtr window)
        {
            _recorder.ForegroundStarted();
            try
            {
                var accepted = _platform.TryForeground(window);
                _recorder.ForegroundReturned(accepted);
                return accepted;
            }
            catch (Exception error) { _recorder.Failure(error); throw; }
        }

        public void FocusOwnedElement(IntPtr window)
        {
            _recorder.ProviderStarted();
            Exception error;
            bool completed;
            try
            {
                completed = BoundedFocus.Observe(delegate
            {
                _recorder.Stage("element_lookup");
                var element = _platform.Find(window);
                _recorder.Stage("identity_check");
                if (element == null || element.ProcessId != _processId || element.Window != window)
                {
                    _recorder.IdentityMismatch();
                    throw new InvalidOperationException("Focus provider identity differs.");
                }
                _recorder.Stage("capabilities");
                var capabilities = element.Inspect();
                if (capabilities == null) { throw new InvalidOperationException("Focus capabilities unavailable."); }
                _recorder.Capabilities(capabilities);
                if (element.ProcessId != _processId || element.Window != window)
                {
                    _recorder.IdentityMismatch();
                    throw new InvalidOperationException("Focus provider identity changed during inspection.");
                }
                if (!capabilities.Enabled || capabilities.Offscreen)
                {
                    _recorder.NotActivatable();
                    throw new InvalidOperationException("Owned UI element is disabled or offscreen.");
                }
                if (capabilities.WindowPattern && capabilities.WindowReady && capabilities.VisualState == 0)
                {
                    _recorder.Method("uia_window_restore");
                    _recorder.Stage("window_restore");
                    element.Restore();
                }
                else if (capabilities.KeyboardFocusable)
                {
                    _recorder.Method("uia_set_focus");
                    _recorder.Stage("set_focus");
                    element.SetFocus();
                }
                else
                {
                    _recorder.NotActivatable();
                    throw new InvalidOperationException("Owned UI element has no supported activation method.");
                }
            }, _waitMilliseconds, out error);
            }
            catch (Exception infrastructureError) { _recorder.Failure(infrastructureError); throw; }
            if (!completed)
            {
                _recorder.TimedOut();
                throw new InvalidOperationException("Focus provider timed out; do not retry. The owned game must be cleaned up.");
            }
            if (error != null)
            {
                _recorder.Failure(error);
                throw new InvalidOperationException("Owned focus provider failed.", error);
            }
            _recorder.ProviderReturned();
        }

        public FocusObservation ReadFocusObservation() { return _recorder.Seal(); }
    }

    internal sealed class NativeFocusPlatform : IFocusPlatform
    {
        public bool TryForeground(IntPtr window) { return NativeMethods.SetForegroundWindow(window); }
        public IFocusElement Find(IntPtr window)
        {
            var element = System.Windows.Automation.AutomationElement.FromHandle(window);
            return element == null ? null : new NativeFocusElement(element);
        }
    }

    internal sealed class NativeFocusElement : IFocusElement
    {
        private readonly System.Windows.Automation.AutomationElement _element;
        private System.Windows.Automation.WindowPattern _windowPattern;
        internal NativeFocusElement(System.Windows.Automation.AutomationElement element) { _element = element; }
        public int ProcessId { get { return _element.Current.ProcessId; } }
        public IntPtr Window { get { return new IntPtr(_element.Current.NativeWindowHandle); } }
        public void SetFocus() { _element.SetFocus(); }
        public FocusCapabilities Inspect()
        {
            var current = _element.Current;
            object pattern;
            var available = _element.TryGetCurrentPattern(System.Windows.Automation.WindowPattern.Pattern, out pattern);
            _windowPattern = available ? pattern as System.Windows.Automation.WindowPattern : null;
            return new FocusCapabilities
            {
                Enabled = current.IsEnabled,
                KeyboardFocusable = current.IsKeyboardFocusable,
                Offscreen = current.IsOffscreen,
                WindowPattern = _windowPattern != null,
                WindowReady = _windowPattern != null && _windowPattern.Current.WindowInteractionState == System.Windows.Automation.WindowInteractionState.ReadyForUserInteraction,
                VisualState = _windowPattern == null ? (int?)null : (int)_windowPattern.Current.WindowVisualState
            };
        }
        public void Restore()
        {
            if (_windowPattern == null) { throw new InvalidOperationException("Owned window pattern is unavailable."); }
            _windowPattern.SetWindowVisualState(System.Windows.Automation.WindowVisualState.Normal);
        }
    }

    internal static class BoundedFocus
    {
        internal static bool Invoke(Action action, int milliseconds)
        {
            Exception failure;
            var completed = Observe(action, milliseconds, out failure);
            if (failure != null) { throw new InvalidOperationException("Owned focus provider failed.", failure); }
            return completed;
        }

        internal static bool Observe(Action action, int milliseconds, out Exception failure)
        {
            if (action == null) { throw new ArgumentNullException("action"); }
            if (milliseconds < 1 || milliseconds > 2000) { throw new ArgumentOutOfRangeException("milliseconds"); }
            Exception captured = null;
            var thread = new Thread(delegate ()
            {
                try { action(); }
                catch (Exception error) { captured = error; } // Transfer failure; never let a provider crash the worker process.
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!thread.Join(milliseconds)) { failure = null; return false; }
            failure = captured; // Read only after Join; a timeout never reads a concurrently written exception.
            return true;
        }
    }
}
