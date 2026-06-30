using System;
using System.Collections.Generic;
using System.Linq;
using DataverseProcessMapper.Models;
using Newtonsoft.Json.Linq;

namespace DataverseProcessMapper.Parsing
{
    /// <summary>
    /// Parses the JSON <c>clientdata</c> definition of a Power Automate (modern)
    /// flow into a process graph.
    ///
    /// The Logic Apps / Power Automate schema expresses ordering through the
    /// <c>runAfter</c> map on each action: an action runs after the listed
    /// actions reach a given status (Succeeded / Failed / Skipped / TimedOut).
    /// Control actions (If / Switch / Foreach / Until / Scope) embed nested
    /// <c>actions</c> blocks which we recurse into.
    /// </summary>
    public class FlowJsonParser : IProcessParser
    {
        public ProcessGraph Parse(ProcessItem item)
        {
            var graph = new ProcessGraph
            {
                Title = item.Name,
                Subtitle = "Power Automate Flow"
            };

            JObject definition;
            try
            {
                definition = LocateDefinition(item.ClientData);
            }
            catch (Exception ex)
            {
                graph.AddNode("Could not parse clientdata JSON:\n" + ex.Message, NodeKind.Note, NodeShape.Rect);
                return graph;
            }

            if (definition == null)
            {
                graph.AddNode("No flow definition found in clientdata.", NodeKind.Note, NodeShape.Rect);
                return graph;
            }

            // --- Trigger(s) become the roots ---
            var triggerIds = new List<string>();
            var triggers = definition["triggers"] as JObject;
            if (triggers != null)
            {
                foreach (var t in triggers.Properties())
                {
                    var node = graph.AddNode(
                        Humanize(t.Name),
                        NodeKind.Trigger,
                        NodeShape.Stadium,
                        id: NodeId("trigger", t.Name),
                        subtitle: ShortType((t.Value as JObject)?["type"]?.ToString()));
                    triggerIds.Add(node.Id);
                }
            }

            if (triggerIds.Count == 0)
            {
                var start = graph.AddNode("Start", NodeKind.Start, NodeShape.Stadium, id: "__start");
                triggerIds.Add(start.Id);
            }

            // --- Actions ---
            var rootActions = definition["actions"] as JObject;
            if (rootActions != null)
            {
                AddActions(graph, rootActions, triggerIds, "act");
            }

            if (graph.Nodes.Count == triggerIds.Count)
            {
                graph.AddNode("Flow has a trigger but no actions.", NodeKind.Note, NodeShape.Rect);
            }

            return graph;
        }

        /// <summary>
        /// Adds every action in a (possibly nested) actions block and wires up the
        /// runAfter edges. <paramref name="parentRoots"/> are the node ids that a
        /// root action of this block (empty runAfter) should connect from.
        /// </summary>
        private void AddActions(ProcessGraph graph, JObject actions, IList<string> parentRoots, string idPrefix)
        {
            // First pass: create a node for every action in this scope.
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in actions.Properties())
            {
                var body = p.Value as JObject;
                var type = body?["type"]?.ToString() ?? "";
                var kind = KindForType(type);
                var shape = ShapeForKind(kind);
                var node = graph.AddNode(
                    Humanize(p.Name),
                    kind,
                    shape,
                    id: NodeId(idPrefix, p.Name),
                    subtitle: ShortType(type));
                ids[p.Name] = node.Id;
            }

            // Second pass: edges + recurse into nested scopes.
            foreach (var p in actions.Properties())
            {
                var body = p.Value as JObject;
                var selfId = ids[p.Name];
                var runAfter = body?["runAfter"] as JObject;

                if (runAfter == null || runAfter.Count == 0)
                {
                    // Root of this scope — connect from the scope's parents.
                    foreach (var root in parentRoots)
                        graph.AddEdge(root, selfId);
                }
                else
                {
                    foreach (var dep in runAfter.Properties())
                    {
                        string fromId;
                        if (!ids.TryGetValue(dep.Name, out fromId)) continue;
                        var statuses = (dep.Value as JArray)?.Select(v => v.ToString()).ToList();
                        var label = StatusLabel(statuses);
                        graph.AddEdge(fromId, selfId, label, dashed: label != null);
                    }
                }

                RecurseControl(graph, body, selfId, idPrefix + "_" + Sanitize(p.Name));
            }
        }

        /// <summary>Recurses into the nested action blocks of control actions.</summary>
        private void RecurseControl(ProcessGraph graph, JObject body, string controlId, string idPrefix)
        {
            if (body == null) return;

            // If: "actions" (true branch) and "else": { "actions": ... } (false branch)
            var trueBranch = body["actions"] as JObject;
            var elseBlock = body["else"] as JObject;
            var falseBranch = elseBlock?["actions"] as JObject;

            var type = body["type"]?.ToString() ?? "";
            bool isIf = type.Equals("If", StringComparison.OrdinalIgnoreCase);

            if (isIf)
            {
                if (trueBranch != null && trueBranch.Count > 0)
                    AddActions(graph, trueBranch, new[] { controlId }, idPrefix + "_yes");
                else
                    LinkBranchPlaceholder(graph, controlId, "Yes", idPrefix + "_yes");

                if (falseBranch != null && falseBranch.Count > 0)
                    AddActions(graph, falseBranch, new[] { controlId }, idPrefix + "_no");
                else
                    LinkBranchPlaceholder(graph, controlId, "No", idPrefix + "_no");

                // Label the first edge of each branch.
                LabelFirstEdges(graph, controlId, idPrefix + "_yes", "Yes");
                LabelFirstEdges(graph, controlId, idPrefix + "_no", "No");
                return;
            }

            // Switch: "cases": { caseName: { "actions": ... } }, "default": { "actions": ... }
            var cases = body["cases"] as JObject;
            if (cases != null)
            {
                foreach (var c in cases.Properties())
                {
                    var caseActions = (c.Value as JObject)?["actions"] as JObject;
                    if (caseActions != null && caseActions.Count > 0)
                        AddActions(graph, caseActions, new[] { controlId }, idPrefix + "_" + Sanitize(c.Name));
                }
                var defActions = (body["default"] as JObject)?["actions"] as JObject;
                if (defActions != null && defActions.Count > 0)
                    AddActions(graph, defActions, new[] { controlId }, idPrefix + "_default");
                return;
            }

            // Foreach / Until / Scope: a single nested "actions" block.
            if (trueBranch != null && trueBranch.Count > 0)
            {
                AddActions(graph, trueBranch, new[] { controlId }, idPrefix + "_body");
            }
        }

        private void LinkBranchPlaceholder(ProcessGraph graph, string controlId, string label, string idPrefix)
        {
            // Empty branch: nothing to draw. (Kept as a hook for future "no-op" nodes.)
        }

        private void LabelFirstEdges(ProcessGraph graph, string controlId, string branchPrefix, string label)
        {
            foreach (var edge in graph.Edges)
            {
                if (edge.FromId == controlId && edge.ToId.StartsWith(branchPrefix, StringComparison.Ordinal)
                    && string.IsNullOrEmpty(edge.Label))
                {
                    edge.Label = label;
                }
            }
        }

        // ---------- helpers ----------

        private static JObject LocateDefinition(string clientData)
        {
            if (string.IsNullOrWhiteSpace(clientData)) return null;
            var root = JObject.Parse(clientData);

            // Common shape: properties.definition
            var def = root.SelectToken("properties.definition") as JObject;
            if (def != null) return def;

            // Sometimes the definition is at the root.
            if (root["triggers"] != null || root["actions"] != null) return root;

            // Or directly under "definition".
            return root["definition"] as JObject;
        }

        private static NodeKind KindForType(string type)
        {
            if (string.IsNullOrEmpty(type)) return NodeKind.Action;
            switch (type.ToLowerInvariant())
            {
                case "if": return NodeKind.Condition;
                case "switch": return NodeKind.Switch;
                case "foreach":
                case "until":
                case "do_until": return NodeKind.Loop;
                case "terminate": return NodeKind.Terminate;
                case "scope": return NodeKind.Note;
                default: return NodeKind.Action;
            }
        }

        private static NodeShape ShapeForKind(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Condition: return NodeShape.Diamond;
                case NodeKind.Switch: return NodeShape.Diamond;
                case NodeKind.Loop: return NodeShape.RoundedRect;
                case NodeKind.Terminate: return NodeShape.Stadium;
                default: return NodeShape.RoundedRect;
            }
        }

        private static string StatusLabel(List<string> statuses)
        {
            if (statuses == null || statuses.Count == 0) return null;
            // The default (implicit) is "Succeeded"; only surface non-default flows.
            if (statuses.Count == 1 && statuses[0].Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
                return null;
            return string.Join("/", statuses);
        }

        private static string ShortType(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            // OpenApiConnection -> "Connector"; otherwise show the raw type.
            if (type.Equals("OpenApiConnection", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("OpenApiConnectionWebhook", StringComparison.OrdinalIgnoreCase))
                return "Connector";
            if (type.Equals("ApiConnection", StringComparison.OrdinalIgnoreCase)) return "Connector";
            return type;
        }

        private static string Humanize(string actionKey)
        {
            if (string.IsNullOrEmpty(actionKey)) return actionKey;
            return actionKey.Replace('_', ' ').Trim();
        }

        private static string NodeId(string prefix, string name) => prefix + "::" + name;

        private static string Sanitize(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray());
    }
}
