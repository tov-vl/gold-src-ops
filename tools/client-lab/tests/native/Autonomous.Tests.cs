using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using GsoLab.Interaction;

namespace GsoLab.Checks
{
    internal sealed class FakeFocusPort : IFocusPort
    {
        internal bool ForegroundAccepted;
        internal bool ProviderFails;
        internal int ForegroundCalls;
        internal int ProviderCalls;
        public bool TryForeground(IntPtr window)
        {
            if (window != new IntPtr(123)) { throw new InvalidOperationException("Wrong focus target."); }
            ForegroundCalls++;
            return ForegroundAccepted;
        }
        public void FocusOwnedElement(IntPtr window)
        {
            if (window != new IntPtr(123)) { throw new InvalidOperationException("Wrong provider target."); }
            ProviderCalls++;
            if (ProviderFails) { throw new InvalidOperationException("Provider denied focus."); }
        }
    }

    internal static class AutonomousChecks
    {
        private static int _checks;
        private static void Require(bool condition, string reason)
        {
            _checks++;
            if (!condition) { throw new InvalidOperationException(reason); }
        }
        private static void Refused(Action action)
        {
            var refused = false;
            try { action(); }
            catch (ArgumentException) { refused = true; }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "Expected a bounded rejection.");
        }
        internal static void Run()
        {
            byte[] original;
            using (var bitmap = new Bitmap(1920, 1200, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var output = new MemoryStream())
            {
                graphics.Clear(Color.Black);
                using (var background = new SolidBrush(Color.FromArgb(24, 33, 43)))
                { graphics.FillRectangle(background, 350, 290, 1200, 600); }
                graphics.FillRectangle(Brushes.Gold, 500, 925, 31, 22);
                bitmap.Save(output, ImageFormat.Bmp);
                original = output.ToArray();
            }
            var body = new byte[1200 * 600 * 3];
            for (var index = 0; index < body.Length; index += 3)
            { body[index] = 43; body[index + 1] = 33; body[index + 2] = 24; }
            string expected;
            using (var algorithm = SHA256.Create())
            { expected = BitConverter.ToString(algorithm.ComputeHash(body)).Replace("-", string.Empty); }
            foreach (var language in new[] { "ru", "en" })
            {
                var result = RulesFrameGate.Analyze(original, language);
                Require(string.Equals(result.BodySha256, expected, StringComparison.Ordinal), "Body digest must cover exactly the cropped BGR pixels.");
                Require(!result.PanelMatches && !result.SceneCandidate, "Panel-colored background and button alone must not authorize input.");
            }
            var changed = (byte[])original.Clone();
            var offset = BitConverter.ToInt32(changed, 10) + ((1200 - 1 - 360) * 1920 + 360) * 3;
            changed[offset] ^= 1;
            Require(!string.Equals(RulesFrameGate.Analyze(changed, "ru").BodySha256, expected, StringComparison.Ordinal), "One changed body pixel must change the digest.");
            using (var bitmap = new Bitmap(1920, 1200, PixelFormat.Format24bppRgb))
            using (var output = new MemoryStream())
            {
                for (var y = 300; y < 880; y += 20)
                { for (var x = 360; x < 1540; x += 20) { bitmap.SetPixel(x, y, Color.FromArgb(x % 256, y % 256, (x + y) % 256)); } }
                bitmap.Save(output, ImageFormat.Bmp);
                var scene = RulesFrameGate.Analyze(output.ToArray(), "en");
                Require(scene.SceneCandidate && !scene.PanelMatches, "Scene variation is a candidate, never an input authorization.");
            }
            Refused(delegate { RulesFrameGate.Analyze(original, "de"); });
            foreach (var panel in new[] { "ranks", "profile", "topall" })
            {
                foreach (var language in new[] { "en", "ru" })
                {
                    var observation = RulesFrameGate.AnalyzePanel(original, panel, language);
                    Require(!observation.PanelMatches && !observation.SceneCandidate, "A colored panel alone cannot authorize fixed MOTD input.");
                }
                var requestedPanel = panel;
                Refused(delegate { RulesFrameGate.AnalyzePanel(original, requestedPanel, "de"); });
            }
            Refused(delegate { RulesFrameGate.AnalyzePanel(original, "guns", "en"); });
            Refused(delegate { RulesFrameGate.AnalyzePanel(original, "ranks;quit", "en"); });
            Refused(delegate { RulesFrameGate.Analyze(new byte[20], "en"); });
            var wrongSize = (byte[])original.Clone();
            Buffer.BlockCopy(BitConverter.GetBytes(1280), 0, wrongSize, 18, 4);
            Refused(delegate { RulesFrameGate.Analyze(wrongSize, "en"); });
            using (var bitmap = new Bitmap(1920, 1200, PixelFormat.Format24bppRgb))
            using (var output = new MemoryStream())
            {
                bitmap.Save(output, ImageFormat.Bmp);
                var blank = RulesFrameGate.Analyze(output.ToArray(), "en");
                Require(!blank.SceneCandidate && !blank.PanelMatches, "Flat/black captures are not gameplay.");
            }
            var focus = new FakeFocusPort { ForegroundAccepted = true };
            Require(OwnedFocus.TryFocus(focus, new IntPtr(123)) && focus.ForegroundCalls == 1 && focus.ProviderCalls == 0,
                "Already accepted activation must not call a provider.");
            focus = new FakeFocusPort();
            Require(OwnedFocus.TryFocus(focus, new IntPtr(123)) && focus.ForegroundCalls == 1 && focus.ProviderCalls == 1,
                "Only one provider call after foreground denial.");
            focus = new FakeFocusPort { ProviderFails = true };
            var denied = focus;
            Refused(delegate { OwnedFocus.TryFocus(denied, new IntPtr(123)); });
            Require(focus.ForegroundCalls == 1 && focus.ProviderCalls == 1, "Denied focus has no retries.");
            foreach (var handle in new[] { IntPtr.Zero, new IntPtr(-1), new IntPtr(0xffff) })
            {
                var invalid = handle;
                focus = new FakeFocusPort();
                var port = focus;
                Refused(delegate { OwnedFocus.TryFocus(port, invalid); });
                Require(focus.ForegroundCalls == 0 && focus.ProviderCalls == 0, "Special HWND must never reach native focus.");
            }
            Require(BoundedFocus.Invoke(delegate { }, 1000), "A completed inert provider operation must return.");
            Refused(delegate { BoundedFocus.Invoke(delegate { throw new InvalidOperationException("Mock provider failure."); }, 1000); });
            Require(!BoundedFocus.Invoke(delegate { Thread.Sleep(100); }, 1), "A hung provider must stop waiting without retry.");
            Refused(delegate { BoundedFocus.Invoke(delegate { }, 2001); });
            Console.WriteLine("Autonomous frame and focus checks passed: " + _checks.ToString(CultureInfo.InvariantCulture) + ". Synthetic pixels only; no native input or focus calls.");
        }
    }
}
