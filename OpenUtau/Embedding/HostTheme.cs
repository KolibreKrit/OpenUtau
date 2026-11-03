using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;

namespace OpenUtau.App.Embedding {
    /// <summary>
    /// Colours a hosting application gives the piano roll in place of the theme OpenUtau's preferences name, from UTF-8
    /// JSON such as <c>{"dark": true, "colors": {"BackgroundColor": "#1C1C1C"}}</c>. Each name is a colour of
    /// OpenUtau's Dark and Light themes. A colour the host leaves out keeps the Dark theme's when dark is true, and
    /// the Light theme's otherwise. Unless the preferences colour the piano roll by track, its keys, checkboxes and
    /// checkmarks take SystemAccentColor, SystemAccentColorLight1, SystemAccentColorDark1 and CenterKeyColorRight.
    /// </summary>
    public sealed class HostTheme {
        public bool Dark { get; }
        public IReadOnlyDictionary<string, Color> Colors { get; }

        HostTheme(bool dark, IReadOnlyDictionary<string, Color> colors) {
            Dark = dark;
            Colors = colors;
        }

        /// <summary>Reads the JSON, refusing a name not among names or a value that isn't a colour.</summary>
        public static HostTheme Parse(string json, IReadOnlyCollection<string> names) {
            using var document = JsonDocument.Parse(json);
            var top = document.RootElement;
            bool dark = top.GetProperty("dark").GetBoolean();
            var colors = new Dictionary<string, Color>();
            foreach (var entry in top.GetProperty("colors").EnumerateObject()) {
                if (!names.Contains(entry.Name)) {
                    throw new ArgumentException($"OpenUtau's themes have no colour {entry.Name}");
                }
                if (!Color.TryParse(entry.Value.GetString(), out var color)) {
                    throw new ArgumentException($"{entry.Name} isn't a colour: {entry.Value}");
                }
                colors[entry.Name] = color;
            }
            return new HostTheme(dark, colors);
        }
    }
}
