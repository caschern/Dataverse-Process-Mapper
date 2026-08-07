using System.Collections.Generic;
using System.Linq;

namespace DataverseProcessMapper.Models
{
    /// <summary>
    /// A set of branches that provably run concurrently: every branch starts at
    /// one entry node, every branch ends at one exit node, and nothing else
    /// enters or leaves the region in between.
    ///
    /// This is deliberately conservative. The map only ever states "these run in
    /// parallel" when the definition proves it — an unprovable fan-out is left
    /// exactly as it was rather than guessed at.
    /// </summary>
    public class ParallelBlock
    {
        /// <summary>The node the branches fan out from.</summary>
        public string EntryId { get; set; }

        /// <summary>The node every branch converges on.</summary>
        public string ExitId { get; set; }

        /// <summary>
        /// Each branch as an ordered chain of flow-level node ids, running from
        /// the entry towards the exit. Nested container contents are not listed
        /// here — see <see cref="AllMemberIds"/>.
        /// </summary>
        public List<List<string>> Branches { get; set; } = new List<List<string>>();

        /// <summary>
        /// Every node inside the region, including the contents of any container
        /// on a branch. Used for bounds and hit-testing.
        /// </summary>
        public List<string> AllMemberIds { get; set; } = new List<string>();

        public int BranchCount => Branches.Count;

        public IEnumerable<string> BranchHeadIds => Branches.Where(b => b.Count > 0).Select(b => b[0]);

        /// <summary>Caption shown on the block, e.g. "runs in parallel · 8 branches".</summary>
        public string Caption => "runs in parallel · " + BranchCount + " branches";
    }
}
