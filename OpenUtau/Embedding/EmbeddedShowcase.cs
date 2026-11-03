using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Rendering.Composition;
using OpenUtau.App.Controls;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;

namespace OpenUtau.App.Embedding {
    public sealed class EmbeddedShowcase {
        readonly PianoRoll roll;
        readonly NotesCanvas canvas;
        Control? frameOrigin;
        double drawnPlayheadX;
        UVoicePart? part;
        double tickWidth, tickOffset, trackHeight, trackOffset;
        bool active, showTips, showPhoneme, showWaveform, playheadWasVisible, highlightWasVisible, updating;
        Control? playheadLine, playheadHighlight;
        double hostSeconds;
        bool held;
        string scrolling = "";
        long externalViewWrites;
        double inputHostSeconds, pendingSeconds;
        string pendingPlan = "", renderedFrame = "";
        bool frameQueued;
        string theme = "Light";
        readonly List<Action> restoreChrome = new();

        public EmbeddedShowcase(PianoRoll roll) {
            this.roll = roll;
            canvas = roll.GetVisualDescendants().OfType<NotesCanvas>().First();
        }

        public string RenderedFrame => Volatile.Read(ref renderedFrame);

        // The overlay reads only the camera and clock of a batch the compositor has drawn. New host samples
        // coalesce until the next render frame, so a faster JUCE display callback cannot run ahead of the roll.
        public void RequestFrame(string plan, double seconds, Control origin) {
            pendingPlan = plan;
            pendingSeconds = seconds;
            frameOrigin = origin;
            if (!active || frameQueued) return;
            var top = TopLevel.GetTopLevel(roll);
            if (top == null) return;
            frameQueued = true;
            top.RequestAnimationFrame(_ => {
                frameQueued = false;
                if (!active) return;
                Update(pendingPlan, pendingSeconds);
                canvas.InvalidateVisual();
            });
        }

        void DrawFrame(DrawingContext context) {
            if (!active) return;
            var vm = roll.ViewModel.NotesViewModel;
            drawnPlayheadX = vm.PlayPosX;
            context.DrawLine(new Pen(ThemeManager.AccentBrush2, 1), new Point(drawnPlayheadX, 0),
                new Point(drawnPlayheadX, vm.Bounds.Height));
            if (frameOrigin == null) return;
            string snapshot = Snapshot(frameOrigin);
            var batch = ElementComposition.GetElementVisual(canvas)!.Compositor.RequestCompositionBatchCommitAsync();
            batch.Rendered.ContinueWith(_ => {
                if (Volatile.Read(ref active)) Volatile.Write(ref renderedFrame, snapshot);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void Begin(bool hideChrome) {
            if (active) return;
            var vm = roll.ViewModel.NotesViewModel;
            part = vm.Part;
            showTips = vm.ShowTips;
            vm.ShowTips = false;
            tickWidth = vm.TickWidth;
            tickOffset = vm.TickOffset;
            trackHeight = vm.TrackHeight;
            trackOffset = vm.TrackOffset;
            theme = Preferences.Default.ThemeName;
            Preferences.Default.ThemeName = "Dark";
            App.SetTheme();
            active = true;
            canvas.HostedFrame += DrawFrame;
            vm.SetHostPlayPos(DocManager.Inst.playPosTick);
            playheadLine = roll.FindControl<Control>("PlayPosLine");
            playheadWasVisible = playheadLine?.IsVisible ?? false;
            playheadLine?.SetCurrentValue(Control.IsVisibleProperty, false);
            playheadHighlight = roll.FindControl<Control>("PlayPosHighlight");
            highlightWasVisible = playheadHighlight?.IsVisible ?? false;
            playheadHighlight?.SetCurrentValue(Control.IsVisibleProperty, false);
            vm.PropertyChanged += ViewChanged;
            frame = null;
            showPhoneme = vm.ShowPhoneme;
            showWaveform = vm.ShowWaveform;
            if (hideChrome) {
                vm.ShowPhoneme = vm.ShowWaveform = false;
                var grid = roll.GetVisualDescendants().OfType<Grid>().First();
                for (int i = 0; i < grid.RowDefinitions.Count; ++i) {
                    if (i == 2 || i == 3) continue;
                    var row = grid.RowDefinitions[i];
                    var height = row.Height;
                    var min = row.MinHeight;
                    restoreChrome.Add(() => { row.MinHeight = min; row.Height = height; });
                    row.MinHeight = 0;
                    row.Height = new GridLength(0);
                }
                foreach (var child in grid.Children.Where(c => Grid.GetRow(c) != 2 && Grid.GetRow(c) != 3)) {
                    var visible = child.IsVisible;
                    restoreChrome.Add(() => child.IsVisible = visible);
                    child.IsVisible = false;
                }
                for (int i = 2; i < grid.ColumnDefinitions.Count; ++i) {
                    var column = grid.ColumnDefinitions[i];
                    var width = column.Width;
                    restoreChrome.Add(() => column.Width = width);
                    column.Width = new GridLength(0);
                }
            }
        }

        void ViewChanged(object? sender, PropertyChangedEventArgs e) {
            if (!updating && (e.PropertyName == "TickOffset" || e.PropertyName == "TickWidth")) ++externalViewWrites;
        }

        record Phrase(double On, double Off);
        record Frame(double Left, double Right, double? Centre, double? PhraseOn);
        Frame? frame;
        int frameTrack = -1;
        string frameLines = "";
        double frameWidth;
        long sequence;
        static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
        static Dictionary<string, string> Fields(string plan) => plan.Split('\t')
            .Where(f => f.Contains('=')).Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1]);

        static List<Phrase> Phrases(string lines, int track, double room) {
            var result = new List<Phrase>();
            foreach (var line in lines.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (int.Parse(fields[0], CultureInfo.InvariantCulture) != track) continue;
                var words = fields.Skip(1).Select(f => f.Split(':')).Select(f => new Phrase(Number(f[0]), Number(f[1]))).ToList();
                for (int first = 0; first < words.Count;) {
                    double on = words[first].On, off = words.Skip(first).Max(w => w.Off);
                    int after = words.Count;
                    int count = (int)Math.Ceiling((off - on) / room - 1e-6);
                    if (count > 1) {
                        double goal = on + (off - on) / count, best = double.PositiveInfinity, reach = words[first].Off;
                        for (int j = first + 1; j < words.Count; ++j) {
                            if (reach - on > room && j > first + 1) break;
                            double score = Math.Abs(words[j].On - goal) - Math.Max(0, words[j].On - reach);
                            if (score < best) { best = score; after = j; }
                            reach = Math.Max(reach, words[j].Off);
                        }
                    }
                    result.Add(new Phrase(on, words.GetRange(first, after - first).Max(w => w.Off)));
                    first = after;
                }
            }
            return result.OrderBy(p => p.On).ToList();
        }

        double? Centre(int track, double left, double right) {
            var axis = DocManager.Inst.Project.timeAxis;
            var pitches = DocManager.Inst.Project.parts.OfType<UVoicePart>().Where(p => p.trackNo == track - 1)
                .SelectMany(p => p.notes.Where(n => axis.TickPosToMsPos(p.position + n.End) / 1000 > left
                    && axis.TickPosToMsPos(p.position + n.position) / 1000 < right).Select(n => n.tone)).ToList();
            return pitches.Count > 0 ? (pitches.Min() + pitches.Max()) / 2.0 : null;
        }

        public void Update(string plan, double seconds) {
            if (!active) return;
            updating = true;
            try { UpdateFrame(plan, seconds); }
            finally { updating = false; }
        }

        void UpdateFrame(string plan, double seconds) {
            var f = Fields(plan);
            string Get(string key, string fallback = "") => f.GetValueOrDefault(key, fallback);
            double N(string key, double fallback) => Get(key) is var v && v.Length > 0 ? Number(v) : fallback;
            if (Get("steer") != "1") return;
            inputHostSeconds = seconds;
            seconds = N("hold", seconds);
            held = Get("hold").Length > 0;
            scrolling = Get("scroll");
            hostSeconds = seconds;
            int track = 1;
            foreach (var entry in Get("switches").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                var pair = entry.Split(':');
                if (Number(pair[0]) > seconds) break;
                track = int.Parse(pair[1], CultureInfo.InvariantCulture);
            }
            var project = DocManager.Inst.Project;
            var axis = project.timeAxis;
            int tick = axis.MsPosToTickPos(seconds * 1000);
            var vm = roll.ViewModel.NotesViewModel;
            vm.BeginHostFrame(axis.MsPosToNonExactTickPos(seconds * 1000));
            EmbeddedHost.ShowPartIn(roll, track - 1, tick, false);
            if (vm.Part == null || vm.Bounds.Width <= 0) { vm.EndHostFrame(); return; }
            double pixels = vm.Bounds.Width * N("shown", 1);
            double width = Math.Max(1.2, pixels / 320), most = Math.Max(1.2, pixels / 240);
            double left, right;
            double? centre;
            if (Get("scroll") == "phrases") {
                var lines = Get("lines");
                if (frameTrack != track || frameLines != lines || Math.Abs(frameWidth - width) > 0.05) frame = null;
                frameTrack = track; frameLines = lines; frameWidth = width;
                var phrases = Phrases(lines, track, Math.Max(0.2, most - 0.65));
                var next = phrases.FirstOrDefault(p => p.Off + 0.1 > seconds);
                bool inside = frame != null && seconds >= frame.Left && seconds < frame.Right - 0.15;
                if (frame == null || !inside || (next != null && seconds >= next.On - 0.45
                        && frame.PhraseOn != next.On)) {
                    double due = frame != null && seconds >= frame.Right - 0.15 ? frame.Right - 0.15 : seconds;
                    left = due - 0.1;
                    right = left + width;
                    double? phraseOn = null;
                    if (next != null && next.On - due <= 2 && (due >= next.On - 0.45 || next.Off + 0.2 - left <= most)) {
                        phraseOn = next.On;
                        left = Math.Min(next.On - 0.45, due - 0.1);
                        right = left + Math.Min(most, Math.Max(width, next.Off + 0.2 - left));
                    }
                    var after = phrases.FirstOrDefault(p => p.On > (next?.On ?? seconds));
                    frame = new Frame(left, right, Centre(track, left, Math.Min(right - 0.15, after?.On - 0.45 ?? right)), phraseOn);
                }
                left = frame.Left; right = frame.Right; centre = frame.Centre;
            } else {
                double bars = Get("zoom") == "1" && Get("chorus").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Split(':')).Any(v => seconds >= Number(v[0]) && seconds < Number(v[1])) ? 3 : 4;
                double span = bars * N("bar", 2);
                left = axis.TickPosToMsPos(vm.Part.position + vm.TickOffset) / 1000;
                if (Get("scroll") == "smooth" && Get("still") != "1") left = seconds - span * 0.3;
                else if (seconds < left || seconds >= left + span - 0.15) left = seconds - span * 0.05;
                right = left + span;
                centre = Centre(track, seconds - 1, seconds + 4);
            }
            double l = axis.MsPosToNonExactTickPos(left * 1000), r = axis.MsPosToNonExactTickPos(right * 1000);
            if (Get("pitch") == "1" && centre.HasValue)
                vm.TrackOffset = vm.TrackCount - 0.5 - centre.Value - N("middle", 0.5) * vm.ViewportTracks;
            vm.SetHostViewport(pixels / Math.Max(1, r - l), l - vm.Part.position);
            vm.EndHostFrame();
            playheadHighlight?.SetCurrentValue(Control.IsVisibleProperty, false);
        }

        public string Snapshot(Control origin) {
            var vm = roll.ViewModel.NotesViewModel;
            var canvas = roll.GetVisualDescendants().OfType<NotesCanvas>().First();
            var at = canvas.TranslatePoint(new Point(), origin) ?? new Point();
            var axis = DocManager.Inst.Project.timeAxis;
            double offset = (vm.Part?.position ?? 0) + vm.TickOffset;
            return JsonSerializer.Serialize(new {
                track = (vm.Part?.trackNo ?? 0) + 1,
                left = axis.TickPosToMsPos(offset) / 1000,
                right = axis.TickPosToMsPos(offset + vm.ViewportTicks) / 1000,
                low = vm.PointToToneDouble(new Point(0, vm.Bounds.Height)),
                high = vm.PointToToneDouble(new Point()),
                xLeft = at.X, xRight = at.X + vm.Bounds.Width,
                yHigh = at.Y, yLow = at.Y + vm.Bounds.Height,
                playhead = hostSeconds,
                hostInputSeconds = inputHostSeconds,
                playheadX = drawnPlayheadX,
                modelPlayheadX = vm.PlayPosX,
                expectedX = (axis.MsPosToNonExactTickPos(hostSeconds * 1000) - offset) * vm.TickWidth,
                highlightVisible = playheadHighlight?.IsVisible ?? false,
                lineVisible = active,
                externalViewWrites,
                held, scrolling,
                sequence = ++sequence,
            });
        }

        public void End() {
            if (!active) return;
            Volatile.Write(ref active, false);
            canvas.HostedFrame -= DrawFrame;
            canvas.InvalidateVisual();
            roll.ViewModel.NotesViewModel.PropertyChanged -= ViewChanged;
            roll.ViewModel.NotesViewModel.SetHostPlayPos(null);
            playheadLine?.SetCurrentValue(Control.IsVisibleProperty, playheadWasVisible);
            playheadHighlight?.SetCurrentValue(Control.IsVisibleProperty, highlightWasVisible);
            foreach (var restore in restoreChrome) restore();
            restoreChrome.Clear();
            var vm = roll.ViewModel.NotesViewModel;
            if (part != null && DocManager.Inst.Project.parts.Contains(part) && vm.Part != part) {
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, DocManager.Inst.Project, part.position));
                roll.AttachExpressions();
            }
            Preferences.Default.ThemeName = theme;
            App.SetTheme();
            vm.ShowTips = showTips;
            vm.ShowPhoneme = showPhoneme;
            vm.ShowWaveform = showWaveform;
            vm.TickWidth = tickWidth;
            vm.TrackHeight = trackHeight;
            vm.TickOffset = tickOffset;
            vm.TrackOffset = trackOffset;
        }
    }
}
