using System;
using System.Globalization;
using GsoLab.Interaction;

namespace GsoLab.Checks
{
    internal static class WindowPatternChecks
    {
        private static int _checks;
        private static void Require(bool value, string reason) { _checks++; if (!value) { throw new InvalidOperationException(reason); } }
        private static FocusObservation Attempt(FakeFocusElement element, bool expected)
        {
            var platform = new FakeFocusPlatform { Lookup = () => element };
            var port = new NativeFocusPort(456, platform, 1000);
            var success = false;
            try { success = OwnedFocus.TryFocus(port, new IntPtr(123)); } catch (InvalidOperationException) { }
            Require(success == expected, "Activation policy outcome differs.");
            Require(platform.ForegroundCalls == 1 && platform.Lookups == 1 && element.Calls + element.Restores <= 1, "Activation repeated an operation.");
            return port.ReadFocusObservation();
        }
        internal static void Run()
        {
            var restore = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123), Capabilities = new FocusCapabilities { Enabled = true, WindowPattern = true, WindowReady = true, VisualState = 0 } };
            var result = Attempt(restore, expected: true);
            Require(restore.Restores == 1 && restore.Calls == 0 && string.Equals(result.Method, "uia_window_restore", StringComparison.Ordinal) && result.WindowPatternAvailable == true && result.KeyboardFocusable == false, "Window pattern was not selected for a non-focusable owned window.");
            Require(result.Enabled == true && result.Offscreen == false && result.WindowReady == true && result.WindowVisualState == 0, "Own-window capabilities were not retained in the diagnostic.");
            var focus = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123) };
            result = Attempt(focus, expected: true);
            Require(focus.Calls == 1 && focus.Restores == 0 && string.Equals(result.Method, "uia_set_focus", StringComparison.Ordinal), "Focusable element lost its supported method.");
            foreach (var caps in new[] {
                new FocusCapabilities { Enabled = false, WindowPattern = true, WindowReady = true, VisualState = 0 },
                new FocusCapabilities { Enabled = true, Offscreen = true, WindowPattern = true, WindowReady = true, VisualState = 0 },
                new FocusCapabilities { Enabled = true },
                new FocusCapabilities { Enabled = true, WindowPattern = true, WindowReady = false, VisualState = 0 },
                new FocusCapabilities { Enabled = true, WindowPattern = true, WindowReady = true, VisualState = 1 }
            })
            {
                var item = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123), Capabilities = caps };
                result = Attempt(item, expected: false);
                Require(item.Calls == 0 && item.Restores == 0 && string.Equals(result.Outcome, "provider_not_activatable", StringComparison.Ordinal), "Unsupported provider received an action.");
            }
            var error = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123), Capabilities = new FocusCapabilities { Enabled = true, WindowPattern = true, WindowReady = true, VisualState = 0 }, Focus = delegate { throw new InvalidOperationException("private-sentinel"); } };
            result = Attempt(error, expected: false);
            Require(error.Restores == 1 && error.Calls == 0 && string.Equals(result.Stage, "window_restore", StringComparison.Ordinal) && string.Equals(result.FailureCategory, "invalid_operation", StringComparison.Ordinal), "Restore error was hidden or retried as SetFocus.");
            Console.WriteLine("Window pattern policy checks passed: " + _checks.ToString(CultureInfo.InvariantCulture) + ". No native window operations.");
        }
    }
}
