using System.Drawing;
using System.Windows.Forms;
using DataverseProcessMapper.Rendering;

namespace DataverseProcessMapper.UI
{
    /// <summary>
    /// A scrollable, zoomable panel that previews a <see cref="ProcessMap"/>
    /// using the GDI+ surface. Ctrl+MouseWheel zooms.
    /// </summary>
    public class DiagramPanel : Panel
    {
        private ProcessMap _map;
        private float _zoom = 1f;
        private bool _autoFit = true;   // re-fit on resize until the user zooms manually
        private bool _fitting;          // guards against resize/scrollbar feedback loops

        public DiagramPanel()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = Color.FromArgb(245, 246, 248);
        }

        public float Zoom
        {
            get => _zoom;
            set
            {
                _zoom = value < 0.2f ? 0.2f : (value > 3f ? 3f : value);
                UpdateScrollSize();
                Invalidate();
            }
        }

        public void SetMap(ProcessMap map)
        {
            _map = map;
            AutoScrollPosition = new Point(0, 0);
            if (_autoFit) FitCore();
            else UpdateScrollSize();
            Invalidate();
        }

        public ProcessMap Map => _map;

        public void ZoomToFit()
        {
            _autoFit = true;
            FitCore();
        }

        private void FitCore()
        {
            if (_map == null || _map.CanvasSize.Width < 1) { Zoom = 1f; return; }
            if (ClientSize.Width < 40 || ClientSize.Height < 40) return;

            _fitting = true;
            try
            {
                // Fit to width; the panel scrolls vertically for the rest.
                float fx = (ClientSize.Width - 20) / _map.CanvasSize.Width;
                Zoom = System.Math.Min(fx, 1f);
            }
            finally
            {
                _fitting = false;
            }
        }

        protected override void OnResize(System.EventArgs e)
        {
            base.OnResize(e);
            // Keep the diagram fitted while the user drags the splitter or
            // resizes the window, unless they have zoomed manually.
            if (_autoFit && !_fitting && _map != null)
                FitCore();
        }

        private void UpdateScrollSize()
        {
            if (_map == null) { AutoScrollMinSize = Size.Empty; return; }
            AutoScrollMinSize = new Size(
                (int)(_map.CanvasSize.Width * _zoom) + 20,
                (int)(_map.CanvasSize.Height * _zoom) + 20);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_map == null)
            {
                TextRenderer.DrawText(e.Graphics,
                    "Select a process from the list to preview its map.",
                    Font, ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var g = e.Graphics;
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            g.ScaleTransform(_zoom, _zoom);

            // White canvas behind the diagram.
            g.FillRectangle(Brushes.White, 0, 0, _map.CanvasSize.Width, _map.CanvasSize.Height);

            using (var surface = new GdiDiagramSurface(g))
                DiagramRenderer.Render(surface, _map.Graph, _map.CanvasSize);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                _autoFit = false; // manual zoom takes over until the next Zoom to Fit
                Zoom += e.Delta > 0 ? 0.1f : -0.1f;
                ((HandledMouseEventArgs)e).Handled = true;
            }
            else
            {
                base.OnMouseWheel(e);
            }
        }
    }
}
