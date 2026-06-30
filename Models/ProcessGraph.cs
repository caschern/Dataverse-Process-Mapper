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
