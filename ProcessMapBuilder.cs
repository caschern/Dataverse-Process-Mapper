using System.Drawing;
using System.Linq;
using DataverseProcessMapper.Layout;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Parsing;
using DataverseProcessMapper.Rendering;

namespace DataverseProcessMapper
{
    /// <summary>A laid-out, ready-to-render process map.</summary>
    public class ProcessMap
    {
        /// <summary>The full parsed graph (details panel, HTML step tree).</summary>
        public ProcessGraph Graph { get; set; }

        /// <summary>The contracted graph currently on screen (collapsed containers hide their subtrees).</summary>
        public ProcessGraph ViewGraph { get; set; }

        public SizeF CanvasSize { get; set; }
        public ProcessItem Source { get; set; }

        /// <summary>How the map is positioned; applied on every <see cref="ProcessMapBuilder.RefreshView"/>.</summary>
        public LayoutMode Mode { get; set; } = ProcessMapBuilder.DefaultMode;
    }

    /// <summary>
    /// Turns a <see cref="ProcessItem"/> into a fully measured and laid-out
    /// <see cref="ProcessMap"/> by choosing the right parser for its category.
    /// </summary>
    public static class ProcessMapBuilder
    {
        private static readonly FlowJsonParser FlowParser = new FlowJsonParser();
        private static readonly XamlWorkflowParser WorkflowParser = new XamlWorkflowParser();

        /// <summary>
        /// Nested blocks: the step after a container is drawn below the
        /// container's frame, so the picture never shows it running alongside the
        /// container's own steps. The flat layout remains available.
        /// </summary>
        public const LayoutMode DefaultMode = LayoutMode.Nested;

        public static ProcessMap Build(ProcessItem item, LayoutMode mode = DefaultMode)
        {
            IProcessParser parser = item.IsModernFlow ? (IProcessParser)FlowParser : WorkflowParser;
            var graph = parser.Parse(item);

            var map = new ProcessMap { Graph = graph, Source = item, Mode = mode };
            RefreshView(map);
            return map;
        }

        /// <summary>At most this many boxes are visible when a large map first opens.</summary>
        public const int OpeningBudget = 100;

        /// <summary>
        /// Collapses containers so a large map opens readable. Starting from
        /// everything collapsed, containers are expanded a whole level at a time
        /// while the visible boxes stay within <paramref name="budget"/>; a map
        /// that fits opens fully expanded. Only the on-screen view changes —
        /// exports always show every step (<see cref="Expanded"/>).
        /// </summary>
        public static void CollapseToFit(ProcessMap map, int budget = OpeningBudget)
        {
            var g = map.Graph;
            if (g.Nodes.Count <= budget)
            {
                // Everything fits: open fully expanded, whatever was collapsed before.
                if (!g.Nodes.Any(n => n.Collapsed)) return;
                foreach (var n in g.Nodes) n.Collapsed = false;
                RefreshView(map);
                return;
            }

            string Parent(ProcessNode n) => n.ParentId != null && g.Contains(n.ParentId) ? n.ParentId : null;
            var childCount = g.Nodes.Where(n => Parent(n) != null)
                .GroupBy(Parent)
                .ToDictionary(grp => grp.Key, grp => grp.Count());

            int Depth(ProcessNode n)
            {
                int d = 0;
                for (var p = Parent(n); p != null && d < g.Nodes.Count; p = Parent(g[p])) d++;
                return d;
            }

            var containers = g.Nodes.Where(n => childCount.ContainsKey(n.Id)).ToList();
            foreach (var c in containers) c.Collapsed = true;

            int visible = g.Nodes.Count(n => Parent(n) == null);
            var byLevel = containers.GroupBy(Depth).OrderBy(grp => grp.Key);
            foreach (var level in byLevel)
            {
                int revealed = level.Sum(c => childCount[c.Id]);
                if (visible + revealed > budget) break;
                foreach (var c in level) c.Collapsed = false;
                visible += revealed;
            }

            RefreshView(map);
        }

        /// <summary>
        /// The map with every container expanded, for exports: a reader cannot
        /// expand a PDF, so an export always shows every step, whatever is
        /// collapsed on screen. The on-screen map is left untouched — a fresh map
        /// is built from the same process rather than re-laying out this one,
        /// whose nodes the screen is drawing.
        /// </summary>
        public static ProcessMap Expanded(ProcessMap map)
        {
            if (map.Source == null || !map.Graph.Nodes.Any(n => n.Collapsed)) return map;
            return Build(map.Source, map.Mode);
        }

        /// <summary>
        /// Recomputes the visible graph from the nodes' Collapsed flags and lays
        /// it out again. Called after every expand/collapse toggle.
        /// </summary>
        public static void RefreshView(ProcessMap map)
        {
            var view = GraphContraction.BuildView(map.Graph);
            NodeSizer.MeasureAll(view);

            SizeF canvas;
            if (map.Mode == LayoutMode.Nested)
            {
                // Detects parallel regions block by block itself.
                canvas = NestedLayoutEngine.Layout(view);
            }
            else
            {
                // Detection is purely structural (edges and ParentId), so it runs
                // before layout: the ordering pass uses block membership to keep a
                // region's branches together within their rank.
                view.ParallelBlocks = ParallelBlockDetector.Detect(view);
                canvas = LayeredLayoutEngine.Layout(view);
            }
            canvas = EnsureTitleFits(view, canvas);

            map.ViewGraph = view;
            map.CanvasSize = canvas;
        }

        /// <summary>
        /// Widens the canvas (recentering the nodes) when the title band is wider
        /// than the diagram, so long process names don't clip in exports.
        /// </summary>
        private static SizeF EnsureTitleFits(ProcessGraph graph, SizeF canvas)
        {
            if (string.IsNullOrEmpty(graph.Title)) return canvas;

            var titleFont = DiagramStyle.TitleFont;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            using (var f = new Font(titleFont.Family, titleFont.Size,
                       titleFont.Bold ? FontStyle.Bold : FontStyle.Regular))
            {
                float needed = g.MeasureString(graph.Title, f, 100000,
                                   StringFormat.GenericTypographic).Width + 2 * DiagramStyle.Margin;
                if (needed <= canvas.Width) return canvas;

                float shift = (needed - canvas.Width) / 2f;
                foreach (var n in graph.Nodes)
                {
                    var b = n.Bounds;
                    b.X += shift;
                    n.Bounds = b;
                }
                // Rails and routed polylines are absolute X values — they move
                // with the nodes, or multi-rank edges are left behind.
                foreach (var e in graph.Edges)
                {
                    if (e.RailX.HasValue) e.RailX += shift;
                    if (e.Route != null)
                        for (int i = 0; i < e.Route.Count; i++)
                            e.Route[i] = new PointF(e.Route[i].X + shift, e.Route[i].Y);
                }
                foreach (var frame in graph.Frames)
                {
                    var b = frame.Bounds;
                    b.X += shift;
                    frame.Bounds = b;
                }
                return new SizeF(needed, canvas.Height);
            }
        }

        /// <summary>Renders the map to a GDI+ bitmap at the given scale.</summary>
        public static Bitmap RenderToBitmap(ProcessMap map, float scale = 1f)
        {
            int w = System.Math.Max(1, (int)System.Math.Ceiling(map.CanvasSize.Width * scale));
            int h = System.Math.Max(1, (int)System.Math.Ceiling(map.CanvasSize.Height * scale));

            // NOTE: no SetResolution here. Fonts scale with BOTH the bitmap DPI and
            // the world transform, so raising the DPI on top of ScaleTransform made
            // text render ~2x larger than the boxes measured for it. The transform
            // alone scales text and geometry together, matching NodeSizer's
            // default-resolution measurements.
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(DiagramStyle.CanvasBackground);
                g.ScaleTransform(scale, scale);
                using (var surface = new GdiDiagramSurface(g))
                    DiagramRenderer.Render(surface, map.ViewGraph, map.CanvasSize);
            }
            return bmp;
        }
    }
}
