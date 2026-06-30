using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Parsing
{
    /// <summary>
    /// Parses the WF4 (Windows Workflow Foundation) XAML stored in the
    /// <c>xaml</c> column of a classic Dataverse workflow into a process graph.
    ///
    /// Real classic-workflow XAML is verbose and varies between designer
    /// versions, so this is a pragmatic <em>structural</em> walker rather than a
    /// full WF4 interpreter: it walks the activity tree in document order,
    /// emits a node for each recognised step activity, and branches for
    /// control-flow activities (If / Flowchart-FlowDecision / While / Switch).
    /// Unknown leaf elements are ignored to keep the map readable.
    /// </summary>
    public class XamlWorkflowParser : IProcessParser
    {
        // Activity local-names treated as visible "steps".
        private static readonly HashSet<string> StepActivities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CreateEntity", "UpdateEntity", "AssignEntity", "DeleteEntity",
            "SetEntityProperty", "SetState", "SetStateActivity", "SetStatus",
            "SendEmail", "SendEmailFromTemplate", "SendEmailActivity",
            "StartWorkflow", "ExecuteWorkflow", "StartChildWorkflow",
            "PerformAction", "Assign", "InvokeMethod", "WriteLine",
            "Persist", "CustomActivity"
        };

        // Containers we just descend into without emitting a node of their own.
        private static readonly HashSet<string> TransparentContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sequence", "Workflow", "Activity", "ActivityBuilder", "Flowchart.StartNode"
        };

        public ProcessGraph Parse(ProcessItem item)
        {
            var graph = new ProcessGraph
            {
                Title = item.Name,
                Subtitle = "Classic Workflow" + (string.IsNullOrEmpty(item.PrimaryEntity) ? "" : " · " + item.PrimaryEntity)
            };

            XElement root;
            try
            {
                root = XDocument.Parse(item.Xaml).Root;
            }
            catch (Exception ex)
            {
                graph.AddNode("Could not parse workflow XAML:\n" + ex.Message, NodeKind.Note, NodeShape.Rect);
                return graph;
            }

            if (root == null)
            {
                graph.AddNode("Empty workflow XAML.", NodeKind.Note, NodeShape.Rect);
                return graph;
            }

            var start = graph.AddNode("Start", NodeKind.Start, NodeShape.Stadium, id: "__start");

            var body = FindWorkflowBody(root);
            var tails = WalkSequence(graph, body, new List<string> { start.Id });

            // Only one node so far (the Start) means nothing recognisable was found.
            if (graph.Nodes.Count == 1)
            {
                graph.AddNode("No recognised workflow steps were found in the XAML.\n" +
                              "The definition may use only expressions or a custom format.",
                              NodeKind.Note, NodeShape.Rect);
                return graph;
            }

            var end = graph.AddNode("End", NodeKind.End, NodeShape.Stadium, id: "__end");
            foreach (var t in tails.Distinct())
                graph.AddEdge(t, end.Id);

            return graph;
        }

        /// <summary>Finds the element that holds the ordered workflow steps.</summary>
        private static XElement FindWorkflowBody(XElement root)
        {
            // Prefer a Flowchart or the first Sequence anywhere in the tree.
            var flowchart = root.Descendants().FirstOrDefault(e => Local(e) == "Flowchart");
            if (flowchart != null) return flowchart;

            var sequence = root.Descendants().FirstOrDefault(e => Local(e) == "Sequence");
            if (sequence != null) return sequence;

            return root;
        }

        /// <summary>
        /// Walks the children of <paramref name="container"/> as an ordered
        /// sequence, chaining each emitted node to the previous tail(s).
        /// Returns the tail node ids to connect onward.
        /// </summary>
        private List<string> WalkSequence(ProcessGraph graph, XElement container, List<string> incoming)
        {
            var tails = incoming;
            foreach (var child in container.Elements())
            {
                // Skip property-element nodes like "If.Then" at the wrong level and
                // designer/metadata members; those are handled by their owners.
                if (Local(child).Contains(".")) continue;
                if (IsMetadata(child)) continue;

                tails = WalkNode(graph, child, tails);
            }
            return tails;
        }

        /// <summary>Walks a single activity element. Returns its tail node ids.</summary>
        private List<string> WalkNode(ProcessGraph graph, XElement el, List<string> incoming)
        {
            var name = Local(el);

            // --- Control flow: If ---
            if (name.Equals("If", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("FlowDecision", StringComparison.OrdinalIgnoreCase))
            {
                return WalkIf(graph, el, incoming);
            }

            // --- Control flow: While / DoWhile ---
            if (name.Equals("While", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DoWhile", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ForEach", StringComparison.OrdinalIgnoreCase))
            {
                return WalkLoop(graph, el, incoming);
            }

            // --- Control flow: Switch ---
            if (name.StartsWith("Switch", StringComparison.OrdinalIgnoreCase))
            {
                return WalkSwitch(graph, el, incoming);
            }

            // --- Transparent containers: descend without a node ---
            if (TransparentContainers.Contains(name))
            {
                return WalkSequence(graph, el, incoming);
            }

            // --- Recognised step activity ---
            if (IsStep(name))
            {
                var node = graph.AddNode(DisplayName(el) ?? Prettify(name), NodeKind.Action, NodeShape.RoundedRect,
                    subtitle: Prettify(name));
                Connect(graph, incoming, node.Id);
                return new List<string> { node.Id };
            }

            // --- Unknown element: if it has step-bearing children, descend; else ignore ---
            if (el.Elements().Any(c => !IsMetadata(c)))
            {
                return WalkSequence(graph, el, incoming);
            }

            return incoming;
        }

        private List<string> WalkIf(ProcessGraph graph, XElement el, List<string> incoming)
        {
            var condition = graph.AddNode(DisplayName(el) ?? "Condition", NodeKind.Condition, NodeShape.Diamond,
                subtitle: "Check condition");
            Connect(graph, incoming, condition.Id);

            var tails = new List<string>();

            var thenEl = ChildBranch(el, "Then");
            if (thenEl != null)
            {
                var thenTails = WalkSequence(graph, thenEl, new List<string> { condition.Id });
                LabelEdges(graph, condition.Id, "Yes");
                tails.AddRange(thenTails);
            }
            else
            {
                tails.Add(condition.Id); // fall-through "Yes" with no body
            }

            var elseEl = ChildBranch(el, "Else");
            if (elseEl != null)
            {
                var elseTails = WalkSequence(graph, elseEl, new List<string> { condition.Id });
                LabelEdges(graph, condition.Id, "No");
                tails.AddRange(elseTails);
            }
            else
            {
                tails.Add(condition.Id); // fall-through "No"
            }

            return tails.Distinct().ToList();
        }

        private List<string> WalkLoop(ProcessGraph graph, XElement el, List<string> incoming)
        {
            var loop = graph.AddNode(DisplayName(el) ?? Prettify(Local(el)), NodeKind.Loop, NodeShape.RoundedRect,
                subtitle: "Loop");
            Connect(graph, incoming, loop.Id);

            var bodyEl = ChildBranch(el, "Body") ?? el;
            var bodyTails = WalkSequence(graph, bodyEl, new List<string> { loop.Id });

            // Back-edge from the body's tail to the loop header.
            foreach (var t in bodyTails.Distinct())
                if (t != loop.Id)
                    graph.AddEdge(t, loop.Id, "repeat", dashed: true);

            return new List<string> { loop.Id };
        }

        private List<string> WalkSwitch(ProcessGraph graph, XElement el, List<string> incoming)
        {
            var sw = graph.AddNode(DisplayName(el) ?? "Switch", NodeKind.Switch, NodeShape.Diamond,
                subtitle: "Switch");
            Connect(graph, incoming, sw.Id);

            var tails = new List<string>();
            // Cases are typically property-elements like "Switch.Cases" or inline cases.
            var caseContainers = el.Elements().Where(c => Local(c).EndsWith("Cases", StringComparison.OrdinalIgnoreCase)
                                                          || Local(c).Equals("FlowSwitch", StringComparison.OrdinalIgnoreCase));
            foreach (var cc in caseContainers)
            {
                foreach (var branch in cc.Elements())
                {
                    var branchTails = WalkSequence(graph, branch, new List<string> { sw.Id });
                    tails.AddRange(branchTails);
                }
            }

            if (tails.Count == 0) tails.Add(sw.Id);
            return tails.Distinct().ToList();
        }

        // ---------- helpers ----------

        private static void Connect(ProcessGraph graph, List<string> from, string to)
        {
            foreach (var f in from.Distinct())
                graph.AddEdge(f, to);
        }

        /// <summary>Adds a branch label to the most recent unlabeled edge out of a node.</summary>
        private static void LabelEdges(ProcessGraph graph, string fromId, string label)
        {
            var edge = graph.Edges.LastOrDefault(e => e.FromId == fromId && string.IsNullOrEmpty(e.Label));
            if (edge != null) edge.Label = label;
        }

        /// <summary>Returns the child element for a property-element branch (e.g. "If.Then").</summary>
        private static XElement ChildBranch(XElement el, string branch)
        {
            // Property element form: <If.Then>...</If.Then>
            var prop = el.Elements().FirstOrDefault(c =>
                Local(c).EndsWith("." + branch, StringComparison.OrdinalIgnoreCase));
            if (prop != null)
            {
                // The actual activity is the single child of the property element.
                return prop.Elements().FirstOrDefault() ?? prop;
            }

            // Attribute-less inline form is uncommon; nothing to return.
            return null;
        }

        private static bool IsStep(string localName)
        {
            if (StepActivities.Contains(localName)) return true;
            // Heuristic: many D365 activities end with "Entity" or "Activity".
            return localName.EndsWith("Entity", StringComparison.OrdinalIgnoreCase)
                || localName.EndsWith("Activity", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMetadata(XElement el)
        {
            var n = Local(el);
            // x:Members, x:Class, TextExpression namespaces, designer goo, variables, arguments.
            return n.Equals("Members", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Class", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("Property", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("TextExpression", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("WorkflowViewState", StringComparison.OrdinalIgnoreCase)
                || n.Equals("Variables", StringComparison.OrdinalIgnoreCase)
                || n.Equals("ImportReference", StringComparison.OrdinalIgnoreCase);
        }

        private static string DisplayName(XElement el)
        {
            var dn = el.Attributes().FirstOrDefault(a =>
                a.Name.LocalName.Equals("DisplayName", StringComparison.OrdinalIgnoreCase))?.Value;
            return string.IsNullOrWhiteSpace(dn) ? null : dn.Trim();
        }

        private static string Prettify(string localName)
        {
            if (string.IsNullOrEmpty(localName)) return localName;
            // Insert spaces before capitals: "CreateEntity" -> "Create Entity".
            var chars = new List<char>();
            for (int i = 0; i < localName.Length; i++)
            {
                if (i > 0 && char.IsUpper(localName[i]) && !char.IsUpper(localName[i - 1]))
                    chars.Add(' ');
                chars.Add(localName[i]);
            }
            return new string(chars.ToArray());
        }

        private static string Local(XElement el) => el.Name.LocalName;
    }
}
