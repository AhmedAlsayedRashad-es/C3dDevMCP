using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using C3dMCP.Engine;

namespace C3dMCP.Host.Palette
{
    /// <summary>The palette view. All host calls that touch Civil3D are posted off the UI thread
    /// through the HTTP-side services (they marshal onto the main thread themselves).</summary>
    public partial class PaletteView : UserControl
    {
        private readonly HostApp _app;
        public PaletteViewModel Model { get; }

        public PaletteView(HostApp app)
        {
            _app = app;
            Model = new PaletteViewModel(app);
            Resources["ChipToggle"] = BuildChipStyle();
            InitializeComponent();
            DataContext = Model;
            Model.Log.CollectionChanged += (s, e) =>
            {
                if (Model.AutoScroll && LogList.Items.Count > 0)
                    try { LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]); } catch { }
            };
            HostLog.LineWritten += _ => { try { Dispatcher.BeginInvoke(new Action(() => Model.HostLog = string.Join(Environment.NewLine, HostLog.Tail(3)))); } catch { } };
            Refresh();
        }

        public int Pid => _app.State.Pid;

        public void Refresh()
        {
            try { Model.Refresh(); } catch (Exception ex) { HostLog.Write("palette refresh failed: " + ex.Message); }
        }

        private Style BuildChipStyle()
        {
            var s = new Style(typeof(ToggleButton));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 1, 6, 1)));
            s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0)));
            s.Setters.Add(new Setter(Control.FontSizeProperty, 10.0));
            s.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Consolas")));
            s.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A))));
            s.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))));
            s.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C))));
            var t = new ControlTemplate(typeof(ToggleButton));
            var border = new FrameworkElementFactory(typeof(Border), "B");
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(cp);
            t.VisualTree = border;
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x36, 0xC2, 0xB4)), "B"));
            on.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x36, 0xC2, 0xB4))));
            t.Triggers.Add(on);
            s.Setters.Add(new Setter(Control.TemplateProperty, t));
            return s;
        }

        // ---- handlers ----------------------------------------------------------------------

        private void CopyPort_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_app.State.Port.ToString()); Model.Copied = "copied " + _app.State.Port; }
            catch (Exception ex) { Model.Copied = ex.Message; }
        }

        private void ReloadLast_Click(object sender, RoutedEventArgs e)
        {
            var dll = _app.State.LastReloadDll; var name = _app.State.PayloadName;
            if (dll == null) return;
            ThreadPool.QueueUserWorkItem(_ => { try { _app.Reload.Reload(dll, name); } catch (Exception ex) { HostLog.Write("reload last: " + ex.Message); } });
        }

        private void Finish_Click(object sender, RoutedEventArgs e) { FinishChoice.Visibility = Visibility.Visible; }
        private void FinishCancel_Click(object sender, RoutedEventArgs e) { FinishChoice.Visibility = Visibility.Collapsed; }

        private void FinishAs_Click(object sender, RoutedEventArgs e)
        {
            FinishChoice.Visibility = Visibility.Collapsed;
            var status = (sender as FrameworkElement)?.Tag as string ?? "completed";
            var rec = _app.Runs.Active;
            if (rec == null || rec.Life.State != RunState.Draining) return;
            _app.Runs.Complete(rec, status, "button", "marked finished by the user in the palette");
        }

        private void IdlePing_Click(object sender, RoutedEventArgs e)
        {
            var rec = _app.Runs.Active;
            if (rec?.Pinger != null && rec.Life.State == RunState.Draining) rec.Pinger.ProbeNow();
            else HostLog.Write("idle ping: no draining run");
        }

        private void Baseline_Click(object sender, RoutedEventArgs e)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _app.Civil.Baseline(); _app.State.BaselineValid = true; _app.State.BaselineMarkedUtc = DateTime.UtcNow; HostLog.Write("baseline marked (palette)"); _app.Registry?.Write(); }
                catch (Exception ex) { HostLog.Write("baseline: " + ex.Message); }
            });
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            bool hard = ((sender as FrameworkElement)?.Tag as string) == "hard";
            if (!hard && !_app.State.BaselineValid) { HostLog.Write("reset: no baseline mark in this session"); return; }
            ThreadPool.QueueUserWorkItem(_ => { try { _app.Civil.Reset(hard); HostLog.Write("reset " + (hard ? "hard" : "soft") + " (palette)"); } catch (Exception ex) { HostLog.Write("reset: " + ex.Message); } });
        }

        private void SrcChip_Click(object sender, RoutedEventArgs e)
        {
            var tb = sender as ToggleButton; if (tb == null) return;
            var src = tb.Tag as string;
            if (tb.IsChecked == true) Model.SrcOff.Remove(src); else Model.SrcOff.Add(src);
            Model.RefilterLog();
        }

        private void OpenFolder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", C3dPaths.Root); } catch { }
        }
    }

    // ---- converters ----------------------------------------------------------------------

    public sealed class StateBrushConverter : IValueConverter
    {
        public bool Background { get; set; }
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            string s = (value as string ?? "").ToLowerInvariant();
            string fg, bg;
            switch (s)
            {
                case "completed": fg = "#63BD73"; bg = "#203828"; break;
                case "failed": fg = "#E16C69"; bg = "#472625"; break;
                case "draining": fg = "#D8A347"; bg = "#46371D"; break;
                case "running": case "invoking": fg = "#36C2B4"; bg = "#173B38"; break;
                default: fg = "#9A9A9A"; bg = "#333333"; break;
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Background ? bg : fg));
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class SrcBrushConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            switch (value as string)
            {
                case "host": return new SolidColorBrush(Color.FromRgb(0x36, 0xC2, 0xB4));
                case "diag": return new SolidColorBrush(Color.FromRgb(0x63, 0xBD, 0x73));
                case "note": return new SolidColorBrush(Color.FromRgb(0xD8, 0xA3, 0x47));
                case "feedback": return new SolidColorBrush(Color.FromRgb(0xB0, 0x8C, 0xE0));
                case "cmd": return new SolidColorBrush(Color.FromRgb(0x6F, 0xB3, 0xE8));
                default: return new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
            }
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class BoolToBrushConverter : IValueConverter
    {
        public string True { get; set; } = "#63BD73";
        public string False { get; set; } = "#E16C69";
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(value is bool b && b ? True : False));
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class StreakConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            int streak = value is int i ? i : 0; int slot = int.Parse((string)p);
            return new SolidColorBrush(streak >= slot ? Color.FromRgb(0x36, 0xC2, 0xB4) : Color.FromRgb(0x3C, 0x3C, 0x3C));
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public sealed class UpperConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => (value as string ?? "").ToUpperInvariant();
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
