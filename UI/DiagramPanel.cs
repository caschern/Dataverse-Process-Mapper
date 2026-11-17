using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Rendering;

namespace DataverseProcessMapper.UI
{
    /// <summary>
    /// A scrollable, zoomable panel that previews a <see cref="ProcessMap"/>
    /// using the GDI+ surface. Ctrl+MouseWheel zooms.
    /// </summary>
    public partial class DiagramPanel : Panel
    {
        // How the zoom follows the panel's size: re-fit on resize until the user zooms manually.
        private enum FitMode { None, Width, Whole }

        private ProcessMap _map;
        private float _zoom = 1f;
        private FitMode _fit = FitMode.Width;
        private bool _fitting;          // guards against resize/scrollbar feedback loops
        private int _zoomRaised;        // ZoomChanged count, so commands can raise it at least once
        private string _selectedId;
        private HashSet<string> _viewParents = new HashSet<string>();

        // Find: matches are node ids of the full graph, so they survive collapse,
        // expand and re-layout; only a new map (or a new search) replaces them.
        private List<string> _matches = new List<string>();
        private int _matchIndex = -1;
        private string _findTerm;
        private bool _findDetails;
        private HashSet<string> _matchOutline = new HashSet<string>();

        // Reading order of the full graph, per map and layout mode (laying out a
        // large map again on every keystroke of an incremental search is too slow).
        private Dictionary<string, int> _readingOrder;
        private ProcessMap _readingOrderMap;
        private LayoutMode _readingOrderMode;

        /// <summary>Raised when the user clicks a node (null when the selection is cleared).</summary>
        public event Action<ProcessNode> NodeSelected;

        /// <summary>Raised when the user enters or leaves full-screen mode.</summary>
        public event Action<bool> FullScreenChanged;

        /// <summary>Raised after <see cref="SetMap"/>, including when the map is cleared.</summary>
        public event Action MapChanged;

        /// <summary>Raised on any zoom change: buttons, Ctrl+wheel, fit, actual size, a new map.</summary>
        public event Action ZoomChanged;

        /// <summary>Raised when the find match set or the current match changes.</summary>
        public event Action FindChanged;

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

        // Low enough that Zoom to Fit can fit very wide maps (thousands of
        // pixels across); readable text is not the point at that
        // scale, seeing the whole shape is.
        private const float MinZoom = 0.05f;
        private const float MaxZoom = 3f;

        /// <summary>The current zoom; 1 = 100 %.</summary>
        public float Zoom
        {
            get => _zoom;
            private set
            {
                float zoom = value < MinZoom ? MinZoom : (value > MaxZoom ? MaxZoom : value);
                bool changed = zoom != _zoom;
                _zoom = zoom;
                UpdateScrollSize();
                Invalidate();
                if (changed) RaiseZoomChanged();
            }
        }

        private void RaiseZoomChanged()
        {
            _zoomRaised++;
            ZoomChanged?.Invoke();
        }

        // A fit or a new map that happens to land on the same zoom still tells
        // listeners, so a label can never be left showing a stale value.
        private void EnsureZoomRaised(int before)
        {
            if (_zoomRaised == before) RaiseZoomChanged();
        }

        public void SetMap(ProcessMap map)
        {
            int before = _zoomRaised;
            _map = map;
            _selectedId = null;
            bool hadFind = ClearFindState();
            _readingOrder = null;
            _readingOrderMap = null;
            NodeSelected?.Invoke(null);
            RebuildViewCaches();
            AutoScrollPosition = new Point(0, 0);
            if (_fit != FitMode.None) FitCore();
            else UpdateScrollSize();
            Invalidate();
            EnsureZoomRaised(before);
            // Only once the new map is fully in place: a listener that searches
            // again from here must not have its result undone by the rest of SetMap.
            if (hadFind) FindChanged?.Invoke();
            MapChanged?.Invoke();
        }

        private void RebuildViewCaches()
        {
            _viewParents = new HashSet<string>();
            if (_map?.ViewGraph == null) return;
            foreach (var n in _map.ViewGraph.Nodes)
                if (n.ParentId != null) _viewParents.Add(n.ParentId);
        }

        public ProcessMap Map => _map;

        /// <summary>Fits the map's width (the panel scrolls vertically for the rest), never above 100 %.</summary>
        public void ZoomToFit() => FitAndKeep(FitMode.Width);

        /// <summary>Fits the whole map, width and height, never above 100 %.</summary>
        public void FitWholeMap() => FitAndKeep(FitMode.Whole);

        private void FitAndKeep(FitMode mode)
        {
            int before = _zoomRaised;
            _fit = mode;
            FitCore();
            EnsureZoomRaised(before);
        }

        /// <summary>100 %, keeping the centre of the view where it is.</summary>
        public void ActualSize()
        {
            int before = _zoomRaised;
            ZoomTo(1f, new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            EnsureZoomRaised(before);
        }

        private void FitCore()
        {
            if (_map == null || _map.CanvasSize.Width < 1) { Zoom = 1f; return; }
            if (ClientSize.Width < 40 || ClientSize.Height < 40) return;

            _fitting = true;
            try
            {
                float zoom = (ClientSize.Width - 20) / _map.CanvasSize.Width;
                // The whole map may zoom out below the width fit, but neither fit
                // enlarges a small map past its natural size.
                if (_fit == FitMode.Whole && _map.CanvasSize.Height >= 1)
                    zoom = Math.Min(zoom, (ClientSize.Height - 20) / _map.CanvasSize.Height);
                Zoom = Math.Min(zoom, 1f);
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
            if (_fit != FitMode.None && !_fitting && _map != null)
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

            // Only the invalidated area needs drawing — while panning that is the
            // strip scrolled into view, not the whole map.
            var clip = e.ClipRectangle;
            var visible = new RectangleF(
                (clip.X - AutoScrollPosition.X) / _zoom,
                (clip.Y - AutoScrollPosition.Y) / _zoom,
                clip.Width / _zoom,
                clip.Height / _zoom);
            visible.Inflate(2f, 2f);

            using (var surface = new GdiDiagramSurface(g))
                DiagramRenderer.Render(surface, _map.ViewGraph, _map.CanvasSize,
                    interactive: true, highlightId: _selectedId, visible: visible, matchIds: _matchOutline);

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

        // Drag-to-pan state: a press starts a potential pan; selection happens
        // on release only when the mouse never actually moved.
        private bool _mouseDown;
        private bool _panned;
        private Point _dragStartClient;
        private Point _dragStartScroll;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left || _map == null) return;

            // Expand/collapse glyphs act immediately.
            var toggle = HitToggle(ToDiagram(e.Location));
            if (toggle != null)
            {
                ToggleCollapse(toggle);
                return;
            }

            _mouseDown = true;
            _panned = false;
            _dragStartClient = e.Location;
            _dragStartScroll = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_map == null) return;

            if (_mouseDown)
            {
                int dx = e.X - _dragStartClient.X;
                int dy = e.Y - _dragStartClient.Y;
                if (!_panned && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
                {
                    _panned = true;
                    Cursor = Cursors.SizeAll;
                }
                if (_panned)
                    AutoScrollPosition = new Point(_dragStartScroll.X - dx, _dragStartScroll.Y - dy);
                return;
            }

            var pt = ToDiagram(e.Location);
            Cursor = HitToggle(pt) != null || HitTest(e.Location) != null
                ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_mouseDown) return;

            _mouseDown = false;
            if (_panned)
            {
                _panned = false;
                Cursor = Cursors.Default;
                return; // it was a pan, not a click
            }

            var node = HitTest(e.Location);
            _selectedId = node?.Id;

            // A clicked match becomes the current one, so "n of m" follows the click.
            int match = node != null ? _matches.IndexOf(node.Id) : -1;
            bool matchMoved = match >= 0 && match != _matchIndex;
            if (matchMoved) _matchIndex = match;

            Invalidate();
            NodeSelected?.Invoke(node);
            if (matchMoved) FindChanged?.Invoke();
        }

        private void ToggleCollapse(ProcessNode container)
        {
            container.Collapsed = !container.Collapsed;
            RefreshStructure();
        }

        /// <summary>Re-lays out the current map flat or as nested blocks.</summary>
        public void SetLayoutMode(LayoutMode mode)
        {
            if (_map == null || _map.Mode == mode) return;
            _map.Mode = mode;
            RefreshStructure();
        }

        /// <summary>Collapses (true) or expands (false) every container at once.</summary>
        public void SetAllCollapsed(bool collapsed)
        {
            if (_map == null) return;

            var containers = new HashSet<string>();
            foreach (var n in _map.Graph.Nodes)
                if (n.ParentId != null) containers.Add(n.ParentId);

            bool changed = false;
            foreach (var n in _map.Graph.Nodes)
            {
                if (!containers.Contains(n.Id) || n.Collapsed == collapsed) continue;
                n.Collapsed = collapsed;
                changed = true;
            }
            if (changed) RefreshStructure();
        }

        /// <summary>Re-derives the view after collapse state changes and re-fits.</summary>
        private void RefreshStructure()
        {
            ProcessMapBuilder.RefreshView(_map);
            RebuildViewCaches();
            UpdateMatchOutline();

            // The selected node may have been hidden by the change.
            if (_selectedId != null && _map.ViewGraph[_selectedId] == null)
            {
                _selectedId = null;
                NodeSelected?.Invoke(null);
            }

            if (_fit != FitMode.None) FitCore();
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
                if (DiagramRenderer.ShapeContains(n, pt)) return n;
            }
            return null;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                ZoomAt(e.Delta > 0 ? 1.1f : 1f / 1.1f, e.Location);
                ((HandledMouseEventArgs)e).Handled = true;
            }
            else
            {
                base.OnMouseWheel(e);
            }
        }

        /// <summary>Zooms by a factor keeping the given client point stationary.</summary>
        private void ZoomAt(float factor, Point clientAnchor) => ZoomTo(_zoom * factor, clientAnchor);

        /// <summary>Zooms to a value keeping the given client point stationary.</summary>
        private void ZoomTo(float zoom, Point clientAnchor)
        {
            _fit = FitMode.None; // manual zoom takes over until the next fit
            var anchor = ToDiagram(clientAnchor);
            Zoom = zoom;
            AutoScrollPosition = new Point(
                (int)(anchor.X * _zoom - clientAnchor.X),
                (int)(anchor.Y * _zoom - clientAnchor.Y));
            Invalidate();
        }

        /// <summary>Zoom in/out anchored at the viewport center (for the +/- buttons).</summary>
        public void ZoomStep(float factor)
            => ZoomAt(factor, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

        // ---------- find ----------

        /// <summary>How many nodes the current search matched (0 when there is none).</summary>
        public int MatchCount => _matches.Count;

        /// <summary>The current match's position in <see cref="MatchCount"/>; -1 when none.</summary>
        public int MatchIndex => _matchIndex;

        /// <summary>
        /// Searches every step of the map, hidden ones included, case-insensitively
        /// on label and subtitle (and, with <paramref name="includeDetails"/>, on
        /// detail names and values). Matches are ordered as read: top to bottom,
        /// then left to right. The first is selected, revealed and centred.
        /// A null or empty term clears the search. Returns the match count.
        /// </summary>
        public int Find(string term, bool includeDetails)
        {
            if (_map == null || string.IsNullOrWhiteSpace(term))
            {
                ClearFind();
                return 0;
            }
            term = term.Trim();

            var order = ReadingOrder();
            _matches = _map.Graph.Nodes
                .Where(n => IsMatch(n, term, includeDetails))
                .OrderBy(n => order.TryGetValue(n.Id, out var rank) ? rank : int.MaxValue)
                .Select(n => n.Id)
                .ToList();
            _findTerm = term;
            _findDetails = includeDetails;
            _matchIndex = _matches.Count > 0 ? 0 : -1;

            if (_matchIndex >= 0) RevealAndSelect(_matches[0]);
            UpdateMatchOutline();
            Invalidate();
            FindChanged?.Invoke();
            return _matches.Count;
        }

        /// <summary>Moves to the next match, wrapping; false when nothing matches.</summary>
        public bool FindNext() => StepMatch(1);

        /// <summary>Moves to the previous match, wrapping; false when nothing matches.</summary>
        public bool FindPrevious() => StepMatch(-1);

        /// <summary>
        /// Selects and centers the next node whose label contains the term,
        /// cycling through matches and expanding collapsed ancestors as needed.
        /// Returns false when nothing matches. Kept for the Enter-to-find box:
        /// the same term again moves on, a new term starts a new search.
        /// </summary>
        public bool FindNext(string term)
        {
            if (_map == null || string.IsNullOrWhiteSpace(term)) return false;
            if (_matches.Count > 0 && !_findDetails &&
                string.Equals(_findTerm, term.Trim(), StringComparison.OrdinalIgnoreCase))
                return FindNext();
            return Find(term, false) > 0;
        }

        /// <summary>Forgets the search and its highlights; the selection stays.</summary>
        public void ClearFind()
        {
            if (!ClearFindState()) return;
            Invalidate();
            FindChanged?.Invoke();
        }

        /// <summary>Resets the search without raising anything; true when there was one.</summary>
        private bool ClearFindState()
        {
            bool had = _findTerm != null || _matches.Count > 0;
            _matches = new List<string>();
            _matchIndex = -1;
            _findTerm = null;
            _findDetails = false;
            _matchOutline = new HashSet<string>();
            return had;
        }

        private bool StepMatch(int direction)
        {
            if (_map == null || _matches.Count == 0) return false;

            // A match the user clicked on is where the next step starts from.
            int current = _selectedId != null ? _matches.IndexOf(_selectedId) : -1;
            if (current < 0) current = _matchIndex;

            int count = _matches.Count;
            _matchIndex = current < 0
                ? (direction > 0 ? 0 : count - 1)
                : ((current + direction) % count + count) % count;

            RevealAndSelect(_matches[_matchIndex]);
            Invalidate();
            FindChanged?.Invoke();
            return true;
        }

        private static bool IsMatch(ProcessNode node, string term, bool includeDetails)
        {
            if (Contains(node.Label, term) || Contains(node.Subtitle, term)) return true;
            if (!includeDetails || node.Details == null) return false;
            foreach (var d in node.Details)
                if (Contains(d.Key, term) || Contains(d.Value, term)) return true;
            return false;
        }

        private static bool Contains(string text, string term)
            => text != null && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Selects and centres a node of the full graph, expanding its collapsed
        /// ancestors first when it is hidden. False when it cannot be shown.
        /// </summary>
        private bool RevealAndSelect(string id)
        {
            var target = _map.Graph[id];
            if (target == null) return false;

            bool expanded = false;
            var cur = target.ParentId;
            int guard = 0;
            while (cur != null && guard++ < _map.Graph.Nodes.Count)
            {
                var parent = _map.Graph[cur];
                if (parent == null) break;
                if (parent.Collapsed) { parent.Collapsed = false; expanded = true; }
                cur = parent.ParentId;
            }
            if (expanded) RefreshStructure();

            var visible = _map.ViewGraph[id];
            if (visible == null) return false;

            _selectedId = visible.Id;
            NodeSelected?.Invoke(visible);
            CenterOn(visible);
            Invalidate();
            return true;
        }

        /// <summary>
        /// The boxes drawn with the match outline: each match itself when shown,
        /// otherwise the collapsed container hiding it, so a hidden match is
        /// still visible as "somewhere in here".
        /// </summary>
        private void UpdateMatchOutline()
        {
            var outline = new HashSet<string>();
            if (_map != null)
            {
                foreach (var id in _matches)
                {
                    string shown = id;
                    var cur = _map.Graph[id]?.ParentId;
                    int guard = 0;
                    while (cur != null && guard++ < _map.Graph.Nodes.Count)
                    {
                        var parent = _map.Graph[cur];
                        if (parent == null) break;
                        if (parent.Collapsed) shown = parent.Id; // the outermost collapse wins
                        cur = parent.ParentId;
                    }
                    if (_map.ViewGraph?[shown] != null) outline.Add(shown);
                }
            }
            _matchOutline = outline;
        }

        /// <summary>
        /// Each node's position in reading order (rows top to bottom, left to
        /// right within a row) on the fully expanded map, so the order does not
        /// depend on what happens to be collapsed.
        /// </summary>
        private Dictionary<string, int> ReadingOrder()
        {
            if (_readingOrder != null && _readingOrderMap == _map && _readingOrderMode == _map.Mode)
                return _readingOrder;

            var laidOut = _map.ViewGraph != null && _map.ViewGraph.Nodes.Count == _map.Graph.Nodes.Count
                ? _map.ViewGraph
                : ExpandedLayout(_map);

            // Rows are runs of vertically overlapping boxes: a row's boxes may be
            // top-aligned or centred, and either way they overlap, while the next
            // row starts below a gap.
            var nodes = laidOut.Nodes.OrderBy(n => n.Bounds.Top).ThenBy(n => n.Bounds.Left).ToList();
            var order = new Dictionary<string, int>();
            int i = 0;
            while (i < nodes.Count)
            {
                float bottom = nodes[i].Bounds.Bottom;
                int j = i + 1;
                while (j < nodes.Count && nodes[j].Bounds.Top < bottom)
                {
                    bottom = Math.Max(bottom, nodes[j].Bounds.Bottom);
                    j++;
                }
                foreach (var n in nodes.GetRange(i, j - i).OrderBy(n => n.Bounds.Left).ThenBy(n => n.Bounds.Top))
                    order[n.Id] = order.Count;
                i = j;
            }

            _readingOrder = order;
            _readingOrderMap = _map;
            _readingOrderMode = _map.Mode;
            return order;
        }

        /// <summary>
        /// Lays out a copy of the map with everything expanded. A copy, because the
        /// on-screen nodes are shared with the view and must keep their positions.
        /// </summary>
        private static ProcessGraph ExpandedLayout(ProcessMap map)
        {
            var full = map.Graph;
            var copy = new ProcessGraph { Title = full.Title, Subtitle = full.Subtitle };
            foreach (var n in full.Nodes)
                copy.AddExisting(new ProcessNode
                {
                    Id = n.Id,
                    Label = n.Label,
                    Subtitle = n.Subtitle,
                    Kind = n.Kind,
                    Shape = n.Shape,
                    ParentId = n.ParentId,
                    Details = n.Details
                });
            foreach (var e in full.Edges)
                copy.AddEdge(e.FromId, e.ToId, e.Label, e.Dashed);

            var expanded = new ProcessMap { Graph = copy, Source = map.Source, Mode = map.Mode };
            ProcessMapBuilder.RefreshView(expanded);
            return expanded.ViewGraph;
        }

        private void CenterOn(ProcessNode node)
        {
            float cx = (node.Bounds.X + node.Bounds.Width / 2f) * _zoom;
            float cy = (node.Bounds.Y + node.Bounds.Height / 2f) * _zoom;
            AutoScrollPosition = new Point(
                (int)(cx - ClientSize.Width / 2f),
                (int)(cy - ClientSize.Height / 2f));
        }
    }
}
