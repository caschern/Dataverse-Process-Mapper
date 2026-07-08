using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DataverseProcessMapper.Models;
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
        private string _selectedId;
        private HashSet<string> _viewParents = new HashSet<string>();

        /// <summary>Raised when the user clicks a node (null when the selection is cleared).</summary>
        public event Action<ProcessNode> NodeSelected;

        /// <summary>Raised when the user enters or leaves full-screen mode.</summary>
        public event Action<bool> FullScreenChanged;

        /// <summary>True while the process list is hidden to give the map the full width.</summary>
        public bool IsFullScreen { get; private set; }

        public DiagramPanel()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = Color.FromArgb(245, 246, 248);
            SetStyle(ControlStyles.Selectable, true); // needed for Esc to exit full screen
            TabStop = true;
        }

        public void ToggleFullScreen()
        {
            IsFullScreen = !IsFullScreen;
            FullScreenChanged?.Invoke(IsFullScreen);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && IsFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
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
            _selectedId = null;
            NodeSelected?.Invoke(null);
            RebuildViewCaches();
            AutoScrollPosition = new Point(0, 0);
            if (_autoFit) FitCore();
            else UpdateScrollSize();
            Invalidate();
        }

        private void RebuildViewCaches()
        {
            _viewParents = new HashSet<string>();
            if (_map?.ViewGraph == null) return;
            foreach (var n in _map.ViewGraph.Nodes)
                if (n.ParentId != null) _viewParents.Add(n.ParentId);
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
                DiagramRenderer.Render(surface, _map.ViewGraph, _map.CanvasSize,
                    interactive: true, highlightId: _selectedId);

            // Selection highlight, drawn in diagram coordinates on top of everything.
            if (_selectedId != null)
            {
                var selected = _map.ViewGraph[_selectedId];
                if (selected != null)
                {
                    var r = selected.Bounds;
                    r.Inflate(3f, 3f);
                    using (var pen = new Pen(Color.FromArgb(37, 118, 220), 2f))
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left || _map == null) return;

            // Expand/collapse glyphs take priority over node selection.
            var toggle = HitToggle(ToDiagram(e.Location));
            if (toggle != null)
            {
                ToggleCollapse(toggle);
                return;
            }

            var node = HitTest(e.Location);
            _selectedId = node?.Id;
            Invalidate();
            NodeSelected?.Invoke(node);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_map != null)
            {
                var pt = ToDiagram(e.Location);
                Cursor = HitToggle(pt) != null || HitTest(e.Location) != null
                    ? Cursors.Hand : Cursors.Default;
            }
        }

        private void ToggleCollapse(ProcessNode container)
        {
            container.Collapsed = !container.Collapsed;
            ProcessMapBuilder.RefreshView(_map);
            RebuildViewCaches();

            // The selected node may have been hidden by the collapse.
            if (_selectedId != null && _map.ViewGraph[_selectedId] == null)
            {
                _selectedId = null;
                NodeSelected?.Invoke(null);
            }

            if (_autoFit) FitCore();
            else UpdateScrollSize();
            Invalidate();
        }

        private PointF ToDiagram(Point client)
        {
            if (_zoom <= 0f) return PointF.Empty;
            return new PointF(
                (client.X - AutoScrollPosition.X) / _zoom,
                (client.Y - AutoScrollPosition.Y) / _zoom);
        }

        /// <summary>The container whose [+]/[-] glyph is under the point, if any.</summary>
        private ProcessNode HitToggle(PointF pt)
        {
            if (_map?.ViewGraph == null) return null;
            foreach (var n in _map.ViewGraph.Nodes)
            {
                if (n.HiddenCount == 0 && !_viewParents.Contains(n.Id)) continue;
                if (DiagramRenderer.ToggleRect(n).Contains(pt)) return n;
            }
            return null;
        }

        /// <summary>Maps a client point back through scroll + zoom to diagram space.</summary>
        private ProcessNode HitTest(Point client)
        {
            if (_map == null || _zoom <= 0f) return null;
            var pt = ToDiagram(client);

            // Topmost node wins (nodes are drawn in list order).
            for (int i = _map.ViewGraph.Nodes.Count - 1; i >= 0; i--)
            {
                var n = _map.ViewGraph.Nodes[i];
                if (n.Bounds.Contains(pt.X, pt.Y)) return n;
            }
            return null;
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
