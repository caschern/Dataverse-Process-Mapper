using System;
using System.Collections.Generic;
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

            var lanes = AssignEdgeLanes(graph);
            var labels = new List<EdgeLabel>();
            foreach (var edge in graph.Edges)
                DrawEdge(surface, graph, edge, lanes.TryGetValue(edge, out var off) ? off : 0f, labels);

            foreach (var node in graph.Nodes)
                DrawNode(surface, node);

            // If two label chips collide, push the later one below the earlier.
            for (int i = 1; i < labels.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (labels[i].Backing.IntersectsWith(labels[j].Backing))
                    {
                        var moved = labels[i];
                        moved.Backing.Y = labels[j].Backing.Bottom + 2f;
                        labels[i] = moved;
                    }
                }
            }

            // Labels go last so no connector line can cross their text.
            foreach (var label in labels)
            {
                surface.FillRoundedRect(Color.White, label.Backing, 3f);
                surface.DrawString(label.Text, DiagramStyle.EdgeLabelFont, DiagramStyle.EdgeLabelColor,
                    label.Backing.X + 3f, label.Backing.Y + 1f);
            }
        }

        private struct EdgeLabel
        {
            public string Text;
            public RectangleF Backing;
        }

        /// <summary>
        /// Spreads the horizontal runs of orthogonal edges that share the same
        /// inter-rank gap onto separate "lanes" so they don't overlap.
        /// Returns a vertical offset (from the gap's midline) per edge.
        /// </summary>
        private static Dictionary<ProcessEdge, float> AssignEdgeLanes(ProcessGraph graph)
        {
            const float laneSpacing = 8f;
            float maxOffset = DiagramStyle.VerticalGap / 2f - 8f;

            var result = new Dictionary<ProcessEdge, float>();

            var jogging = graph.Edges
                .Where(e => !e.IsBack)
                .Select(e => new { Edge = e, From = graph[e.FromId], To = graph[e.ToId] })
                .Where(x => x.From != null && x.To != null)
                .Select(x => new
                {
                    x.Edge,
                    StartX = x.From.Bounds.X + x.From.Bounds.Width / 2f,
                    EndX = x.To.Bounds.X + x.To.Bounds.Width / 2f,
                    MidY = (x.From.Bounds.Bottom + x.To.Bounds.Y) / 2f
                })
                .Where(x => Math.Abs(x.StartX - x.EndX) >= 0.5f); // straight drops need no lane

            // Edges whose horizontal run lands in the same gap collide; group them.
            foreach (var group in jogging.GroupBy(x => (int)Math.Round(x.MidY / 4f)))
            {
                var list = group.OrderBy(x => Math.Min(x.StartX, x.EndX)).ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    float offset = (i - (list.Count - 1) / 2f) * laneSpacing;
                    if (offset > maxOffset) offset = maxOffset;
                    if (offset < -maxOffset) offset = -maxOffset;
                    result[list[i].Edge] = offset;
                }
            }

            return result;
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

        private static void DrawEdge(IDiagramSurface s, ProcessGraph graph, ProcessEdge edge, float laneOffset,
            List<EdgeLabel> labels)
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

                if (Math.Abs(start.X - end.X) < 0.5f)
                {
                    // Vertically aligned: a single straight drop.
                    path = new[] { start, end };
                }
                else
                {
                    // Orthogonal V-H-V route: drop to a midpoint, run across, drop in.
                    // The lane offset spreads parallel runs in the same gap apart.
                    float midY = (start.Y + end.Y) / 2f + laneOffset;
                    path = new[]
                    {
                        start,
                        new PointF(start.X, midY),
                        new PointF(end.X, midY),
                        end
                    };
                }
            }

            for (int i = 0; i < path.Length - 1; i++)
                s.DrawLine(DiagramStyle.EdgeColor, 1.4f, path[i].X, path[i].Y, path[i + 1].X, path[i + 1].Y,
                    edge.Dashed || edge.IsBack);

            DrawArrowHead(s, path[path.Length - 2], path[path.Length - 1]);

            if (!string.IsNullOrEmpty(edge.Label))
            {
                // Anchor the label to the middle segment of the path (the horizontal
                // run on orthogonal routes) so it sits on the connector.
                int seg = (path.Length - 1) / 2;
                var mid = new PointF(
                    (path[seg].X + path[seg + 1].X) / 2f,
                    (path[seg].Y + path[seg + 1].Y) / 2f);
                var size = s.MeasureString(edge.Label, DiagramStyle.EdgeLabelFont);

                // Centered on the connector; queued and drawn after all edges
                // (with a white backing chip) so no line crosses the text.
                float lx = mid.X - size.Width / 2f;
                float ly = mid.Y - size.Height / 2f;
                labels.Add(new EdgeLabel
                {
                    Text = edge.Label,
                    Backing = new RectangleF(lx - 3f, ly - 1f, size.Width + 6f, size.Height + 2f)
                });
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
