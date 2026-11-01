using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;

namespace OpenUtau.App.Views {
    /// <summary>
    /// Offers to remap a track's voice colors, and imported DiffSinger vocal modes, when a
    /// <see cref="VoiceColorRemappingNotification"/> asks: after an import, after a singer change, or when the user
    /// asks. Its dialogs are owned by the window that <c>owner</c> gives as each one opens, so a host without
    /// <see cref="MainWindow"/> can remap too. Such a host's dialogs may block nothing, so while one is open the
    /// project can be replaced, the track can change singer again and other edits can be made. A mapping therefore
    /// applies only while its project and its track's singer are still the ones it was asked for, each in an undo
    /// group of its own once its dialog has closed, and a new remap of a track closes the one it supersedes.
    /// </summary>
    public class VoiceColorRemapper {
        readonly Func<Window> owner;
        readonly List<(UTrack track, Window dialog)> pending = new List<(UTrack, Window)>();

        public VoiceColorRemapper(Func<Window> owner) {
            this.owner = owner;
        }

        /// <summary>Closes the dialogs still open, as when the project they were asked for is replaced. Closing one
        /// applies nothing.</summary>
        public void DismissPending() {
            foreach (var (_, dialog) in pending.ToArray()) {
                dialog.Close();
            }
        }

        // Closes the dialogs still open for a track, which a new remap of it supersedes.
        void Dismiss(UTrack track) {
            foreach (var (_, dialog) in pending.Where(p => p.track == track).ToArray()) {
                dialog.Close();
            }
        }

        // Shows a mapping dialog for a track, kept in pending until it closes.
        Task Show(UTrack track, Window dialog) {
            var entry = (track, dialog);
            pending.Add(entry);
            dialog.Closed += (_, _) => pending.Remove(entry);
            return dialog.ShowDialog(owner());
        }

        static bool Still(UProject project) => DocManager.Inst.Project == project;

        // Whether a mapping asked for this project and this singer of the track still applies.
        static bool Current(UProject project, UTrack track, USinger? singer) => Still(project) && track.Singer == singer;

        // Runs a mapping's commands as an undo step of its own.
        static void Apply(Action commands) {
            DocManager.Inst.StartUndoGroup("command.track.remapvc");
            try {
                commands();
            } finally {
                DocManager.Inst.EndUndoGroup();
            }
        }

        public void Handle(VoiceColorRemappingNotification voicecolorNotif) {
            var project = DocManager.Inst.Project;
            if (voicecolorNotif.TrackNo < 0 || project.tracks.Count <= voicecolorNotif.TrackNo) {
                // Verify whether remapping is required when the voice color lineup changes
                ValidateTracksVoiceColor();
            } else {
                UTrack track = project.tracks[voicecolorNotif.TrackNo];
                Dismiss(track);
                if (!voicecolorNotif.Validate) {
                    // When the user intentionally invokes remapping
                    if (track.VoiceColorExp.options.Length == 0) {
                        MessageBox.Show(owner(), ThemeManager.GetString("dialogs.voicecolorremapping.error"), ThemeManager.GetString("errors.caption"), MessageBox.MessageBoxButtons.Ok);
                    } else {
                        VoiceColorRemapping(project, track, track.VoiceColorNames, track.VoiceColorExp.options, true);
                    }
                } else if (track.ValidateVoiceColor(out var oldColors, out var newColors)) { // Verify whether remapping is required when the singer is changed
                    VoiceColorRemapping(project, track, oldColors, newColors);
                }
                _ = RemapImportedVocalModesAsync(project, track);
            }
        }

        /// <summary>
        /// Remaps voice colors, and imported DiffSinger vocal modes, of every track after an import
        /// or after a singer change. Each accepted mapping is an undo step of its own.
        /// </summary>
        public async void ValidateTracksVoiceColor() {
            var project = DocManager.Inst.Project;
            foreach (var track in project.tracks.ToArray()) {
                if (!Still(project)) {
                    break;
                }
                if (track.ValidateVoiceColor(out var oldColors, out var newColors)) {
                    await VoiceColorRemappingAsync(project, track, oldColors, newColors);
                }
                await RemapImportedVocalModesAsync(project, track);
            }
        }

        /// <summary>
        /// Offers to copy the curves of vocal modes that came in with an imported project onto
        /// DiffSinger voice color curves, so the modes keep working on the current singer.
        /// </summary>
        async Task RemapImportedVocalModesAsync(UProject project, UTrack track) {
            if (!Still(project) || track.Singer?.SingerType != USingerType.DiffSinger) return;
            var singer = track.Singer;
            singer.EnsureLoaded();
            if (!singer.Loaded) return;
            // A DiffSinger voicebank that declares no subbanks has no voice color to remap onto,
            // so there is nothing to ask the user about.
            var colors = singer.Subbanks.Select(s => s.Color).ToArray();
            if (colors.Length == 0) return;
            var parts = project.parts.Where(p => p.trackNo == track.TrackNo && p is UVoicePart).Cast<UVoicePart>().ToArray();
            var modes = parts.SelectMany(p => p.curves)
                .Where(c => c.descriptor != null && IsImportedVocalModeCurve(c.abbr))
                .Select(c => c.descriptor.name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (modes.Length == 0) return;

            var oldModes = new[] { "" }.Concat(modes).ToArray();
            var dialog = new VoiceColorMappingDialog { DataContext = new VoiceColorMappingViewModel(oldModes, colors, track.TrackName) };
            await Show(track, dialog);
            if (!dialog.Apply || !Current(project, track, singer)) return;

            // Commands, so that the copies can be undone, mark the project edited and are rendered.
            Apply(() => {
                // SelectedIndex 0 is "(Default)", which leaves the imported curve untouched, so only rows
                // mapped to an actual voice color are copied.
                foreach (var mapping in ((VoiceColorMappingViewModel)dialog.DataContext).ColorMappings.Where(m => m.OldIndex > 0 && m.SelectedIndex > 0)) {
                    var sourceName = modes[mapping.OldIndex - 1];
                    var sourceDescriptor = project.expressions.Values.FirstOrDefault(d => d.name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
                    if (sourceDescriptor == null) continue;
                    // SelectedIndex is the subbank index chosen in the dialog, while DiffSinger exposes
                    // subbank k as curve cl(k+1) (see DiffSingerRenderer.GetExpressions).
                    string targetAbbr = $"cl{mapping.SelectedIndex + 1:D2}";
                    if (!project.expressions.ContainsKey(targetAbbr)) {
                        var targetDescriptor = new UExpressionDescriptor($"voice color {colors[mapping.SelectedIndex]}", targetAbbr, 0, 100, 0) { type = UExpressionType.Curve };
                        DocManager.Inst.ExecuteCmd(new ConfigureExpressionsCommand(project, project.expressions.Values.Append(targetDescriptor).ToArray()));
                    }
                    foreach (var part in parts) {
                        var source = part.curves.FirstOrDefault(c => c.abbr == sourceDescriptor.abbr);
                        if (source == null || source.xs.Count == 0 || part.curves.Any(c => c.abbr == targetAbbr)) continue;
                        DocManager.Inst.ExecuteCmd(new PasteCurveCommand(project, part, targetAbbr,
                            source.xs, source.ys.Select(y => Math.Clamp(y <= 1 ? y * 100 : y, 0, 100))));
                    }
                }
            });
        }

        static bool IsImportedVocalModeCurve(string abbr) {
            if (abbr.StartsWith("cl", StringComparison.OrdinalIgnoreCase)) return false;
            return abbr != Ustx.DYN && abbr != Ustx.PITD && abbr != Ustx.TENC &&
                abbr != Ustx.BREC && abbr != Ustx.GENC && abbr != Ustx.VOIC &&
                abbr != Ustx.SHFC && abbr != Ustx.CLR && abbr != Ustx.CLRY &&
                abbr != "opec";
        }
        async Task VoiceColorRemappingAsync(UProject project, UTrack track, string[] oldColors, string[] newColors) {
            if (!Still(project)) return;
            var singer = track.Singer;
            var parts = project.parts
                .Where(part => part.trackNo == track.TrackNo && part is UVoicePart)
                .Cast<UVoicePart>()
                .Where(vpart => vpart.notes.Count > 0)
                .ToArray();
            if (parts.Any()) {
                var dialog = new VoiceColorMappingDialog();
                VoiceColorMappingViewModel vm = new VoiceColorMappingViewModel(oldColors, newColors, track.TrackName);
                dialog.DataContext = vm;
                await Show(track, dialog);

                if (dialog.Apply && Current(project, track, singer)) {
                    Apply(() => SetVoiceColorRemapping(track, parts, vm));
                }
            }
        }
        void VoiceColorRemapping(UProject project, UTrack track, string[] oldColors, string[] newColors, bool manually = false) {
            var singer = track.Singer;
            var parts = project.parts
                .Where(part => part.trackNo == track.TrackNo && part is UVoicePart)
                .Cast<UVoicePart>()
                .Where(vpart => vpart.notes.Count > 0)
                .ToArray();
            if (parts.Any()) {
                var dialog = new VoiceColorMappingDialog();
                VoiceColorMappingViewModel vm = new VoiceColorMappingViewModel(oldColors, newColors, track.TrackName);
                dialog.DataContext = vm;
                dialog.onFinish = () => {
                    if (Current(project, track, singer)) {
                        Apply(() => SetVoiceColorRemapping(track, parts, vm));
                    }
                };
                _ = Show(track, dialog);
            } else if (manually) {
                MessageBox.Show(owner(), ThemeManager.GetString("lyrics.nonote"), ThemeManager.GetString("errors.caption"), MessageBox.MessageBoxButtons.Ok);
            }
        }
        void SetVoiceColorRemapping(UTrack track, IEnumerable<UVoicePart> parts, VoiceColorMappingViewModel vm) {
            foreach (var part in parts) {
                foreach (var phoneme in part.phonemes) {
                    var tuple = phoneme.GetExpression(DocManager.Inst.Project, track, Ustx.CLR);
                    if (vm.ColorMappings.Any(m => m.OldIndex == tuple.Item1)) {
                        var mapping = vm.ColorMappings.First(m => m.OldIndex == tuple.Item1);
                        if (mapping.OldIndex != mapping.SelectedIndex) {
                            if (mapping.SelectedIndex == 0) {
                                DocManager.Inst.ExecuteCmd(new SetPhonemeExpressionCommand(DocManager.Inst.Project, track, part, phoneme, Ustx.CLR, null));
                            } else {
                                DocManager.Inst.ExecuteCmd(new SetPhonemeExpressionCommand(DocManager.Inst.Project, track, part, phoneme, Ustx.CLR, mapping.SelectedIndex));
                            }
                        }
                    } else {
                        DocManager.Inst.ExecuteCmd(new SetPhonemeExpressionCommand(DocManager.Inst.Project, track, part, phoneme, Ustx.CLR, null));
                    }
                }
            }
        }
    }
}
