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
        public ProcessGraph Graph { get; set; }
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

            NodeSizer.MeasureAll(graph);
            var canvas = LayeredLayoutEngine.Layout(graph);

            return new ProcessMap { Graph = graph, CanvasSize = canvas, Source = item };
        }

        /// <summary>Renders the map to a GDI+ bitmap at the given scale.</summary>
        public static Bitmap RenderToBitmap(ProcessMap map, float scale = 1f)
        {
            int w = System.Math.Max(1, (int)System.Math.Ceiling(map.CanvasSize.Width * scale));
            int h = System.Math.Max(1, (int)System.Math.Ceiling(map.CanvasSize.Height * scale));

            var bmp = new Bitmap(w, h);
            bmp.SetResolution(96f * scale, 96f * scale);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(DiagramStyle.CanvasBackground);
                g.ScaleTransform(scale, scale);
                using (var surface = new GdiDiagramSurface(g))
                    DiagramRenderer.Render(surface, map.Graph, map.CanvasSize);
            }
            return bmp;
        }
    }
}
