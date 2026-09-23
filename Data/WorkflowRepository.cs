using System;
using System.Collections.Generic;
using System.Linq;
using DataverseProcessMapper.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseProcessMapper.Data
{
    /// <summary>
    /// Reads process definitions from the Dataverse <c>workflow</c> table.
    /// </summary>
    public static class WorkflowRepository
    {
        // workflow.type option set: 1 = Definition, 2 = Activation, 3 = Template.
        private const int TypeDefinition = 1;

        // Categories we know how to visualise.
        public const int CategoryClassicWorkflow = 0;
        public const int CategoryModernFlow = 5;

        // Rows per request when fetching definitions for many processes at once.
        private const int DefinitionBatchSize = 50;

        /// <summary>
        /// Retrieves all classic workflow and modern flow <em>definitions</em>.
        /// Uses paging so environments with many processes are fully returned.
        /// The definitions themselves (xaml / clientdata) are left out — they are
        /// the largest columns by far and most processes are never opened — and
        /// are fetched on demand by <see cref="LoadDefinitions"/>.
        /// </summary>
        public static List<ProcessItem> RetrieveProcesses(IOrganizationService service)
        {
            var query = new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet(
                    "workflowid", "name", "category", "type",
                    "primaryentity", "statecode", "modifiedon",
                    "createdon", "createdby", "modifiedby", "ownerid", "scope"),
                Criteria = new FilterExpression(LogicalOperator.And),
                PageInfo = new PagingInfo { Count = 500, PageNumber = 1 },
                Orders = { new OrderExpression("name", OrderType.Ascending) }
            };

            query.Criteria.AddCondition("type", ConditionOperator.Equal, TypeDefinition);
            query.Criteria.AddCondition("category", ConditionOperator.In,
                CategoryClassicWorkflow, CategoryModernFlow);

            var results = new List<ProcessItem>();

            while (true)
            {
                var page = service.RetrieveMultiple(query);
                foreach (var e in page.Entities)
                {
                    results.Add(Map(e));
                }

                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }

            return results;
        }

        /// <summary>
        /// Fills in the definitions of the given processes that don't have them
        /// yet, a batch of rows per request. <paramref name="progress"/> receives
        /// (done, total) after each batch.
        /// </summary>
        public static void LoadDefinitions(IOrganizationService service, IEnumerable<ProcessItem> items,
            Action<int, int> progress = null)
        {
            var pending = items.Where(i => !i.DefinitionLoaded).ToList();
            for (int start = 0; start < pending.Count; start += DefinitionBatchSize)
            {
                var batch = pending.Skip(start).Take(DefinitionBatchSize).ToList();
                var byId = batch.ToDictionary(i => i.Id);

                var query = new QueryExpression("workflow")
                {
                    ColumnSet = new ColumnSet("workflowid", "xaml", "clientdata")
                };
                query.Criteria.AddCondition("workflowid", ConditionOperator.In,
                    batch.Select(i => (object)i.Id).ToArray());

                foreach (var e in service.RetrieveMultiple(query).Entities)
                {
                    ProcessItem item;
                    if (!byId.TryGetValue(e.Id, out item)) continue;
                    item.Xaml = e.GetAttributeValue<string>("xaml");
                    item.ClientData = e.GetAttributeValue<string>("clientdata");
                }

                // A row that came back without a definition (or not at all) has
                // none to load; asking again would not change that.
                foreach (var item in batch) item.DefinitionLoaded = true;
                progress?.Invoke(Math.Min(start + batch.Count, pending.Count), pending.Count);
            }
        }

        private static ProcessItem Map(Entity e)
        {
            return new ProcessItem
            {
                Id = e.Id,
                Name = e.GetAttributeValue<string>("name"),
                Category = GetOptionSet(e, "category"),
                PrimaryEntity = e.GetAttributeValue<string>("primaryentity"),
                State = GetOptionSet(e, "statecode"),
                ModifiedOn = e.Contains("modifiedon")
                    ? e.GetAttributeValue<System.DateTime>("modifiedon")
                    : (System.DateTime?)null,
                CreatedOn = e.Contains("createdon")
                    ? e.GetAttributeValue<System.DateTime>("createdon")
                    : (System.DateTime?)null,
                CreatedBy = GetLookupName(e, "createdby"),
                ModifiedBy = GetLookupName(e, "modifiedby"),
                Owner = GetLookupName(e, "ownerid"),
                Scope = GetScopeLabel(e)
            };
        }

        private static int GetOptionSet(Entity e, string attr)
        {
            var v = e.GetAttributeValue<OptionSetValue>(attr);
            return v?.Value ?? -1;
        }

        /// <summary>Display name of a lookup, preferring the server-formatted value.</summary>
        private static string GetLookupName(Entity e, string attr)
        {
            if (e.FormattedValues.Contains(attr)) return e.FormattedValues[attr];
            return e.GetAttributeValue<EntityReference>(attr)?.Name;
        }

        private static string GetScopeLabel(Entity e)
        {
            // The server-formatted (localized) label when available.
            if (e.FormattedValues.Contains("scope")) return e.FormattedValues["scope"];

            switch (GetOptionSet(e, "scope"))
            {
                case 1: return "User";
                case 2: return "Business Unit";
                case 3: return "Parent: Child Business Units";
                case 4: return "Organization";
                default: return null;
            }
        }
    }
}
