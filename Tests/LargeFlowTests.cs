using System.IO;
using System.Linq;
using DataverseProcessMapper;
using DataverseProcessMapper.Models;
using Xunit;

namespace DataverseProcessMapper.Tests
{
    /// <summary>
    /// Benchmarks against a real 239-node production flow rather than a synthetic
    /// one, so parser and layout changes are measured on the shape that actually
    /// causes trouble: a long narrow spine with a few very wide parallel fans.
    /// </summary>
    public class LargeFlowTests
    {
        private static ProcessMap Lapd() => ProcessMapBuilder.Build(new ProcessItem
        {
            Name = "Integration - LAPD",
            Category = 5,
            ClientData = File.ReadAllText("lapd-clientdata.json")
        });

        [Fact]
        public void RealFlow_ParsesToTheExpectedShape()
        {
            var g = Lapd().ViewGraph;
            Assert.Equal(239, g.Nodes.Count);
            Assert.Equal(50, g.Nodes.Count(n => n.Kind == NodeKind.Condition));
            Assert.Equal(7, g.Nodes.Count(n => n.Kind == NodeKind.Loop));
            Assert.Single(g.Nodes.Where(n => n.Kind == NodeKind.Trigger));
        }

        [Fact]
        public void RealFlow_DetectsItsThreeParallelBlocks()
        {
            var g = Lapd().ViewGraph;
            var blocks = g.ParallelBlocks
                .OrderBy(b => b.BranchCount)
                .Select(b => (Entry: g[b.EntryId]?.Label, Exit: g[b.ExitId]?.Label, b.BranchCount))
                .ToList();

            Assert.Equal(3, blocks.Count);
            Assert.Contains(blocks, b => b.Entry == "Compose - Parallelize"
                                      && b.Exit == "Condition - Incident Location ne NULL"
                                      && b.BranchCount == 7);
            Assert.Contains(blocks, b => b.Entry == "Condition - Incident found"
                                      && b.Exit == "Filter array - Offense Code Sections"
                                      && b.BranchCount == 8);
        }

        [Fact]
        public void RealFlow_BlocksAreClosedRegions()
        {
            var g = Lapd().ViewGraph;
            foreach (var block in g.ParallelBlocks)
            {
                // Every branch must start at the entry and end at the exit —
                // that closure is what makes "runs in parallel" safe to assert.
                foreach (var branch in block.Branches)
                {
                    Assert.Contains(g.Edges, e => e.FromId == block.EntryId && e.ToId == branch.First());
                    Assert.Contains(g.Edges, e => e.FromId == branch.Last() && e.ToId == block.ExitId);
                }
            }
        }

        [Fact]
        public void RealFlow_LayoutIsDeterministic()
        {
            var a = Lapd();
            var b = Lapd();
            Assert.Equal(a.CanvasSize, b.CanvasSize);
            Assert.Equal(a.ViewGraph.ParallelBlocks.Count, b.ViewGraph.ParallelBlocks.Count);
        }
    }
}
