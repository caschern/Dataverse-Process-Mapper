using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DataverseProcessMapper.Parsing
{
    /// <summary>
    /// Rebuilds the readable text of a classic-workflow condition ("account.name
    /// begins with 'A'") from the activities the designer generates for it.
    ///
    /// The designer never stores the condition as text. Each check becomes a
    /// small data-flow inside the step: GetEntityProperty reads a field into a
    /// variable, EvaluateExpression turns a literal into another, EvaluateCondition
    /// compares them into a boolean, EvaluateLogicalCondition combines booleans,
    /// and the branch finally tests one variable. Replaying that flow over the
    /// variable names recovers the condition. It is best-effort: anything it
    /// cannot resolve reads as "?", and a branch it cannot explain returns null.
    /// </summary>
    internal sealed class ClassicConditionText
    {
        private static readonly Regex VariableRef = new Regex(@"^\[\s*([A-Za-z_][A-Za-z0-9_]*)\s*\]$");
        private static readonly Regex RelatedEntity = new Regex(@"related_([A-Za-z0-9_]+)#");

        private readonly Dictionary<string, string> _text = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _logicalOp = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _spans = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <param name="step">The ConditionSequence element, for its declared variables.</param>
        /// <param name="activities">Its activities, in order.</param>
        public ClassicConditionText(XElement step, IEnumerable<XElement> activities)
        {
            // Wait timeouts keep their duration as a literal variable default.
            foreach (var variable in ClassicXaml.Variables(step))
            {
                var span = variable.Descendants().FirstOrDefault(d => d.Name.LocalName == "XrmTimeSpan");
                var name = (string)variable.Attribute("Name");
                if (span != null && name != null) _spans[name] = FormatSpan(span);
            }

            foreach (var activity in activities) Learn(activity);
        }

        /// <summary>The condition a branch tests, or null when it cannot be explained.</summary>
        public string For(XElement branch)
        {
            var name = VariableName(ClassicXaml.Argument(branch, "Condition"));
            string text;
            return name != null && _text.TryGetValue(name, out text) ? text : null;
        }

        /// <summary>True for a branch that always runs — the designer's "Otherwise".</summary>
        public static bool IsUnconditional(XElement branch)
        {
            var condition = (ClassicXaml.Argument(branch, "Condition") ?? "").Trim().Trim('[', ']').Trim();
            return condition.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A timeout branch's duration ("2 days"), when it is a fixed one.</summary>
        public string DurationFor(string branchName)
        {
            if (string.IsNullOrEmpty(branchName)) return null;
            return _spans.Where(s => s.Key.StartsWith(branchName + "_", StringComparison.Ordinal))
                         .Select(s => s.Value).FirstOrDefault();
        }

        // ---------- replaying the data-flow ----------

        private void Learn(XElement activity)
        {
            var local = activity.Name.LocalName;
            if (local == "GetEntityProperty")
            {
                var target = VariableName((string)activity.Attribute("Value"));
                if (target != null) _text[target] = Field(activity);
                return;
            }
            if (local != "ActivityReference") return;

            var result = VariableName(ClassicXaml.Argument(activity, "Result"));
            if (result == null) return;

            switch (ClassicXaml.ActivityType(activity))
            {
                case "EvaluateExpression":
                    _text[result] = Expression(activity);
                    break;
                case "EvaluateCondition":
                    _text[result] = Condition(activity);
                    break;
                case "EvaluateLogicalCondition":
                    var op = (ClassicXaml.Argument(activity, "LogicalOperator") ?? "and").Trim().ToLowerInvariant();
                    _text[result] = Operand(ClassicXaml.Argument(activity, "LeftOperand"), op) + " " + op + " " +
                                    Operand(ClassicXaml.Argument(activity, "RightOperand"), op);
                    _logicalOp[result] = op;
                    break;
            }
        }

        private static string Field(XElement getProperty)
        {
            var attribute = (string)getProperty.Attribute("Attribute") ?? "?";
            var entity = (string)getProperty.Attribute("Entity") ?? "";
            var related = RelatedEntity.Match(entity);
            // A related record is named by the lookup that reaches it, which is
            // what tells two lookups to the same table apart.
            var owner = related.Success ? related.Groups[1].Value : (string)getProperty.Attribute("EntityName");
            return string.IsNullOrEmpty(owner) ? attribute : owner + "." + attribute;
        }

        private string Expression(XElement activity)
        {
            var op = (ClassicXaml.Argument(activity, "ExpressionOperator") ?? "").Trim();
            var parameters = ClassicXaml.Argument(activity, "Parameters") ?? "";
            switch (op)
            {
                case "CreateCrmType":
                    return Literal(parameters);
                case "SelectFirstNonNull":
                    return ParameterList(parameters).Select(Resolve).FirstOrDefault() ?? "(value)";
                case "Add":
                case "Subtract":
                    // Date arithmetic: a date field plus or minus a duration.
                    var sides = ParameterList(parameters).Select(Resolve).ToList();
                    return sides.Count == 2
                        ? sides[0] + (op == "Add" ? " + " : " - ") + sides[1]
                        : "(" + Words(op) + ")";
                case "RetrieveCurrentTime": return "now";
                case "RetrieveLastExecutionTime": return "the last run time";
                default: return "(" + Words(op) + ")";
            }
        }

        /// <summary>
        /// A constant typed into the designer: "{ WorkflowPropertyType.String, "Then", "String" }".
        /// A lookup constant carries the record's name after its table —
        /// "{ …EntityReference, "processstage", "Review", …}" — and the name is what
        /// the designer shows.
        /// </summary>
        private static string Literal(string parameters)
        {
            var type = Regex.Match(parameters, @"WorkflowPropertyType\.(\w+)");
            // Names can arrive HTML-encoded ("Notify&#160;request") — decode them,
            // and turn non-breaking spaces into plain ones.
            var quoted = Regex.Matches(parameters, @"""((?:[^""]|"""")*)""")
                .Cast<Match>()
                .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Replace("\"\"", "\"")).Replace(' ', ' '))
                .ToList();
            if (!type.Success || quoted.Count == 0) return "(value)";

            switch (type.Groups[1].Value)
            {
                case "String":
                case "Memo":
                    return "'" + quoted[0] + "'";
                case "EntityReference":
                    return "'" + (quoted.Count > 1 ? quoted[1] : quoted[0]) + "'";
                case "Boolean":
                    return quoted[0] == "1" ? "true" : quoted[0] == "0" ? "false" : quoted[0];
                default:
                    return quoted[0];
            }
        }

        private string Condition(XElement activity)
        {
            var operand = Resolve(VariableName(ClassicXaml.Argument(activity, "Operand")));
            var op = OperatorPhrase(ClassicXaml.Argument(activity, "ConditionOperator"));
            var values = ParameterList(ClassicXaml.Argument(activity, "Parameters") ?? "").Select(Resolve).ToList();
            return values.Count == 0
                ? operand + " " + op
                : operand + " " + op + " " + string.Join(", ", values);
        }

        /// <summary>One side of an and/or, bracketed when it mixes in the other operator.</summary>
        private string Operand(string argument, string op)
        {
            var name = VariableName(argument);
            var text = Resolve(name);
            string inner;
            return name != null && _logicalOp.TryGetValue(name, out inner) && inner != op ? "(" + text + ")" : text;
        }

        private string Resolve(string variable)
        {
            string text;
            if (variable == null) return "?";
            if (_text.TryGetValue(variable, out text)) return text;
            return _spans.TryGetValue(variable, out text) ? text : "?";
        }

        // ---------- small parsers ----------

        private static string VariableName(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument)) return null;
            var m = VariableRef.Match(argument.Trim());
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>The variable names in "[New Object() { a, b }]".</summary>
        private static IEnumerable<string> ParameterList(string parameters)
        {
            int open = parameters.IndexOf('{');
            int close = parameters.LastIndexOf('}');
            if (open < 0 || close <= open) return Enumerable.Empty<string>();
            return parameters.Substring(open + 1, close - open - 1)
                .Split(',')
                .Select(p => p.Trim())
                .Where(p => Regex.IsMatch(p, @"^[A-Za-z_][A-Za-z0-9_]*$"));
        }

        private static string OperatorPhrase(string op)
        {
            switch ((op ?? "").Trim())
            {
                case "Equal": return "equals";
                case "NotEqual": return "does not equal";
                case "Null": return "does not contain data";
                case "NotNull": return "contains data";
                case "Contains": case "Like": return "contains";
                case "DoesNotContain": case "NotLike": return "does not contain";
                case "BeginsWith": return "begins with";
                case "DoesNotBeginWith": return "does not begin with";
                case "EndsWith": return "ends with";
                case "DoesNotEndWith": return "does not end with";
                case "In": return "is one of";
                case "NotIn": return "is not one of";
                case "GreaterThan": return "is greater than";
                case "GreaterEqual": return "is greater than or equal to";
                case "LessThan": return "is less than";
                case "LessEqual": return "is less than or equal to";
                case "On": return "is on";
                case "OnOrAfter": return "is on or after";
                case "OnOrBefore": return "is on or before";
                default: return Words(op).ToLowerInvariant();
            }
        }

        private static string FormatSpan(XElement span)
        {
            var parts = new List<string>();
            foreach (var unit in new[] { "Years", "Months", "Days", "Hours", "Minutes" })
            {
                int n;
                if (!int.TryParse((string)span.Attribute(unit), out n) || n == 0) continue;
                var word = unit.ToLowerInvariant();
                parts.Add(n + " " + (n == 1 ? word.TrimEnd('s') : word));
            }
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        /// <summary>"RetrieveCurrentTime" -> "Retrieve Current Time".</summary>
        private static string Words(string pascal)
            => string.IsNullOrEmpty(pascal) ? "" : Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ");
    }
}
