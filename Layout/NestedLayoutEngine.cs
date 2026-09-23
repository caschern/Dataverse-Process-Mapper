using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Rendering;

namespace DataverseProcessMapper.Layout
{
    /// <summary>
    /// Lays a process out as nested blocks (preview). Every expanded container —
    /// Scope, Condition, Switch case, Loop, classic condition or wait — becomes a
    /// frame around its own steps, laid out on its own by
    /// <see cref="LayeredLayoutEngine"/>; in its parent's layout the whole frame
    /// is one node. So a step that runs after a container is drawn below the
    /// container's frame, never beside the steps inside it — which is what the
    /// flat layout gets wrong, since there the next step hangs off the
    /// container's header at the same height as its first inner steps.
    ///
    /// Edges never cross a frame sideways. One that leaves a container from
    /// inside (a classic workflow's branch ends continuing to the next step)
    /// drops to an exit bus at the bottom of the frame, and continues from the
    /// frame's bottom edge in the parent block.
    /// </summary>
    public static class NestedLayoutEngine
    {
        private const float PadX = 16f;       // frame side padding
        private const float PadTop = 18f;     // frame top padding (room for the entry jog and toggle glyph)
        private const float PadBottom = 16f;  // frame bottom padding
        private const float BusGap = 16f;     // content bottom to exit bus
        private const float RailGap = 16f;    // content right edge to the first exit rail
        private const float RailStep = 10f;   // between exit rails

        private sealed class Block
        {
            public string ContainerId;              // null for the top level
            public int Depth;                       // 0 for the top level
            public readonly List<string> Members = new List<string>();
            public readonly List<Segment> Segments = new List<Segment>();
            public bool HasExitBus;
            public ProcessGraph Sub;                // the block's own layout graph
            public RectangleF Content;              // Sub coordinates, incl. rails and the bus
            public SizeF Size;                      // frame size (containers) or content size (top level)
            public PointF Offset;                   // Sub coordinates -> canvas
        }

        /// <summary>One piece of an edge, inside a single block.</summary>
        private sealed class Segment
        {
            public Block Block;
            public string From;
            public string To;                       // null: the block's exit bus
            public string Label;
            public bool Drawn;                      // bus segments: drawn only when they say something
            public ProcessEdge LayoutEdge;          // the edge in Block.Sub (null for bus segments)
            public List<PointF> Path;               // Sub coordinates
        }

        public static SizeF Layout(ProcessGraph view)
        {
            view.Frames = new List<ContainerFrame>();
            view.ParallelBlocks = new List<ParallelBlock>();
            foreach (var e in view.Edges)
            {
                e.LaneY = null;
                e.RailX = null;
                e.Route = null;
                e.IsBack = false;
            }
            if (view.Nodes.Count == 0) return new SizeF(200, 100);

            var tree = new Hierarchy(view);
            var blocks = new Dictionary<string, Block>();

            Block BlockOf(string containerId)
            {
                var key = containerId ?? "";
                if (blocks.TryGetValue(key, out var b)) return b;
                b = new Block { ContainerId = containerId, Depth = containerId == null ? 0 : tree.Ancestors(containerId).Count + 1 };
                b.Members.AddRange(containerId == null ? tree.TopLevel : tree.Children[containerId]);
                blocks[key] = b;
                return b;
            }

            BlockOf(null);
            foreach (var id in tree.Children.Keys) BlockOf(id);

            // Containers some path leaves from inside (a classic workflow's branch
            // ends continuing to the next step).
            var leftFromInside = new HashSet<string>();
            foreach (var e in view.Edges)
            {
                if (!view.Contains(e.FromId) || !view.Contains(e.ToId)) continue;
                foreach (var x in tree.Ancestors(e.FromId))
                    if (!tree.Within(e.ToId, x)) leftFromInside.Add(x);
            }

            var segmentsOf = Decompose(view, tree, leftFromInside, BlockOf);

            // A branch simply ending inside its frame already says "then carry on
            // after the frame", as in the Power Automate designer — a line from
            // each branch end down to the frame's edge would add nothing. Only a
            // path that says something is drawn out through the exit bus: a
            // labelled one (a condition's "No" fall-through) or one leaving the
            // container's header itself.
            foreach (var segs in segmentsOf.Values)
            {
                if (segs.Count == 0 || segs[0].To != null) continue;
                if (segs[0].Label == null && segs[0].From != segs[0].Block.ContainerId) continue;
                foreach (var s in segs.TakeWhile(s => s.To == null)) s.Drawn = true;
            }
            foreach (var block in blocks.Values)
                block.HasExitBus = block.Segments.Any(s => s.To == null && s.Drawn);

            // Innermost blocks first: a container's frame size must be known
            // before its parent can lay it out as a single node.
            foreach (var block in blocks.Values.OrderByDescending(b => b.Depth))
                LayOutBlock(block, view, tree, blocks);

            // Outermost first: place each frame, then the block inside it.
            var top = blocks[""];
            top.Offset = new PointF(DiagramStyle.Margin - top.Content.Left,
                DiagramStyle.Margin + DiagramStyle.TitleBandHeight - top.Content.Top);
            Place(top, view, tree, blocks);

            ComposeRoutes(view, tree, segmentsOf);
            CollectParallelBlocks(view, tree, blocks.Values);

            return new SizeF(top.Content.Width + 2 * DiagramStyle.Margin,
                DiagramStyle.Margin + DiagramStyle.TitleBandHeight + top.Content.Height + DiagramStyle.Margin);
        }

        // ---------- splitting edges into per-block segments ----------

        /// <summary>
        /// Splits every edge into the pieces that live in each block it passes
        /// through: out of each container it leaves (to that container's exit
        /// bus), then across the block where its source and target meet.
        /// </summary>
        private static Dictionary<ProcessEdge, List<Segment>> Decompose(ProcessGraph view, Hierarchy tree,
            HashSet<string> leftFromInside, Func<string, Block> blockOf)
        {
            var shared = new Dictionary<string, Segment>();
            Segment Seg(Block block, string from, string to, string label)
            {
                var key = (block.ContainerId ?? "") + "|" + from + "|" + (to ?? "<bus>") + "|" + label;
                if (!shared.TryGetValue(key, out var seg))
                {
                    seg = new Segment { Block = block, From = from, To = to, Label = label };
                    shared[key] = seg;
                    block.Segments.Add(seg);
                }
                return seg;
            }

            var result = new Dictionary<ProcessEdge, List<Segment>>();
            foreach (var e in view.Edges)
            {
                string u = e.FromId, v = e.ToId;
                if (!view.Contains(u) || !view.Contains(v) || u == v) continue;
                var segs = new List<Segment>();

                if (tree.IsContainer(u) && tree.Within(v, u))
                {
                    // A container's header leading into its own steps.
                    var to = tree.MemberOf(u, v);
                    if (to == null) continue;
                    segs.Add(Seg(blockOf(u), u, to, e.Label));
                }
                else
                {
                    // Containers this edge leaves, innermost first.
                    var exits = new List<string>();
                    if (tree.IsContainer(u) && leftFromInside.Contains(u)) exits.Add(u);
                    foreach (var x in tree.Ancestors(u))
                    {
                        if (tree.Within(v, x)) break;
                        exits.Add(x);
                    }

                    var src = u;
                    foreach (var x in exits)
                    {
                        var from = src == x ? x : tree.MemberOf(x, src);
                        segs.Add(Seg(blockOf(x), from, null, segs.Count == 0 ? e.Label : null));
                        src = x;
                    }

                    // The block where the remaining source and the target meet.
                    string meet = null;
                    foreach (var y in tree.Ancestors(src))
                        if (tree.Within(v, y)) { meet = y; break; }
                    var f = tree.MemberOf(meet, src);
                    var t = tree.MemberOf(meet, v);
                    if (f == null || t == null || f == t) continue;
                    segs.Add(Seg(blockOf(meet), f, t, segs.Count == 0 ? e.Label : null));
                }
                result[e] = segs;
            }
            return result;
        }

        // ---------- laying out one block ----------

        private static void LayOutBlock(Block block, ProcessGraph view, Hierarchy tree, Dictionary<string, Block> blocks)
        {
            var sub = new ProcessGraph();
            if (block.ContainerId != null)
                Copy(sub, view[block.ContainerId], view[block.ContainerId].Bounds.Size);
            foreach (var m in block.Members)
                Copy(sub, view[m], tree.IsContainer(m) ? blocks[m].Size : view[m].Bounds.Size);

            foreach (var seg in block.Segments.Where(s => s.To != null))
                seg.LayoutEdge = sub.AddEdge(seg.From, seg.To, seg.Label);

            // Parallel regions are found per block; the ordering pass then keeps
            // each region's branches together, and here they are always compact.
            sub.ParallelBlocks = ParallelBlockDetector.Detect(sub);
            LayeredLayoutEngine.Layout(sub, topAlignRows: true, centeredNodeId: block.ContainerId);
            block.Sub = sub;

            float left = sub.Nodes.Min(n => n.Bounds.Left);
            float top = sub.Nodes.Min(n => n.Bounds.Top);
            float nodesRight = sub.Nodes.Max(n => n.Bounds.Right);
            float right = nodesRight;
            float bottom = sub.Nodes.Max(n => n.Bounds.Bottom);
            foreach (var e in sub.Edges)
                if (e.RailX.HasValue) right = Math.Max(right, e.RailX.Value + 4f);

            foreach (var seg in block.Segments.Where(s => s.LayoutEdge != null))
                seg.Path = DiagramRenderer.EdgePath(sub, seg.LayoutEdge)?.ToList();

            if (block.HasExitBus)
            {
                float busY = bottom + BusGap;
                var bus = block.Segments.Where(s => s.To == null && s.Drawn).ToList();
                int rails = 0;
                float railRight = right;
                foreach (var seg in bus)
                {
                    var s = sub[seg.From];
                    float sx = s.Bounds.X + s.Bounds.Width / 2f;
                    // Drop straight to the bus when nothing is in the way; the
                    // header, and anything above other steps, goes round the
                    // right-hand side instead.
                    bool blocked = seg.From == block.ContainerId || sub.Nodes.Any(n =>
                        n != s && n.Bounds.Left - 4f < sx && sx < n.Bounds.Right + 4f &&
                        n.Bounds.Top > s.Bounds.Top && n.Bounds.Top < busY);
                    if (!blocked)
                    {
                        seg.Path = new List<PointF> { new PointF(sx, s.Bounds.Bottom), new PointF(sx, busY) };
                    }
                    else
                    {
                        float railX = right + RailGap + rails++ * RailStep;
                        railRight = Math.Max(railRight, railX);
                        float sy = s.Bounds.Y + s.Bounds.Height / 2f;
                        seg.Path = new List<PointF>
                        {
                            new PointF(s.Bounds.Right, sy), new PointF(railX, sy), new PointF(railX, busY)
                        };
                    }
                }
                right = railRight;

                // Rails on the right get matching room on the left, so the header
                // stays centred in its frame.
                if (block.ContainerId != null) left -= right - nodesRight;

                // Every exit meets at the bottom centre, where the frame's own
                // outgoing edge begins in the parent block.
                float busX = (left + right) / 2f;
                foreach (var seg in bus)
                {
                    seg.Path.Add(new PointF(busX, busY));
                    seg.Path.Add(new PointF(busX, busY + PadBottom));
                }
                bottom = busY;
            }
            else if (block.ContainerId != null)
            {
                left -= right - nodesRight; // loop rails, likewise balanced
            }

            block.Content = RectangleF.FromLTRB(left, top, right, bottom);
            block.Size = block.ContainerId == null
                ? block.Content.Size
                : new SizeF(block.Content.Width + 2 * PadX, block.Content.Height + PadTop + PadBottom);
        }

        private static void Copy(ProcessGraph sub, ProcessNode node, SizeF size)
        {
            var copy = sub.AddNode(node.Label, node.Kind, node.Shape, id: node.Id, subtitle: node.Subtitle);
            copy.Bounds = new RectangleF(0, 0, size.Width, size.Height);
        }

        // ---------- placing blocks on the canvas ----------

        private static void Place(Block block, ProcessGraph view, Hierarchy tree, Dictionary<string, Block> blocks)
        {
            if (block.ContainerId != null)
                view[block.ContainerId].Bounds = Shift(block.Sub[block.ContainerId].Bounds, block.Offset);

            foreach (var m in block.Members)
            {
                var box = Shift(block.Sub[m].Bounds, block.Offset);
                if (!tree.IsContainer(m))
                {
                    view[m].Bounds = box;
                    continue;
                }

                var inner = blocks[m];
                view.Frames.Add(new ContainerFrame { ContainerId = m, Bounds = box, Depth = inner.Depth });
                inner.Offset = new PointF(box.X + PadX - inner.Content.Left, box.Y + PadTop - inner.Content.Top);
                Place(inner, view, tree, blocks);
            }
        }

        private static RectangleF Shift(RectangleF r, PointF by) => new RectangleF(r.X + by.X, r.Y + by.Y, r.Width, r.Height);

        // ---------- joining the pieces back into drawable edges ----------

        private static void ComposeRoutes(ProcessGraph view, Hierarchy tree, Dictionary<ProcessEdge, List<Segment>> segmentsOf)
        {
            foreach (var kv in segmentsOf)
            {
                var edge = kv.Key;
                // Undrawn exits: the edge starts where it leaves the outermost frame.
                var segs = kv.Value.SkipWhile(s => s.To == null && !s.Drawn).ToList();
                if (segs.Count == 0 || segs.Any(s => s.Path == null || s.Path.Count < 2)) continue;

                var pts = new List<PointF>();
                foreach (var seg in segs)
                    foreach (var p in seg.Path)
                        pts.Add(new PointF(p.X + seg.Block.Offset.X, p.Y + seg.Block.Offset.Y));

                // Arriving at a container's frame: continue in to its header,
                // which need not sit at the frame's centre.
                var last = segs[segs.Count - 1];
                if (last.To != null && last.To != last.Block.ContainerId && tree.IsContainer(last.To))
                {
                    var header = view[last.To].Bounds;
                    float hx = header.X + header.Width / 2f;
                    var end = pts[pts.Count - 1];
                    if (Math.Abs(end.X - hx) < 0.5f)
                    {
                        pts[pts.Count - 1] = new PointF(hx, header.Y);
                    }
                    else
                    {
                        float jogY = end.Y + PadTop / 2f;
                        pts[pts.Count - 1] = new PointF(end.X, jogY);
                        pts.Add(new PointF(hx, jogY));
                        pts.Add(new PointF(hx, header.Y));
                    }
                }

                edge.Route = Simplify(pts);
                var first = segs[0];
                if (first.LayoutEdge?.LaneY != null)
                    edge.LaneY = first.LayoutEdge.LaneY + first.Block.Offset.Y;

                // Loops back to a header are drawn along their route, dashed.
                if (segs.Any(s => s.LayoutEdge != null && s.LayoutEdge.IsBack))
                    edge.Dashed = true;
            }
        }

        /// <summary>Drops repeated points and the middle of straight runs.</summary>
        private static List<PointF> Simplify(List<PointF> pts)
        {
            var result = new List<PointF>();
            foreach (var p in pts)
            {
                if (result.Count > 0 && Near(result[result.Count - 1], p)) continue;
                if (result.Count >= 2)
                {
                    var a = result[result.Count - 2];
                    var b = result[result.Count - 1];
                    bool vertical = Math.Abs(a.X - b.X) < 0.01f && Math.Abs(b.X - p.X) < 0.01f;
                    bool horizontal = Math.Abs(a.Y - b.Y) < 0.01f && Math.Abs(b.Y - p.Y) < 0.01f;
                    if (vertical || horizontal) result.RemoveAt(result.Count - 1);
                }
                result.Add(p);
            }
            return result;
        }

        private static bool Near(PointF a, PointF b) => Math.Abs(a.X - b.X) < 0.01f && Math.Abs(a.Y - b.Y) < 0.01f;

        private static void CollectParallelBlocks(ProcessGraph view, Hierarchy tree, IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                foreach (var region in block.Sub.ParallelBlocks)
                {
                    // A member that is a container stands for its whole frame.
                    var members = region.AllMemberIds
                        .SelectMany(id => new[] { id }.Concat(tree.Descendants(id)))
                        .Distinct()
                        .ToList();
                    view.ParallelBlocks.Add(new ParallelBlock
                    {
                        EntryId = region.EntryId,
                        ExitId = region.ExitId,
                        Branches = region.Branches,
                        AllMemberIds = members
                    });
                }
            }
        }

        // ---------- containment ----------

        /// <summary>The container tree of the visible graph.</summary>
        private sealed class Hierarchy
        {
            private readonly ProcessGraph _g;
            private readonly Dictionary<string, List<string>> _ancestors = new Dictionary<string, List<string>>();

            public readonly Dictionary<string, List<string>> Children = new Dictionary<string, List<string>>();
            public readonly List<string> TopLevel = new List<string>();

            public Hierarchy(ProcessGraph g)
            {
                _g = g;
                foreach (var n in g.Nodes)
                {
                    var p = Parent(n.Id);
                    if (p == null)
                    {
                        TopLevel.Add(n.Id);
                        continue;
                    }
                    if (!Children.TryGetValue(p, out var list)) Children[p] = list = new List<string>();
                    list.Add(n.Id);
                }
            }

            public bool IsContainer(string id) => Children.ContainsKey(id);

            private string Parent(string id)
            {
                var p = _g[id]?.ParentId;
                return p != null && _g.Contains(p) ? p : null;
            }

            /// <summary>Containers strictly enclosing <paramref name="id"/>, innermost first.</summary>
            public List<string> Ancestors(string id)
            {
                if (_ancestors.TryGetValue(id, out var cached)) return cached;
                var list = new List<string>();
                var cur = Parent(id);
                for (int guard = 0; cur != null && guard < _g.Nodes.Count; guard++)
                {
                    list.Add(cur);
                    cur = Parent(cur);
                }
                _ancestors[id] = list;
                return list;
            }

            /// <summary>True when <paramref name="id"/> is the container or inside it.</summary>
            public bool Within(string id, string container) => id == container || Ancestors(id).Contains(container);

            /// <summary>
            /// The node standing for <paramref name="id"/> in the block of
            /// <paramref name="container"/> (null = top level): the node itself, the
            /// member container enclosing it, or the block's own header.
            /// </summary>
            public string MemberOf(string container, string id)
            {
                if (id == container) return id;
                var cur = id;
                for (int guard = 0; cur != null && guard < _g.Nodes.Count; guard++)
                {
                    var p = Parent(cur);
                    if (p == container) return cur;
                    cur = p;
                }
                return null;
            }

            public IEnumerable<string> Descendants(string id)
            {
                if (!Children.TryGetValue(id, out var kids)) yield break;
                foreach (var k in kids)
                {
                    yield return k;
                    foreach (var d in Descendants(k)) yield return d;
                }
            }
        }
    }
}
