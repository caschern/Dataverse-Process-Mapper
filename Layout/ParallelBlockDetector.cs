using System.Collections.Generic;
using System.Linq;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Layout
{
    /// <summary>
    /// Finds regions of the graph that provably run concurrently — a single
    /// fan-out node, a single convergence node, and independent chains between
    /// them with no other way in or out.
    ///
    /// Two details make this work on real Power Automate flows:
    ///
    /// 1. Branches are usually multi-step chains, not single nodes. The seven
    ///    branches off a "Compose - Parallelize" each run "List rows" then
    ///    "Set variable" before converging, so a same-rank sibling test would
    ///    miss them entirely.
    /// 2. A container (If / Foreach / Scope) has edges to its own children as
    ///    well as to its successor. Those child edges stay inside the branch, so
    ///    they are excluded when deciding whether a node fans out.
    /// </summary>
    public static class ParallelBlockDetector
    {
        /// <summary>Fewer branches than this is not worth calling out.</summary>
        public const int MinBranches = 3;

        private const int MaxChainLength = 500; // cycle guard

        public static List<ParallelBlock> Detect(ProcessGraph graph)
        {
            var blocks = new List<ParallelBlock>();
            if (graph == null || graph.Nodes.Count == 0) return blocks;

            // Flow-level adjacency: skip loop back-edges, and skip edges that
            // merely connect a container to its own contents.
            var outs = graph.Nodes.ToDictionary(n => n.Id, n => new List<string>());
            var ins = graph.Nodes.ToDictionary(n => n.Id, n => new List<string>());
            foreach (var e in graph.Edges)
            {
                if (e.IsBack) continue;
                if (!outs.ContainsKey(e.FromId) || !ins.ContainsKey(e.ToId)) continue;
                if (IsDescendantOf(graph, e.ToId, e.FromId)) continue;
                outs[e.FromId].Add(e.ToId);
                ins[e.ToId].Add(e.FromId);
            }

            var claimed = new HashSet<string>();

            foreach (var exit in graph.Nodes)
            {
                var preds = ins[exit.Id];
                if (preds.Count < MinBranches) continue;
                if (claimed.Contains(exit.Id)) continue;

                var branches = new List<List<string>>();
                string entry = null;
                bool ok = true;

                foreach (var p in preds)
                {
                    var chain = WalkBackToFanOut(p, outs, ins, out var foundEntry);
                    if (chain == null || foundEntry == null) { ok = false; break; }
                    if (entry == null) entry = foundEntry;
                    else if (entry != foundEntry) { ok = false; break; }
                    branches.Add(chain);
                }

                if (!ok || entry == null) continue;

                // The entry must fan out to these branches and nothing else,
                // otherwise something escapes the region.
                if (outs[entry].Count != branches.Count) continue;
                var heads = new HashSet<string>(branches.Select(b => b[0]));
                if (!outs[entry].All(heads.Contains)) continue;

                // Every branch node must lead only onwards — no shortcuts out.
                var flat = branches.SelectMany(b => b).ToList();
                if (flat.Distinct().Count() != flat.Count) continue;
                if (flat.Any(claimed.Contains)) continue;

                var block = new ParallelBlock
                {
                    EntryId = entry,
                    ExitId = exit.Id,
                    Branches = branches,
                    AllMemberIds = flat.SelectMany(id => WithDescendants(graph, id)).Distinct().ToList()
                };
                blocks.Add(block);

                claimed.Add(exit.Id);
                foreach (var id in block.AllMemberIds) claimed.Add(id);
            }

            return blocks;
        }

        /// <summary>
        /// Walks backwards from a convergence predecessor along its own private
        /// chain. Returns the chain ordered entry-to-exit, and the fan-out node
        /// it hangs off. Returns null when the chain forks or merges on the way,
        /// which means the region is not a clean parallel block.
        /// </summary>
        private static List<string> WalkBackToFanOut(string start,
            Dictionary<string, List<string>> outs, Dictionary<string, List<string>> ins,
            out string entry)
        {
            entry = null;
            var chain = new List<string>();
            var cur = start;

            for (int guard = 0; guard < MaxChainLength; guard++)
            {
                // A node on a parallel branch has exactly one way in and one way
                // out at flow level; anything else means the region leaks.
                if (outs[cur].Count != 1 || ins[cur].Count != 1) return null;

                chain.Insert(0, cur);
                var prev = ins[cur][0];

                if (outs[prev].Count > 1)
                {
                    entry = prev;
                    return chain;
                }
                cur = prev;
            }
            return null;
        }

        private static bool IsDescendantOf(ProcessGraph graph, string candidateId, string ancestorId)
        {
            var node = graph[candidateId];
            for (int guard = 0; node?.ParentId != null && guard < 64; guard++)
            {
                if (node.ParentId == ancestorId) return true;
                node = graph[node.ParentId];
            }
            return false;
        }

        private static IEnumerable<string> WithDescendants(ProcessGraph graph, string id)
        {
            yield return id;
            foreach (var n in graph.Nodes)
                if (n.Id != id && IsDescendantOf(graph, n.Id, id))
                    yield return n.Id;
        }
    }
}
