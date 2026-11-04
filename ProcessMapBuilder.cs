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
            return RenderOnto(NewBitmap(map, scale), map, scale);
        }

        private static Bitmap NewBitmap(ProcessMap map, float scale)
        {
            int w = System.Math.Max(1, (int)System.Math.Ceiling(map.CanvasSize.Width * scale));
            int h = System.Math.Max(1, (int)System.Math.Ceiling(map.CanvasSize.Height * scale));

            // NOTE: no SetResolution here. Fonts scale with BOTH the bitmap DPI and
            // the world transform, so raising the DPI on top of ScaleTransform made
            // text render ~2x larger than the boxes measured for it. The transform
            // alone scales text and geometry together, matching NodeSizer's
            // default-resolution measurements.
            return new Bitmap(w, h);
        }

        private static Bitmap RenderOnto(Bitmap bmp, ProcessMap map, float scale)
        {
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(DiagramStyle.CanvasBackground);
                    g.ScaleTransform(scale, scale);
                    using (var surface = new GdiDiagramSurface(g))
                        DiagramRenderer.Render(surface, map.ViewGraph, map.CanvasSize);
                }
            }
            catch
            {
                bmp.Dispose(); // a large bitmap is a lot of memory to leave to the finalizer
                throw;
            }
            return bmp;
        }

        /// <summary>PNG exports render at this scale when the map is small enough.</summary>
        public const float PngScale = 2f;

        /// <summary>Below this scale a PNG is too blurry to read; SVG or PDF is the answer.</summary>
        public const float MinPngScale = 0.5f;

        /// <summary>GDI+'s PNG encoder cannot save a longer side ("A generic error occurred").</summary>
        public const int MaxPngSide = 65535;

        /// <summary>
        /// Most pixels a PNG export allocates (4 bytes each). Keeps very large
        /// maps at the full 2x.
        /// </summary>
        public const double PngPixelBudget = 350e6;

        /// <summary>
        /// The scale a PNG of a canvas this size is rendered at: <see cref="PngScale"/>,
        /// reduced as needed to keep both sides and the pixel count within what
        /// GDI+ can allocate and save. 0 when that would fall below
        /// <see cref="MinPngScale"/>, i.e. the map is too large for a PNG.
        /// </summary>
        public static float PngScaleFor(SizeF canvas)
        {
            double w = System.Math.Max(1.0, canvas.Width);
            double h = System.Math.Max(1.0, canvas.Height);
            // One pixel short of the limit: the bitmap's sides are rounded up.
            double scale = System.Math.Min(PngScale, System.Math.Min((MaxPngSide - 1) / w, (MaxPngSide - 1) / h));
            scale = System.Math.Min(scale, System.Math.Sqrt(PngPixelBudget / (w * h)));
            return scale < MinPngScale ? 0f : (float)scale;
        }

        /// <summary>
        /// Saves the map as a PNG at <see cref="PngScaleFor"/>. Returns false,
        /// writing no file, when the map is too large for a PNG image — too
        /// large to scale down legibly, or for GDI+ to allocate.
        /// </summary>
        public static bool SavePng(ProcessMap map, string path)
        {
            float scale = PngScaleFor(map.CanvasSize);
            if (scale <= 0f) return false;

            Bitmap bmp;
            try
            {
                bmp = NewBitmap(map, scale);
            }
            catch (System.Exception ex) when (ex is System.ArgumentException || ex is System.OutOfMemoryException)
            {
                // How GDI+ reports a bitmap it has no memory for ("Parameter is not valid").
                // Only the allocation is caught: a drawing error is not "too large".
                return false;
            }
            using (RenderOnto(bmp, map, scale))
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }
    }
}
