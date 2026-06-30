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
            return Position(graph);
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

        private static SizeF Position(ProcessGraph graph)
        {
            var byRank = graph.Nodes.GroupBy(n => n.Rank).OrderBy(g => g.Key).ToList();

            // Per-rank height = tallest node in that rank.
            var rankHeights = byRank.ToDictionary(g => g.Key, g => g.Max(n => n.Bounds.Height));

            // First pass: rank widths to find the widest (canvas width).
            float canvasWidth = 0f;
            var rankWidths = new Dictionary<int, float>();
            foreach (var rank in byRank)
            {
                float w = rank.Sum(n => n.Bounds.Width) + DiagramStyle.HorizontalGap * (rank.Count() - 1);
                rankWidths[rank.Key] = w;
                if (w > canvasWidth) canvasWidth = w;
            }
            canvasWidth += 2 * DiagramStyle.Margin;

            // Second pass: assign positions, centering each rank.
            float y = DiagramStyle.Margin + DiagramStyle.TitleBandHeight;
            foreach (var rank in byRank)
            {
                float rowHeight = rankHeights[rank.Key];
                float x = (canvasWidth - rankWidths[rank.Key]) / 2f;
                foreach (var node in rank)
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
