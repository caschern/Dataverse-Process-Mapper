using System;
using System.Collections.Generic;
using System.Linq;
using DataverseProcessMapper.Layout;
using DataverseProcessMapper.Models;
using Xunit;

namespace DataverseProcessMapper.Tests
{
    public class LayoutTests
    {
        /// <summary>
        /// Three anchored parallel chains plus two crossing edges whose
        /// barycenter values tie, so crossing reduction cannot untangle them —
        /// the crossers are forced onto overlapping horizontal runs.
        /// </summary>
        private static ProcessGraph CrossingGraph()
        {
            var g = new ProcessGraph { Title = "crossing" };
            g.AddNode("Root", NodeKind.Start, NodeShape.Stadium, id: "root");
            for (int i = 0; i < 3; i++)
            {
                g.AddNode("A" + i, NodeKind.Action, NodeShape.RoundedRect, id: "a" + i);
                g.AddNode("B" + i, NodeKind.Action, NodeShape.RoundedRect, id: "b" + i);
                g.AddEdge("root", "a" + i);
                g.AddEdge("a" + i, "b" + i);
            }
            g.AddEdge("a0", "b2"); // crossers with tied barycenters
            g.AddEdge("a2", "b0");
            return g;
        }

        private static ProcessGraph Layout(ProcessGraph g)
        {
            NodeSizer.MeasureAll(g);
            LayeredLayoutEngine.Layout(g);
            return g;
        }

        [Fact]
        public void Nodes_DoNotOverlap()
        {
            var g = Layout(CrossingGraph());
            var nodes = g.Nodes;
            for (int i = 0; i < nodes.Count; i++)
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    var a = nodes[i].Bounds;
                    var b = nodes[j].Bounds;
                    a.Inflate(-1f, -1f); // 1px tolerance
                    Assert.False(a.IntersectsWith(b),
                        $"{nodes[i].Label} overlaps {nodes[j].Label}");
                }
        }

        [Fact]
        public void CrossingRuns_GetDistinctLanes()
        {
            var g = Layout(CrossingGraph());

            var jogging = g.Edges.Where(e =>
            {
                if (e.IsBack || e.LaneY == null) return false;
                var from = g[e.FromId];
                var to = g[e.ToId];
                float sx = from.Bounds.X + from.Bounds.Width / 2f;
                float tx = to.Bounds.X + to.Bounds.Width / 2f;
                return Math.Abs(sx - tx) >= 0.5f;
            }).ToList();

            Assert.True(jogging.Count >= 2, "expected at least the two crossing edges to jog");

            // Any two overlapping runs from different sources into different
            // targets must sit on distinct lanes.
            for (int i = 0; i < jogging.Count; i++)
                for (int j = i + 1; j < jogging.Count; j++)
                {
                    var e1 = jogging[i];
                    var e2 = jogging[j];
                    if (e1.FromId == e2.FromId || e1.ToId == e2.ToId) continue;

                    var i1 = RunInterval(g, e1);
                    var i2 = RunInterval(g, e2);
                    bool overlap = i1.Item1 < i2.Item2 && i2.Item1 < i1.Item2;
                    if (!overlap) continue;

                    Assert.True(Math.Abs(e1.LaneY.Value - e2.LaneY.Value) >= 9f,
                        $"{e1.FromId}->{e1.ToId} and {e2.FromId}->{e2.ToId} share a lane");
                }
        }

        [Fact]
        public void LaneY_SitsInsideItsGap()
        {
            var g = Layout(CrossingGraph());
            foreach (var e in g.Edges.Where(e => e.LaneY != null))
            {
                var from = g[e.FromId];
                var to = g[e.ToId];
                Assert.True(e.LaneY.Value >= from.Bounds.Bottom - 0.5f,
                    "lane must be below the source node");
                Assert.True(e.LaneY.Value <= to.Bounds.Y + 0.5f,
                    "lane must be above the target node");
            }
        }

        [Fact]
        public void BusyGap_StretchesToFitItsLanes()
        {
            // Full-reversal crossers: every b(k) has parents a(k) and a(w-1-k),
            // so all barycenters tie at (w-1)/2 and crossing reduction cannot
            // reorder anything — the mutually overlapping runs MUST get their
            // own lanes, stretching the gap.
            var g = new ProcessGraph { Title = "busy" };
            const int width = 6;
            g.AddNode("Root", NodeKind.Start, NodeShape.Stadium, id: "root");
            for (int i = 0; i < width; i++)
            {
                g.AddNode("A" + i, NodeKind.Action, NodeShape.RoundedRect, id: "a" + i);
                g.AddNode("B" + i, NodeKind.Action, NodeShape.RoundedRect, id: "b" + i);
                g.AddEdge("root", "a" + i);
                g.AddEdge("a" + i, "b" + i);                    // anchors
                g.AddEdge("a" + i, "b" + (width - 1 - i));      // reversal crossers
            }
            Layout(g);

            float aBottom = Enumerable.Range(0, width).Max(i => g["a" + i].Bounds.Bottom);
            float bTop = Enumerable.Range(0, width).Min(i => g["b" + i].Bounds.Y);
            Assert.True(bTop - aBottom > 46f + 0.5f,
                $"busy gap should stretch beyond the default 46px, was {bTop - aBottom}");
        }

        [Fact]
        public void Children_AlignUnderTheirParents()
        {
            // Two parallel branches; without alignment the grandchildren pack
            // left and land far from their parents.
            var g = new ProcessGraph { Title = "align" };
            g.AddNode("Root", NodeKind.Start, NodeShape.Stadium, id: "root");
            g.AddNode("Left branch step", NodeKind.Action, NodeShape.RoundedRect, id: "a");
            g.AddNode("Right branch step", NodeKind.Action, NodeShape.RoundedRect, id: "b");
            g.AddNode("Left child", NodeKind.Action, NodeShape.RoundedRect, id: "a1");
            g.AddNode("Right child", NodeKind.Action, NodeShape.RoundedRect, id: "b1");
            g.AddEdge("root", "a");
            g.AddEdge("root", "b");
            g.AddEdge("a", "a1");
            g.AddEdge("b", "b1");
            Layout(g);

            float Center(string id) => g[id].Bounds.X + g[id].Bounds.Width / 2f;
            Assert.True(Math.Abs(Center("a1") - Center("a")) < 1f,
                $"left child should sit under its parent (parent {Center("a")}, child {Center("a1")})");
            Assert.True(Math.Abs(Center("b1") - Center("b")) < 1f,
                $"right child should sit under its parent (parent {Center("b")}, child {Center("b1")})");
        }

        [Fact]
        public void ContainerMembers_StayContiguous_WithinARank()
        {
            // a1 is contained in scope A but edge-fed from scope B, so its raw
            // barycenter lands between B's members — pure barycenter ordering
            // interleaves the groups as [a2, b2, a1, b1]. Cohesion must pull
            // A's members back into one contiguous block.
            var g = new ProcessGraph { Title = "cohesion" };
            g.AddNode("Root", NodeKind.Start, NodeShape.Stadium, id: "root");
            g.AddNode("Scope A", NodeKind.Loop, NodeShape.RoundedRect, id: "A");
            g.AddNode("Scope B", NodeKind.Loop, NodeShape.RoundedRect, id: "B");
            g.AddNode("A member 1", NodeKind.Action, NodeShape.RoundedRect, id: "a1");
            g.AddNode("A member 2", NodeKind.Action, NodeShape.RoundedRect, id: "a2");
            g.AddNode("B member 1", NodeKind.Action, NodeShape.RoundedRect, id: "b1");
            g.AddNode("B member 2", NodeKind.Action, NodeShape.RoundedRect, id: "b2");
            g["a1"].ParentId = "A";
            g["a2"].ParentId = "A";
            g["b1"].ParentId = "B";
            g["b2"].ParentId = "B";
            g.AddEdge("root", "A");
            g.AddEdge("root", "B");
            g.AddEdge("A", "a2");   // barycenter 0
            g.AddEdge("B", "a1");   // barycenter 1 — interleaves into B's range
            g.AddEdge("A", "b2");
            g.AddEdge("B", "b2");   // barycenter 0.5
            g.AddEdge("B", "b1");   // barycenter 1
            Layout(g);

            var rank = new[] { g["a1"], g["a2"], g["b1"], g["b2"] }
                .OrderBy(n => n.Bounds.X).ToList();
            int firstA = rank.FindIndex(n => n.ParentId == "A");
            int lastA = rank.FindLastIndex(n => n.ParentId == "A");
            Assert.True(lastA - firstA == 1,
                "scope A's members must be adjacent, got order: " +
                string.Join(", ", rank.Select(n => n.Id)));
        }

        [Fact]
        public void MultiRankEdge_RoutesAroundNodesInItsColumn()
        {
            // a -> b -> c plus a skip edge a -> c. The skip edge's virtual node
            // reserves a channel beside b, so no vertical segment of its route
            // may pass through b's box.
            var g = new ProcessGraph { Title = "route" };
            g.AddNode("A", NodeKind.Action, NodeShape.RoundedRect, id: "a");
            g.AddNode("B", NodeKind.Action, NodeShape.RoundedRect, id: "b");
            g.AddNode("C", NodeKind.Action, NodeShape.RoundedRect, id: "c");
            g.AddEdge("a", "b");
            g.AddEdge("b", "c");
            g.AddEdge("a", "c");
            Layout(g);

            var skip = g.Edges.Single(e => e.FromId == "a" && e.ToId == "c");
            Assert.NotNull(skip.Route);
            Assert.True(skip.Route.Count >= 2, "route must be a drawable polyline");

            var blocker = g["b"].Bounds;
            for (int i = 0; i < skip.Route.Count - 1; i++)
            {
                var p = skip.Route[i];
                var q = skip.Route[i + 1];
                if (Math.Abs(p.X - q.X) > 0.01f) continue; // only vertical segments matter
                bool inX = p.X > blocker.X - 1f && p.X < blocker.Right + 1f;
                bool inY = Math.Max(p.Y, q.Y) > blocker.Y && Math.Min(p.Y, q.Y) < blocker.Bottom;
                Assert.False(inX && inY,
                    $"vertical at x={p.X} passes through B [{blocker.X}..{blocker.Right}]");
            }
        }

        [Fact]
        public void OverlappingBackEdges_GetDistinctRails()
        {
            var g = new ProcessGraph { Title = "loops" };
            g.AddNode("N0", NodeKind.Action, NodeShape.RoundedRect, id: "n0");
            g.AddNode("N1", NodeKind.Loop, NodeShape.RoundedRect, id: "n1");
            g.AddNode("N2", NodeKind.Loop, NodeShape.RoundedRect, id: "n2");
            g.AddNode("N3", NodeKind.Action, NodeShape.RoundedRect, id: "n3");
            g.AddEdge("n0", "n1");
            g.AddEdge("n1", "n2");
            g.AddEdge("n2", "n3");
            g.AddEdge("n3", "n1"); // loop spanning n1..n3
            g.AddEdge("n3", "n2"); // loop spanning n2..n3 — vertically overlapping
            Layout(g);

            var rails = g.Edges.Where(e => e.IsBack).Select(e => e.RailX).ToList();
            Assert.Equal(2, rails.Count);
            Assert.All(rails, r => Assert.NotNull(r));
            Assert.True(Math.Abs(rails[0].Value - rails[1].Value) >= 8f,
                "overlapping loops must not share a rail");
        }

        [Fact]
        public void FullPipeline_OnSampleFlow_ProducesRoutedOverlapFreeMap()
        {
            var map = ProcessMapBuilder.Build(FlowJsonParserTests.SampleFlow());

            Assert.NotNull(map.ViewGraph);
            Assert.True(map.CanvasSize.Width > 0 && map.CanvasSize.Height > 0);

            var nodes = map.ViewGraph.Nodes;
            for (int i = 0; i < nodes.Count; i++)
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    var a = nodes[i].Bounds;
                    var b = nodes[j].Bounds;
                    a.Inflate(-1f, -1f);
                    Assert.False(a.IntersectsWith(b),
                        $"{nodes[i].Label} overlaps {nodes[j].Label}");
                }
        }

        private static Tuple<float, float> RunInterval(ProcessGraph g, ProcessEdge e)
        {
            var from = g[e.FromId];
            var to = g[e.ToId];
            float sx = from.Bounds.X + from.Bounds.Width / 2f;
            float tx = to.Bounds.X + to.Bounds.Width / 2f;
            return Tuple.Create(Math.Min(sx, tx), Math.Max(sx, tx));
        }
    }
}
