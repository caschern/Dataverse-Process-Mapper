using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Rendering;

namespace DataverseProcessMapper.Layout
{
    /// <summary>
    /// A lightweight layered (Sugiyama-style) layout: assigns each node a rank
    /// using longest-path from the roots, detects back-edges (loops) so they
    /// don't distort ranking, then positions ranks top-to-bottom with each rank
    /// centered horizontally.
    /// </summary>
    public static class LayeredLayoutEngine
    {
        /// <summary>Lays out the graph and returns the overall canvas size.</summary>
        public static SizeF Layout(ProcessGraph graph)
        {
            if (graph.Nodes.Count == 0) return new SizeF(200, 100);

            var outgoing = BuildAdjacency(graph);
            MarkBackEdges(graph, outgoing);

            var forward = graph.Edges.Where(e => !e.IsBack).ToList();
            AssignRanks(graph, forward);
            var ranks = OrderRanks(graph, forward);
            return Position(graph, ranks);
        }

        /// <summary>
        /// Crossing reduction: orders the nodes of each rank by the barycenter
        /// (average position) of their neighbors in the adjacent rank, sweeping
        /// down and up a few times, so children line up under their parents and
        /// connectors don't cross.
        /// </summary>
        private static List<List<ProcessNode>> OrderRanks(ProcessGraph graph, List<ProcessEdge> forward)
        {
            var ranks = graph.Nodes
                .GroupBy(n => n.Rank)
                .OrderBy(g => g.Key)
                .Select(g => g.ToList())
                .ToList();

            var order = new Dictionary<string, int>();
            foreach (var rank in ranks)
                for (int i = 0; i < rank.Count; i++)
                    order[rank[i].Id] = i;

            var parents = forward.GroupBy(e => e.ToId)
                .ToDictionary(g => g.Key, g => g.Select(e => e.FromId).ToList());
            var children = forward.GroupBy(e => e.FromId)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());

            for (int iter = 0; iter < 4; iter++)
            {
                for (int r = 1; r < ranks.Count; r++)          // downward: follow parents
                    SortByBarycenter(ranks[r], parents, order);
                for (int r = ranks.Count - 2; r >= 0; r--)     // upward: follow children
                    SortByBarycenter(ranks[r], children, order);
            }

            ApplyContainerCohesion(graph, ranks, parents, order);

            return ranks;
        }

        /// <summary>
        /// Final ordering pass: nodes that share a container (ParentId chain)
        /// are pulled into contiguous blocks per rank, each block placed at its
        /// members' mean barycenter (recursively for nested containers). Stops
        /// the children of different scopes/branches interleaving even when
        /// their raw barycenters do.
        /// </summary>
        private static void ApplyContainerCohesion(ProcessGraph graph, List<List<ProcessNode>> ranks,
            Dictionary<string, List<string>> parents, Dictionary<string, int> order)
        {
            var pathCache = new Dictionary<string, List<string>>();
            List<string> PathOf(ProcessNode node)
            {
                if (pathCache.TryGetValue(node.Id, out var cached)) return cached;
                var path = new List<string>();
                var cur = node.ParentId;
                int guard = 0;
                while (cur != null && guard++ < graph.Nodes.Count)
                {
                    path.Add(cur);
                    cur = graph[cur]?.ParentId;
                }
                path.Reverse(); // outermost container first
                pathCache[node.Id] = path;
                return path;
            }

            foreach (var rank in ranks)
            {
                // Final barycenter per node from its parents' settled order.
                var bary = new Dictionary<string, float>();
                for (int i = 0; i < rank.Count; i++)
                {
                    float value = i;
                    if (parents.TryGetValue(rank[i].Id, out var ps) && ps.Count > 0)
                    {
                        float sum = 0f;
                        int count = 0;
                        foreach (var id in ps)
                            if (order.TryGetValue(id, out var o)) { sum += o; count++; }
                        if (count > 0) value = sum / count;
                    }
                    bary[rank[i].Id] = value;
                }

                var sorted = SortCohesive(rank, 0, bary, PathOf);
                rank.Clear();
                rank.AddRange(sorted);
                for (int i = 0; i < rank.Count; i++)
                    order[rank[i].Id] = i;
            }
        }

        private static List<ProcessNode> SortCohesive(List<ProcessNode> members, int depth,
            Dictionary<string, float> bary, Func<ProcessNode, List<string>> pathOf)
        {
            if (members.Count <= 1) return new List<ProcessNode>(members);

            // Partition into container groups at this depth; nodes without a
            // container at this depth stay singletons.
            var groups = new List<CohesionGroup>();
            var byKey = new Dictionary<string, CohesionGroup>();
            foreach (var m in members)
            {
                var path = pathOf(m);
                string key = path.Count > depth ? path[depth] : null;
                if (key == null)
                {
                    var single = new CohesionGroup();
                    single.Members.Add(m);
                    groups.Add(single);
                }
                else if (byKey.TryGetValue(key, out var grp))
                {
                    grp.Members.Add(m);
                }
                else
                {
                    grp = new CohesionGroup();
                    grp.Members.Add(m);
                    byKey[key] = grp;
                    groups.Add(grp);
                }
            }

            // No shared containers at this depth: plain (stable) barycenter order.
            if (groups.Count == members.Count)
                return members.OrderBy(m => bary[m.Id]).ToList();

            foreach (var grp in groups)
                grp.Mean = grp.Members.Average(m => bary[m.Id]);

            var result = new List<ProcessNode>();
            foreach (var grp in groups.OrderBy(g => g.Mean))
            {
                if (grp.Members.Count == 1) result.Add(grp.Members[0]);
                else result.AddRange(SortCohesive(grp.Members, depth + 1, bary, pathOf));
            }
            return result;
        }

        private class CohesionGroup
        {
            public List<ProcessNode> Members = new List<ProcessNode>();
            public float Mean;
        }

        private static void SortByBarycenter(List<ProcessNode> rank,
            Dictionary<string, List<string>> neighbors, Dictionary<string, int> order)
        {
            var barycenter = new Dictionary<string, float>();
            for (int i = 0; i < rank.Count; i++)
            {
                var node = rank[i];
                float value = i; // nodes without neighbors keep their position
                if (neighbors.TryGetValue(node.Id, out var ids) && ids.Count > 0)
                {
                    float sum = 0;
                    int count = 0;
                    foreach (var id in ids)
                        if (order.TryGetValue(id, out var o)) { sum += o; count++; }
                    if (count > 0) value = sum / count;
                }
                barycenter[node.Id] = value;
            }

            var sorted = rank.OrderBy(n => barycenter[n.Id]).ToList(); // stable
            rank.Clear();
            rank.AddRange(sorted);
            for (int i = 0; i < rank.Count; i++)
                order[rank[i].Id] = i;
        }

        private static Dictionary<string, List<string>> BuildAdjacency(ProcessGraph graph)
        {
            var map = graph.Nodes.ToDictionary(n => n.Id, n => new List<string>());
            foreach (var e in graph.Edges)
                if (map.ContainsKey(e.FromId) && graph.Contains(e.ToId))
                    map[e.FromId].Add(e.ToId);
            return map;
        }

        /// <summary>DFS to flag edges that return to an ancestor (cycles/loops).</summary>
        private static void MarkBackEdges(ProcessGraph graph, Dictionary<string, List<string>> outgoing)
        {
            var state = new Dictionary<string, int>(); // 0=unseen,1=in-stack,2=done
            foreach (var n in graph.Nodes) state[n.Id] = 0;

            var edgeLookup = graph.Edges
                .GroupBy(e => e.FromId)
                .ToDictionary(grp => grp.Key, grp => grp.ToList());

            var roots = Roots(graph);
            foreach (var root in roots)
                Visit(root, state, edgeLookup);

            // Any node not reached (disconnected component) — visit too.
            foreach (var n in graph.Nodes)
                if (state[n.Id] == 0)
                    Visit(n.Id, state, edgeLookup);
        }

        private static void Visit(string id, Dictionary<string, int> state, Dictionary<string, List<ProcessEdge>> edges)
        {
            state[id] = 1;
            if (edges.TryGetValue(id, out var outs))
            {
                foreach (var e in outs)
                {
                    if (!state.ContainsKey(e.ToId)) continue;
                    if (state[e.ToId] == 1) e.IsBack = true;       // edge to an in-stack ancestor
                    else if (state[e.ToId] == 0) Visit(e.ToId, state, edges);
                }
            }
            state[id] = 2;
        }

        private static List<string> Roots(ProcessGraph graph)
        {
            var hasIncoming = new HashSet<string>(graph.Edges.Where(e => !e.IsBack).Select(e => e.ToId));
            var roots = graph.Nodes.Where(n => !hasIncoming.Contains(n.Id)).Select(n => n.Id).ToList();
            if (roots.Count == 0 && graph.Nodes.Count > 0)
                roots.Add(graph.Nodes[0].Id);
            return roots;
        }

        /// <summary>Longest-path ranking over the acyclic (forward) edge set.</summary>
        private static void AssignRanks(ProcessGraph graph, List<ProcessEdge> forward)
        {
            foreach (var n in graph.Nodes) n.Rank = 0;

            var incoming = forward.GroupBy(e => e.ToId).ToDictionary(g => g.Key, g => g.Select(e => e.FromId).ToList());

            // Iterate to a fixed point (graph is a DAG on forward edges).
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < graph.Nodes.Count + 2)
            {
                changed = false;
                foreach (var n in graph.Nodes)
                {
                    if (!incoming.TryGetValue(n.Id, out var preds)) continue;
                    int best = 0;
                    foreach (var p in preds)
                    {
                        var pn = graph[p];
                        if (pn != null && pn.Rank + 1 > best) best = pn.Rank + 1;
                    }
                    if (best != n.Rank) { n.Rank = best; changed = true; }
                }
            }
        }

        // Lane geometry: distance between parallel horizontal runs, and the
        // minimum horizontal clearance for two runs to share a lane.
        private const float LaneSpacing = 10f;
        private const float LaneMinSeparation = 12f;

        private static SizeF Position(ProcessGraph graph, List<List<ProcessNode>> ranks)
        {
            // Per-rank height = tallest node in that rank.
            var rankHeights = ranks.Select(r => r.Max(n => n.Bounds.Height)).ToList();

            // --- X pass: feasible packed start, then median alignment ---
            foreach (var rank in ranks)
            {
                float x = DiagramStyle.Margin;
                foreach (var node in rank)
                {
                    node.Bounds = new RectangleF(x, 0f, node.Bounds.Width, node.Bounds.Height);
                    x += node.Bounds.Width + DiagramStyle.HorizontalGap;
                }
            }
            AlignColumns(graph, ranks);

            // Normalize: leftmost node at the margin, canvas hugs the content.
            float minX = float.MaxValue, maxRight = float.MinValue;
            foreach (var rank in ranks)
                foreach (var node in rank)
                {
                    if (node.Bounds.X < minX) minX = node.Bounds.X;
                    if (node.Bounds.Right > maxRight) maxRight = node.Bounds.Right;
                }
            float shiftAll = DiagramStyle.Margin - minX;
            foreach (var rank in ranks)
                foreach (var node in rank)
                {
                    var b = node.Bounds;
                    b.X += shiftAll;
                    node.Bounds = b;
                }
            float canvasWidth = maxRight + shiftAll + DiagramStyle.Margin;

            // --- Routing pass: give every horizontal run its own lane ---
            var rankIndexByValue = new Dictionary<int, int>();
            for (int r = 0; r < ranks.Count; r++)
                rankIndexByValue[ranks[r][0].Rank] = r;

            var laneOfEdge = new Dictionary<ProcessEdge, KeyValuePair<int, int>>(); // edge -> (gap, lane)
            var laneCount = new int[Math.Max(1, ranks.Count)];
            RouteForwardEdges(graph, rankIndexByValue, laneOfEdge, laneCount);

            // --- Y pass: rows separated by gaps stretched to fit their lanes ---
            var rowTops = new float[ranks.Count];
            var gapHeights = new float[ranks.Count]; // gap BELOW rank r
            float y = DiagramStyle.Margin + DiagramStyle.TitleBandHeight;
            for (int r = 0; r < ranks.Count; r++)
            {
                rowTops[r] = y;
                float rowHeight = rankHeights[r];
                foreach (var node in ranks[r])
                {
                    float ny = y + (rowHeight - node.Bounds.Height) / 2f;
                    node.Bounds = new RectangleF(node.Bounds.X, ny, node.Bounds.Width, node.Bounds.Height);
                }

                float gap = 0f;
                if (r < ranks.Count - 1)
                    gap = Math.Max(DiagramStyle.VerticalGap, laneCount[r] * LaneSpacing + 16f);
                gapHeights[r] = gap;
                y += rowHeight + gap;
            }
            float canvasHeight = y + DiagramStyle.Margin;

            // --- Absolute lane Y per routed edge (lanes centered in their gap) ---
            foreach (var kv in laneOfEdge)
            {
                int gap = kv.Value.Key;
                int lane = kv.Value.Value;
                float gapTop = rowTops[gap] + rankHeights[gap];
                float firstLane = gapTop + (gapHeights[gap] - (laneCount[gap] - 1) * LaneSpacing) / 2f;
                kv.Key.LaneY = firstLane + lane * LaneSpacing;
            }

            // --- Back-edge rails: overlapping loops each get their own rail ---
            float maxRail = AssignBackEdgeRails(graph);
            if (maxRail > 0f)
                canvasWidth = Math.Max(canvasWidth, maxRail + DiagramStyle.Margin);

            return new SizeF(canvasWidth, canvasHeight);
        }

        // ---------- x-coordinate alignment ----------

        private const int AlignmentSweeps = 3;

        /// <summary>
        /// Pulls every node toward the median X of its parents (down sweeps) or
        /// children (up sweeps) while preserving the crossing-reduced order and
        /// minimum spacing. Children end up underneath their parents, so most
        /// edges become straight drops instead of long horizontal runs.
        /// </summary>
        private static void AlignColumns(ProcessGraph graph, List<List<ProcessNode>> ranks)
        {
            var parents = BuildNeighborMap(graph, incoming: true);
            var children = BuildNeighborMap(graph, incoming: false);

            for (int sweep = 0; sweep < AlignmentSweeps; sweep++)
            {
                if (sweep % 2 == 0)
                {
                    for (int r = 1; r < ranks.Count; r++)
                        PlaceRank(ranks[r], parents);
                }
                else
                {
                    for (int r = ranks.Count - 2; r >= 0; r--)
                        PlaceRank(ranks[r], children);
                }
            }
        }

        private static Dictionary<string, List<ProcessNode>> BuildNeighborMap(ProcessGraph graph, bool incoming)
        {
            var map = new Dictionary<string, List<ProcessNode>>();
            foreach (var e in graph.Edges)
            {
                if (e.IsBack) continue;
                var key = incoming ? e.ToId : e.FromId;
                var other = graph[incoming ? e.FromId : e.ToId];
                if (other == null || graph[key] == null) continue;
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = new List<ProcessNode>();
                list.Add(other);
            }
            return map;
        }

        private static float CenterX(ProcessNode n) => n.Bounds.X + n.Bounds.Width / 2f;

        /// <summary>
        /// Places one rank at the nodes' desired centers using cluster merging:
        /// when neighbors want overlapping spots they fuse into a cluster placed
        /// at the mean of their desires, keeping order and minimum separation.
        /// </summary>
        private static void PlaceRank(List<ProcessNode> rank, Dictionary<string, List<ProcessNode>> neighbors)
        {
            int n = rank.Count;

            var desired = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (neighbors.TryGetValue(rank[i].Id, out var ns) && ns.Count > 0)
                {
                    var xs = ns.Select(CenterX).OrderBy(x => x).ToList();
                    desired[i] = xs.Count % 2 == 1
                        ? xs[xs.Count / 2]
                        : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2f;
                }
                else
                {
                    desired[i] = CenterX(rank[i]); // nothing to align to: stay put
                }
            }

            // Minimum center-to-center distance between node i-1 and node i.
            var sep = new float[n];
            for (int i = 1; i < n; i++)
                sep[i] = (rank[i - 1].Bounds.Width + rank[i].Bounds.Width) / 2f + DiagramStyle.HorizontalGap;

            var clusters = new List<Cluster>();
            for (int i = 0; i < n; i++)
            {
                clusters.Add(new Cluster
                {
                    First = i,
                    Last = i,
                    Count = 1,
                    Sum = desired[i],
                    OffsetLast = 0f
                });
                clusters[clusters.Count - 1].Position = desired[i];

                // Merge with the previous cluster while they would overlap.
                while (clusters.Count >= 2)
                {
                    var b = clusters[clusters.Count - 1];
                    var a = clusters[clusters.Count - 2];
                    float gap = sep[b.First]; // b.First >= 1 whenever a exists
                    if (a.Position + a.OffsetLast + gap <= b.Position) break;

                    float shift = a.OffsetLast + gap; // b's first member offset in merged cluster
                    a.Sum += b.Sum - b.Count * shift;
                    a.Count += b.Count;
                    a.OffsetLast = shift + b.OffsetLast;
                    a.Last = b.Last;
                    a.Position = a.Sum / a.Count;
                    clusters.RemoveAt(clusters.Count - 1);
                }
            }

            foreach (var cluster in clusters)
            {
                float offset = 0f;
                for (int i = cluster.First; i <= cluster.Last; i++)
                {
                    if (i > cluster.First) offset += sep[i];
                    float center = cluster.Position + offset;
                    var b = rank[i].Bounds;
                    b.X = center - b.Width / 2f;
                    rank[i].Bounds = b;
                }
            }
        }

        private class Cluster
        {
            public int First;
            public int Last;
            public int Count;
            public float Sum;        // sum of (desired center - offset within cluster)
            public float OffsetLast; // offset of the last member from the first
            public float Position;   // center X of the first member
        }

        /// <summary>
        /// Interval-graph coloring per gap: two runs may share a lane when they
        /// don't overlap horizontally, or when they share a source (fan-out) or
        /// target (fan-in) — those merge into a single visual "bus".
        /// </summary>
        private static void RouteForwardEdges(ProcessGraph graph, Dictionary<int, int> rankIndex,
            Dictionary<ProcessEdge, KeyValuePair<int, int>> laneOfEdge, int[] laneCount)
        {
            var byGap = new Dictionary<int, List<Run>>();
            foreach (var e in graph.Edges)
            {
                e.LaneY = null;
                e.RailX = null;
                if (e.IsBack) continue;

                var from = graph[e.FromId];
                var to = graph[e.ToId];
                if (from == null || to == null) continue;
                if (!rankIndex.TryGetValue(to.Rank, out var targetRank)) continue;
                int gap = targetRank - 1;
                if (gap < 0 || gap >= laneCount.Length) continue;

                float sx = from.Bounds.X + from.Bounds.Width / 2f;
                float tx = to.Bounds.X + to.Bounds.Width / 2f;
                if (Math.Abs(sx - tx) < 0.5f) continue; // straight drop, no lane needed

                if (!byGap.TryGetValue(gap, out var list))
                    byGap[gap] = list = new List<Run>();
                list.Add(new Run
                {
                    Edge = e,
                    Left = Math.Min(sx, tx),
                    Right = Math.Max(sx, tx)
                });
            }

            foreach (var kv in byGap)
            {
                var runs = kv.Value.OrderBy(s => s.Left).ThenBy(s => s.Right).ToList();
                var lanes = new List<List<Run>>();
                foreach (var run in runs)
                {
                    int laneIdx = -1;
                    for (int i = 0; i < lanes.Count && laneIdx < 0; i++)
                    {
                        bool fits = true;
                        foreach (var other in lanes[i])
                        {
                            bool overlaps = run.Left < other.Right + LaneMinSeparation &&
                                            other.Left < run.Right + LaneMinSeparation;
                            bool sharesFlow = run.Edge.FromId == other.Edge.FromId ||
                                              run.Edge.ToId == other.Edge.ToId;
                            if (overlaps && !sharesFlow) { fits = false; break; }
                        }
                        if (fits) laneIdx = i;
                    }
                    if (laneIdx < 0)
                    {
                        laneIdx = lanes.Count;
                        lanes.Add(new List<Run>());
                    }
                    lanes[laneIdx].Add(run);
                    laneOfEdge[run.Edge] = new KeyValuePair<int, int>(kv.Key, laneIdx);
                }
                laneCount[kv.Key] = lanes.Count;
            }
        }

        private class Run
        {
            public ProcessEdge Edge;
            public float Left;
            public float Right;
        }

        /// <summary>Assigns each back edge a right-hand rail; vertically overlapping loops get distinct rails.</summary>
        private static float AssignBackEdgeRails(ProcessGraph graph)
        {
            var backs = new List<Rail>();
            foreach (var e in graph.Edges)
            {
                if (!e.IsBack) continue;
                var from = graph[e.FromId];
                var to = graph[e.ToId];
                if (from == null || to == null) continue;
                float y1 = from.Bounds.Y + from.Bounds.Height / 2f;
                float y2 = to.Bounds.Y + to.Bounds.Height / 2f;
                backs.Add(new Rail
                {
                    Edge = e,
                    Top = Math.Min(y1, y2),
                    Bottom = Math.Max(y1, y2),
                    BaseX = Math.Max(from.Bounds.Right, to.Bounds.Right)
                });
            }
            if (backs.Count == 0) return 0f;

            float maxRail = 0f;
            var laneBottom = new List<float>();
            foreach (var b in backs.OrderBy(b => b.Top))
            {
                int lane = -1;
                for (int i = 0; i < laneBottom.Count; i++)
                {
                    if (laneBottom[i] + 8f <= b.Top) { lane = i; break; }
                }
                if (lane < 0) { lane = laneBottom.Count; laneBottom.Add(b.Bottom); }
                else laneBottom[lane] = b.Bottom;

                float rail = b.BaseX + 24f + lane * 12f;
                b.Edge.RailX = rail;
                if (rail > maxRail) maxRail = rail;
            }
            return maxRail;
        }

        private class Rail
        {
            public ProcessEdge Edge;
            public float Top;
            public float Bottom;
            public float BaseX;
        }
    }
}
