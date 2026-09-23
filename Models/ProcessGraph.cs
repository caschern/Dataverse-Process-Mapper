using System.Collections.Generic;
using System.Drawing;

namespace DataverseProcessMapper.Models
{
    public enum NodeKind
    {
        Start,
        End,
        Trigger,
        Action,
        Condition,
        Loop,
        Switch,
        Terminate,
        Note,

        /// <summary>One branch of a switch, labelled with the value it matches.</summary>
        Case,

        /// <summary>Marker for a branch that is declared but contains no actions.</summary>
        Empty,

        /// <summary>A Scope action — a named group of steps, often Try / Catch / Finally.</summary>
        Scope,

        /// <summary>A classic workflow Wait: the run pauses until a condition or timeout is met.</summary>
        Wait
    }

    public enum NodeShape
    {
        RoundedRect,
        Rect,
        Diamond,
        Stadium,
        Ellipse
    }

    /// <summary>A single box in a process map.</summary>
    public class ProcessNode
    {
        public string Id { get; set; }

        /// <summary>Primary text shown in the node.</summary>
        public string Label { get; set; }

        /// <summary>Optional secondary text (e.g. the action/connector type).</summary>
        public string Subtitle { get; set; }

        public NodeKind Kind { get; set; }
        public NodeShape Shape { get; set; }

        // --- assigned by the layout engine ---
        public int Rank { get; set; } = -1;
        public RectangleF Bounds { get; set; }

        // --- assigned by the sizing pass (wrapped lines of Label) ---
        public List<string> Lines { get; set; } = new List<string>();

        /// <summary>Id of the containing control action (Scope/If/Loop/Switch); null at top level.</summary>
        public string ParentId { get; set; }

        /// <summary>Ordered label/value pairs describing what the step does (inputs, expressions).</summary>
        public List<KeyValuePair<string, string>> Details { get; set; } = new List<KeyValuePair<string, string>>();

        // --- view state (containers in the interactive preview) ---

        /// <summary>True when this container's descendants are hidden in the view.</summary>
        public bool Collapsed { get; set; }

        /// <summary>Descendants hidden behind this node in the current view (0 = none).</summary>
        public int HiddenCount { get; set; }

        /// <summary>Subtitle including the hidden-step count when collapsed.</summary>
        public string DisplaySubtitle => HiddenCount > 0
            ? (string.IsNullOrEmpty(Subtitle) ? "" : Subtitle + " · ") + HiddenCount + " hidden"
            : Subtitle;
    }

    /// <summary>
    /// The box drawn around an expanded container's contents in the nested
    /// layout: the container's header sits at its top, and the step after the
    /// container leaves from its bottom edge.
    /// </summary>
    public class ContainerFrame
    {
        public string ContainerId { get; set; }
        public RectangleF Bounds { get; set; }

        /// <summary>1 for a top-level container, 2 inside that, and so on.</summary>
        public int Depth { get; set; }
    }

    /// <summary>How a map is positioned.</summary>
    public enum LayoutMode
    {
        /// <summary>One layered diagram; containers are headers with their steps hanging below.</summary>
        Flat,

        /// <summary>Each expanded container is a frame around its own steps (preview).</summary>
        Nested
    }

    /// <summary>A directed connector between two nodes.</summary>
    public class ProcessEdge
    {
        public string FromId { get; set; }
        public string ToId { get; set; }

        /// <summary>Optional branch label (e.g. "Yes", "No", "Failed").</summary>
        public string Label { get; set; }

        public bool Dashed { get; set; }

        /// <summary>True when this edge points "backwards" (a loop) — routed to the side.</summary>
        public bool IsBack { get; set; }

        // --- assigned by the layout engine's routing pass ---

        /// <summary>Absolute Y of this edge's horizontal run (null = midpoint fallback).</summary>
        public float? LaneY { get; set; }

        /// <summary>Absolute X of the right-hand rail for back edges (null = local fallback).</summary>
        public float? RailX { get; set; }

        /// <summary>
        /// Full polyline for edges spanning multiple ranks, routed through the
        /// layout engine's virtual waypoints (null = simple direct route).
        /// </summary>
        public List<PointF> Route { get; set; }
    }

    public class ProcessGraph
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }

        public List<ProcessNode> Nodes { get; } = new List<ProcessNode>();
        public List<ProcessEdge> Edges { get; } = new List<ProcessEdge>();

        /// <summary>
        /// Regions proven to run concurrently. Recomputed on every layout; empty
        /// when nothing in the graph provably forms a parallel block.
        /// </summary>
        public List<ParallelBlock> ParallelBlocks { get; set; } = new List<ParallelBlock>();

        /// <summary>Container frames, outermost first. Empty in the flat layout.</summary>
        public List<ContainerFrame> Frames { get; set; } = new List<ContainerFrame>();

        private readonly Dictionary<string, ProcessNode> _byId = new Dictionary<string, ProcessNode>();
        private int _auto;

        public ProcessNode AddNode(string label, NodeKind kind, NodeShape shape, string id = null, string subtitle = null)
        {
            if (id == null)
            {
                do id = "n" + (_auto++);
                while (_byId.ContainsKey(id));
            }
            else if (_byId.ContainsKey(id))
            {
                // A second node under the same id would silently take over the
                // lookup and orphan the first — a parser bug, so fail loudly.
                throw new System.ArgumentException("Duplicate node id: " + id, nameof(id));
            }

            var node = new ProcessNode
            {
                Id = id,
                Label = string.IsNullOrEmpty(label) ? "(unnamed)" : label,
                Subtitle = subtitle,
                Kind = kind,
                Shape = shape
            };
            Nodes.Add(node);
            _byId[id] = node;
            return node;
        }

        /// <summary>Adds an existing node instance (used when deriving view graphs).</summary>
        public void AddExisting(ProcessNode node)
        {
            Nodes.Add(node);
            _byId[node.Id] = node;
        }

        public ProcessEdge AddEdge(string fromId, string toId, string label = null, bool dashed = false)
        {
            if (fromId == null || toId == null) return null;
            var edge = new ProcessEdge { FromId = fromId, ToId = toId, Label = label, Dashed = dashed };
            Edges.Add(edge);
            return edge;
        }

        public ProcessNode this[string id]
        {
            get
            {
                ProcessNode n;
                return _byId.TryGetValue(id, out n) ? n : null;
            }
        }

        public bool Contains(string id) => _byId.ContainsKey(id);
    }
}
