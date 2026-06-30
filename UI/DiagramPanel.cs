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
            UpdateScrollSize();
            Invalidate();
        }

        public ProcessMap Map => _map;

        public void ZoomToFit()
        {
            if (_map == null || _map.CanvasSize.Width < 1) { Zoom = 1f; return; }
            float fx = (ClientSize.Width - 20) / _map.CanvasSize.Width;
            float fy = (ClientSize.Height - 20) / _map.CanvasSize.Height;
            Zoom = System.Math.Min(System.Math.Min(fx, fy), 1f);
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
