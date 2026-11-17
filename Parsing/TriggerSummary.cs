using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DataverseProcessMapper.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataverseProcessMapper.Parsing
{
    /// <summary>
    /// One-line answers to "what starts this process?" for the lists, the
    /// classic map's Start node and the export headers. A flow's comes from its
    /// definition's trigger; a classic workflow's from the workflow columns
    /// alone, so its list needs no XAML.
    /// </summary>
    public static class TriggerSummary
    {
        // The list column stays readable; the Start node's details list them all.
        private const int MaxListedAttributes = 4;

        // ---------- flows ----------

        /// <summary>
        /// Summary of a flow's trigger ("Dataverse: account – Added or Modified",
        /// "Schedule: every 2 hours", "Manual", "shared_office365: OnNewEmailV3").
        /// Never throws; null when the definition has no trigger or cannot be read.
        /// </summary>
        public static string ForFlow(string clientData)
        {
            try
            {
                var definition = LocateDefinition(clientData);
                var trigger = (definition?["triggers"] as JObject)?.Properties().FirstOrDefault();
                if (trigger == null) return null;
                return Describe(trigger.Value as JObject, definition) ?? Humanize(trigger.Name);
            }
            catch (Exception)
            {
                // A summary is a nicety: a broken definition is reported by the map.
                return null;
            }
        }

        private static string Describe(JObject trigger, JObject definition)
        {
            if (trigger == null) return null;
            var type = Str(trigger["type"]);
            var inputs = trigger["inputs"] as JObject;

            if (Is(type, "Recurrence") || Is(type, "SlidingWindow"))
                return Schedule(trigger["recurrence"] as JObject) ?? "Schedule";

            if (Is(type, "Request"))
                return RequestKind(Str(trigger["kind"]), definition);

            var host = inputs?["host"] as JObject;
            var connector = ConnectorName(host);
            var operation = Str(host?["operationId"]);
            var parameters = inputs?["parameters"] as JObject;

            if (IsDataverse(connector))
                return Dataverse(parameters, operation);

            if (connector != null)
                return connector + (operation != null ? ": " + operation : "");

            return string.IsNullOrEmpty(type) ? null : type;
        }

        /// <summary>The same naming the map's details use: the last segment of the connector's apiId.</summary>
        private static string ConnectorName(JObject host)
        {
            var apiId = Str(host?["apiId"]);
            if (apiId != null) return apiId.Split('/').Last();
            // Older ApiConnection definitions name the connection instead.
            return Str(host?["connectionName"]);
        }

        private static bool IsDataverse(string connector)
            => connector != null
               && (Is(connector, "shared_commondataserviceforapps") || Is(connector, "shared_commondataservice"));

        /// <summary>"Dataverse: account – Added or Modified" from the trigger's subscription request.</summary>
        private static string Dataverse(JObject parameters, string operation)
        {
            var table = Param(parameters, "subscriptionRequest/entityname")
                        ?? Param(parameters, "entityName")
                        ?? Param(parameters, "table");
            var change = ChangeType(Param(parameters, "subscriptionRequest/message"));

            if (table == null) return "Dataverse" + (operation != null ? ": " + operation : "");
            return "Dataverse: " + table + (change != null ? " – " + change : "");
        }

        /// <summary>The designer's "Change type" for the subscription's message number.</summary>
        private static string ChangeType(string message)
        {
            switch (message)
            {
                case "1": return "Added";
                case "2": return "Deleted";
                case "3": return "Modified";
                case "4": return "Added or Modified";
                case "5": return "Added or Deleted";
                case "6": return "Modified or Deleted";
                case "7": return "Added, Modified or Deleted";
                default: return null;
            }
        }

        private static string Schedule(JObject recurrence)
        {
            var frequency = Str(recurrence?["frequency"]);
            if (frequency == null) return null;
            var unit = frequency.ToLowerInvariant();
            // Invariant text: a JSON number would otherwise print in the user's culture ("1,5").
            var raw = recurrence["interval"] as JValue;
            var text = raw?.Value == null ? "" : Convert.ToString(raw.Value, CultureInfo.InvariantCulture).Trim();
            if (text.Length == 0) text = "1";
            decimal interval;
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out interval))
                return "Schedule: every " + text + " " + unit; // an expression, kept as written
            if (interval <= 0) return null; // not a schedule the service would run: just "Schedule"
            return interval == 1
                ? "Schedule: every " + unit
                : "Schedule: every " + interval.ToString(CultureInfo.InvariantCulture) + " " + unit + "s";
        }

        private static string RequestKind(string kind, JObject definition)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "http": return "HTTP request";
                case "powerapp":
                case "powerappv2": return "Power Apps";
                case "skills":
                case "virtualagent": return "Copilot Studio";
                default:
                    // A manually triggered flow that answers "Respond to a PowerApp or
                    // flow" is called by another flow: the trigger itself looks the same.
                    // A heuristic: a manual flow that answers a Power App reads as one too,
                    // so a tooltip should say "probably".
                    return RespondsToCaller(definition?["actions"] as JObject) ? "Child flow" : "Manual";
            }
        }

        private static bool RespondsToCaller(JObject actions)
        {
            if (actions == null) return false;
            foreach (var p in actions.Properties())
            {
                var body = p.Value as JObject;
                if (body == null) continue;
                if (Is(Str(body["type"]), "Response") && Is(Str(body["kind"]), "PowerApp")) return true;
                if (RespondsToCaller(body["actions"] as JObject)) return true;
                if (RespondsToCaller((body["else"] as JObject)?["actions"] as JObject)) return true;
                if (RespondsToCaller((body["default"] as JObject)?["actions"] as JObject)) return true;
                var cases = body["cases"] as JObject;
                if (cases != null && cases.Properties().Any(c => RespondsToCaller((c.Value as JObject)?["actions"] as JObject)))
                    return true;
            }
            return false;
        }

        private static string Param(JObject parameters, string name)
        {
            if (parameters == null) return null;
            var match = parameters.Properties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return Str(match?.Value);
        }

        /// <summary>Text of a scalar token; null for a missing, null, empty or structured one.</summary>
        private static string Str(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token is JContainer) return null;
            var text = token.ToString().Trim();
            return text.Length == 0 ? null : text;
        }

        private static bool Is(string value, string expected)
            => value != null && value.Equals(expected, StringComparison.OrdinalIgnoreCase);

        private static string Humanize(string key) => string.IsNullOrEmpty(key) ? null : key.Replace('_', ' ').Trim();

        // The same three shapes FlowJsonParser accepts.
        private static JObject LocateDefinition(string clientData)
        {
            if (string.IsNullOrWhiteSpace(clientData)) return null;
            JObject root;
            using (var reader = new JsonTextReader(new StringReader(clientData)) { DateParseHandling = DateParseHandling.None })
                root = JObject.Load(reader);
            var def = root.SelectToken("properties.definition") as JObject;
            if (def != null) return def;
            if (root["triggers"] != null || root["actions"] != null) return root;
            return root["definition"] as JObject;
        }

        // ---------- classic workflows ----------

        /// <summary>
        /// Summary from the workflow columns: "Create, Update (statuscode, ownerid),
        /// Delete · On demand · Child". Empty when none of them is set: callers treat
        /// empty, like <see cref="ForFlow"/>'s null, as "unknown". Changed fields are
        /// logical names; display names would need metadata.
        /// </summary>
        public static string ForClassic(ProcessItem item)
        {
            if (item == null) return "";
            var events = new List<string>();
            if (item.TriggerOnCreate) events.Add("Create");
            var attributes = UpdateAttributes(item);
            if (attributes.Count > 0)
            {
                var shown = attributes.Take(MaxListedAttributes).ToList();
                var more = attributes.Count - shown.Count;
                events.Add("Update (" + string.Join(", ", shown) + (more > 0 ? " +" + more + " more" : "") + ")");
            }
            if (item.TriggerOnDelete) events.Add("Delete");

            var parts = new List<string>();
            if (events.Count > 0) parts.Add(string.Join(", ", events));
            if (item.OnDemand) parts.Add("On demand");
            if (item.IsChild) parts.Add("Child");
            return string.Join(" · ", parts);
        }

        /// <summary>"Background" (mode 0) / "Real-time" (mode 1); empty for flows and an unknown mode.</summary>
        public static string ModeLabel(ProcessItem item)
        {
            if (item == null || item.IsModernFlow) return "";
            switch (item.Mode)
            {
                case 0: return "Background";
                case 1: return "Real-time";
                default: return "";
            }
        }

        /// <summary>
        /// The summary a header shows: the item's own, or worked out now (an export
        /// can run before the background pass has reached the flow).
        /// </summary>
        public static string ForItem(ProcessItem item)
        {
            if (item == null) return null;
            if (!string.IsNullOrEmpty(item.TriggerSummary)) return item.TriggerSummary;
            var summary = item.IsModernFlow ? ForFlow(item.ClientData) : ForClassic(item);
            return string.IsNullOrEmpty(summary) ? null : summary;
        }

        /// <summary>Short line under the classic Start node ("Record created or updated"); null when unknown.</summary>
        public static string ClassicSubtitle(ProcessItem item)
        {
            if (item == null) return null;
            var verbs = new List<string>();
            if (item.TriggerOnCreate) verbs.Add("created");
            if (UpdateAttributes(item).Count > 0) verbs.Add("updated");
            if (item.TriggerOnDelete) verbs.Add("deleted");
            if (verbs.Count > 0) return "Record " + JoinWithOr(verbs);
            if (item.OnDemand) return "On demand";
            if (item.IsChild) return "Child process";
            return null;
        }

        /// <summary>
        /// The classic Start node's details, as the workflow designer's
        /// properties put them. Rows whose column is unknown are left out.
        /// </summary>
        public static List<KeyValuePair<string, string>> ClassicStartDetails(ProcessItem item)
        {
            var details = new List<KeyValuePair<string, string>>();
            if (item == null) return details;

            var starts = new List<string>();
            if (item.TriggerOnCreate) starts.Add("Record is created");
            var attributes = UpdateAttributes(item);
            if (attributes.Count > 0) starts.Add("Record fields change (" + string.Join(", ", attributes) + ")");
            if (item.TriggerOnDelete) starts.Add("Record is deleted");
            if (item.OnDemand) starts.Add("On demand");
            if (item.IsChild) starts.Add("As a child process");
            Add(details, "Starts when", string.Join("; ", starts));

            switch (item.Mode)
            {
                case 0: Add(details, "Runs", "In the background"); break;
                case 1: Add(details, "Runs", "Real-time"); break;
            }
            switch (item.RunAs)
            {
                case 0: Add(details, "Run as", "Owner of the workflow"); break;
                case 1: Add(details, "Run as", "User who made changes to the record"); break;
            }
            Add(details, "Scope", item.Scope);
            return details;
        }

        private static void Add(List<KeyValuePair<string, string>> details, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) details.Add(new KeyValuePair<string, string>(key, value));
        }

        private static List<string> UpdateAttributes(ProcessItem item)
            => (item.TriggerOnUpdateAttributes ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .ToList();

        private static string JoinWithOr(List<string> words)
            => words.Count == 1
                ? words[0]
                : string.Join(", ", words.Take(words.Count - 1)) + " or " + words[words.Count - 1];
    }
}
