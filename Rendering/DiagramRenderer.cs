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
        // Parallel-block annotation: a quiet boundary that never competes with the steps.
        private static readonly Color BlockBorderColor = Color.FromArgb(0, 137, 123);
        private static readonly Color BlockCaptionColor = Color.FromArgb(0, 121, 107);
        private const float BlockPad = 14f;
        // Vertically the outline stays close to the branches: the fan-out and
        // fan-in lanes (and their "always" chips) run about a half-gap away,
        // and a border on the same line would run through them.
        private const float BlockPadY = 8f;

        // Selection highlighting: the selected node's edges pop, everything else recedes.
        private static readonly Color HighlightEdgeColor = Color.FromArgb(37, 118, 220);
        private static readonly Color DimEdgeColor = Color.FromArgb(214, 218, 224);
        private static readonly Color DimLabelColor = Color.FromArgb(196, 201, 208);

        /// <param name="visible">
        /// The part of the diagram being painted, in diagram coordinates; anything
        /// wholly outside it is skipped. A pan exposes only a thin strip, so this
        /// keeps a repaint proportional to what is on screen rather than to the
        /// size of the map. Null draws everything (exports).
        /// </param>
        public static void Render(IDiagramSurface surface, ProcessGraph graph, SizeF canvas,
            bool interactive = false, string highlightId = null, RectangleF? visible = null)
        {
            if (Shows(visible, new RectangleF(0, 0, canvas.Width, DiagramStyle.Margin + 32f)))
                DrawTitle(surface, graph, canvas);
            DrawFrames(surface, graph, visible);
            DrawParallelBlocks(surface, graph, visible);

            bool highlight = interactive && !string.IsNullOrEmpty(highlightId);
            var labels = new List<EdgeLabel>();

            // Highlighted edges draw last so they sit on top of dimmed ones.
            IEnumerable<ProcessEdge> ordered = graph.Edges;
            if (highlight)
                ordered = graph.Edges
                    .OrderBy(e => e.FromId == highlightId || e.ToId == highlightId ? 1 : 0);

            foreach (var edge in ordered)
            {
                bool incident = highlight && (edge.FromId == highlightId || edge.ToId == highlightId);
                var stroke = highlight
                    ? (incident ? HighlightEdgeColor : DimEdgeColor)
                    : DiagramStyle.EdgeColor;
                var labelColor = highlight
                    ? (incident ? DiagramStyle.EdgeLabelColor : DimLabelColor)
                    : DiagramStyle.EdgeLabelColor;
                float width = incident ? 2.2f : 1.4f;

                DrawEdge(surface, graph, edge, labels, stroke, width, labelColor, visible);
            }

            foreach (var node in graph.Nodes)
                if (Shows(visible, RectangleF.Inflate(node.Bounds, 4f, 4f)))
                    DrawNode(surface, node);

            // Expand/collapse glyphs only make sense on screen, not in exports.
            if (interactive)
                DrawToggleGlyphs(surface, graph, visible);

            // If two label chips collide, slide the later one to the RIGHT along
            // its lane — pushing down would leave the node-free gap band and
            // drop the chip onto the row below.
            for (int i = 1; i < labels.Count; i++)
            {
                bool moved = true;
                int guard = 0;
                while (moved && guard++ < 8)
                {
                    moved = false;
                    for (int j = 0; j < i; j++)
                    {
                        if (labels[i].Backing.IntersectsWith(labels[j].Backing))
                        {
                            var shifted = labels[i];
                            shifted.Backing.X = labels[j].Backing.Right + 4f;
                            labels[i] = shifted;
                            moved = true;
                        }
                    }
                }
            }

            // Labels go last so no connector line can cross their text. Their
            // positions were settled over every edge above, visible or not, so a
            // partial repaint puts each chip exactly where the full paint did.
            foreach (var label in labels)
            {
                if (!Shows(visible, label.Backing)) continue;
                surface.FillRoundedRect(Color.White, label.Backing, 3f);
                surface.DrawString(label.Text, DiagramStyle.EdgeLabelFont, label.Color,
                    label.Backing.X + 3f, label.Backing.Y + 1f);
            }
        }

        private struct EdgeLabel
        {
            public string Text;
            public RectangleF Backing;
            public Color Color;
        }

        // ---------- expand/collapse glyphs ----------

        /// <summary>The clickable [+]/[-] square on a container node.</summary>
        public static RectangleF ToggleRect(ProcessNode n)
            => new RectangleF(n.Bounds.Right - 8f, n.Bounds.Y - 6f, 14f, 14f);

        /// <summary>
        /// Whether a point is on the node's drawn shape. A diamond fills only
        /// half its bounding box, so its corners are empty canvas — a click
        /// there belongs to whatever is behind it, not the condition.
        /// </summary>
        public static bool ShapeContains(ProcessNode node, PointF pt)
        {
            var r = node.Bounds;
            if (!r.Contains(pt)) return false;
            if (node.Shape != NodeShape.Diamond) return true;

            float dx = Math.Abs(pt.X - (r.X + r.Width / 2f)) / (r.Width / 2f);
            float dy = Math.Abs(pt.Y - (r.Y + r.Height / 2f)) / (r.Height / 2f);
            return dx + dy <= 1f;
        }

        private static void DrawToggleGlyphs(IDiagramSurface s, ProcessGraph graph, RectangleF? visible)
        {
            var expandedContainers = new HashSet<string>();
            foreach (var n in graph.Nodes)
                if (n.ParentId != null) expandedContainers.Add(n.ParentId);

            foreach (var n in graph.Nodes)
            {
                bool collapsed = n.HiddenCount > 0;
                bool expanded = expandedContainers.Contains(n.Id);
                if (!collapsed && !expanded) continue;

                var r = ToggleRect(n);
                if (!Shows(visible, r)) continue;
                s.FillRoundedRect(Color.White, r, 3f);
                s.DrawRoundedRect(DiagramStyle.EdgeColor, 1f, r, 3f);

                float cy = r.Y + r.Height / 2f;
                s.DrawLine(DiagramStyle.EdgeColor, 1.4f, r.X + 3.5f, cy, r.Right - 3.5f, cy, false);
                if (collapsed)
                {
                    float cx = r.X + r.Width / 2f;
                    s.DrawLine(DiagramStyle.EdgeColor, 1.4f, cx, r.Y + 3.5f, cx, r.Bottom - 3.5f, false);
                }
            }
        }

        // Container frames (nested layout): alternating fills keep nesting legible
        // when frames sit several deep.
        private static readonly Color FrameBorderColor = Color.FromArgb(203, 209, 218);
        private static readonly Color FrameFillOdd = Color.FromArgb(247, 248, 250);
        private static readonly Color FrameFillEven = Color.White;

        /// <summary>Draws container frames behind everything else, outermost first.</summary>
        private static void DrawFrames(IDiagramSurface s, ProcessGraph graph, RectangleF? visible)
        {
            if (graph.Frames == null) return;
            foreach (var f in graph.Frames.OrderBy(f => f.Depth))
            {
                if (!Shows(visible, RectangleF.Inflate(f.Bounds, 2f, 2f))) continue;
                s.FillRoundedRect(f.Depth % 2 == 1 ? FrameFillOdd : FrameFillEven, f.Bounds, DiagramStyle.CornerRadius);
                s.DrawRoundedRect(FrameBorderColor, 1f, f.Bounds, DiagramStyle.CornerRadius);
            }
        }

        /// <summary>A node's extent on the canvas: its frame when it is an expanded container.</summary>
        private static RectangleF Extent(ProcessGraph graph, ProcessNode node)
        {
            var frame = graph.Frames?.FirstOrDefault(f => f.ContainerId == node.Id);
            return frame != null ? frame.Bounds : node.Bounds;
        }

        /// <summary>True when nothing restricts the paint, or the area overlaps it.</summary>
        private static bool Shows(RectangleF? visible, RectangleF area)
            => visible == null || visible.Value.IntersectsWith(area);

        /// <summary>
        /// Outlines each proven parallel region and captions it, so concurrency
        /// is stated rather than merely implied by nodes sitting side by side.
        /// Nothing here moves a node — if a block's members are not contiguous on
        /// the canvas, the boundary would enclose unrelated steps and is skipped.
        /// </summary>
        private static void DrawParallelBlocks(IDiagramSurface s, ProcessGraph graph, RectangleF? visible)
        {
            if (graph.ParallelBlocks == null || graph.ParallelBlocks.Count == 0) return;

            foreach (var block in graph.ParallelBlocks)
            {
                var members = block.AllMemberIds
                    .Select(id => graph[id])
                    .Where(n => n != null)
                    .ToList();
                if (members.Count == 0) continue;

                var box = Union(members.Select(n => Extent(graph, n)));
                box = RectangleF.Inflate(box, BlockPad, BlockPadY);
                if (!Shows(visible, RectangleF.Inflate(box, 2f, 10f))) continue; // incl. the caption

                // Contiguity check: any unrelated node overlapping the box means
                // the outline would claim steps that are not part of this region.
                // The entry and exit are the block's own anchors, not intruders.
                var memberIds = new HashSet<string>(block.AllMemberIds) { block.EntryId, block.ExitId };
                bool clean = graph.Nodes.All(n => memberIds.Contains(n.Id) || !n.Bounds.IntersectsWith(box));
                if (!clean) continue;

                var caption = block.Caption;
                var size = s.MeasureString(caption, DiagramStyle.SubtitleFont);
                float capX = box.Left + 12f;

                // Four dashed lines rather than a dashed rounded-rect: DrawLine
                // already carries a dash flag on every surface, so this needs no
                // change to GDI+, PDF and SVG backends. The top edge breaks around
                // the caption so the text reads cleanly, like a fieldset legend.
                s.DrawLine(BlockBorderColor, 1.2f, box.Left, box.Top, capX - 4f, box.Top, true);
                s.DrawLine(BlockBorderColor, 1.2f, capX + size.Width + 4f, box.Top, box.Right, box.Top, true);
                s.DrawLine(BlockBorderColor, 1.2f, box.Right, box.Top, box.Right, box.Bottom, true);
                s.DrawLine(BlockBorderColor, 1.2f, box.Right, box.Bottom, box.Left, box.Bottom, true);
                s.DrawLine(BlockBorderColor, 1.2f, box.Left, box.Bottom, box.Left, box.Top, true);

                s.DrawString(caption, DiagramStyle.SubtitleFont, BlockCaptionColor,
                    capX, box.Top - size.Height / 2f);
            }
        }

        private static RectangleF Union(IEnumerable<RectangleF> rects)
        {
            RectangleF? acc = null;
            foreach (var r in rects)
                acc = acc == null ? r : RectangleF.Union(acc.Value, r);
            return acc ?? RectangleF.Empty;
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
            var subtitle = node.DisplaySubtitle;
            bool hasSub = !string.IsNullOrEmpty(subtitle);
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
                var size = s.MeasureString(subtitle, DiagramStyle.SubtitleFont);
                float x = r.X + (r.Width - size.Width) / 2f;
                s.DrawString(subtitle, DiagramStyle.SubtitleFont, style.SubtitleText, x, y);
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

        private static void DrawEdge(IDiagramSurface s, ProcessGraph graph, ProcessEdge edge,
            List<EdgeLabel> labels, Color stroke, float width, Color labelColor, RectangleF? visible)
        {
            var path = EdgePath(graph, edge);
            if (path == null) return;

            if (Shows(visible, RectangleF.Inflate(Bounds(path), 6f, 6f))) // arrowhead + stroke
            {
                for (int i = 0; i < path.Length - 1; i++)
                    s.DrawLine(stroke, width, path[i].X, path[i].Y, path[i + 1].X, path[i + 1].Y,
                        edge.Dashed || edge.IsBack);

                DrawArrowHead(s, path[path.Length - 2], path[path.Length - 1], stroke);
            }

            if (!string.IsNullOrEmpty(edge.Label))
                QueueLabel(s, edge, path, labels, labelColor);
        }

        /// <summary>
        /// The polyline an edge is drawn along, from its source to the arrow tip:
        /// its routed polyline when the layout gave it one, otherwise a straight
        /// drop or a V-H-V jog on its lane; loops return up a right-hand rail.
        /// Null when an endpoint is not in the graph.
        /// </summary>
        public static PointF[] EdgePath(ProcessGraph graph, ProcessEdge edge)
        {
            var from = graph[edge.FromId];
            var to = graph[edge.ToId];
            if (from == null || to == null) return null;

            PointF start, end;
            PointF[] path;

            if (edge.IsBack)
            {
                // Route loop edges down the right-hand side, on their assigned rail.
                start = new PointF(from.Bounds.Right, from.Bounds.Y + from.Bounds.Height / 2f);
                end = new PointF(to.Bounds.Right, to.Bounds.Y + to.Bounds.Height / 2f);
                float bend = edge.RailX ?? (Math.Max(from.Bounds.Right, to.Bounds.Right) + 30f);
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

                if (edge.Route != null && edge.Route.Count >= 2)
                {
                    // Multi-rank edge: the layout engine already routed it
                    // through reserved channels via virtual waypoints.
                    path = edge.Route.ToArray();
                }
                else if (Math.Abs(start.X - end.X) < 0.5f)
                {
                    // Vertically aligned: a single straight drop.
                    path = new[] { start, end };
                }
                else
                {
                    // Orthogonal V-H-V route on the lane assigned by the layout
                    // engine's routing pass; the midpoint as a fallback.
                    float midY = edge.LaneY ?? (start.Y + end.Y) / 2f;
                    path = new[]
                    {
                        start,
                        new PointF(start.X, midY),
                        new PointF(end.X, midY),
                        end
                    };
                }
            }
            return path;
        }

        private static void QueueLabel(IDiagramSurface s, ProcessEdge edge, PointF[] path,
            List<EdgeLabel> labels, Color labelColor)
        {
            // Anchor the label to the FIRST horizontal run of the path: those
            // lie in the gaps between rows, which are guaranteed node-free.
            // A vertical mid-segment (multi-rank routes) would drop the chip
            // on top of node boxes.
            var mid = default(PointF);
            bool found = false;
            for (int i = 0; i < path.Length - 1 && !found; i++)
            {
                if (Math.Abs(path[i].Y - path[i + 1].Y) < 0.01f &&
                    Math.Abs(path[i].X - path[i + 1].X) >= 0.5f)
                {
                    mid = new PointF((path[i].X + path[i + 1].X) / 2f, path[i].Y);
                    found = true;
                }
            }
            if (!found)
                mid = new PointF(path[0].X, edge.LaneY ?? (path[0].Y + 14f)); // its reserved label lane

            var size = s.MeasureString(edge.Label, DiagramStyle.EdgeLabelFont);

            // Centered on the connector; queued and drawn after all edges
            // (with a white backing chip) so no line crosses the text.
            float lx = mid.X - size.Width / 2f;
            float ly = mid.Y - size.Height / 2f;
            labels.Add(new EdgeLabel
            {
                Text = edge.Label,
                Backing = new RectangleF(lx - 3f, ly - 1f, size.Width + 6f, size.Height + 2f),
                Color = labelColor
            });
        }

        private static RectangleF Bounds(PointF[] path)
        {
            float minX = path[0].X, maxX = path[0].X, minY = path[0].Y, maxY = path[0].Y;
            foreach (var p in path)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return RectangleF.FromLTRB(minX, minY, maxX, maxY);
        }

        private static void DrawArrowHead(IDiagramSurface s, PointF from, PointF to, Color color)
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

            s.FillPolygon(color, new[] { p1, p2, p3 });
        }
    }
}
