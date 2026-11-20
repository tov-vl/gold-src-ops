using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using GsoLab.Interaction;

namespace GsoLab.Checks
{
    internal sealed class Message
    {
        internal uint Id;
        internal long Word;
        internal long Data;
    }

    internal sealed class FakeWindowPort : IWindowPort, IFocusDiagnosticsPort
    {
        internal readonly List<Message> Messages = new List<Message>();
        internal readonly List<int> Delays = new List<int>();
        internal readonly WindowState State = Ready();
        internal int Reads;
        internal int FailAt = -1;
        internal int? NativeReadError;
        internal bool NativePostFailure;
        internal int Activations;
        internal bool ActivationAllowed = true;
        internal bool ActivationChangesFocus = true;
        internal Action<FakeWindowPort> BeforeRead;
        internal FocusObservation FocusTrace;
        internal Exception ActivationFailure;

        public FocusObservation ReadFocusObservation() { return FocusTrace; }

        private static WindowState Ready()
        {
            return new WindowState
            {
                Handle = new IntPtr(123),
                IdentityMatches = true,
                Visible = true,
                Foreground = true,
                DesktopUnlocked = true,
                SessionActive = true,
                PhysicalGeometry = true,
                WindowCount = 1,
                Width = 1920,
                Height = 1200
            };
        }

        public WindowState Read()
        {
            Reads++;
            if (NativeReadError.HasValue) { throw new Win32Exception(NativeReadError.Value); }
            if (BeforeRead != null) { BeforeRead(this); }
            return State;
        }

        public void Post(IntPtr window, uint message, IntPtr word, IntPtr data)
        {
            if (window != new IntPtr(123)) { throw new InvalidOperationException("Unexpected target."); }
            if (Messages.Count == FailAt)
            {
                if (NativePostFailure) { throw new Win32Exception(5); }
                throw new InvalidOperationException("Queue rejected input.");
            }
            Messages.Add(new Message { Id = message, Word = word.ToInt64(), Data = data.ToInt64() });
        }

        public void Delay(int milliseconds) { Delays.Add(milliseconds); }

        public bool Activate(IntPtr window)
        {
            if (window != new IntPtr(123)) { throw new InvalidOperationException("Activation target differs."); }
            Activations++;
            if (ActivationFailure != null) { throw ActivationFailure; }
            if (ActivationAllowed && ActivationChangesFocus) { State.Foreground = true; }
            return ActivationAllowed;
        }
    }

    internal sealed class FakeDpiContextPort : IDpiContextPort
    {
        internal readonly List<IntPtr> Requested = new List<IntPtr>();
        internal IntPtr Current = new IntPtr(-2);
        internal int FailAt = -1;
        public int LastError { get { return 5; } }
        public IntPtr Set(IntPtr context)
        {
            Requested.Add(context);
            if (Requested.Count - 1 == FailAt) { return IntPtr.Zero; }
            var previous = Current;
            Current = context;
            return previous;
        }
    }

    internal sealed class FakeFrameCapturePort : IFrameCapturePort
    {
        internal int Calls;
        internal byte[] Bytes = new byte[64];
        internal Action BeforeCapture;
        internal bool Fails;

        public byte[] Capture(IntPtr window, int width, int height)
        {
            if (window != new IntPtr(123) || width != 1920 || height != 1200) { throw new InvalidOperationException("Unexpected frame target."); }
            Calls++;
            if (BeforeCapture != null) { BeforeCapture(); }
            if (Fails) { throw new Win32Exception(5); }
            return Bytes;
        }
    }

    internal static class BridgeTests
    {
        private static int _checks;
        private static void Require(bool value, string name)
        {
            _checks += value ? 1 : 0;
            if (!value) { throw new InvalidOperationException(name); }
        }

        private static void Refused(Action action, string name)
        {
            var refused = false;
            try { action(); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, name);
        }

        private static void Guard(Action<WindowState> mutate, string code)
        {
            var port = new FakeWindowPort();
            mutate(port.State);
            var window = new GameWindow(port);
            Refused(window.ClickOk, "Guard must reject unsafe state.");
            Require(port.Messages.Count == 0, "Guard must prevent all input.");
            Require(string.Equals(window.GuardCode, code, StringComparison.Ordinal), "Precise refusal code.");
            Require(window.GuardObservationComplete && window.ClientWidth == port.State.Width && window.ClientHeight == port.State.Height && window.VisibleWindowCount == port.State.WindowCount, "Bounded observation matches the rejected state.");
        }

        private static void CheckDesktopForeground(string executable, string code, string ownerCategory, bool stable = true,
            bool shellDesktop = false, string explorerRole = null, bool idleTaskbar = false)
        {
            var observed = DesktopLaunchAdmission.ClassifyForeground(stable, shellDesktop, executable, @"C:\FixtureSteam\steam.exe",
                explorerRole, idleTaskbar);
            Require(string.Equals(observed.Code, code, StringComparison.Ordinal) &&
                string.Equals(observed.OwnerCategory, ownerCategory, StringComparison.Ordinal),
                "Launch verdict and foreground category must match the same observation: " + ownerCategory);
        }

        private static void CheckTaskbarAdmission()
        {
            var taskbar = new IntPtr(1);
            var other = new IntPtr(2);
            var quiet = new NativeMethods.GuiThreadInfo { Active = taskbar };
            Require(DesktopLaunchAdmission.IsTaskbarThreadQuiet(taskbar, quiet), "An active taskbar without GUI activity can be idle.");
            CheckTaskbarBusy(taskbar,
                new NativeMethods.GuiThreadInfo { Active = other },
                new NativeMethods.GuiThreadInfo { Active = taskbar, Flags = 2 },
                new NativeMethods.GuiThreadInfo { Active = taskbar, Flags = 4 },
                new NativeMethods.GuiThreadInfo { Active = taskbar, Flags = 8 },
                new NativeMethods.GuiThreadInfo { Active = taskbar, Flags = 16 },
                new NativeMethods.GuiThreadInfo { Active = taskbar, Capture = other },
                new NativeMethods.GuiThreadInfo { Active = taskbar, MenuOwner = other },
                new NativeMethods.GuiThreadInfo { Active = taskbar, MoveSize = other },
                new NativeMethods.GuiThreadInfo { Active = taskbar, Caret = other });

            Require(DesktopLaunchAdmission.HasTaskbarInputIdle(100, 100, 30100), "Thirty seconds is sufficient.");
            Require(!DesktopLaunchAdmission.HasTaskbarInputIdle(100, 100, 30099), "Recent input blocks launch.");
            Require(!DesktopLaunchAdmission.HasTaskbarInputIdle(100, 200, 50000), "Input during observation invalidates it.");
            Require(!DesktopLaunchAdmission.HasTaskbarInputIdle(101, 101, 100), "A future tick is not idle evidence.");
            Require(DesktopLaunchAdmission.HasTaskbarInputIdle(uint.MaxValue - 999, uint.MaxValue - 999, 29000),
                "Forward idle duration survives wraparound.");

            const string explorer = @"C:\FixtureWindows\explorer.exe";
            CheckDesktopForeground(explorer, "ready", "idle_taskbar", explorerRole: "explorer_taskbar", idleTaskbar: true);
            CheckDesktopForeground(explorer, "desktop_busy", "explorer_taskbar", explorerRole: "explorer_taskbar");
            CheckDesktopForeground(explorer, "desktop_busy", "explorer_folder", explorerRole: "explorer_folder", idleTaskbar: true);
            CheckDesktopForeground(explorer, "desktop_busy", "explorer_desktop_host", explorerRole: "explorer_desktop_host", idleTaskbar: true);
            CheckDesktopForeground(@"C:\Other\app.exe", "desktop_busy", "other_application", explorerRole: "explorer_taskbar", idleTaskbar: true);
            CheckDesktopForeground(explorer, "desktop_foreground_unverified", "unverified", stable: false,
                explorerRole: "explorer_taskbar", idleTaskbar: true);
        }

        private static void CheckTaskbarBusy(IntPtr taskbar, params NativeMethods.GuiThreadInfo[] observations)
        {
            foreach (var gui in observations)
            {
                Require(!DesktopLaunchAdmission.IsTaskbarThreadQuiet(taskbar, gui), "The taskbar must remain blocked during GUI activity.");
            }
        }

        private static void Main()
        {
            var port = new FakeWindowPort();
            var window = new GameWindow(port);
            window.ClickOk();
            Require(port.Messages.Count == 3 && port.Messages[0].Id == 0x200 && port.Messages[1].Id == 0x201 && port.Messages[2].Id == 0x202, "Fixed click sequence.");
            foreach (var message in port.Messages) { Require(message.Data == (516 | (936 << 16)), "Fixed client coordinates."); }
            Require(port.Messages[1].Word == 1 && port.Messages[2].Word == 0 && port.Reads == 3, "Mouse state and per-message guard.");

            port = new FakeWindowPort(); window = new GameWindow(port); window.MoveControls();
            Require(port.Messages.Count == 4 && port.Messages[0].Word == 0x53 && port.Messages[2].Word == 0x41, "Only S and A movement.");
            Require(port.Delays.Count == 2 && port.Delays[0] == 250 && port.Delays[1] == 250, "Bounded movement duration.");
            Require(port.Messages[0].Id == 0x100 && port.Messages[1].Id == 0x101 && (port.Messages[1].Data & 0xc0000000L) == 0xc0000000L, "Key release flags.");

            port = new FakeWindowPort(); window = new GameWindow(port); window.ReopenRules();
            Require(port.Messages.Count == 10 && port.Messages[0].Word == 0x55 && port.Messages[8].Word == 13, "Fixed team-chat alias route.");
            var text = new string(port.Messages.Where(message => message.Id == 0x102).Select(message => (char)message.Word).ToArray());
            Require(string.Equals(text, "/rules", StringComparison.Ordinal), "No arbitrary text.");
            foreach (var panel in new[] { "ranks", "profile", "topall" })
            {
                port = new FakeWindowPort(); window = new GameWindow(port); window.ReopenPanel(panel);
                text = new string(port.Messages.Where(message => message.Id == 0x102).Select(message => (char)message.Word).ToArray());
                Require(string.Equals(text, "/" + panel, StringComparison.Ordinal), "Only the selected fixed alias.");
            }
            foreach (var panel in new[] { "", "RANKS", "guns", "rules;quit", "profile\nquit", null })
            {
                port = new FakeWindowPort(); window = new GameWindow(port);
                var refused = false;
                try { window.ReopenPanel(panel); }
                catch (ArgumentException) { refused = true; }
                Require(refused && port.Messages.Count == 0, "Unknown aliases must fail before any input.");
            }

            port = new FakeWindowPort(); window = new GameWindow(port); window.Snapshot();
            Require(port.Messages.Count == 2 && port.Messages[0].Word == 0x74 && port.Messages[1].Id == 0x101, "Only native F5 capture.");

            Guard(delegate (WindowState state) { state.IdentityMatches = false; }, "process_identity_changed");
            Guard(delegate (WindowState state) { state.Visible = false; }, "window_not_visible");
            Guard(delegate (WindowState state) { state.Minimized = true; }, "window_minimized");
            Guard(delegate (WindowState state) { state.Foreground = false; }, "window_not_foreground");
            Guard(delegate (WindowState state) { state.DesktopUnlocked = false; }, "desktop_not_default");
            Guard(delegate (WindowState state) { state.SessionActive = false; }, "session_not_active");
            Guard(delegate (WindowState state) { state.WindowCount = 2; }, "window_count_mismatch");
            Guard(delegate (WindowState state) { state.WindowCount = 0; }, "window_count_mismatch");
            Guard(delegate (WindowState state) { state.Width = 1280; }, "client_size_mismatch");
            Guard(delegate (WindowState state) { state.Height = 800; }, "client_size_mismatch");
            Guard(delegate (WindowState state) { state.PhysicalGeometry = false; }, "physical_geometry_unverified");
            Guard(delegate (WindowState state) { state.Handle = IntPtr.Zero; }, "invalid_window_handle");
            Guard(delegate (WindowState state) { state.Handle = new IntPtr(-1); }, "invalid_window_handle");
            Guard(delegate (WindowState state) { state.Handle = new IntPtr(0xffff); }, "invalid_window_handle");
            port = new FakeWindowPort(); window = new GameWindow(port); window.CheckReady(); port.State.Handle = new IntPtr(124);
            Refused(window.Snapshot, "Changed HWND."); Require(port.Messages.Count == 0, "Changed window gets no input.");

            port = new FakeWindowPort(); window = new GameWindow(port);
            port.BeforeRead = delegate (FakeWindowPort value) { if (value.Reads == 2) { value.State.Foreground = false; } };
            Refused(window.ClickOk, "Focus changed before mouse-down.");
            Require(port.Messages.Count == 1 && port.Messages[0].Id == 0x200, "No click after losing focus.");
            port = new FakeWindowPort { FailAt = 0 }; window = new GameWindow(port);
            Refused(window.Snapshot, "Failed enqueue."); Require(port.Reads == 1 && port.Messages.Count == 0, "No automatic retry.");
            port = new FakeWindowPort(); window = new GameWindow(port);
            port.BeforeRead = delegate (FakeWindowPort value) { if (value.Reads == 2) { value.State.IdentityMatches = false; } };
            Refused(window.MoveControls, "Identity changed during action.");
            Require(port.Messages.Count == 1, "No further input after process identity changes.");
            port = new FakeWindowPort { NativeReadError = 5 }; window = new GameWindow(port);
            var nativeFailure = false;
            try { window.CheckReady(); } catch (Win32Exception) { nativeFailure = true; }
            Require(nativeFailure && window.NativeErrorCode == 5 && !window.GuardObservationComplete && window.GuardFlags == 0 && port.Messages.Count == 0, "Native observation error is reported without inventing state.");
            port = new FakeWindowPort { FailAt = 0, NativePostFailure = true }; window = new GameWindow(port);
            nativeFailure = false;
            try { window.Snapshot(); } catch (Win32Exception) { nativeFailure = true; }
            Require(nativeFailure && window.NativeErrorCode == 5 && window.GuardObservationComplete && string.Equals(window.GuardCode, "input_delivery_uncertain", StringComparison.Ordinal) && port.Reads == 1, "Native queue failure is reported without retry.");

            CheckDesktopForeground(@"C:\FixtureWindows\WindowsTerminal.exe", "desktop_busy", "windows_terminal");
            CheckDesktopForeground(@"C:\FixtureWindows\explorer.exe", "desktop_busy", "explorer_process");
            var desktopSurface = DesktopLaunchAdmission.ClassifyExplorerRole("WorkerW", shellOwned: true, shellRoot: false, desktopView: true);
            Require(string.Equals(desktopSurface, "explorer_desktop_surface", StringComparison.Ordinal), "The confirmed desktop view has its own diagnostic category.");
            CheckDesktopForeground(@"C:\FixtureWindows\explorer.exe", "desktop_busy", "explorer_desktop_surface", explorerRole: desktopSurface);
            CheckDesktopForeground(@"C:\FixtureWindows\explorer.exe", "desktop_foreground_unverified", "unverified", stable: false, explorerRole: desktopSurface);
            Require(string.Equals(DesktopLaunchAdmission.ClassifyExplorerRole("Progman", shellOwned: true, shellRoot: true, desktopView: false),
                "explorer_desktop_surface", StringComparison.Ordinal), "A descendant of the shell desktop is diagnosed as a desktop surface.");
            Require(string.Equals(DesktopLaunchAdmission.ClassifyExplorerRole("WorkerW", shellOwned: true, shellRoot: false, desktopView: false),
                "explorer_desktop_host", StringComparison.Ordinal), "A host without a desktop view has a distinct category.");
            Require(string.Equals(DesktopLaunchAdmission.ClassifyExplorerRole("WorkerW", shellOwned: false, shellRoot: false, desktopView: true),
                "explorer_process", StringComparison.Ordinal), "Window class alone must not prove desktop ownership.");
            Require(string.Equals(DesktopLaunchAdmission.ClassifyExplorerRole("Shell_SecondaryTrayWnd", shellOwned: true, shellRoot: false, desktopView: false),
                "explorer_taskbar", StringComparison.Ordinal), "The taskbar has its own diagnostic category.");
            Require(string.Equals(DesktopLaunchAdmission.ClassifyExplorerRole("CabinetWClass", shellOwned: false, shellRoot: false, desktopView: true),
                "explorer_folder", StringComparison.Ordinal), "An Explorer folder stays distinct even with a shell view.");
            CheckDesktopForeground(@"C:\Other\steam.exe", "desktop_busy", "other_steam");
            CheckDesktopForeground(@"C:\FixtureSteam\bin\SteamWebHelper.exe", "desktop_busy", "steam_web_helper");
            CheckDesktopForeground(@"C:\FixtureSteamCopy\bin\steamwebhelper.exe", "desktop_busy", "other_application");
            CheckDesktopForeground(@"C:\Other\editor.exe", "desktop_busy", "other_application");
            CheckDesktopForeground(@"c:\fixturesteam\Steam.exe", "ready", "configured_steam");
            CheckDesktopForeground(executable: null, code: "ready", ownerCategory: "shell_desktop", shellDesktop: true);
            CheckDesktopForeground(executable: null, code: "desktop_foreground_unverified", ownerCategory: "unverified", stable: false, shellDesktop: true);
            CheckDesktopForeground(executable: null, code: "desktop_foreground_unverified", ownerCategory: "unverified");
            CheckTaskbarAdmission();
            const string steamExecutable = @"C:\FixtureSteam\steam.exe";
            var inactive = DesktopLaunchAdmission.Observe(0, steamExecutable);
            Require(string.Equals(inactive.Code, "desktop_session_inactive", StringComparison.Ordinal) &&
                string.Equals(inactive.OwnerCategory, "not_observed", StringComparison.Ordinal) &&
                string.Equals(DesktopLaunchAdmission.Check(0, steamExecutable), inactive.Code, StringComparison.Ordinal),
                "An inactive session must not report any foreground owner, and the legacy verdict must match.");

            port = new FakeWindowPort { State = { Foreground = false } }; window = new GameWindow(port);
            window.PrepareOwnWindow();
            Require(port.Activations == 1 && window.ActivationAttempted && window.ActivationAccepted == true && port.Messages.Count == 0, "One owned-window activation, no input.");
            Refused(window.PrepareOwnWindow, "Preparation cannot be repeated.");
            Require(port.Activations == 1, "No activation retry.");
            port.State.Foreground = false;
            Refused(window.ClickOk, "Focus loss after preparation stops input.");
            Require(port.Activations == 1 && port.Messages.Count == 0, "Do not activate again after later focus loss.");

            port = new FakeWindowPort { ActivationAllowed = false, State = { Foreground = false } }; window = new GameWindow(port);
            Refused(window.PrepareOwnWindow, "Windows activation refusal.");
            Require(string.Equals(window.GuardCode, "window_activation_denied", StringComparison.Ordinal) && window.ActivationAccepted == false && port.Activations == 1 && port.Messages.Count == 0, "Refusal is preserved without fallback.");
            Require(port.Delays.Count == 5 && port.Delays.TrueForAll(delay => delay == 200), "Foreground observation must have a fixed wait budget.");
            port = new FakeWindowPort { ActivationAllowed = false, State = { Foreground = false } }; window = new GameWindow(port);
            port.BeforeRead = candidate => { candidate.State.Foreground = candidate.Delays.Count >= 3; };
            window.PrepareOwnWindow();
            Require(window.ActivationAccepted == false && port.Activations == 1 && port.Delays.Count == 3 && port.Messages.Count == 0, "A late foreground change may satisfy the guard without a retry or input.");
            window.Snapshot();
            Require(port.Messages.Count == 2, "The confirmed window can receive the expected input.");
            port = new FakeWindowPort { ActivationAllowed = false, State = { Foreground = false } }; window = new GameWindow(port);
            port.BeforeRead = candidate =>
            {
                if (candidate.Delays.Count != 2) { return; }
                candidate.State.Handle = new IntPtr(124);
                candidate.State.Foreground = true;
            };
            Refused(window.PrepareOwnWindow, "A different foreground HWND must fail during observation.");
            Refused(window.Snapshot, "A different foreground HWND must also fail the input guard.");
            Require(string.Equals(window.GuardCode, "window_identity_changed", StringComparison.Ordinal) && port.Activations == 1 && port.Messages.Count == 0, "Foreground wait must keep the pinned HWND.");
            port = new FakeWindowPort { ActivationChangesFocus = false, State = { Foreground = false } }; window = new GameWindow(port);
            Refused(window.PrepareOwnWindow, "Recheck actual foreground after accepted activation.");
            Require(window.ActivationAccepted == true && string.Equals(window.GuardCode, "window_not_foreground", StringComparison.Ordinal) && port.Messages.Count == 0, "API acceptance does not permit background input.");
            port = new FakeWindowPort(); window = new GameWindow(port); window.PrepareOwnWindow();
            Require(!window.ActivationAttempted && port.Activations == 0 && port.Messages.Count == 0, "Already-active window needs no activation.");
            foreach (var mutation in new Action<WindowState>[]
            {
            delegate(WindowState state) { state.IdentityMatches = false; },
            delegate(WindowState state) { state.DesktopUnlocked = false; },
            delegate(WindowState state) { state.SessionActive = false; },
            delegate(WindowState state) { state.Minimized = true; },
            delegate(WindowState state) { state.Width = 1536; },
            delegate(WindowState state) { state.PhysicalGeometry = false; },
            delegate(WindowState state) { state.WindowCount = 2; }
            })
            {
                port = new FakeWindowPort { State = { Foreground = false } }; mutation(port.State); window = new GameWindow(port);
                Refused(window.PrepareOwnWindow, "Unsafe state prevents activation.");
                Require(port.Activations == 0 && port.Messages.Count == 0, "All other guards apply before activation.");
            }

            var dpi = new FakeDpiContextPort();
            var scope = new PhysicalDpiScope(dpi);
            Require(dpi.Current == new IntPtr(-4) && dpi.Requested.Count == 1, "Physical context entered on the test port.");
            scope.Dispose(); scope.Dispose();
            Require(dpi.Current == new IntPtr(-2) && dpi.Requested.Count == 2, "Previous context restored exactly once.");
            var exceptionalDpi = new FakeDpiContextPort();
            Refused(delegate { using (new PhysicalDpiScope(exceptionalDpi)) { throw new InvalidOperationException("Body failed."); } }, "Body failure.");
            Require(exceptionalDpi.Current == new IntPtr(-2) && exceptionalDpi.Requested.Count == 2, "Exception path restores DPI context.");
            dpi = new FakeDpiContextPort { FailAt = 0 };
            nativeFailure = false;
            try { using (new PhysicalDpiScope(dpi)) { } } catch (Win32Exception) { nativeFailure = true; }
            Require(nativeFailure && dpi.Requested.Count == 1 && dpi.Current == new IntPtr(-2), "Entry failure leaves original context and blocks action.");
            dpi = new FakeDpiContextPort { FailAt = 1 }; scope = new PhysicalDpiScope(dpi);
            nativeFailure = false;
            try { scope.Dispose(); } catch (Win32Exception) { nativeFailure = true; }
            scope.Dispose();
            Require(nativeFailure && dpi.Requested.Count == 2, "Failed restoration is reported and not retried.");
            port = new FakeWindowPort();
            var frames = new FakeFrameCapturePort();
            window = new GameWindow(port, frames);
            Require(ReferenceEquals(window.CaptureFrame(), frames.Bytes) && frames.Calls == 1 && port.Reads == 2 && port.Messages.Count == 0, "Capture checks the same owned window before and after, with no input.");
            foreach (var mutation in new Action<WindowState>[]
            {
            delegate(WindowState state) { state.Foreground = false; },
            delegate(WindowState state) { state.IdentityMatches = false; },
            delegate(WindowState state) { state.DesktopUnlocked = false; },
            delegate(WindowState state) { state.SessionActive = false; },
            delegate(WindowState state) { state.Minimized = true; },
            delegate(WindowState state) { state.Width = 1536; },
            delegate(WindowState state) { state.PhysicalGeometry = false; },
            delegate(WindowState state) { state.WindowCount = 2; }
            })
            {
                port = new FakeWindowPort(); mutation(port.State); frames = new FakeFrameCapturePort(); window = new GameWindow(port, frames);
                var guardedWindow = window;
                Refused(delegate { guardedWindow.CaptureFrame(); }, "Unsafe window cannot be captured.");
                Require(frames.Calls == 0 && port.Messages.Count == 0, "Rejected capture does not access pixels or send input.");
            }
            port = new FakeWindowPort(); frames = new FakeFrameCapturePort();
            var changedPort = port;
            frames.BeforeCapture = delegate { changedPort.State.Handle = new IntPtr(124); };
            window = new GameWindow(port, frames);
            var changedWindow = window;
            Refused(delegate { changedWindow.CaptureFrame(); }, "Discard a frame after HWND changes.");
            Require(frames.Calls == 1 && string.Equals(window.GuardCode, "window_identity_changed", StringComparison.Ordinal), "Changed target has no capture retry.");
            port = new FakeWindowPort(); frames = new FakeFrameCapturePort { Fails = true }; window = new GameWindow(port, frames);
            nativeFailure = false;
            try { window.CaptureFrame(); } catch (Win32Exception) { nativeFailure = true; }
            Require(nativeFailure && frames.Calls == 1 && port.Messages.Count == 0 && window.NativeErrorCode == 5 && string.Equals(window.GuardCode, "owned_frame_capture_failed", StringComparison.Ordinal), "Capture failure is bounded and does not fall back to the desktop.");
            port = new FakeWindowPort(); frames = new FakeFrameCapturePort { Bytes = Array.Empty<byte>() }; window = new GameWindow(port, frames);
            Refused(delegate { window.CaptureFrame(); }, "Reject empty capture.");
            Refused(delegate { new NativeFrameCapturePort().Capture(IntPtr.Zero, 1920, 1200); }, "Native port rejects the desktop DC selector before any native call.");
            AutonomousChecks.Run();
            FocusDiagnosticsChecks.Run();
            WindowPatternChecks.Run();
            GunsChecks.Run();
            Console.WriteLine("Bridge mock checks passed: " + _checks.ToString(CultureInfo.InvariantCulture) + ". No native input or capture was sent.");
        }
    }
}
