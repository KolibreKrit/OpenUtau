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
    /// <see cref="MainWindow"/> can remap too. Such a host may replace the project while a dialog is open, so a
    /// mapping is applied only to the project it was asked for, and <see cref="DismissPending"/> closes the dialogs.
    /// </summary>
    public class VoiceColorRemapper {
        readonly Func<Window> owner;
        readonly List<Window> pending = new List<Window>();

        public VoiceColorRemapper(Func<Window> owner) {
            this.owner = owner;
        }

        /// <summary>Closes the dialogs still open, as when the project they were asked for is replaced. Closing one
        /// applies nothing.</summary>
        public void DismissPending() {
            foreach (var dialog in pending.ToArray()) {
                dialog.Close();
            }
        }

        // Shows a mapping dialog, kept in pending until it closes.
        Task Show(Window dialog) {
            pending.Add(dialog);
            dialog.Closed += (_, _) => pending.Remove(dialog);
            return dialog.ShowDialog(owner());
        }

        static bool Still(UProject project) => DocManager.Inst.Project == project;

        public void Handle(VoiceColorRemappingNotification voicecolorNotif) {
            var project = DocManager.Inst.Project;
            if (voicecolorNotif.TrackNo < 0 || project.tracks.Count <= voicecolorNotif.TrackNo) {
                // Verify whether remapping is required when the voice color lineup changes
                ValidateTracksVoiceColor();
            } else {
                UTrack track = project.tracks[voicecolorNotif.TrackNo];
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
        /// or after a singer change.
        /// </summary>
        public async void ValidateTracksVoiceColor() {
            var project = DocManager.Inst.Project;
            DocManager.Inst.StartUndoGroup("command.track.remapvc");
            try {
                foreach (var track in project.tracks.ToArray()) {
                    if (!Still(project)) {
                        break;
                    }
                    if (track.ValidateVoiceColor(out var oldColors, out var newColors)) {
                        await VoiceColorRemappingAsync(project, track, oldColors, newColors);
                    }
                    await RemapImportedVocalModesAsync(project, track);
                }
            } finally {
                // Must close the group even if a remapping dialog or an import threw, otherwise
                // the undo stack stays inside a group nobody ends. Loading another project already
                // dropped it, and the group open now, if any, is the new project's.
                if (Still(project)) {
                    DocManager.Inst.EndUndoGroup();
                }
            }
        }

        /// <summary>
        /// Offers to copy the curves of vocal modes that came in with an imported project onto
        /// DiffSinger voice color curves, so the modes keep working on the current singer.
        /// </summary>
        async Task RemapImportedVocalModesAsync(UProject project, UTrack track) {
            if (!Still(project) || track.Singer?.SingerType != USingerType.DiffSinger) return;
            track.Singer.EnsureLoaded();
            if (!track.Singer.Loaded) return;
            // A DiffSinger voicebank that declares no subbanks has no voice color to remap onto,
            // so there is nothing to ask the user about.
            var colors = track.Singer.Subbanks.Select(s => s.Color).ToArray();
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
            await Show(dialog);
            if (!dialog.Apply || !Still(project)) return;

            // SelectedIndex 0 is "(Default)", which leaves the imported curve untouched, so only rows
            // mapped to an actual voice color are copied.
            foreach (var mapping in ((VoiceColorMappingViewModel)dialog.DataContext).ColorMappings.Where(m => m.OldIndex > 0 && m.SelectedIndex > 0)) {
                var sourceName = modes[mapping.OldIndex - 1];
                var sourceDescriptor = project.expressions.Values.FirstOrDefault(d => d.name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
                if (sourceDescriptor == null) continue;
                // SelectedIndex is the subbank index chosen in the dialog, while DiffSinger exposes
                // subbank k as curve cl(k+1) (see DiffSingerRenderer.GetExpressions).
                string targetAbbr = $"cl{mapping.SelectedIndex + 1:D2}";
                if (!project.expressions.TryGetValue(targetAbbr, out var targetDescriptor)) {
                    targetDescriptor = new UExpressionDescriptor($"voice color {colors[mapping.SelectedIndex]}", targetAbbr, 0, 100, 0) { type = UExpressionType.Curve };
                    project.RegisterExpression(targetDescriptor);
                }
                foreach (var part in parts) {
                    var source = part.curves.FirstOrDefault(c => c.abbr == sourceDescriptor.abbr);
                    if (source == null || part.curves.Any(c => c.abbr == targetAbbr)) continue;
                    part.curves.Add(new UCurve(targetDescriptor) { xs = source.xs.ToList(), ys = source.ys.Select(y => Math.Clamp(y <= 1 ? y * 100 : y, 0, 100)).ToList() });
                }
            }
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
            var parts = project.parts
                .Where(part => part.trackNo == track.TrackNo && part is UVoicePart)
                .Cast<UVoicePart>()
                .Where(vpart => vpart.notes.Count > 0)
                .ToArray();
            if (parts.Any()) {
                var dialog = new VoiceColorMappingDialog();
                VoiceColorMappingViewModel vm = new VoiceColorMappingViewModel(oldColors, newColors, track.TrackName);
                dialog.DataContext = vm;
                await Show(dialog);

                if (dialog.Apply && Still(project)) {
                    SetVoiceColorRemapping(track, parts, vm);
                }
            }
        }
        void VoiceColorRemapping(UProject project, UTrack track, string[] oldColors, string[] newColors, bool manually = false) {
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
                    if (!Still(project)) {
                        return;
                    }
                    DocManager.Inst.StartUndoGroup("command.track.remapvc");
                    SetVoiceColorRemapping(track, parts, vm);
                    DocManager.Inst.EndUndoGroup();
                };
                _ = Show(dialog);
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
