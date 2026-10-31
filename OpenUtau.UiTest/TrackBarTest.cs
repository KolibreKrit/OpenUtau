using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenUtau.App.Controls;
using OpenUtau.App.Embedding;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.UiTest {
    /// <summary>The bar above the embedded piano roll, with a tab for each track naming its singer.</summary>
    public class TrackBarTest {
        sealed class Singer : USinger {
            public Singer(string name) {
                Name = name;
                found = true;
                loaded = true;
            }
            public override string Id => Name;
            public override string Name { get; }
            public override USingerType SingerType => USingerType.Classic;
            public override IList<USubbank> Subbanks { get; } = new List<USubbank>();
        }

        // Lead sings with Alto and Harmony has no singer. Each has a part.
        static UProject Project() {
            var project = Core.Format.Ustx.Create();
            project.tracks.Add(new UTrack(project) { TrackNo = 1 });
            project.tracks[0].TrackName = "Lead";
            project.tracks[1].TrackName = "Harmony";
            foreach (var track in project.tracks) {
                EmbeddedHostTest.AddPart(project, track.TrackNo, 1920 * track.TrackNo, 4);
            }
            project.AfterLoad();
            project.tracks[0].Singer = new Singer("Alto");
            project.tracks[1].Singer = USinger.CreateMissing("SynthV voice");
            return project;
        }

        // Opens the project and shows the bar in a window. The bar shows tracks through shown. The project is
        // validated once OpenUtau has started, so that phonemes are asked for.
        static void WithBar(UProject project, Action<TrackBar, List<int>> test) => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            DocManager.Inst.RequestFrame = null;
            project.ValidateFull();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            var shown = new List<int>();
            var bar = new TrackBar(shown.Add);
            var window = new Window { Width = 1200, Height = 100, Content = bar };
            var recentSingers = Preferences.Default.RecentSingers.ToList();
            try {
                window.Show();
                HeadlessUi.Flush();
                test(bar, shown);
                // Phonemes arriving after the project is replaced would belong to no project.
                EmbeddedHostTest.WaitForPhonemes(project);
            } finally {
                window.Close();
                DocManager.Inst.RemoveSubscriber(bar);
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                Preferences.Default.RecentSingers = recentSingers;
                Preferences.Save();
                HeadlessUi.Flush();
            }
        });

        static IEnumerable<string?> Texts(TrackBar bar) =>
            bar.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text);

        static Button TabButton(TrackBar bar, string text) => bar.GetVisualDescendants().OfType<Button>()
            .Single(button => button.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));

        static Border Tab(TrackBar bar, string text) => bar.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Child is StackPanel panel && panel.Children.Contains(TabButton(bar, text)));

        [Fact]
        public void EachTrackHasATabNamingItsSinger() {
            WithBar(Project(), (bar, _) => {
                Assert.Equal(new[] { "Lead · Alto", "Harmony · No singer" }, bar.Tabs.Select(tab => tab.Text));
                Assert.Contains("Lead · Alto", Texts(bar));
                Assert.Contains("Harmony · No singer", Texts(bar));
                Assert.Equal(new[] { true, false }, bar.Tabs.Select(tab => tab.HasSinger));
                Assert.All(bar.GetVisualDescendants().OfType<TextBlock>(), text => Assert.NotNull(text.Foreground));
            });
        }

        [Fact]
        public void ATabWithoutASingerHasOpenUtausWarningColour() {
            WithBar(Project(), (bar, _) => {
                Assert.True(bar.TryFindResource("WarningBrush", bar.ActualThemeVariant, out var warning));
                Assert.Equal(warning, Tab(bar, "Harmony · No singer").Background);
                Assert.NotEqual(warning, Tab(bar, "Lead · Alto").Background);
            });
        }

        [Fact]
        public void ClickingATabShowsItsTrackAndTheTabFollowsThePianoRoll() {
            var project = Project();
            WithBar(project, (bar, shown) => {
                TabButton(bar, "Harmony · No singer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(new[] { 1 }, shown);

                var part = project.parts.Single(p => p.trackNo == 1);
                DocManager.Inst.ExecuteCmd(new LoadPartNotification(part, project, part.position));
                HeadlessUi.Flush();

                Assert.Equal(new[] { false, true }, bar.Tabs.Select(tab => tab.Shown));
            });
        }

        [Fact]
        public void ASingerChosenFromATabsMenuIsOneStepUndoTakesBack() {
            var project = Project();
            WithBar(project, (bar, _) => {
                var soprano = new Singer("Soprano");
                ((ICommand)bar.Tabs[1].Menu().SelectSingerCommand).Execute(soprano);
                HeadlessUi.Flush();
                Assert.Same(soprano, project.tracks[1].Singer);
                Assert.Equal("Harmony · Soprano", bar.Tabs[1].Text);

                DocManager.Inst.Undo();
                HeadlessUi.Flush();

                Assert.False(project.tracks[1].Singer.Found);
                Assert.Equal("Harmony · No singer", bar.Tabs[1].Text);
            });
        }

        [Fact]
        public void ATabsArrowOpensTheSingerMenuWithThePhonemizerAndRendererUnderAdvanced() {
            var project = Project();
            WithBar(project, (bar, _) => {
                var arrow = bar.GetVisualDescendants().OfType<Button>().First(button => button.Content as string == "▾");
                arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HeadlessUi.Flush();

                var window = TopLevel.GetTopLevel(bar)!;
                var singers = Assert.Single(window.GetVisualDescendants().OfType<SingerFlyout>());
                Assert.True(singers.Bounds.Width > 0 && singers.Bounds.Height > 0, $"The singer menu is {singers.Bounds}");
                var advanced = Assert.Single(window.GetVisualDescendants().OfType<Expander>());
                Assert.Equal("Advanced", advanced.Header);
                // Though OpenUtau's theme opens expanders.
                Assert.False(advanced.IsExpanded);
                advanced.IsExpanded = true;
                HeadlessUi.Flush();
                var lead = project.tracks[0];
                var choices = ((Control)advanced.Content!).GetVisualDescendants();
                Assert.Equal(new[] { "Phonemizer", "Renderer" },
                    choices.OfType<TextBlock>().Where(text => text.Parent is DockPanel).Select(text => text.Text));
                Assert.Equal(new[] { $"{lead.Phonemizer.Tag} ▾", $"{lead.RendererSettings.renderer} ▾" },
                    choices.OfType<Button>().Select(button => button.Content as string));
            });
        }

        [Fact]
        public void TabsFollowTheTracks() {
            var project = Project();
            WithBar(project, (bar, _) => {
                DocManager.Inst.StartUndoGroup("test");
                DocManager.Inst.ExecuteCmd(new RenameTrackCommand(project, project.tracks[0], "Solo"));
                DocManager.Inst.ExecuteCmd(new AddTrackCommand(project, new UTrack(project) { TrackNo = 2, TrackName = "Bass" }));
                DocManager.Inst.EndUndoGroup();
                HeadlessUi.Flush();
                Assert.Equal(new[] { "Solo · Alto", "Harmony · No singer", "Bass · No singer" }, bar.Tabs.Select(tab => tab.Text));
                Assert.Contains("Bass · No singer", Texts(bar));

                DocManager.Inst.Undo();
                HeadlessUi.Flush();

                Assert.Equal(new[] { "Lead · Alto", "Harmony · No singer" }, bar.Tabs.Select(tab => tab.Text));
            });
        }

        [Fact]
        public void ATrackHoldingOnlyAudioHasNoTab() {
            // Its singer sings nothing, and its tab would show no notes.
            var project = Project();
            project.tracks.Add(new UTrack(project) { TrackNo = 2, TrackName = "Backing" });
            var file = Path.Combine(Path.GetTempPath(), $"TrackBarTest-{Guid.NewGuid()}.wav");
            WaveFileWriter.CreateWaveFile16(file, new SignalGenerator(44100, 1) { Gain = 0 }.Take(TimeSpan.FromSeconds(1)));
            try {
                var backing = new UWavePart { trackNo = 2, FilePath = file };
                backing.Load(project);
                project.parts.Add(backing);
                WithBar(project, (bar, _) => {
                    Assert.Equal(new[] { 0, 1 }, bar.Tabs.Select(tab => tab.TrackNo));
                });
            } finally {
                File.Delete(file);
            }
        }
    }
}
