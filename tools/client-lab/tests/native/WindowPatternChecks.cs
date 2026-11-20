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
            Require(platform.ForegroundCalls == 1 && platform.Lookups == 1 && element.Calls <= 1, "Activation repeated an operation.");
            return port.ReadFocusObservation();
        }
        internal static void Run()
        {
            var normal = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123), Capabilities = new FocusCapabilities { Enabled = true, WindowPattern = true, WindowReady = true, VisualState = 0 } };
            var result = Attempt(normal, expected: false);
            Require(normal.Calls == 0 && string.Equals(result.Method, "none", StringComparison.Ordinal) && string.Equals(result.Outcome, "provider_not_activatable", StringComparison.Ordinal) && !result.FailureHResult.HasValue, "A ready, non-focusable window must be observed without mutating it or reporting an exception.");
            Require(result.Enabled == true && result.Offscreen == false && result.WindowReady == true && result.WindowVisualState == 0, "Own-window capabilities were not retained in the diagnostic.");
            normal.Capabilities.KeyboardFocusable = true;
            result = Attempt(normal, expected: true);
            Require(normal.Calls == 1 && string.Equals(result.Method, "uia_set_focus", StringComparison.Ordinal), "A ready, focusable window must use SetFocus.");
            foreach (var caps in new[] {
                new FocusCapabilities { Enabled = false, KeyboardFocusable = true, WindowPattern = true, WindowReady = true, VisualState = 0 },
                new FocusCapabilities { Enabled = true, KeyboardFocusable = true, Offscreen = true, WindowPattern = true, WindowReady = true, VisualState = 0 },
                new FocusCapabilities { Enabled = true },
                new FocusCapabilities { Enabled = true, WindowPattern = true, WindowReady = false, VisualState = 0 },
                new FocusCapabilities { Enabled = true, WindowPattern = true, WindowReady = true, VisualState = 1 },
            })
            {
                var item = new FakeFocusElement { ProcessId = 456, Window = new IntPtr(123), Capabilities = caps };
                result = Attempt(item, expected: false);
                Require(item.Calls == 0 && string.Equals(result.Outcome, "provider_not_activatable", StringComparison.Ordinal), "Unsupported provider received an action.");
                Require(result.FailureHResult.HasValue == (!caps.Enabled || caps.Offscreen), "Unsafe provider state must fail before the foreground observation window.");
            }
            Console.WriteLine("Window pattern policy checks passed: " + _checks.ToString(CultureInfo.InvariantCulture) + ". No native window operations.");
        }
    }
}
