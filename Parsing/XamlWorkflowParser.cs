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
    /// XAML written by the Dataverse workflow designer (everything under an
    /// <c>mxswa:Workflow</c> element) is read one designer step at a time by
    /// <see cref="ClassicDesignerWalker"/>. Any other WF4 XAML falls back to a
    /// structural walker that emits a node per recognised activity and branches
    /// for If / While / Switch.
    /// </summary>
    public class XamlWorkflowParser : IProcessParser
    {
        // Activity local-names treated as visible "steps" by the fallback walker.
        private static readonly HashSet<string> StepActivities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CreateEntity", "UpdateEntity", "AssignEntity", "DeleteEntity",
            "SetEntityProperty", "SetState", "SetStateActivity", "SetStatus",
            "SendEmail", "SendEmailFromTemplate", "SendEmailActivity",
            "StartWorkflow", "ExecuteWorkflow", "StartChildWorkflow",
            "PerformAction", "Assign", "InvokeMethod", "WriteLine",
            "Persist", "CustomActivity"
        };

        // Containers the fallback walker descends into without a node of their own.
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

            if (string.IsNullOrWhiteSpace(item.Xaml))
            {
                graph.AddNode("Empty workflow XAML.", NodeKind.Note, NodeShape.Rect);
                return graph;
            }

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
            var entry = new List<Exit> { new Exit(start.Id) };

            var designerBody = root.DescendantsAndSelf().FirstOrDefault(IsDesignerWorkflow);
            var tails = designerBody != null
                ? new ClassicDesignerWalker(graph).Walk(designerBody.Elements(), entry, null)
                : WalkSequence(graph, FindWorkflowBody(root), entry, null);

            // Only one node so far (the Start) means nothing recognisable was found.
            if (graph.Nodes.Count == 1)
            {
                graph.AddNode(designerBody != null
                        ? "This workflow has no steps."
                        : "No recognised workflow steps were found in the XAML.\n" +
                          "The definition may use only expressions or a custom format.",
                    NodeKind.Note, NodeShape.Rect);
                return graph;
            }

            // Every path may have ended in "Stop workflow" — then there is no End to reach.
            if (tails.Count > 0)
            {
                var end = graph.AddNode("End", NodeKind.End, NodeShape.Stadium, id: "__end");
                Connect(graph, tails, end.Id);
            }

            return graph;
        }

        /// <summary>The designer's step container: mxswa:Workflow from the Xrm workflow SDK.</summary>
        private static bool IsDesignerWorkflow(XElement el)
            => el.Name.LocalName == "Workflow"
               && el.Name.NamespaceName.IndexOf("Microsoft.Xrm.Sdk.Workflow", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---------- fallback: generic WF4 XAML ----------

        /// <summary>Finds the element that holds the ordered workflow steps.</summary>
        private static XElement FindWorkflowBody(XElement root)
        {
            // A Flowchart is its own body; otherwise walk from the root, whose
            // transparent containers lead to every step rather than only the first.
            return root.Descendants().FirstOrDefault(e => Local(e) == "Flowchart") ?? root;
        }

        /// <summary>
        /// Walks the children of <paramref name="container"/> as an ordered
        /// sequence, chaining each emitted node to the previous exit(s).
        /// </summary>
        private List<Exit> WalkSequence(ProcessGraph graph, XElement container, List<Exit> incoming, string parentId)
        {
            var tails = incoming;
            foreach (var child in container.Elements())
            {
                // Skip property-element nodes like "If.Then" at the wrong level and
                // designer/metadata members; those are handled by their owners.
                if (Local(child).Contains(".")) continue;
                if (IsMetadata(child)) continue;

                tails = WalkNode(graph, child, tails, parentId);
            }
            return tails;
        }

        /// <summary>Walks a single activity element. Returns its exits.</summary>
        private List<Exit> WalkNode(ProcessGraph graph, XElement el, List<Exit> incoming, string parentId)
        {
            var name = Local(el);

            if (name.Equals("If", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("FlowDecision", StringComparison.OrdinalIgnoreCase))
                return WalkIf(graph, el, incoming, parentId);

            if (name.Equals("While", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("DoWhile", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ForEach", StringComparison.OrdinalIgnoreCase))
                return WalkLoop(graph, el, incoming, parentId);

            if (name.StartsWith("Switch", StringComparison.OrdinalIgnoreCase))
                return WalkSwitch(graph, el, incoming, parentId);

            if (TransparentContainers.Contains(name))
                return WalkSequence(graph, el, incoming, parentId);

            if (IsStep(name))
            {
                var node = graph.AddNode(DisplayName(el) ?? Prettify(name), NodeKind.Action, NodeShape.RoundedRect,
                    subtitle: Prettify(name));
                node.ParentId = parentId;
                node.Details = ActivityDetails(el);
                Connect(graph, incoming, node.Id);
                return new List<Exit> { new Exit(node.Id) };
            }

            // Unknown element: if it has step-bearing children, descend; else ignore.
            if (el.Elements().Any(c => !IsMetadata(c)))
                return WalkSequence(graph, el, incoming, parentId);

            return incoming;
        }

        private List<Exit> WalkIf(ProcessGraph graph, XElement el, List<Exit> incoming, string parentId)
        {
            var condition = graph.AddNode(DisplayName(el) ?? "Condition", NodeKind.Condition, NodeShape.Diamond,
                subtitle: "Check condition");
            condition.ParentId = parentId;
            condition.Details = ActivityDetails(el);
            Connect(graph, incoming, condition.Id);

            var tails = new List<Exit>();
            tails.AddRange(WalkBranch(graph, ChildBranch(el, "Then"), condition.Id, "Yes"));
            tails.AddRange(WalkBranch(graph, ChildBranch(el, "Else"), condition.Id, "No"));
            return tails;
        }

        /// <summary>
        /// One side of an If. A missing branch falls straight through, keeping its
        /// Yes/No label for whatever follows; a branch that exists but holds no
        /// steps is still a real branch and gets an explicit marker.
        /// </summary>
        private List<Exit> WalkBranch(ProcessGraph graph, XElement branch, string conditionId, string label)
        {
            var entry = new List<Exit> { new Exit(conditionId, label) };
            if (branch == null) return entry;

            // The branch element is the activity itself — a Sequence, or a single
            // step — so it is walked as a node, not by its children.
            int before = graph.Nodes.Count;
            var tails = WalkNode(graph, branch, entry, conditionId);
            if (graph.Nodes.Count > before) return tails;

            return new List<Exit> { new Exit(AddEmptyMarker(graph, conditionId, label).Id) };
        }

        private List<Exit> WalkLoop(ProcessGraph graph, XElement el, List<Exit> incoming, string parentId)
        {
            var loop = graph.AddNode(DisplayName(el) ?? Prettify(Local(el)), NodeKind.Loop, NodeShape.RoundedRect,
                subtitle: "Loop");
            loop.ParentId = parentId;
            loop.Details = ActivityDetails(el);
            Connect(graph, incoming, loop.Id);

            var bodyEl = ChildBranch(el, "Body");
            var entry = new List<Exit> { new Exit(loop.Id) };
            int before = graph.Nodes.Count;
            var bodyTails = bodyEl != null
                ? WalkNode(graph, bodyEl, entry, loop.Id)
                : WalkSequence(graph, el, entry, loop.Id);
            if (graph.Nodes.Count == before)
                bodyTails = new List<Exit> { new Exit(AddEmptyMarker(graph, loop.Id).Id) };

            // Back-edge from the body's tail to the loop header.
            foreach (var t in bodyTails.Select(t => t.Id).Distinct())
                if (t != loop.Id)
                    graph.AddEdge(t, loop.Id, "repeat", dashed: true);

            return new List<Exit> { new Exit(loop.Id) };
        }

        private List<Exit> WalkSwitch(ProcessGraph graph, XElement el, List<Exit> incoming, string parentId)
        {
            var sw = graph.AddNode(DisplayName(el) ?? "Switch", NodeKind.Switch, NodeShape.Diamond,
                subtitle: "Switch");
            sw.ParentId = parentId;
            sw.Details = ActivityDetails(el);
            Connect(graph, incoming, sw.Id);

            var tails = new List<Exit>();
            // Cases are typically property-elements like "Switch.Cases" or inline cases.
            var caseContainers = el.Elements().Where(c => Local(c).EndsWith("Cases", StringComparison.OrdinalIgnoreCase)
                                                          || Local(c).Equals("FlowSwitch", StringComparison.OrdinalIgnoreCase));
            foreach (var cc in caseContainers)
            {
                foreach (var branch in cc.Elements())
                {
                    // A chip carries the matched value, so every branch is identifiable.
                    var chip = graph.AddNode(CaseKey(branch), NodeKind.Case, NodeShape.Stadium);
                    chip.ParentId = sw.Id;
                    graph.AddEdge(sw.Id, chip.Id);

                    int before = graph.Nodes.Count;
                    var branchTails = WalkSequence(graph, branch, new List<Exit> { new Exit(chip.Id) }, chip.Id);
                    if (graph.Nodes.Count == before)
                        branchTails = new List<Exit> { new Exit(AddEmptyMarker(graph, chip.Id).Id) };
                    tails.AddRange(branchTails);
                }
            }

            if (tails.Count == 0) tails.Add(new Exit(sw.Id));
            return tails;
        }

        /// <summary>Explicit "this branch does nothing" node, so empty branches stay visible.</summary>
        private static ProcessNode AddEmptyMarker(ProcessGraph graph, string parentId, string edgeLabel = null)
        {
            var node = graph.AddNode("(no actions)", NodeKind.Empty, NodeShape.RoundedRect);
            node.ParentId = parentId;
            graph.AddEdge(parentId, node.Id, edgeLabel);
            return node;
        }

        /// <summary>The value a switch case matches, taken from its x:Key attribute.</summary>
        private static string CaseKey(XElement branch)
        {
            var key = branch.Attributes().FirstOrDefault(a =>
                a.Name.LocalName.Equals("Key", StringComparison.OrdinalIgnoreCase))?.Value;
            return string.IsNullOrWhiteSpace(key) ? "case" : key.Trim();
        }

        // ---------- helpers ----------

        /// <summary>Details from the activity's XML attributes (entity names, expressions…).</summary>
        private static List<KeyValuePair<string, string>> ActivityDetails(XElement el)
        {
            var details = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Activity", Prettify(Local(el)))
            };
            foreach (var a in el.Attributes())
            {
                if (a.IsNamespaceDeclaration) continue;
                if (a.Name.LocalName.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)) continue;
                details.Add(new KeyValuePair<string, string>(a.Name.LocalName, DetailText.Clean(a.Value)));
            }
            return details;
        }

        private static void Connect(ProcessGraph graph, List<Exit> from, string to)
        {
            var seen = new HashSet<string>();
            foreach (var exit in from)
                if (seen.Add(exit.Id + "|" + exit.Label))
                    graph.AddEdge(exit.Id, to, exit.Label);
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
