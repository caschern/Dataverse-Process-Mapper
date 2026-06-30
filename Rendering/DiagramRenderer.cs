using System;
using System.Drawing;
using System.Linq;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Rendering
{
    /// <summary>
    /// Backend-independent drawing of a <see cref="ProcessGraph"/>. Draws the
    /// title band, connectors (with arrowheads and branch labels) and then the
    /// nodes onto any <see cref="IDiagramSurface"/>.
    /// </summary>
    public static class DiagramRenderer
    {
        public static void Render(IDiagramSurface surface, ProcessGraph graph, SizeF canvas)
        {
            DrawTitle(surface, graph, canvas);

            foreach (var edge in graph.Edges)
                DrawEdge(surface, graph, edge);

            foreach (var node in graph.Nodes)
                DrawNode(surface, node);
        }

        private static void DrawTitle(IDiagramSurface s, ProcessGraph graph, SizeF canvas)
        {
            if (!string.IsNullOrEmpty(graph.Title))
                s.DrawString(graph.Title, DiagramStyle.TitleFont, DiagramStyle.TitleColor,
                    DiagramStyle.Margin, DiagramStyle.Margin - 6);

            if (!string.IsNullOrEmpty(graph.Subtitle))
                s.DrawString(graph.Subtitle, DiagramStyle.SubtitleFont, DiagramStyle.SubtitleColor,
                    DiagramStyle.Margin, DiagramStyle.Margin + 16);
        }

        // ---------- nodes ----------

        private static void DrawNode(IDiagramSurface s, ProcessNode node)
        {
            var style = DiagramStyle.For(node.Kind);
            var r = node.Bounds;

            switch (node.Shape)
            {
                case NodeShape.Diamond:
                    var diamond = DiamondPoints(r);
                    s.FillPolygon(style.Fill, diamond);
                    s.DrawPolygon(style.Border, 1.5f, diamond);
                    break;
                case NodeShape.Ellipse:
                    s.FillEllipse(style.Fill, r);
                    s.DrawEllipse(style.Border, 1.5f, r);
                    break;
                case NodeShape.Stadium:
                    float radius = r.Height / 2f;
                    s.FillRoundedRect(style.Fill, r, radius);
                    s.DrawRoundedRect(style.Border, 1.5f, r, radius);
                    break;
                case NodeShape.Rect:
                    s.FillRoundedRect(style.Fill, r, 2f);
                    s.DrawRoundedRect(style.Border, 1.5f, r, 2f);
                    break;
                case NodeShape.RoundedRect:
                default:
                    s.FillRoundedRect(style.Fill, r, DiagramStyle.CornerRadius);
                    s.DrawRoundedRect(style.Border, 1.5f, r, DiagramStyle.CornerRadius);
                    break;
            }

            DrawNodeText(s, node, style);
        }

        private static void DrawNodeText(IDiagramSurface s, ProcessNode node, NodeStyle style)
        {
            var r = node.Bounds;
            bool hasSub = !string.IsNullOrEmpty(node.Subtitle);
            int lineCount = node.Lines.Count + (hasSub ? 1 : 0);
            float blockHeight = node.Lines.Count * DiagramStyle.LineHeight +
                                (hasSub ? DiagramStyle.SubtitleLineHeight : 0);
            float y = r.Y + (r.Height - blockHeight) / 2f;

            foreach (var line in node.Lines)
            {
                var size = s.MeasureString(line, DiagramStyle.LabelFont);
                float x = r.X + (r.Width - size.Width) / 2f;
                s.DrawString(line, DiagramStyle.LabelFont, style.Text, x, y);
                y += DiagramStyle.LineHeight;
            }

            if (hasSub)
            {
                var size = s.MeasureString(node.Subtitle, DiagramStyle.SubtitleFont);
                float x = r.X + (r.Width - size.Width) / 2f;
                s.DrawString(node.Subtitle, DiagramStyle.SubtitleFont, style.SubtitleText, x, y);
            }
        }

        private static PointF[] DiamondPoints(RectangleF r)
        {
            return new[]
            {
                new PointF(r.X + r.Width / 2f, r.Y),
                new PointF(r.Right, r.Y + r.Height / 2f),
                new PointF(r.X + r.Width / 2f, r.Bottom),
                new PointF(r.X, r.Y + r.Height / 2f)
            };
        }

        // ---------- edges ----------

        private static void DrawEdge(IDiagramSurface s, ProcessGraph graph, ProcessEdge edge)
        {
            var from = graph[edge.FromId];
            var to = graph[edge.ToId];
            if (from == null || to == null) return;

            PointF start, end;
            PointF[] path;

            if (edge.IsBack)
            {
                // Route loop edges down the right-hand side.
                start = new PointF(from.Bounds.Right, from.Bounds.Y + from.Bounds.Height / 2f);
                end = new PointF(to.Bounds.Right, to.Bounds.Y + to.Bounds.Height / 2f);
                float bend = Math.Max(from.Bounds.Right, to.Bounds.Right) + 30f;
                path = new[]
                {
                    start,
                    new PointF(bend, start.Y),
                    new PointF(bend, end.Y),
                    end
                };
            }
            else
            {
                start = new PointF(from.Bounds.X + from.Bounds.Width / 2f, from.Bounds.Bottom);
                end = new PointF(to.Bounds.X + to.Bounds.Width / 2f, to.Bounds.Y);
                path = new[] { start, end };
            }

            for (int i = 0; i < path.Length - 1; i++)
                s.DrawLine(DiagramStyle.EdgeColor, 1.4f, path[i].X, path[i].Y, path[i + 1].X, path[i + 1].Y,
                    edge.Dashed || edge.IsBack);

            DrawArrowHead(s, path[path.Length - 2], path[path.Length - 1]);

            if (!string.IsNullOrEmpty(edge.Label))
            {
                var mid = new PointF((start.X + end.X) / 2f, (start.Y + end.Y) / 2f);
                var size = s.MeasureString(edge.Label, DiagramStyle.EdgeLabelFont);
                // Small white-ish backing isn't available; just offset the text slightly.
                s.DrawString(edge.Label, DiagramStyle.EdgeLabelFont, DiagramStyle.EdgeLabelColor,
                    mid.X + 4, mid.Y - size.Height / 2f);
            }
        }

        private static void DrawArrowHead(IDiagramSurface s, PointF from, PointF to)
        {
            const float len = 9f;
            const float halfWidth = 4.5f;
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < 0.001) return;
            dx /= dist; dy /= dist;

            // Base of the arrowhead, "len" back along the line from the tip.
            var baseX = to.X - dx * len;
            var baseY = to.Y - dy * len;
            // Perpendicular vector.
            var px = -dy;
            var py = dx;

            var p1 = new PointF((float)to.X, (float)to.Y);
            var p2 = new PointF((float)(baseX + px * halfWidth), (float)(baseY + py * halfWidth));
            var p3 = new PointF((float)(baseX - px * halfWidth), (float)(baseY - py * halfWidth));

            s.FillPolygon(DiagramStyle.EdgeColor, new[] { p1, p2, p3 });
        }
    }
}
