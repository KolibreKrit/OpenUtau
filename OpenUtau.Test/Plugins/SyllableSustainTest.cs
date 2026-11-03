using System.Collections.Generic;
using System.IO;
using System.Reflection;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;
using Xunit;

namespace OpenUtau.Plugins {
    public class SyllableSustainTest {
        sealed class SustainPhonemizer : SyllableBasedPhonemizer {
            public SustainPhonemizer() {
                vowelSustains["aI"] = ("aI -", 0.6);
            }
            protected override string[] GetVowels() => new[] { "aI" };
            protected override string[] GetConsonants() => new[] { "t" };
            protected override string[] GetSymbols(Note note) => note.phoneticHint.Split(' ');
            protected override List<string> ProcessSyllable(Syllable syllable) => string.IsNullOrEmpty(syllable.prevV) ? new() { "aI" } : new() { "aI t", "t", "taI" };
            protected override List<string> ProcessEnding(Ending ending) => ending.cc.Length == 0 ? new() : new() { "aI t", "t-" };
        }

        [Theory]
        [InlineData(120, "aI t", new[] { "aI", "aI t", "t-" })]
        [InlineData(480, "aI t", new[] { "aI", "aI -", "aI t", "t-" })]
        [InlineData(240, "aI t aI t", new[] { "aI", "aI t", "t", "taI", "aI t", "t-" })]
        [InlineData(960, "aI t aI t", new[] { "aI", "aI -", "aI t", "t", "taI", "aI -", "aI t", "t-" })]
        [InlineData(15, "aI", new[] { "aI" })]
        [InlineData(480, "aI", new[] { "aI", "aI -" })]
        public void SustainsFitBeforeTheNextTransition(int duration, string hint, string[] aliases) {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Files", "en_delta7");
            VoicebankLoader.IsTest = true;
            var bank = new Voicebank { File = Path.Combine(directory, "character.txt"), BasePath = directory };
            VoicebankLoader.LoadVoicebank(bank);
            using var singer = new ClassicSinger(bank);
            singer.EnsureLoaded();
            var project = new UProject();
            project.tempos[0].bpm = 105;
            project.timeAxis.BuildSegments(project);
            var phonemizer = new SustainPhonemizer();
            phonemizer.SetSinger(singer);
            phonemizer.SetTiming(project.timeAxis);
            var notes = new[] { new Phonemizer.Note { lyric = "bite", phoneticHint = hint, position = 240, duration = duration, tone = 60 } };
            var result = phonemizer.Process(notes, null, null, null, null, null).phonemes;
            Assert.Equal(aliases, System.Linq.Enumerable.Select(result, p => p.phoneme));
            for (int i = 1; i < result.Length; ++i) {
                Assert.True(result[i - 1].position < result[i].position);
            }
        }

        [Theory]
        [InlineData(120, new[] { "aI" })]
        [InlineData(480, new[] { "aI", "aI -" })]
        public void SustainFitsBeforeTheFollowingWordsConsonants(int duration, string[] aliases) {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Files", "en_delta7");
            VoicebankLoader.IsTest = true;
            var bank = new Voicebank { File = Path.Combine(directory, "character.txt"), BasePath = directory };
            VoicebankLoader.LoadVoicebank(bank);
            using var singer = new ClassicSinger(bank);
            singer.EnsureLoaded();
            var project = new UProject();
            project.tempos[0].bpm = 105;
            project.timeAxis.BuildSegments(project);
            var phonemizer = new SustainPhonemizer();
            phonemizer.SetSinger(singer);
            phonemizer.SetTiming(project.timeAxis);
            var note = new Phonemizer.Note { lyric = "I", phoneticHint = "aI", position = 240, duration = duration, tone = 60 };
            var next = new Phonemizer.Note { lyric = "try", phoneticHint = "t aI", position = 240 + duration, duration = 480, tone = 60 };
            var result = phonemizer.Process(new[] { note }, null, next, null, next, null).phonemes;
            Assert.Equal(aliases, System.Linq.Enumerable.Select(result, p => p.phoneme));
        }
    }
}
