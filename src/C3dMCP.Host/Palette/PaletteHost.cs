using System;
using Autodesk.AutoCAD.Windows;

namespace C3dMCP.Host.Palette
{
    /// <summary>Owns the AutoCAD PaletteSet that hosts the WPF view. Phase 1a shows the Instance
    /// section only; the full palette follows the reviewed mockups in Phase 2.</summary>
    internal sealed class PaletteHost
    {
        private static readonly Guid PaletteId = new Guid("A1C3D9E4-7B2F-4C61-9E0A-0C3D0000A001");
        private readonly PaletteSet _set;
        private readonly PaletteView _view;

        public PaletteHost(HostApp app)
        {
            _view = new PaletteView(app);
            _set = new PaletteSet("C3dMCP", PaletteId)
            {
                Style = PaletteSetStyles.ShowPropertiesMenu | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton,
                MinimumSize = new System.Drawing.Size(340, 300),
                Size = new System.Drawing.Size(420, 900),
                KeepFocus = false,
            };
            _set.AddVisual("C3dMCP", _view);
        }

        public void Show() { _set.Visible = true; Refresh(); }

        public void Refresh() { try { _view.Dispatcher.BeginInvoke(new Action(_view.Refresh)); } catch { } }

        public void SetEscalation(Routes.EscalationBody b)
        {
            try
            {
                _view.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _view.Model.Escalation = b == null ? null : new EscalationCard { Id = b.Id, Title = b.Title, Round1Reason = b.Round1Reason, Round1Checks = b.Round1Checks, Round2Reason = b.Round2Reason, Round2Checks = b.Round2Checks };
                    if (b != null) _set.Visible = true;
                }));
            }
            catch { }
        }
    }
}
