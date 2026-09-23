using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Parsing
{
    /// <summary>
    /// A way out of an emitted node, carrying the label the next connector gets
    /// ("No" for a condition's fall-through), so a branch's meaning survives
    /// until whatever follows it is known.
    /// </summary>
    internal struct Exit
    {
        public readonly string Id;
        public readonly string Label;

        public Exit(string id, string label = null)
        {
            Id = id;
            Label = label;
        }
    }

    /// <summary>A designer step name: "UpdateStep3: Set the stage" → Update, 3, "Set the stage".</summary>
    internal sealed class DesignerStep
    {
        public string Type;
        public string Name;
        public string Description;
    }

    /// <summary>Reading helpers for the XAML the classic workflow designer writes.</summary>
    internal static class ClassicXaml
    {
        private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static readonly Regex StepPattern =
            new Regex(@"^(?<type>[A-Za-z]+?)Step(?<n>\d+)(?:\s*:\s*(?<desc>.*))?$", RegexOptions.Singleline);

        /// <summary>The designer step an element represents, from its DisplayName; null for plumbing.</summary>
        public static DesignerStep Step(XElement el)
        {
            var name = (string)el.Attribute("DisplayName");
            if (string.IsNullOrWhiteSpace(name)) return null;
            var m = StepPattern.Match(name.Trim());
            if (!m.Success) return null;
            var description = m.Groups["desc"].Value.Trim();
            return new DesignerStep
            {
                Type = m.Groups["type"].Value,
                Name = m.Groups["type"].Value + "Step" + m.Groups["n"].Value,
                Description = description.Length == 0 ? null : description
            };
        }

        /// <summary>"Microsoft.Crm.Workflow.Activities.ConditionSequence, …" → "ConditionSequence".</summary>
        public static string ActivityType(XElement el)
        {
            var full = FullActivityType(el);
            if (full == null) return null;
            var dot = full.LastIndexOf('.');
            return dot < 0 ? full : full.Substring(dot + 1);
        }

        public static string FullActivityType(XElement el)
        {
            var aqn = (string)el.Attribute("AssemblyQualifiedName");
            return string.IsNullOrWhiteSpace(aqn) ? null : aqn.Split(',')[0].Trim();
        }

        public static string Key(XElement el) => (string)el.Attribute(XName.Get("Key", XamlNamespace));

        /// <summary>The text of an ActivityReference argument, by its x:Key.</summary>
        public static string Argument(XElement el, string key)
            => Member(el, ".Arguments", key)?.Value;

        /// <summary>An ActivityReference property (Then, Else, Activities…), by its x:Key.</summary>
        public static XElement Property(XElement el, string key) => Member(el, ".Properties", key);

        public static IEnumerable<XElement> Activities(XElement el)
            => Property(el, "Activities")?.Elements() ?? Enumerable.Empty<XElement>();

        public static IEnumerable<XElement> Variables(XElement el)
            => Property(el, "Variables")?.Elements() ?? Enumerable.Empty<XElement>();

        private static XElement Member(XElement el, string suffix, string key)
        {
            var holder = el.Elements().FirstOrDefault(c => c.Name.LocalName.EndsWith(suffix, StringComparison.Ordinal));
            return holder?.Elements().FirstOrDefault(c => Key(c) == key);
        }
    }

    /// <summary>
    /// Walks the XAML the Dataverse classic workflow designer writes, one designer
    /// step at a time.
    ///
    /// The designer does not emit one activity per step. An "Update record" step is
    /// a Sequence of plumbing — Assign, GetEntityProperty, EvaluateExpression, one
    /// SetEntityProperty per field, UpdateEntity, Persist — and a "Check condition"
    /// is a ConditionSequence that computes its tests before each branch. What
    /// marks a real step is its DisplayName: "UpdateStep3", "ConditionStep1: Check
    /// active stage". So this walker recognises steps by that name and treats
    /// everything else inside them as detail, which maps each designer step to
    /// exactly one node.
    /// </summary>
    internal sealed class ClassicDesignerWalker
    {
        private const int MaxLabelLength = 90;

        private readonly ProcessGraph _graph;

        public ClassicDesignerWalker(ProcessGraph graph)
        {
            _graph = graph;
        }

        /// <summary>Walks a list of designer steps in order. Returns the ways out of the last one.</summary>
        public List<Exit> Walk(IEnumerable<XElement> steps, List<Exit> incoming, string parentId)
        {
            var tails = incoming;
            foreach (var el in steps)
                tails = Step(el, tails, parentId);
            return tails;
        }

        private List<Exit> Step(XElement el, List<Exit> incoming, string parentId)
        {
            var step = ClassicXaml.Step(el);
            var local = el.Name.LocalName;

            if (local == "ActivityReference")
            {
                switch (ClassicXaml.ActivityType(el))
                {
                    case "ConditionSequence":
                        return step != null && step.Type == "Wait"
                            ? Wait(el, step, incoming, parentId)
                            : Condition(el, step, incoming, parentId);
                    case "ConditionBranch":
                    case "EvaluateCondition":
                    case "EvaluateLogicalCondition":
                    case "EvaluateExpression":
                        return incoming; // only meaningful inside their condition
                }

                if (step != null && step.Type == "CustomActivity") return CustomActivity(el, step, incoming, parentId);
                if (step != null && step.Type == "Stage") return Stage(el, step, incoming, parentId);
                if (step != null) return Leaf(el, step, incoming, parentId);

                // An unnamed wrapper: its contents are the steps.
                return Walk(ClassicXaml.Activities(el), incoming, parentId);
            }

            if (local == "Sequence")
            {
                return step != null
                    ? Leaf(el, step, incoming, parentId)
                    : Walk(el.Elements(), incoming, parentId);
            }

            // A step activity the designer wrote without its usual wrapper.
            return step != null ? Leaf(el, step, incoming, parentId) : incoming;
        }

        // ---------- actions ----------

        private List<Exit> Leaf(XElement el, DesignerStep step, List<Exit> incoming, string parentId)
        {
            var main = MainActivity(el, step);
            var entity = (string)main?.Attribute("EntityName");
            var lookup = RelatedLookup((string)main?.Attribute("Entity"));
            var kind = step.Type == "StopWorkflow" ? NodeKind.Terminate : NodeKind.Action;
            var stepTitle = StepTitle(step.Type);

            var node = _graph.AddNode(
                Shorten(step.Description ?? DefaultLabel(step.Type, entity, lookup)),
                kind,
                kind == NodeKind.Terminate ? NodeShape.Stadium : NodeShape.RoundedRect,
                subtitle: stepTitle);
            node.ParentId = parentId;

            Add(node, "Step", stepTitle);
            Add(node, "Description", step.Description);
            Add(node, "Table", entity == null ? null : lookup == null ? entity : entity + " (related, via " + lookup + ")");

            var fields = el.Descendants()
                .Where(d => d.Name.LocalName == "SetEntityProperty")
                .Select(d => (string)d.Attribute("Attribute"))
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct()
                .ToList();
            if (fields.Count > 0) Add(node, "Sets", string.Join(", ", fields));

            if (step.Type == "StopWorkflow")
            {
                var status = Regex.Match((string)main?.Attribute("Exception") ?? "", @"OperationStatus\.(\w+)");
                Add(node, "Ends the workflow as", status.Success ? status.Groups[1].Value : null);
            }

            var childWorkflow = (string)main?.Attribute("WorkflowId");
            Add(node, "Child workflow", childWorkflow);
            if (main != null && stepTitle == Words(step.Type))
                Add(node, "Activity", main.Name.LocalName);

            Connect(incoming, node.Id);

            // A stop ends the run: nothing after it in the same block is reached.
            return kind == NodeKind.Terminate ? new List<Exit>() : new List<Exit> { new Exit(node.Id) };
        }

        private List<Exit> CustomActivity(XElement el, DesignerStep step, List<Exit> incoming, string parentId)
        {
            // The composite also holds the designer's own plumbing (including a WF4
            // If that maps outputs) — only the custom activity itself matters.
            var custom = el.Descendants()
                .Where(d => d.Name.LocalName == "ActivityReference")
                .FirstOrDefault(d => !(ClassicXaml.FullActivityType(d) ?? "Microsoft.Crm.").StartsWith("Microsoft.Crm.", StringComparison.Ordinal));
            var typeName = custom == null ? null : ClassicXaml.ActivityType(custom);

            var node = _graph.AddNode(
                Shorten(step.Description ?? (typeName != null ? Words(typeName) : "Custom step")),
                NodeKind.Action, NodeShape.RoundedRect, subtitle: "Custom step");
            node.ParentId = parentId;
            Add(node, "Step", "Custom workflow activity");
            Add(node, "Description", step.Description);
            if (custom != null)
            {
                Add(node, "Activity", ClassicXaml.FullActivityType(custom));
                var aqn = ((string)custom.Attribute("AssemblyQualifiedName") ?? "").Split(',');
                if (aqn.Length > 1) Add(node, "Assembly", aqn[1].Trim());
            }

            Connect(incoming, node.Id);
            return new List<Exit> { new Exit(node.Id) };
        }

        private List<Exit> Stage(XElement el, DesignerStep step, List<Exit> incoming, string parentId)
        {
            var node = _graph.AddNode(Shorten(step.Description ?? "Stage"), NodeKind.Scope, NodeShape.RoundedRect,
                subtitle: "Stage");
            node.ParentId = parentId;
            Add(node, "Step", "Stage");
            Connect(incoming, node.Id);
            return Body(ClassicXaml.Activities(el), node.Id, null, markEmpty: true);
        }

        // ---------- conditions ----------

        /// <summary>
        /// A "Check condition" step: an If, any number of "Otherwise, if"s and an
        /// optional "Otherwise". Drawn as a chain of Yes/No diamonds — each
        /// "Otherwise, if" is the next test on the previous one's No path — which is
        /// exactly what the chain does at run time.
        ///
        /// The whole chain is one designer step, so every diamond after the first
        /// and every branch's steps belong to the first diamond: collapsing it hides
        /// the step, and the nested layout draws the step as a single frame.
        /// </summary>
        private List<Exit> Condition(XElement el, DesignerStep step, List<Exit> incoming, string parentId)
        {
            var activities = ClassicXaml.Activities(el).ToList();
            var text = new ClassicConditionText(el, activities);
            var branches = activities.Where(a => ClassicXaml.ActivityType(a) == "ConditionBranch").ToList();
            bool declaresOtherwise = string.Equals(
                ClassicXaml.Property(el, "ContainsElseBranch")?.Value?.Trim(), "True", StringComparison.OrdinalIgnoreCase);

            var tails = new List<Exit>();
            ProcessNode first = null;
            ProcessNode current = null;
            for (int i = 0; i < branches.Count; i++)
            {
                var branch = branches[i];
                bool otherwise = i > 0 &&
                    (ClassicConditionText.IsUnconditional(branch) || (declaresOtherwise && i == branches.Count - 1));
                if (otherwise)
                {
                    // Runs when no test above matched — there is no fall-through left.
                    tails.AddRange(Body(Then(branch), current.Id, "No", markEmpty: true, parentId: first.Id));
                    return tails;
                }

                var condition = text.For(branch);
                ProcessNode diamond;
                if (current == null)
                {
                    diamond = _graph.AddNode(Shorten(step?.Description ?? condition ?? "Check condition"),
                        NodeKind.Condition, NodeShape.Diamond, subtitle: "Check condition");
                    diamond.ParentId = parentId;
                    Add(diamond, "Step", "Check condition");
                    Add(diamond, "Description", step?.Description);
                    Connect(incoming, diamond.Id);
                    first = diamond;
                }
                else
                {
                    var description = BranchDescription(branch);
                    diamond = _graph.AddNode(Shorten(description ?? condition ?? "Otherwise, if"),
                        NodeKind.Condition, NodeShape.Diamond, subtitle: "Otherwise, if");
                    diamond.ParentId = first.Id;
                    Add(diamond, "Step", "Otherwise, if");
                    Add(diamond, "Description", description);
                    _graph.AddEdge(current.Id, diamond.Id, "No");
                }
                Add(diamond, "If", condition ?? "(the condition could not be read)");

                tails.AddRange(Body(Then(branch), diamond.Id, "Yes", markEmpty: true, parentId: first.Id));
                current = diamond;
            }

            if (current == null)
            {
                // A condition step with no branches at all: say so, and carry on.
                current = _graph.AddNode(Shorten(step?.Description ?? "Check condition"),
                    NodeKind.Condition, NodeShape.Diamond, subtitle: "Check condition");
                current.ParentId = parentId;
                Connect(incoming, current.Id);
            }

            // No "Otherwise": when nothing matched, the workflow simply moves on.
            tails.Add(new Exit(current.Id, "No"));
            return tails;
        }

        // ---------- waits ----------

        /// <summary>
        /// A "Wait" step. The run pauses until one of its branches is satisfied —
        /// a condition, or a timeout — and then runs that branch's steps. A single
        /// branch is one wait node; parallel branches get a chip each, like switch
        /// cases, because only the first one satisfied runs.
        /// </summary>
        private List<Exit> Wait(XElement el, DesignerStep step, List<Exit> incoming, string parentId)
        {
            var activities = ClassicXaml.Activities(el).ToList();
            var text = new ClassicConditionText(el, activities);
            var branches = activities.Where(a => ClassicXaml.ActivityType(a) == "ConditionBranch").ToList();
            var descriptions = branches.Select(b => WaitBranchText(b, text)).ToList();

            string label = step.Description;
            if (label == null && branches.Count == 1)
                label = descriptions[0].StartsWith("Timeout: ", StringComparison.Ordinal)
                    ? "Wait " + descriptions[0].Substring("Timeout: ".Length)
                    : "Wait " + descriptions[0];

            var node = _graph.AddNode(Shorten(label ?? "Wait"), NodeKind.Wait, NodeShape.RoundedRect, subtitle: "Wait");
            node.ParentId = parentId;
            Add(node, "Step", branches.Count > 1 ? "Wait — the first branch satisfied runs" : "Wait");
            Add(node, "Description", step.Description);
            foreach (var d in descriptions) Add(node, "Branch", d);
            Connect(incoming, node.Id);

            if (branches.Count == 0) return new List<Exit> { new Exit(node.Id) };

            if (branches.Count == 1)
            {
                // Steps inside the wait run once it is satisfied; an empty body just
                // means the run continues with whatever follows the wait.
                return Body(Then(branches[0]), node.Id, null, markEmpty: false);
            }

            var tails = new List<Exit>();
            for (int i = 0; i < branches.Count; i++)
            {
                var chip = _graph.AddNode(Shorten(descriptions[i]), NodeKind.Case, NodeShape.Stadium);
                chip.ParentId = node.Id;
                Add(chip, "Runs when", descriptions[i] + ", if that happens first");
                _graph.AddEdge(node.Id, chip.Id);
                tails.AddRange(Body(Then(branches[i]), chip.Id, null, markEmpty: true));
            }
            return tails;
        }

        private static string WaitBranchText(XElement branch, ClassicConditionText text)
        {
            var step = ClassicXaml.Step(branch);
            var condition = text.For(branch);
            if (step != null && step.Type == "WaitTimeout")
            {
                var duration = text.DurationFor(step.Name);
                return duration != null ? "Timeout: " + duration
                     : condition != null ? "Timeout: " + condition
                     : "Timeout";
            }
            return condition != null ? "until " + condition : "until the condition is met";
        }

        // ---------- shared ----------

        /// <summary>
        /// Walks the steps under a branch, starting from <paramref name="fromId"/>.
        /// They belong to <paramref name="parentId"/> — the branch's own node unless
        /// told otherwise. A branch that holds nothing gets an explicit empty marker
        /// when asked, so a path that exists in the workflow is never silently missing.
        /// </summary>
        private List<Exit> Body(IEnumerable<XElement> steps, string fromId, string label, bool markEmpty,
            string parentId = null)
        {
            parentId = parentId ?? fromId;
            var entry = new List<Exit> { new Exit(fromId, label) };
            int before = _graph.Nodes.Count;
            var tails = Walk(steps, entry, parentId);
            if (_graph.Nodes.Count > before || !markEmpty) return tails;

            var marker = _graph.AddNode("(no actions)", NodeKind.Empty, NodeShape.RoundedRect);
            marker.ParentId = parentId;
            _graph.AddEdge(fromId, marker.Id, label);
            return new List<Exit> { new Exit(marker.Id) };
        }

        private static IEnumerable<XElement> Then(XElement branch)
        {
            var then = ClassicXaml.Property(branch, "Then");
            return then == null ? Enumerable.Empty<XElement>() : ClassicXaml.Activities(then);
        }

        private static string BranchDescription(XElement branch)
        {
            var description = ClassicXaml.Property(branch, "Description");
            if (description == null || description.Name.LocalName == "Null") return null;
            var value = description.Value.Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>The activity that does the step's work: it carries the step's own name.</summary>
        private static XElement MainActivity(XElement el, DesignerStep step)
        {
            return el.DescendantsAndSelf()
                .Where(d => d != el)
                .LastOrDefault(d =>
                {
                    var name = ((string)d.Attribute("DisplayName"))?.Trim();
                    return name != null && (name == step.Name || name.StartsWith(step.Name + ":", StringComparison.Ordinal));
                }) ?? el;
        }

        private static string RelatedLookup(string entityExpression)
        {
            if (string.IsNullOrEmpty(entityExpression)) return null;
            var m = Regex.Match(entityExpression, @"related_([A-Za-z0-9_]+)#");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string StepTitle(string type)
        {
            switch (type)
            {
                case "Update": return "Update record";
                case "Create": return "Create record";
                case "Assign": return "Assign record";
                case "SetState":
                case "ChangeStatus": return "Change status";
                case "SendEmail": return "Send email";
                case "ChildWorkflow":
                case "StartChildWorkflow": return "Start child workflow";
                case "StopWorkflow": return "Stop workflow";
                case "Action": return "Perform action";
                default: return Words(type);
            }
        }

        private static string DefaultLabel(string type, string entity, string lookup)
        {
            var target = entity == null ? null : lookup == null ? entity : entity + " (via " + lookup + ")";
            switch (type)
            {
                case "Update": return target == null ? "Update record" : "Update " + target;
                case "Create": return target == null ? "Create record" : "Create " + target;
                case "Assign": return target == null ? "Assign record" : "Assign " + target;
                case "SetState":
                case "ChangeStatus": return target == null ? "Change status" : "Change status of " + target;
                default: return StepTitle(type);
            }
        }

        private void Connect(List<Exit> from, string to)
        {
            var seen = new HashSet<string>();
            foreach (var exit in from)
                if (seen.Add(exit.Id + "|" + exit.Label))
                    _graph.AddEdge(exit.Id, to, exit.Label);
        }

        private static void Add(ProcessNode node, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                node.Details.Add(new KeyValuePair<string, string>(key, value));
        }

        private static string Shorten(string label)
            => label.Length <= MaxLabelLength ? label : label.Substring(0, MaxLabelLength - 1).TrimEnd() + "…";

        private static string Words(string pascal)
            => string.IsNullOrEmpty(pascal) ? pascal : Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ");
    }
}
