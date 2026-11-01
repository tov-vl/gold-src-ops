using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using GsoLab.Interaction;

namespace GsoLab.Checks
{
    internal static class GunsChecks
    {
        private static int _checks;
        private static void Require(bool value, string reason)
        {
            _checks++;
            if (!value) { throw new InvalidOperationException(reason); }
        }

        private static byte[] Frame(bool ink)
        {
            using (var bitmap = new Bitmap(1920, 1200, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var output = new MemoryStream())
            {
                graphics.Clear(Color.Black);
                if (ink) { graphics.FillRectangle(Brushes.Gold, 20, 380, 80, 20); }
                bitmap.Save(output, ImageFormat.Bmp);
                return output.ToArray();
            }
        }

        internal static void Run()
        {
            var bytes = Frame(ink: true);
            var measured = GunsFrameGate.Analyze(bytes, expectedSha256: null);
            Require(!measured.PanelMatches, "Measuring pixels never enables input without a reviewed pin.");
            Require(GunsFrameGate.Analyze(bytes, measured.BodySha256).PanelMatches, "Exact reviewed menu pixels match.");
            Require(!GunsFrameGate.Analyze(bytes, new string('A', 64)).PanelMatches, "A different menu or language pin refuses input.");
            var blank = Frame(ink: false);
            Require(!GunsFrameGate.Analyze(blank, GunsFrameGate.Analyze(blank, expectedSha256: null).BodySha256).PanelMatches, "Even an exactly pinned blank frame refuses input.");
            var changed = (byte[])bytes.Clone();
            var pixel = BitConverter.ToInt32(changed, 10) + ((1199 - 380) * 1920 + 20) * 3;
            changed[pixel] ^= 1;
            Require(!GunsFrameGate.Analyze(changed, measured.BodySha256).PanelMatches, "One changed menu pixel refuses input.");
            foreach (var phase in new[] { "exit-primary", "select-primary", "select-pistol", "repeat-primary", "exit-pistol-respawn" })
            {
                var port = new FakeWindowPort();
                var window = new GameWindow(port);
                window.SelectGunsItem(phase);
                var key = phase.StartsWith("exit", StringComparison.Ordinal) ? 0x30 : string.Equals(phase, "select-pistol", StringComparison.Ordinal) ? 0x33 : 0x32;
                Require(port.Messages.Count == 2 && port.Messages[0].Id == 0x100 && port.Messages[1].Id == 0x101 &&
                    port.Messages.TrueForAll(message => message.Word == key), "Only the fixed menu key is sent to the owned window.");
                port = new FakeWindowPort { State = { Foreground = false } };
                var refused = false;
                try { new GameWindow(port).SelectGunsItem(phase); } catch (InvalidOperationException) { refused = true; }
                Require(refused && port.Messages.Count == 0, "A background game cannot receive menu input.");
            }
            var invalid = new FakeWindowPort();
            var rejected = false;
            try { new GameWindow(invalid).SelectGunsItem("slot9"); } catch (ArgumentException) { rejected = true; }
            Require(rejected && invalid.Messages.Count == 0, "No arbitrary key action exists.");
            var chat = new FakeWindowPort();
            new GameWindow(chat).ReopenGuns();
            Require(string.Equals(new string(chat.Messages.Where(message => message.Id == 0x102).Select(message => (char)message.Word).ToArray()), "/guns", StringComparison.Ordinal), "The chat command is fixed.");
            foreach (var phase in new[] { "open-settings", "open-languages", "language-ru", "language-en", "close-player-menu", "ack-ru", "ack-en" })
            {
                var port = new FakeWindowPort();
                new GameWindow(port).GunsSetupAction(phase);
                var text = new string(port.Messages.Where(message => message.Id == 0x102).Select(message => (char)message.Word).ToArray());
                var expected = string.Equals(phase, "open-settings", StringComparison.Ordinal) ? "/settings" :
                    string.Equals(phase, "ack-ru", StringComparison.Ordinal) ? "/gso_lab_ready_ru" :
                    string.Equals(phase, "ack-en", StringComparison.Ordinal) ? "/gso_lab_ready_en" : string.Empty;
                Require(string.Equals(text, expected, StringComparison.Ordinal), "Setup chat is restricted to three fixed literals.");
                if (expected.Length == 0)
                {
                    var key = string.Equals(phase, "language-ru", StringComparison.Ordinal) ? 0x31 :
                        string.Equals(phase, "close-player-menu", StringComparison.Ordinal) ? 0x30 : 0x32;
                    Require(port.Messages.Count == 2 && port.Messages[0].Id == 0x100 && port.Messages[1].Id == 0x101 &&
                        port.Messages.TrueForAll(message => message.Word == key), "Setup posts only the fixed menu key.");
                }
                var background = new FakeWindowPort { State = { Foreground = false } };
                var refused = false;
                try { new GameWindow(background).GunsSetupAction(phase); } catch (InvalidOperationException) { refused = true; }
                Require(refused && background.Messages.Count == 0, "Setup cannot type into a background window.");
            }
            foreach (var phase in new[] { "exec", "language-de", "ack-ru;quit", "slot9", string.Empty })
            {
                var port = new FakeWindowPort();
                var refused = false;
                try { new GameWindow(port).GunsSetupAction(phase); } catch (ArgumentException) { refused = true; }
                Require(refused && port.Messages.Count == 0, "Setup has no arbitrary text or key interface.");
            }
            Console.WriteLine("Weapons native checks passed: " + _checks.ToString(CultureInfo.InvariantCulture) + ". No native input or capture.");
        }
    }
}
