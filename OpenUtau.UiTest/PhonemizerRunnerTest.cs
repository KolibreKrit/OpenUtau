using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using OpenUtau.Api;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.UiTest {
    public class PhonemizerRunnerTest {
        sealed class Singer : USinger {
            public Singer() {
                found = true;
                loaded = true;
            }
            public override string Id => Name;
            public override string Name => "Soprano";
            public override USingerType SingerType => USingerType.Classic;
            public override IList<USubbank> Subbanks { get; } = new List<USubbank>();
        }

        // Gives each word its lyric as its phoneme, and tells when its pass over a part has ended.
        sealed class LyricPhonemizer : Phonemizer {
            public readonly ManualResetEventSlim Finished = new ManualResetEventSlim();

            public override void SetSinger(USinger singer) { }

            // Runs whether or not the pass could set the phonemizer up.
            public override void CleanUp() => Finished.Set();

            public override Result Process(Note[] notes, Note? prev, Note? next, Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) =>
                new Result { phonemes = new[] { new Phoneme { phoneme = notes[0].lyric } } };
        }

        [Fact]
        public void APartOnASecondTrackValidatedBeforeItsProjectOpensGetsItsPhonemes() => HeadlessUi.Run(() => {
            MainWindowTest.InitCore();
            // One track, as the project OpenUtau has open before it opens a file.
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
            var project = Core.Format.Ustx.Create();
            project.tracks.Add(new UTrack(project) { TrackNo = 1 });
            var part = new UVoicePart { trackNo = 1, position = 0, Duration = 1920 };
            var note = project.CreateNote(60, 0, 480);
            note.lyric = "la";
            part.notes.Add(note);
            project.parts.Add(part);
            project.AfterLoad();
            var phonemizer = new LyricPhonemizer();
            project.tracks[1].Singer = new Singer();
            project.tracks[1].Phonemizer = phonemizer;
            try {
                // A file's reader validates the project, which asks for phonemes, and only then does it open.
                project.ValidateFull();
                Assert.True(phonemizer.Finished.Wait(TimeSpan.FromSeconds(60)), "The part's phonemes weren't worked out");
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
                var clock = Stopwatch.StartNew();
                while (!part.PhonemesUpToDate) {
                    Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "Phonemes didn't arrive");
                    Thread.Sleep(10);
                    HeadlessUi.Flush();
                }
                Assert.Equal(new[] { "la" }, part.phonemes.Select(phoneme => phoneme.phoneme));
                Assert.Same(project, phonemizer.project);
                Assert.Same(project.tracks[1], phonemizer.track);
            } finally {
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(Core.Format.Ustx.Create()));
                HeadlessUi.Flush();
            }
        });
    }
}
