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
            return Position(ranks);
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

            return ranks;
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

        private static SizeF Position(List<List<ProcessNode>> ranks)
        {
            // Per-rank height = tallest node in that rank.
            var rankHeights = ranks.Select(r => r.Max(n => n.Bounds.Height)).ToList();

            // First pass: rank widths to find the widest (canvas width).
            float canvasWidth = 0f;
            var rankWidths = new List<float>();
            foreach (var rank in ranks)
            {
                float w = rank.Sum(n => n.Bounds.Width) + DiagramStyle.HorizontalGap * (rank.Count - 1);
                rankWidths.Add(w);
                if (w > canvasWidth) canvasWidth = w;
            }
            canvasWidth += 2 * DiagramStyle.Margin;

            // Second pass: assign positions, centering each rank.
            float y = DiagramStyle.Margin + DiagramStyle.TitleBandHeight;
            for (int r = 0; r < ranks.Count; r++)
            {
                float rowHeight = rankHeights[r];
                float x = (canvasWidth - rankWidths[r]) / 2f;
                foreach (var node in ranks[r])
                {
                    float ny = y + (rowHeight - node.Bounds.Height) / 2f;
                    node.Bounds = new RectangleF(x, ny, node.Bounds.Width, node.Bounds.Height);
                    x += node.Bounds.Width + DiagramStyle.HorizontalGap;
                }
                y += rowHeight + DiagramStyle.VerticalGap;
            }

            float canvasHeight = y - DiagramStyle.VerticalGap + DiagramStyle.Margin;
            return new SizeF(canvasWidth, canvasHeight);
        }
    }
}
