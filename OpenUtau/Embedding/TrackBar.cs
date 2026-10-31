using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenUtau.App.Controls;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;

namespace OpenUtau.App.Embedding {
    /// <summary>
    /// The bar above the embedded piano roll, which has no main window and so no track headers. Each track that
    /// sings has a tab naming its singer, in OpenUtau's warning colour while it has none, and the shown track's tab is
    /// underlined. Clicking the tab shows the track, and the arrow beside it opens the track header's singer menu, with
    /// the phonemizer and renderer under Advanced. Each choice is one step OpenUtau's undo takes back.
    /// </summary>
    public class TrackBar : UserControl, ICmdSubscriber {
        readonly Action<int> showTrack;
        readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        // The part the piano roll shows, whose track's tab is the shown one.
        UPart? shownPart;

        /// <param name="showTrack">Shows the track of the given number in the piano roll.</param>
        public TrackBar(Action<int> showTrack) {
            this.showTrack = showTrack;
            // As the track header has it, so the singer grid isn't cut to a flyout's usual size.
            Styles.Add(new Style(x => x.OfType<FlyoutPresenter>().Class("singerFlyout")) {
                Setters = {
                    new Setter(MaxWidthProperty, 10000.0),
                    new Setter(MaxHeightProperty, 10000.0),
                    new Setter(PaddingProperty, new Thickness(5)),
                },
            });
            Content = new ScrollViewer {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = tabs,
            };
            DocManager.Inst.AddSubscriber(this);
            Refresh();
        }

        /// <summary>The tabs, in the project's track order.</summary>
        public IReadOnlyList<TrackTab> Tabs { get; private set; } = Array.Empty<TrackTab>();

        public void OnNext(UCommand cmd, bool isUndo) {
            switch (cmd) {
                case LoadPartNotification load:
                    shownPart = load.part;
                    break;
                case LoadProjectNotification:
                    shownPart = null;
                    break;
                case TrackCommand or PartCommand or SingersRefreshedNotification:
                    break;
                default:
                    return;
            }
            if (Dispatcher.UIThread.CheckAccess()) {
                Refresh();
            } else {
                Dispatcher.UIThread.Post(Refresh);
            }
        }

        void Refresh() {
            var project = DocManager.Inst.Project;
            Tabs = project.tracks
                .Where(track => Sings(project, track))
                .Select(track => new TrackTab(track, shownPart != null && shownPart.trackNo == track.TrackNo))
                .ToArray();
            tabs.Children.Clear();
            foreach (var tab in Tabs) {
                tabs.Children.Add(View(tab));
            }
        }

        // A track holding only audio has no notes to show, and its singer sings nothing.
        static bool Sings(UProject project, UTrack track) {
            var parts = project.parts.Where(part => part.trackNo == track.TrackNo).ToList();
            return parts.Count == 0 || parts.Any(part => part is UVoicePart);
        }

        Control View(TrackTab tab) {
            // The buttons don't take the keyboard from the piano roll.
            var name = new Button {
                Content = tab.Text,
                Background = Brushes.Transparent,
                Focusable = false,
                Padding = new Thickness(8, 3, 4, 3),
            };
            name.Click += (_, args) => {
                args.Handled = true;
                showTrack(tab.TrackNo);
            };
            var menu = new Button {
                Content = "▾",
                Background = Brushes.Transparent,
                Focusable = false,
                Padding = new Thickness(4, 3, 8, 3),
            };
            ToolTip.SetTip(menu, "Choose the singer");
            menu.Click += (_, args) => {
                args.Handled = true;
                ShowMenu(tab, menu);
            };
            var view = new Border {
                BorderBrush = tab.Shown ? ThemeManager.AccentBrush1 : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(4),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { name, menu } },
            };
            if (tab.HasSinger) {
                view.Background = ThemeManager.NeutralAccentBrushSemi;
            } else {
                view.Bind(Border.BackgroundProperty, view.GetResourceObservable("WarningBrush"));
            }
            return view;
        }

        // As the track header opens it, with Advanced below the singers.
        static void ShowMenu(TrackTab tab, Control anchor) {
            try {
                var header = tab.Menu();
                var singers = new SingerFlyoutViewModel(() => header.Singer, header.SelectSingerCommand);
                var content = new SingerFlyout { DataContext = singers };
                var advanced = Advanced(header, tab);
                DockPanel.SetDock(advanced, Dock.Bottom);
                var flyout = new Flyout {
                    Content = new DockPanel { Children = { advanced, content } },
                    Placement = PlacementMode.BottomEdgeAlignedLeft,
                    ShowMode = FlyoutShowMode.Standard,
                };
                flyout.FlyoutPresenterClasses.Add("singerFlyout");
                singers.CloseRequested += flyout.Hide;
                flyout.Opened += (_, _) => DocManager.Inst.AddSubscriber(singers);
                flyout.Closed += (_, _) => DocManager.Inst.RemoveSubscriber(singers);
                content.FitToScreen(anchor);
                flyout.ShowAt(anchor);
            } catch (Exception e) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(e));
            }
        }

        // The phonemizer and renderer, which follow the singer unless chosen here.
        static Control Advanced(TrackHeaderViewModel header, TrackTab tab) {
            header.RefreshPhonemizers();
            header.RefreshRenderers();
            var renderers = header.RenderersMenuItems ?? Array.Empty<MenuItemViewModel>();
            // Closed, though OpenUtau's theme opens expanders, so that the singers come first.
            return new Expander {
                Header = "Advanced",
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 0),
                Content = new StackPanel {
                    Spacing = 4,
                    Children = {
                        Choice("Phonemizer", header.PhonemizerTag, header.PhonemizerMenuItems),
                        Choice("Renderer", tab.Renderer, renderers),
                    },
                },
            };
        }

        static Control Choice(string label, string value, IEnumerable<MenuItemViewModel>? items) {
            var list = items?.ToList() ?? new List<MenuItemViewModel>();
            var name = new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(name, Dock.Left);
            return new DockPanel {
                Children = {
                    name,
                    new Button {
                        Content = $"{value} ▾",
                        IsEnabled = list.Count > 0,
                        Flyout = new MenuFlyout { ItemsSource = list.Select(Item).ToArray() },
                    },
                },
            };
        }

        static MenuItem Item(MenuItemViewModel item) => new() {
            Header = item.Header,
            Command = item.Command,
            CommandParameter = item.CommandParameter,
            ItemsSource = item.Items?.Select(Item).ToArray(),
        };
    }

    /// <summary>A track's tab in the <see cref="TrackBar"/>.</summary>
    public sealed class TrackTab {
        readonly UTrack track;

        internal TrackTab(UTrack track, bool shown) {
            this.track = track;
            Shown = shown;
        }

        public int TrackNo => track.TrackNo;
        public bool HasSinger => track.Singer != null && track.Singer.Found;
        /// <summary>The track's name and its singer's, such as "Singer 1 · Kasane Teto EN".</summary>
        public string Text => $"{track.TrackName} · {(HasSinger ? track.Singer!.LocalizedName : "No singer")}";
        /// <summary>The renderer's name, or "None".</summary>
        public string Renderer => string.IsNullOrEmpty(track.RendererSettings.renderer) ? "None" : track.RendererSettings.renderer;
        /// <summary>Whether the piano roll shows a part of the track.</summary>
        public bool Shown { get; }

        /// <summary>A new track header model for the tab's menu, whose commands choose the singer, phonemizer and
        /// renderer as the track header does.</summary>
        public TrackHeaderViewModel Menu() => new TrackHeaderViewModel(track);
    }
}
