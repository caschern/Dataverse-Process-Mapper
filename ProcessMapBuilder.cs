using System.Drawing;
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
    }

    /// <summary>
    /// Turns a <see cref="ProcessItem"/> into a fully measured and laid-out
    /// <see cref="ProcessMap"/> by choosing the right parser for its category.
    /// </summary>
    public static class ProcessMapBuilder
    {
        private static readonly FlowJsonParser FlowParser = new FlowJsonParser();
        private static readonly XamlWorkflowParser WorkflowParser = new XamlWorkflowParser();

        public static ProcessMap Build(ProcessItem item)
        {
            IProcessParser parser = item.IsModernFlow ? (IProcessParser)FlowParser : WorkflowParser;
            var graph = parser.Parse(item);

            var map = new ProcessMap { Graph = graph, Source = item };
            RefreshView(map);
            return map;
        }

        /// <summary>
        /// Recomputes the visible graph from the nodes' Collapsed flags and lays
        /// it out again. Called after every expand/collapse toggle.
        /// </summary>
        public static void RefreshView(ProcessMap map)
        {
            var view = GraphContraction.BuildView(map.Graph);
            NodeSizer.MeasureAll(view);
            var canvas = LayeredLayoutEngine.Layout(view);
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
