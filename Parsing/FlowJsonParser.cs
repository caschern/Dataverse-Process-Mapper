using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DataverseProcessMapper.Models;
using Newtonsoft.Json;
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
                        id: ChildId("trigger", t.Name),
                        subtitle: ShortType((t.Value as JObject)?["type"]?.ToString()));
                    node.Details = ExtractDetails(t.Value as JObject);
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
        /// root action of this block (empty runAfter) should connect from, and
        /// <paramref name="idPrefix"/> is the block's path (see <see cref="ChildId"/>).
        /// </summary>
        private void AddActions(ProcessGraph graph, JObject actions, IList<string> parentRoots, string idPrefix,
            string parentId = null)
        {
            // First pass: create a node for every action in this scope. Names that
            // differ only in case ("Cond", "cond") are distinct actions, so each
            // keeps its own id; runAfter is matched exact-case first, and only then
            // case-insensitively, where the first such action wins.
            var created = new List<KeyValuePair<JProperty, string>>();
            var exactIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var looseIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    id: ChildId(idPrefix, p.Name),
                    subtitle: ShortType(type));
                node.ParentId = parentId;
                node.Details = ExtractDetails(body);
                created.Add(new KeyValuePair<JProperty, string>(p, node.Id));
                exactIds[p.Name] = node.Id;
                if (!looseIds.ContainsKey(p.Name)) looseIds[p.Name] = node.Id;
            }

            // Second pass: edges + recurse into nested scopes.
            foreach (var pair in created)
            {
                var body = pair.Key.Value as JObject;
                var selfId = pair.Value;
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
                        if (!exactIds.TryGetValue(dep.Name, out fromId)
                            && !looseIds.TryGetValue(dep.Name, out fromId)) continue;
                        var statuses = (dep.Value as JArray)?.Select(v => v.ToString()).ToList();
                        var label = StatusLabel(statuses);
                        graph.AddEdge(fromId, selfId, label, dashed: label != null);
                    }
                }

                RecurseControl(graph, body, selfId);
            }
        }

        /// <summary>Recurses into the nested action blocks of control actions.</summary>
        private void RecurseControl(ProcessGraph graph, JObject body, string controlId)
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
                var yesPath = BranchPath(controlId, "yes");
                var noPath = BranchPath(controlId, "no");

                if (trueBranch != null && trueBranch.Count > 0)
                    AddActions(graph, trueBranch, new[] { controlId }, yesPath, controlId);
                else
                    LinkBranchPlaceholder(graph, controlId, "Yes", yesPath);

                if (falseBranch != null && falseBranch.Count > 0)
                    AddActions(graph, falseBranch, new[] { controlId }, noPath, controlId);
                else
                    LinkBranchPlaceholder(graph, controlId, "No", noPath);

                // Label the first edge of each branch.
                LabelFirstEdges(graph, controlId, yesPath, "Yes");
                LabelFirstEdges(graph, controlId, noPath, "No");
                return;
            }

            // Switch: "cases": { key: { "case": <value>, "actions": … } }, "default": { "actions": … }
            // A Switch may declare only a default (no "cases", or not an object):
            // it is still a switch, never a single-body container.
            var cases = body["cases"] as JObject;
            if (cases != null || type.Equals("Switch", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in cases?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var caseObj = c.Value as JObject;
                    var value = CaseValue(caseObj);
                    AddCaseBranch(graph, caseObj?["actions"] as JObject, controlId,
                        CaseTitle(c.Name, value), BranchPath(controlId, "case:" + Escape(c.Name)),
                        value, isDefault: false);
                }

                // Drawn whenever the default branch is declared, even when it is empty.
                var defaultObj = body["default"] as JObject;
                if (defaultObj != null)
                    AddCaseBranch(graph, defaultObj["actions"] as JObject, controlId,
                        "default", BranchPath(controlId, "default"), null, isDefault: true);
                return;
            }

            // Foreach / Until / Scope: a single nested "actions" block.
            var bodyPath = BranchPath(controlId, "body");
            if (trueBranch != null && trueBranch.Count > 0)
            {
                AddActions(graph, trueBranch, new[] { controlId }, bodyPath, controlId);
            }
            else if (IsControlType(type))
            {
                // A container that declares no actions still says so out loud.
                AddEmptyMarker(graph, controlId, bodyPath);
            }
        }

        /// <summary>
        /// Adds one switch branch: a chip node titled the way the Power Automate
        /// designer titles it, with that case's actions beneath it. The value the
        /// case matches is kept as well — shortened under the title when the title
        /// is the author's own words, and in full in the details. A declared case
        /// with no actions still gets its chip plus an empty marker, so a branch
        /// that exists in the flow is never silently missing from the map.
        /// </summary>
        private void AddCaseBranch(ProcessGraph graph, JObject actions, string switchId,
            string title, string idPrefix, string matchValue, bool isDefault)
        {
            // Only repeat the value under the title when the title is not already it.
            string subtitle = matchValue != null && title != matchValue
                ? "equals " + ShortValue(matchValue)
                : null;

            var chip = graph.AddNode(title, NodeKind.Case, NodeShape.Stadium,
                id: SyntheticId(idPrefix, "case"), subtitle: subtitle);
            chip.ParentId = switchId;
            if (isDefault)
                chip.Details.Add(new KeyValuePair<string, string>("Runs when", "no other case matches"));
            else if (matchValue != null)
                chip.Details.Add(new KeyValuePair<string, string>("Equals", matchValue));
            graph.AddEdge(switchId, chip.Id);

            if (actions != null && actions.Count > 0)
                AddActions(graph, actions, new[] { chip.Id }, idPrefix, chip.Id);
            else
                AddEmptyMarker(graph, chip.Id, idPrefix);
        }

        /// <summary>Explicit "this branch does nothing" node, so empty branches stay visible.</summary>
        private static ProcessNode AddEmptyMarker(ProcessGraph graph, string parentId, string idPrefix, string edgeLabel = null)
        {
            var node = graph.AddNode("(no actions)", NodeKind.Empty, NodeShape.RoundedRect,
                id: SyntheticId(idPrefix, "empty"));
            node.ParentId = parentId;
            graph.AddEdge(parentId, node.Id, edgeLabel);
            return node;
        }

        /// <summary>
        /// The chip's title, matching the designer. A renamed case carries its
        /// author's title in its key — "Case_-_Order_approved" is shown by
        /// the designer as "Case - Order approved" — and that is far more
        /// readable than the matched value, which is often a record GUID. Only an
        /// untouched designer default ("Case", "Case_2") says nothing, and for
        /// those the matched value is the better label.
        /// </summary>
        private static string CaseTitle(string key, string matchValue)
        {
            if (IsDefaultCaseKey(key) && !string.IsNullOrWhiteSpace(matchValue))
                return matchValue;
            return Humanize(key);
        }

        /// <summary>True for the names the designer assigns before anyone renames a case.</summary>
        private static bool IsDefaultCaseKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            if (key.Equals("Case", StringComparison.OrdinalIgnoreCase)) return true;
            return key.Length > 5
                && key.StartsWith("Case_", StringComparison.OrdinalIgnoreCase)
                && key.Substring(5).All(char.IsDigit);
        }

        /// <summary>The value a case matches on, from the case's own "case" property.</summary>
        private static string CaseValue(JObject caseObj)
        {
            var value = caseObj?["case"];
            if (value == null || value.Type == JTokenType.Null) return null;
            var text = Compact(value);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>
        /// Shortens a long match value for display under the title, keeping both
        /// ends — for a GUID the start and end are what tell two apart. The full
        /// value stays in the details, so nothing is lost.
        /// </summary>
        private static string ShortValue(string value)
            => value.Length <= 24
                ? value
                : DetailText.Head(value, 8) + "…" + DetailText.Tail(value, 8);

        private void LinkBranchPlaceholder(ProcessGraph graph, string controlId, string label, string idPrefix)
        {
            AddEmptyMarker(graph, controlId, idPrefix, label);
        }

        private void LabelFirstEdges(ProcessGraph graph, string controlId, string branchPath, string label)
        {
            var prefix = branchPath + "/";
            foreach (var edge in graph.Edges)
            {
                if (edge.FromId == controlId && edge.ToId.StartsWith(prefix, StringComparison.Ordinal)
                    && string.IsNullOrEmpty(edge.Label))
                {
                    edge.Label = label;
                }
            }
        }

        // ---------- details extraction ----------

        /// <summary>Builds the label/value pairs shown in the details panel and HTML export.</summary>
        private static List<KeyValuePair<string, string>> ExtractDetails(JObject body)
        {
            var details = new List<KeyValuePair<string, string>>();
            if (body == null) return details;

            void Add(string key, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    details.Add(new KeyValuePair<string, string>(key, Truncate(value)));
            }

            try
            {
                var type = body["type"]?.ToString();
                Add("Type", type);
                Add("Description", body["description"]?.ToString());

                // Indexing a JValue ("inputs": "@variables('x')") throws — only
                // drill in when inputs is actually an object.
                var inputs = body["inputs"];
                var inputsObj = MaskSecrets(inputs as JObject);

                var host = inputsObj?["host"] as JObject;
                if (host != null)
                {
                    Add("Operation", host["operationId"]?.ToString());
                    var apiId = host["apiId"]?.ToString();
                    if (!string.IsNullOrEmpty(apiId))
                        Add("Connector", apiId.Split('/').Last());
                }

                // Every parameter, dynamically — whatever the connector defines.
                var parameters = inputsObj?["parameters"] as JObject;
                if (parameters != null)
                {
                    foreach (var p in parameters.Properties())
                        Add(p.Name, Compact(p.Value));
                }

                // Any other input fields, generically (host/parameters are summarized
                // above; authentication is skipped — it can carry connection secrets).
                if (inputsObj != null)
                {
                    foreach (var p in inputsObj.Properties())
                    {
                        var key = p.Name.ToLowerInvariant();
                        if (key == "host" || key == "parameters" || key == "authentication") continue;
                        Add(p.Name, Compact(p.Value));
                    }
                }
                else if (inputs != null && !IsControlType(type))
                {
                    // Scalar/array inputs (e.g. Compose with a plain value).
                    Add("Inputs", Compact(inputs));
                }

                switch ((type ?? "").ToLowerInvariant())
                {
                    case "if": Add("Condition", Compact(body["expression"])); break;
                    case "switch": Add("Switch on", Compact(body["expression"])); break;
                    case "foreach": Add("For each", Compact(body["foreach"])); break;
                    case "until":
                        Add("Until", Compact(body["expression"]));
                        Add("Limit", Compact(body["limit"]));
                        break;
                }

                var nested = body["actions"] as JObject;
                if (nested != null && IsControlType(type))
                    Add("Nested actions", nested.Count.ToString());

                var recurrence = body["recurrence"] as JObject;
                if (recurrence != null)
                    Add("Recurrence", ("every " + recurrence["interval"] + " " + recurrence["frequency"]).Trim());

                // Trigger conditions (Settings > Trigger conditions) are a bare
                // "conditions" array on the trigger itself — NOT under inputs.
                var conditions = body["conditions"] as JArray;
                if (conditions != null && conditions.Count > 0)
                {
                    int index = 0;
                    foreach (var c in conditions)
                    {
                        index++;
                        var expression = (c as JObject)?["expression"]?.ToString() ?? Compact(c);
                        Add(conditions.Count == 1 ? "Trigger condition" : "Trigger condition " + index,
                            expression);
                    }
                }

                Add("Split on", body["splitOn"]?.ToString());

                var runAfter = body["runAfter"] as JObject;
                if (runAfter != null && runAfter.Count > 0)
                    Add("Runs after", string.Join(", ", runAfter.Properties().Select(p => p.Name.Replace('_', ' '))));
            }
            catch (Exception ex)
            {
                // Details are best-effort — never let them break the map itself.
                Add("Note", "Some details could not be read: " + ex.Message);
            }

            return details;
        }

        private static bool IsControlType(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "if":
                case "switch":
                case "foreach":
                case "until":
                case "scope": return true;
                default: return false;
            }
        }

        /// <summary>
        /// Single-line compact rendering of a JSON value (strings unquoted).
        /// Deliberately avoids ToString(Formatting): the plugin runs against the
        /// HOST's Newtonsoft.Json, which may be older than the compile reference,
        /// and that overload throws MissingMethodException there.
        /// </summary>
        private static string Compact(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String) return token.ToString();
            return Regex.Replace(token.ToString(), @"\s+", " ");
        }

        private static string Truncate(string s) => DetailText.Clean(s);

        // ---------- secrets ----------
        //
        // Details end up in the details pane and in every HTML/Markdown export,
        // bulk packs included, which are made to be shared. So a literal
        // credential in an HTTP action's headers, queries, uri or body, or in a
        // connector parameter, is masked. An
        // expression ("Bearer @{body('Get_token')?['access_token']}") is not a
        // secret: it documents where the secret comes from, so it stays.

        private const string Hidden = "(hidden)";

        private static readonly HashSet<string> SecretHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
            "Ocp-Apim-Subscription-Key", "x-functions-key"
        };

        private static readonly string[] SecretNameParts = { "api-key", "apikey", "api_key", "token", "secret", "password" };

        // Query parameters that carry a key: an Azure Function's code, a SAS sig…
        private static readonly HashSet<string> SecretQueryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "code", "sig", "key", "api-key", "apikey", "api_key", "access_token", "token",
            "client_secret", "secret", "password", "subscription-key"
        };

        private static readonly Regex UriQueryParam = new Regex(@"([?&])([^=&#?]+)=([^&#]*)");
        private static readonly Regex UriPassword = new Regex(@"^(\s*[A-Za-z][A-Za-z0-9+.\-]*://[^/?#@:\s]*:)([^/?#@\s]*)@");

        /// <summary>
        /// A copy of an inputs object with literal credentials replaced by
        /// "(hidden)" — in every headers / queries / authentication object, uri,
        /// request body and connector parameter it holds, at any depth (an HTTP
        /// webhook nests them under subscribe / unsubscribe). The parsed
        /// definition itself is never changed.
        /// </summary>
        private static JObject MaskSecrets(JObject inputs)
        {
            if (inputs == null) return null;
            var copy = (JObject)inputs.DeepClone();
            MaskWithin(copy);
            return copy;
        }

        /// <param name="inBody">Inside a request body: a literal under a secret name is masked too.</param>
        private static void MaskWithin(JToken token, bool inBody = false)
        {
            var obj = token as JObject;
            if (obj == null)
            {
                if (token is JArray)
                    foreach (var item in token.Children().ToList()) MaskWithin(item, inBody);
                return;
            }

            foreach (var p in obj.Properties().ToList())
                MaskProperty(p, p.Name, inBody);
        }

        /// <summary>Masks one property, judged by <paramref name="name"/> (for a parameter, the last part of its path).</summary>
        private static void MaskProperty(JProperty p, string name, bool inBody)
        {
            var values = p.Value as JObject;
            var text = p.Value.Type == JTokenType.String ? p.Value.ToString() : null;
            if (values != null && Is(name, "headers"))
                MaskValues(values, IsSecretHeader);
            else if (values != null && Is(name, "queries"))
                MaskValues(values, IsSecretQuery);
            else if (Is(name, "authentication"))
            {
                // Skipped at the top of inputs; nested (a webhook's subscribe) it is
                // shown, so all but its type is masked — secret fields vary by type.
                if (values != null)
                    MaskValues(values, n => !n.Equals("type", StringComparison.OrdinalIgnoreCase));
                else if (IsLiteral(p.Value))
                    p.Value = new JValue(Hidden);
            }
            else if (text != null && (Is(name, "uri") || Is(name, "url")))
                Replace(p, text, MaskUri(text));
            else if (values != null && !inBody && Is(name, "parameters"))
                MaskParameters(values);
            else if (Is(name, "body"))
            {
                // A token request posts its credentials: as JSON, or form-encoded.
                if (text != null) Replace(p, text, MaskForm(text));
                else MaskWithin(p.Value, true);
            }
            else if (text != null && inBody)
            {
                if (IsSecretHeader(name) && IsLiteral(text)) p.Value = new JValue(Hidden);
            }
            else
                MaskWithin(p.Value, inBody);
        }

        /// <summary>
        /// A connector's parameters are named by path ("parameters/headers",
        /// "request/url", "item/code"), so each is judged by its last part. Besides
        /// the headers / queries / uri / body rules, a parameter named like a secret
        /// header ("api-key", "Ocp-Apim-Subscription-Key") hides its literal value,
        /// and so does a header line ("Authorization: Bearer …") in a plain one.
        /// Bare query names (code, key, sig) don't count here: "item/code" is data.
        /// </summary>
        private static void MaskParameters(JObject parameters)
        {
            foreach (var p in parameters.Properties().ToList())
            {
                var name = p.Name.Substring(p.Name.LastIndexOf('/') + 1);
                var text = p.Value.Type == JTokenType.String ? p.Value.ToString() : null;
                if (text == null || Is(name, "uri") || Is(name, "url") || Is(name, "body") || Is(name, "authentication"))
                    MaskProperty(p, name, false);
                else if (IsSecretHeader(name))
                {
                    if (IsLiteral(text)) p.Value = new JValue(Hidden);
                }
                else
                    Replace(p, text, MaskHeaderLine(text));
            }
        }

        private static bool Is(string name, string expected) => name.Equals(expected, StringComparison.OrdinalIgnoreCase);

        // Only a value that changed is replaced, so the rest keep their exact token.
        private static void Replace(JProperty p, string before, string after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal)) p.Value = new JValue(after);
        }

        private static void MaskValues(JObject values, Func<string, bool> isSecret)
        {
            foreach (var p in values.Properties().ToList())
                if (isSecret(p.Name) && IsLiteral(p.Value))
                    p.Value = new JValue(Hidden);
        }

        private static bool IsSecretHeader(string name)
            => SecretHeaderNames.Contains(name)
               || SecretNameParts.Any(part => name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);

        // One rule for a queries object and a uri's query string, so a parameter
        // is masked the same whichever way the action passes it.
        private static bool IsSecretQuery(string name)
            => IsSecretHeader(name) || SecretQueryNames.Contains(name);

        private static string MaskUri(string uri)
        {
            // "@concat('https://…?code=', …)" is code that builds the uri, not the uri.
            if (IsExpression(uri)) return uri;
            uri = UriPassword.Replace(uri, m => m.Groups[2].Length > 0 && IsLiteral(m.Groups[2].Value)
                ? m.Groups[1].Value + Hidden + "@"
                : m.Value);
            return MaskPairs(uri, UriQueryParam);
        }

        private static readonly Regex FormPair = new Regex(@"(^|&)([^=&]+)=([^&]*)");

        /// <summary>A form-encoded body ("grant_type=…&amp;client_secret=…") with its secret values masked.</summary>
        private static string MaskForm(string body)
        {
            if (IsExpression(body) || body.IndexOf('\n') >= 0 || body.IndexOf('\r') >= 0) return body;
            // Only name=value pairs joined by '&' are a form; other text is left alone.
            foreach (var pair in body.Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0 || pair.Substring(0, equals).Any(char.IsWhiteSpace)) return body;
            }
            return MaskPairs(body, FormPair);
        }

        /// <summary>
        /// Masks each name=value <paramref name="pairs"/> match that the query rule
        /// calls secret. A match inside an @{…} span is part of the expression, and
        /// a value reaching into one is not literal, so an expression is never cut.
        /// </summary>
        private static string MaskPairs(string text, Regex pairs)
        {
            var spans = ExpressionSpans(text);
            return pairs.Replace(text, m => !spans.Any(s => m.Index >= s[0] && m.Index <= s[1])
                                            && IsSecretQuery(m.Groups[2].Value) && IsLiteral(m.Groups[3].Value)
                ? m.Groups[1].Value + m.Groups[2].Value + "=" + Hidden
                : m.Value);
        }

        /// <summary>First and last index of each @{…} in <paramref name="text"/>, braces in quoted strings ignored.</summary>
        private static List<int[]> ExpressionSpans(string text)
        {
            var spans = new List<int[]>();
            int start = text.IndexOf("@{", StringComparison.Ordinal);
            while (start >= 0)
            {
                int depth = 0, end = start + 1;
                bool quoted = false;
                for (; end < text.Length; end++)
                {
                    var c = text[end];
                    if (c == '\'') quoted = !quoted;
                    else if (!quoted && c == '{') depth++;
                    else if (!quoted && c == '}' && --depth == 0) break;
                }
                // An unclosed span runs to the end.
                spans.Add(new[] { start, Math.Min(end, text.Length - 1) });
                if (end >= text.Length - 1) break;
                start = text.IndexOf("@{", end + 1, StringComparison.Ordinal);
            }
            return spans;
        }

        private static readonly Regex HeaderLine = new Regex(@"^(\s*([A-Za-z0-9!#$%&'*+.^_`|~-]+)\s*:\s*)(\S.*)$");

        /// <summary>"Authorization: Bearer …" becomes "Authorization: (hidden)"; any other text stays.</summary>
        private static string MaskHeaderLine(string text)
        {
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0) return text;
            var m = HeaderLine.Match(text);
            return m.Success && IsSecretHeader(m.Groups[2].Value) && IsLiteral(m.Groups[3].Value)
                ? m.Groups[1].Value + Hidden
                : text;
        }

        // A whole-value expression ("@parameters('x')"). "@{…}" is a template whose
        // text outside the braces is literal; "@@" escapes a literal '@'.
        private static bool IsExpression(string value)
            => value.StartsWith("@", StringComparison.Ordinal)
               && !value.StartsWith("@@", StringComparison.Ordinal)
               && !value.StartsWith("@{", StringComparison.Ordinal);

        /// <summary>True for a written-in value; false for an empty one or an expression reference.</summary>
        private static bool IsLiteral(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return false;
            return value.Type != JTokenType.String || IsLiteral(value.ToString());
        }

        private static bool IsLiteral(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Contains("@{")) return false;
            // A leading "@@" escapes the '@': the value is plain text, not an expression.
            return !value.StartsWith("@", StringComparison.Ordinal) || value.StartsWith("@@", StringComparison.Ordinal);
        }

        // ---------- helpers ----------

        private static JObject LocateDefinition(string clientData)
        {
            if (string.IsNullOrWhiteSpace(clientData)) return null;

            // Dates stay the strings the author wrote: by default Newtonsoft turns
            // ISO text into DateTime, which then prints in local time and the
            // current culture, offset dropped ("4/30/2024 6:00:00 PM").
            JObject root;
            using (var reader = new JsonTextReader(new StringReader(clientData)) { DateParseHandling = DateParseHandling.None })
                root = JObject.Load(reader);

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
                case "scope": return NodeKind.Scope;
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

            // All four statuses = "run regardless of outcome" — one word, not four.
            if (statuses.Count == 4 &&
                statuses.Contains("Succeeded", StringComparer.OrdinalIgnoreCase) &&
                statuses.Contains("Failed", StringComparer.OrdinalIgnoreCase) &&
                statuses.Contains("Skipped", StringComparer.OrdinalIgnoreCase) &&
                statuses.Contains("TimedOut", StringComparer.OrdinalIgnoreCase))
                return "always";

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

        // ---------- node ids ----------
        //
        // An id is a path of '/'-separated segments that alternates between the
        // flow's own names and branch roles: act/Condition/yes/Send_email. Names
        // are escaped so they can never contain '/' or ':', which makes every
        // path decompose one way only; nodes the parser adds itself (case chips,
        // empty markers) end in a segment starting with ':', which no escaped
        // name can equal. So two distinct places in a flow can never share an
        // id, however the author named things.

        /// <summary>Id of the flow item <paramref name="name"/> inside <paramref name="path"/>.</summary>
        private static string ChildId(string path, string name) => path + "/" + Escape(name);

        /// <summary>Path of one of a control's branches ("yes", "no", "body", "default", "case:&lt;key&gt;").</summary>
        private static string BranchPath(string controlId, string role) => controlId + "/" + role;

        /// <summary>Id of a node the parser adds to a branch itself.</summary>
        private static string SyntheticId(string branchPath, string role) => branchPath + "/:" + role;

        private static string Escape(string name) =>
            (name ?? "").Replace("%", "%25").Replace("/", "%2F").Replace(":", "%3A");
    }
}
