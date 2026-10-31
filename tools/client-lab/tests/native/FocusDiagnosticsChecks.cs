using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using GsoLab.Interaction;

namespace GsoLab.Checks
{
    internal sealed class FakeFocusElement : IFocusElement
    {
        public int ProcessId { get; set; }
        public IntPtr Window { get; set; }
        internal Action Focus;
        internal int Calls;
        internal int Restores;
        internal FocusCapabilities Capabilities = new FocusCapabilities { Enabled = true, KeyboardFocusable = true };
        public FocusCapabilities Inspect() { return Capabilities; }
        public void Restore() { Restores++; if (Focus != null) { Focus(); } }
        public void SetFocus() { Calls++; if (Focus != null) { Focus(); } }
    }

    internal sealed class FakeFocusPlatform : IFocusPlatform
    {
        internal bool Foreground;
        internal Exception ForegroundError;
        internal Func<IFocusElement> Lookup;
        internal int ForegroundCalls;
        internal int Lookups;
        public bool TryForeground(IntPtr window)
        {
            if (window != new IntPtr(123)) { throw new InvalidOperationException("Wrong test HWND."); }
            ForegroundCalls++;
            if (ForegroundError != null) { throw ForegroundError; }
            return Foreground;
        }
        public IFocusElement Find(IntPtr window) { Lookups++; return Lookup(); }
    }

    internal static class FocusDiagnosticsChecks
    {
        private static int _checks;
        private static void Require(bool value, string reason)
        { _checks++; if (!value) { throw new InvalidOperationException(reason); } }
        private static bool Is(string actual, string expected) { return string.Equals(actual, expected, StringComparison.Ordinal); }

        private static FocusObservation Attempt(FakeFocusPlatform platform, bool expectedSuccess)
        {
            var port = new NativeFocusPort(456, platform, 2000);
            var succeeded = false;
            try { succeeded = OwnedFocus.TryFocus(port, new IntPtr(123)); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            Require(succeeded == expectedSuccess, "Focus outcome changed.");
            Require(platform.ForegroundCalls == 1 && platform.Lookups <= 1, "Focus was retried.");
            var observation = port.ReadFocusObservation();
            var allowed = new[] { "Outcome", "Stage", "ForegroundAttempted", "ForegroundAccepted", "ProviderAttempted", "FailureCategory", "FailureHResult", "NativeFailureCode", "ElapsedMilliseconds", "Enabled", "KeyboardFocusable", "Offscreen", "WindowPatternAvailable", "WindowVisualState", "WindowReady", "Method" };
            Require(new HashSet<string>(allowed, StringComparer.Ordinal).SetEquals(typeof(FocusObservation).GetProperties().Select(p => p.Name)), "Unexpected diagnostic property.");
            Require(observation.ForegroundAttempted && observation.ElapsedMilliseconds >= 0, "Attempt timing was not recorded.");
            foreach (var property in typeof(FocusObservation).GetProperties())
            {
                var value = property.GetValue(observation, index: null) as string;
                Require(value == null || value.IndexOf("private-sentinel", StringComparison.Ordinal) < 0, "Exception content leaked.");
            }
            return observation;
        }

        internal static void Run()
        {
            var platform = new FakeFocusPlatform { Foreground = true };
            var value = Attempt(platform, expectedSuccess: true);
            Require(Is(value.Outcome, "foreground_accepted") && Is(value.Stage, "foreground") && value.ForegroundAccepted == true && !value.ProviderAttempted, "Primary activation was misclassified.");
            platform = new FakeFocusPlatform { ForegroundError = new Win32Exception(error: 5, message: "private-sentinel") };
            value = Attempt(platform, expectedSuccess: false);
            Require(Is(value.Outcome, "foreground_exception") && Is(value.FailureCategory, "win32") && value.NativeFailureCode == 5, "Native failure code was lost.");
            var acceptedElement = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123) };
            platform = new FakeFocusPlatform { Lookup = () => acceptedElement };
            value = Attempt(platform, expectedSuccess: true);
            Require(Is(value.Outcome, "provider_returned") && Is(value.Stage, "provider_completed") && acceptedElement.Calls == 1 && value.ForegroundAccepted == false, "Provider success is distinct from actual foreground.");
            foreach (var wrong in new IFocusElement[] { null, new FakeFocusElement { ProcessId = 999, Window = new IntPtr(123) }, new FakeFocusElement { ProcessId = 456, Window = new IntPtr(124) } })
            {
                var candidate = wrong;
                platform = new FakeFocusPlatform { Lookup = () => candidate };
                value = Attempt(platform, expectedSuccess: false);
                Require(Is(value.Outcome, "provider_identity_mismatch") && Is(value.Stage, "identity_check"), "Identity rejection was hidden.");
                Require(candidate == null || ((FakeFocusElement)candidate).Calls == 0, "Wrong element received focus.");
            }
            foreach (var failure in new Exception[] { new FileNotFoundException("private-sentinel"), new TypeLoadException("private-sentinel"), new InvalidOperationException("private-sentinel"), new UnauthorizedAccessException("private-sentinel") })
            {
                var error = failure;
                platform = new FakeFocusPlatform { Lookup = delegate { throw error; } };
                value = Attempt(platform, expectedSuccess: false);
                Require(Is(value.Outcome, "provider_exception") && Is(value.Stage, "element_lookup") && value.FailureHResult == error.HResult, "Lookup exception details were lost.");
                Require(!Is(value.FailureCategory, "none") && !Is(value.FailureCategory, "unexpected"), "Known framework error was not classified.");
            }
            var deniedElement = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123), Focus = delegate { throw new InvalidOperationException("private-sentinel"); } };
            platform = new FakeFocusPlatform { Lookup = () => deniedElement };
            value = Attempt(platform, expectedSuccess: false);
            Require(Is(value.Stage, "set_focus") && Is(value.Outcome, "provider_exception") && deniedElement.Calls == 1, "SetFocus failure was not distinguished.");

            using (var delayed = new DelayedFocusFixture())
            {
                platform = new FakeFocusPlatform { Lookup = delayed.Find };
                var port = new NativeFocusPort(456, platform, 100);
                var timedOut = false;
                try { OwnedFocus.TryFocus(port, new IntPtr(123)); } catch (InvalidOperationException) { timedOut = true; }
                value = port.ReadFocusObservation();
                Require(timedOut && Is(value.Outcome, "provider_timeout") && Is(value.Stage, "element_lookup") && !value.FailureHResult.HasValue, "Timeout was conflated with a provider exception.");
                delayed.Release();
                Require(delayed.WaitFinished(), "Inert late provider did not finish.");
                var after = port.ReadFocusObservation();
                Require(ReferenceEquals(value, after) && Is(after.Stage, "element_lookup") && Is(after.Outcome, "provider_timeout"), "Late provider changed the sealed diagnostic.");
                Require(platform.ForegroundCalls == 1 && platform.Lookups == 1 && delayed.Element.Calls == 1, "Timeout caused a retry.");
            }
            var trace = new FocusObservation { Outcome = "provider_exception", Stage = "set_focus", FailureCategory = "invalid_operation" };
            var windowPort = new FakeWindowPort { ActivationFailure = new InvalidOperationException("private-sentinel"), FocusTrace = trace, State = { Foreground = false } };
            var window = new GameWindow(windowPort);
            var rejected = false;
            try { window.PrepareOwnWindow(); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && ReferenceEquals(window.FocusAttempt, trace) && Is(window.GuardCode, "window_activation_unavailable") && windowPort.Messages.Count == 0, "GameWindow lost failure diagnostics or allowed input.");
            windowPort = new FakeWindowPort { ActivationChangesFocus = false, FocusTrace = trace, State = { Foreground = false } };
            window = new GameWindow(windowPort);
            rejected = false;
            try { window.PrepareOwnWindow(); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && Is(window.GuardCode, "window_not_foreground") && windowPort.Messages.Count == 0, "Provider return bypassed the foreground recheck.");
            Console.WriteLine("Focus diagnostics checks passed: " + _checks.ToString(CultureInfo.InvariantCulture) + ". No real window, provider, focus or input calls.");
        }
    }

    internal sealed class DelayedFocusFixture : IDisposable
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(initialState: false);
        private volatile bool _finished;
        internal readonly FakeFocusElement Element;
        internal DelayedFocusFixture()
        { Element = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123), Focus = Finish }; }
        private void Finish() { _finished = true; }
        private bool IsFinished() { return _finished; }
        internal IFocusElement Find() { _release.Wait(millisecondsTimeout: 5000); return Element; }
        internal void Release() { _release.Set(); }
        internal bool WaitFinished() { return SpinWait.SpinUntil(IsFinished, millisecondsTimeout: 2000); }
        public void Dispose()
        {
            Release();
            if (!WaitFinished()) { throw new InvalidOperationException("Inert provider did not release before fixture disposal."); }
            _release.Dispose();
        }
    }
}
