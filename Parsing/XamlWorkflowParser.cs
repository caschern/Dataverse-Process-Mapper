using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
            "Sequence", "Workflow", "Activity", "ActivityBuilder"
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

            // What starts the workflow comes from its columns, not the XAML.
            var start = graph.AddNode("Start", NodeKind.Start, NodeShape.Stadium, id: "__start",
                subtitle: TriggerSummary.ClassicSubtitle(item));
            start.Details = TriggerSummary.ClassicStartDetails(item);
            var entry = new List<Exit> { new Exit(start.Id) };

            var designerBody = root.DescendantsAndSelf().FirstOrDefault(IsDesignerWorkflow);
            List<Exit> tails;
            if (designerBody != null)
                tails = new ClassicDesignerWalker(graph).Walk(designerBody.Elements(), entry, null);
            else
            {
                try
                {
                    tails = WalkGeneric(graph, root, entry);
                }
                catch (InsufficientExecutionStackException)
                {
                    // Thousands of decisions nested in one path: a stack overflow
                    // can't be caught and would close XrmToolBox, so stop short of it.
                    var note = new ProcessGraph { Title = graph.Title, Subtitle = graph.Subtitle };
                    note.AddNode("This workflow is nested too deeply to map.", NodeKind.Note, NodeShape.Rect);
                    return note;
                }
                catch (TooLargeToMapException)
                {
                    var note = new ProcessGraph { Title = graph.Title, Subtitle = graph.Subtitle };
                    note.AddNode("This workflow is too large to map.", NodeKind.Note, NodeShape.Rect);
                    return note;
                }
            }

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

        // Elements WalkNode is inside right now. Links can point anywhere in the
        // document, so reaching one of these again is a cycle, which would
        // otherwise recurse until the stack overflow takes the host down.
        // Per thread: one parser instance is shared by every map being built.
        [ThreadStatic] private static HashSet<XElement> _walking;

        // Far beyond any real workflow.
        private const int MaxGenericNodes = 10000;

        private sealed class TooLargeToMapException : Exception { }

        /// <summary>Walks generic WF4 XAML from its root.</summary>
        private List<Exit> WalkGeneric(ProcessGraph graph, XElement root, List<Exit> entry)
        {
            // The root itself (DynamicActivity, a custom *Activity) is no step, so it
            // is walked by its children; a Flowchart among them is walked in place,
            // keeping the steps before and after it.
            var tails = Local(root).Equals("Flowchart", StringComparison.OrdinalIgnoreCase)
                ? WalkNode(graph, root, entry, null)
                : WalkSequence(graph, root, entry, null);
            if (graph.Nodes.Count > 1) return tails;

            // Nothing on the walked path (the flowchart sits in a property element
            // such as DynamicActivity.Implementation): draw the first flowchart.
            var flowchart = root.Descendants().FirstOrDefault(e => Local(e) == "Flowchart");
            return flowchart != null ? WalkNode(graph, flowchart, entry, null) : tails;
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
            RuntimeHelpers.EnsureSufficientExecutionStack();
            // Malformed links (flow nodes inside an inline Sequence) can make the
            // walk draw the same steps again for every decision, doubling each
            // time; stop long before that would hang the tool.
            if (graph.Nodes.Count > MaxGenericNodes) throw new TooLargeToMapException();
            if (_walking == null) _walking = new HashSet<XElement>();
            if (!_walking.Add(el)) return incoming;
            try
            {
                return WalkActivity(graph, el, incoming, parentId);
            }
            finally
            {
                _walking.Remove(el);
            }
        }

        private List<Exit> WalkActivity(ProcessGraph graph, XElement el, List<Exit> incoming, string parentId)
        {
            var name = Local(el);

            if (name.Equals("If", StringComparison.OrdinalIgnoreCase))
                return WalkIf(graph, el, incoming, parentId);

            if (name.Equals("Flowchart", StringComparison.OrdinalIgnoreCase))
                return WalkFlowchart(graph, el, incoming, parentId);

            // A flow node outside any flowchart is still walked by its links.
            if (IsFlowNode(name))
                return WalkFlowNode(graph, el, incoming, parentId, new FlowWalk(el.Document?.Root ?? el));

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

        // ---------- fallback: WF4 flowcharts ----------
        //
        // A Flowchart is not an ordered list: it runs from its StartNode along each
        // node's links — FlowStep.Next, FlowDecision.True/False, a FlowSwitch's
        // cases and Default — and its child list only declares the nodes. Links are
        // inline elements or x:Reference to a node's x:Name, so a node can be
        // reached twice: from another path (a join, drawn as an ordinary edge) or
        // from its own continuation (a loop back, drawn dashed like a loop's).
        // Branches are links, not containers, so they are drawn flat.

        private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static readonly Regex ReferenceMarkup =
            new Regex(@"^\s*\{\s*\w+:Reference\s+(?:Name\s*=\s*)?([^}\s]+)\s*\}\s*$");

        /// <summary>One flowchart walk: its nodes by x:Name, and where each one was drawn.</summary>
        private sealed class FlowWalk
        {
            private readonly Dictionary<string, XElement> _named = new Dictionary<string, XElement>(StringComparer.Ordinal);

            /// <summary>Visited flow node → its first box (null when it drew none).</summary>
            public readonly Dictionary<XElement, string> Drawn = new Dictionary<XElement, string>();

            /// <summary>Flow nodes on the path being walked; reaching one again is a loop.</summary>
            public readonly HashSet<XElement> OnPath = new HashSet<XElement>();

            /// <summary>Steps that drew nothing, being passed through to their Next; guards a cycle of them.</summary>
            public readonly HashSet<XElement> PassingThrough = new HashSet<XElement>();

            public FlowWalk(XElement scope)
            {
                foreach (var e in scope.DescendantsAndSelf())
                {
                    var name = e.Attribute(XName.Get("Name", XamlNamespace))?.Value;
                    if (!string.IsNullOrEmpty(name) && !_named.ContainsKey(name)) _named[name] = e;
                }
            }

            public XElement Find(string name)
            {
                XElement el;
                return name != null && _named.TryGetValue(name.Trim(), out el) ? el : null;
            }

            /// <summary>The node an &lt;x:Reference&gt; element stands for; any other element is itself.</summary>
            public XElement Resolve(XElement el)
                => IsReference(el) ? Find(OwnText(el)) : el;
        }

        private static bool IsReference(XElement el) => el.Name == XName.Get("Reference", XamlNamespace);

        private static bool IsXamlKey(XElement el) => el.Name == XName.Get("Key", XamlNamespace);

        // The designer writes a switch case as <x:Reference>name<x:Key>1</x:Key></x:Reference>:
        // the name is the element's own text, not the key nested after it.
        private static string OwnText(XElement el) => string.Concat(el.Nodes().OfType<XText>().Select(t => t.Value));

        private static bool IsFlowNode(string name)
            => name.Equals("FlowStep", StringComparison.OrdinalIgnoreCase)
               || name.Equals("FlowDecision", StringComparison.OrdinalIgnoreCase)
               || name.Equals("FlowSwitch", StringComparison.OrdinalIgnoreCase);

        private List<Exit> WalkFlowchart(ProcessGraph graph, XElement el, List<Exit> incoming, string parentId)
        {
            var flow = new FlowWalk(el);
            var start = FlowLink(el, "StartNode", flow);
            return start != null ? WalkFlowNode(graph, start, incoming, parentId, flow) : incoming;
        }

        /// <summary>The node a link property points to: an element (inline or x:Reference) or "{x:Reference name}".</summary>
        private static XElement FlowLink(XElement el, string property, FlowWalk flow)
        {
            var inline = ChildBranch(el, property);
            if (inline != null) return inline;

            var value = el.Attributes().FirstOrDefault(a =>
                a.Name.LocalName.Equals(property, StringComparison.OrdinalIgnoreCase))?.Value;
            var match = value == null ? null : ReferenceMarkup.Match(value);
            return match != null && match.Success ? flow.Find(match.Groups[1].Value) : null;
        }

        /// <summary>Walks one flow node and, through its links, everything after it. Returns the path ends.</summary>
        /// <remarks>
        /// A chain of Next links is followed in a loop, not by recursion, so the
        /// stack grows with the decisions and switches on a path, not its length.
        /// </remarks>
        private List<Exit> WalkFlowNode(ProcessGraph graph, XElement el, List<Exit> incoming, string parentId, FlowWalk flow)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();

            // What this call put on the path or passed through, taken off again on
            // return, as each recursive call used to do on its own way back.
            var onPath = new List<XElement>();
            var passed = new List<XElement>();
            try
            {
                while (true)
                {
                    var link = el;
                    el = flow.Resolve(el);
                    if (el == null) return incoming;

                    // Already drawn: link to it; what follows it is drawn already too.
                    string drawnId;
                    if (flow.Drawn.TryGetValue(el, out drawnId))
                    {
                        // A step with no box of its own (a Delay, say) stands for what it
                        // leads to, so the arrival goes on to its Next — or, with no Next, on
                        // to End. Only a cycle made of such steps alone ends here.
                        if (drawnId == null)
                        {
                            if (!flow.PassingThrough.Add(el)) return new List<Exit>();
                            passed.Add(el);
                            el = FlowLink(el, "Next", flow);
                            if (el == null) return incoming;
                            continue;
                        }
                        if (flow.OnPath.Contains(el))
                        {
                            foreach (var exit in incoming)
                                graph.AddEdge(exit.Id, drawnId, exit.Label ?? "repeat", dashed: true);
                        }
                        else
                        {
                            Connect(graph, incoming, drawnId);
                        }
                        return new List<Exit>();
                    }

                    var name = Local(el);
                    if (!IsFlowNode(name))
                    {
                        // Links are flow nodes. A reference to anything else (the
                        // flowchart itself, say) is a missing link; an activity written
                        // inline in a link (FlowDecision.True) is walked as one.
                        return link == el && IsInline(el) ? WalkNode(graph, el, incoming, parentId) : incoming;
                    }

                    flow.OnPath.Add(el);
                    onPath.Add(el);
                    if (!name.Equals("FlowStep", StringComparison.OrdinalIgnoreCase))
                        return WalkFlowBranches(graph, el, name, incoming, parentId, flow);

                    // The step's action, then whatever its Next leads to.
                    int before = graph.Nodes.Count;
                    var action = el.Elements().FirstOrDefault(c => !Local(c).Contains(".") && !IsMetadata(c));
                    if (action != null) incoming = WalkNode(graph, action, incoming, parentId);
                    flow.Drawn[el] = graph.Nodes.Count > before ? graph.Nodes[before].Id : null;
                    el = FlowLink(el, "Next", flow);
                    if (el == null) return incoming;
                }
            }
            finally
            {
                foreach (var n in onPath) flow.OnPath.Remove(n);
                foreach (var n in passed) flow.PassingThrough.Remove(n);
            }
        }

        /// <summary>
        /// True for an element written inside a link's property element
        /// (FlowDecision.True…); false for one declared in a flowchart's list or
        /// anywhere else a reference can reach.
        /// </summary>
        private static bool IsInline(XElement el) => el.Parent != null && Local(el.Parent).Contains(".");

        /// <summary>A FlowDecision or FlowSwitch, then everything its branches lead to.</summary>
        private List<Exit> WalkFlowBranches(ProcessGraph graph, XElement el, string name, List<Exit> incoming, string parentId, FlowWalk flow)
        {
            List<Exit> tails;
            if (name.Equals("FlowDecision", StringComparison.OrdinalIgnoreCase))
            {
                var condition = graph.AddNode(DisplayName(el) ?? "Condition", NodeKind.Condition, NodeShape.Diamond,
                    subtitle: "Check condition");
                condition.ParentId = parentId;
                condition.Details = ActivityDetails(el);
                Connect(graph, incoming, condition.Id);
                flow.Drawn[el] = condition.Id;

                // A missing side ends the flowchart there, keeping its Yes/No label.
                tails = new List<Exit>();
                tails.AddRange(FollowLink(graph, FlowLink(el, "True", flow),
                    new List<Exit> { new Exit(condition.Id, "Yes") }, parentId, flow));
                tails.AddRange(FollowLink(graph, FlowLink(el, "False", flow),
                    new List<Exit> { new Exit(condition.Id, "No") }, parentId, flow));
            }
            else
            {
                var sw = graph.AddNode(DisplayName(el) ?? "Switch", NodeKind.Switch, NodeShape.Diamond,
                    subtitle: "Switch");
                sw.ParentId = parentId;
                sw.Details = ActivityDetails(el);
                Connect(graph, incoming, sw.Id);
                flow.Drawn[el] = sw.Id;

                // Cases are the switch's own child nodes, keyed by the value they match.
                tails = new List<Exit>();
                foreach (var c in el.Elements().Where(c => !Local(c).Contains(".") && !IsMetadata(c)))
                    tails.AddRange(FollowLink(graph, c, new List<Exit> { new Exit(sw.Id, CaseKey(c)) }, parentId, flow));
                var fallback = FlowLink(el, "Default", flow);
                if (fallback != null)
                    tails.AddRange(FollowLink(graph, fallback, new List<Exit> { new Exit(sw.Id, "default") }, parentId, flow));
                if (tails.Count == 0 && !graph.Edges.Any(e => e.FromId == sw.Id)) tails.Add(new Exit(sw.Id));
            }
            return tails;
        }

        private List<Exit> FollowLink(ProcessGraph graph, XElement target, List<Exit> incoming, string parentId, FlowWalk flow)
            => target != null ? WalkFlowNode(graph, target, incoming, parentId, flow) : incoming;

        /// <summary>Explicit "this branch does nothing" node, so empty branches stay visible.</summary>
        private static ProcessNode AddEmptyMarker(ProcessGraph graph, string parentId, string edgeLabel = null)
        {
            var node = graph.AddNode("(no actions)", NodeKind.Empty, NodeShape.RoundedRect);
            node.ParentId = parentId;
            graph.AddEdge(parentId, node.Id, edgeLabel);
            return node;
        }

        private static readonly Regex NullMarkup = new Regex(@"^\s*\{\s*\w+:Null\s*\}\s*$");

        /// <summary>
        /// The value a switch case matches: its x:Key attribute, or the x:Key
        /// element the designer writes for a typed key (&lt;x:Key&gt;1&lt;/x:Key&gt;,
        /// &lt;x:Key&gt;&lt;x:String&gt;Quorum&lt;/x:String&gt;&lt;/x:Key&gt;).
        /// </summary>
        private static string CaseKey(XElement branch)
        {
            var key = branch.Attributes().FirstOrDefault(a =>
                a.Name.LocalName.Equals("Key", StringComparison.OrdinalIgnoreCase))?.Value;
            if (key == null)
            {
                var element = branch.Elements().FirstOrDefault(IsXamlKey);
                var typed = element?.Elements().FirstOrDefault();
                if (typed != null && typed.Name == XName.Get("Null", XamlNamespace)) return "null";
                key = (typed ?? element)?.Value;
            }
            if (string.IsNullOrWhiteSpace(key)) return "case";
            // The null key, written {x:Null}, matches a null value.
            return NullMarkup.IsMatch(key) ? "null" : key.Trim();
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
                || n.Equals("ImportReference", StringComparison.OrdinalIgnoreCase)
                || IsXamlKey(el); // a switch case's key, not a step
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
