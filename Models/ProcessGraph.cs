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
        Note
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

        private readonly Dictionary<string, ProcessNode> _byId = new Dictionary<string, ProcessNode>();
        private int _auto;

        public ProcessNode AddNode(string label, NodeKind kind, NodeShape shape, string id = null, string subtitle = null)
        {
            id = id ?? "n" + (_auto++);
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
