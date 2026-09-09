using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace C3dMCP.Host.Palette
{
    /// <summary>Phase 1a palette: the Instance section, built in code (the XAML version comes after
    /// the mockup review). Holds only host values, never payload objects.</summary>
    internal sealed class PaletteView : UserControl
    {
        private readonly HostApp _app;
        private readonly TextBlock _port = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _listener = new TextBlock();
        private readonly TextBlock _drawing = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _payload = new TextBlock();
        private readonly TextBlock _run = new TextBlock();
        private readonly TextBlock _copied = new TextBlock { Foreground = Brushes.SeaGreen, Visibility = Visibility.Collapsed };
        private readonly TextBlock _log = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap };

        public PaletteView(HostApp app)
        {
            _app = app;
            var root = new StackPanel { Margin = new Thickness(10) };
            // AutoCAD palettes are dark; WPF defaults to black text. Paint the view explicitly.
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
            foreach (var tb in new[] { _port, _drawing, _payload, _run, _log }) tb.Foreground = Foreground;

            root.Children.Add(Header("Instance"));
            root.Children.Add(Row("Civil3D", new TextBlock { Text = HostApp.CivilVersion + "   pid " + app.State.Pid, Foreground = Foreground }));

            var portRow = new StackPanel { Orientation = Orientation.Horizontal };
            portRow.Children.Add(_port);
            var copy = new Button { Content = "Copy port", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(app.State.Port.ToString()); _copied.Text = "copied"; _copied.Visibility = Visibility.Visible; }
                catch (Exception ex) { _copied.Text = ex.Message; _copied.Visibility = Visibility.Visible; }
            };
            portRow.Children.Add(copy);
            portRow.Children.Add(_copied);
            _copied.Margin = new Thickness(8, 0, 0, 0);
            _copied.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(Row("Port", portRow));
            root.Children.Add(Row("Listener", _listener));
            root.Children.Add(Row("Drawing", _drawing));

            root.Children.Add(Header("Payload"));
            root.Children.Add(_payload);

            root.Children.Add(Header("Run"));
            root.Children.Add(_run);

            root.Children.Add(Header("Host log"));
            root.Children.Add(new ScrollViewer { Content = _log, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var hint = new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Text = "Tell the agent this port. Data folder: " + C3dPaths.Root,
            };
            root.Children.Add(hint);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            HostLog.LineWritten += _ => { try { Dispatcher.BeginInvoke(new Action(RefreshLog)); } catch { } };
            Refresh();
        }

        public void Refresh()
        {
            var st = _app.State;
            _port.Text = st.Port > 0 ? st.Port.ToString() : "—";
            _listener.Text = st.ListenerState + (st.ListenerError != null ? "  " + st.ListenerError : "");
            _listener.Foreground = st.ListenerState == "listening" ? Brushes.SeaGreen : Brushes.IndianRed;
            _drawing.Text = st.Drawing ?? "(no drawing)";
            _payload.Text = st.PayloadName == null ? "none loaded" : st.PayloadName + "  " + st.PayloadVersion + "  at " + st.PayloadLoadedUtc?.ToLocalTime().ToString("HH:mm:ss");
            _run.Text = st.ActiveRunId == null ? "none" : st.ActiveRunId + "  " + st.ActiveRunState;
            RefreshLog();
        }

        private void RefreshLog() { _log.Text = string.Join(Environment.NewLine, HostLog.Tail(8)); }

        private static TextBlock Header(string text) =>
            new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 10, 0, 4), Foreground = Brushes.DimGray };

        private static Grid Row(string label, UIElement value)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(l, 0); Grid.SetColumn((FrameworkElement)value, 1);
            g.Children.Add(l); g.Children.Add(value);
            return g;
        }
    }
}
