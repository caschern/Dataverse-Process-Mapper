using System.Collections.Generic;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Layout
{
    /// <summary>
    /// Derives the visible "view" graph from the full graph and the nodes'
    /// Collapsed flags: descendants of collapsed containers are hidden, and
    /// every edge that crossed a collapsed boundary is re-anchored onto the
    /// collapsed container so the flow stays connected.
    /// </summary>
    public static class GraphContraction
    {
        public static ProcessGraph BuildView(ProcessGraph full)
        {
            foreach (var n in full.Nodes) n.HiddenCount = 0;

            var view = new ProcessGraph { Title = full.Title, Subtitle = full.Subtitle };

            // Representative of each node: itself when visible, otherwise the
            // collapsed ancestor nearest the root (outermost collapse wins).
            var rep = new Dictionary<string, string>();
            foreach (var n in full.Nodes)
            {
                string repId = null;
                var cur = n.ParentId;
                int guard = 0;
                while (cur != null && guard++ < full.Nodes.Count)
                {
                    var parent = full[cur];
                    if (parent == null) break;
                    if (parent.Collapsed) repId = parent.Id;
                    cur = parent.ParentId;
                }
                rep[n.Id] = repId ?? n.Id;
            }

            foreach (var n in full.Nodes)
            {
                if (rep[n.Id] == n.Id)
                {
                    view.AddExisting(n);
                }
                else
                {
                    var container = full[rep[n.Id]];
                    if (container != null) container.HiddenCount++;
                }
            }

            // Re-anchor edges; collapse duplicates and drop self-loops.
            var seen = new HashSet<string>();
            foreach (var e in full.Edges)
            {
                string from, to;
                if (!rep.TryGetValue(e.FromId, out from) || !rep.TryGetValue(e.ToId, out to)) continue;
                if (from == to) continue;
                if (!seen.Add(from + "|" + to)) continue;
                view.AddEdge(from, to, e.Label, e.Dashed);
            }

            return view;
        }
    }
}
