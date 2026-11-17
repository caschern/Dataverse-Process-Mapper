using System;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using McTools.Xrm.Connection;

namespace DataverseProcessMapper.Data
{
    /// <summary>
    /// Builds the links that open a process where it is edited: a flow in the
    /// Power Automate portal of the environment's cloud, a classic workflow in
    /// the legacy workflow designer.
    /// </summary>
    public static class MakerLinks
    {
        public const string CommercialMakerHost = "https://make.powerautomate.com";

        // Sovereign clouds have their own portal, recognised by the org URL's domain.
        private static readonly string[][] SovereignHosts =
        {
            new[] { ".crm9.dynamics.com", "https://make.gov.powerautomate.us" },              // GCC
            new[] { ".crm.microsoftdynamics.us", "https://make.high.powerautomate.us" },      // GCC High
            new[] { ".crm.appsplatform.us", "https://make.powerautomate.appsplatform.us" },   // DoD
            new[] { ".crm.dynamics.cn", "https://make.powerautomate.cn" }                     // China
        };

        /// <summary>The Power Automate portal for the cloud <paramref name="orgUrl"/> lives in.</summary>
        public static string MakerHost(string orgUrl)
        {
            var host = HostOf(orgUrl);
            if (host != null)
            {
                foreach (var pair in SovereignHosts)
                    if (host.EndsWith(pair[0], StringComparison.OrdinalIgnoreCase)) return pair[1];
            }
            return CommercialMakerHost;
        }

        /// <summary>
        /// The connection's environment id: from the connection itself when it
        /// knows it, otherwise asked of the organization. Null when neither works
        /// (on-premises, or an older server). The caller caches it per connection.
        /// A string, not a Guid: a tenant's default environment is
        /// "Default-{tenantId}", and the portal's links use that form.
        /// </summary>
        public static string ResolveEnvironmentId(IOrganizationService service, ConnectionDetail detail)
        {
            var id = NormalizeEnvironmentId(detail?.EnvironmentId);
            if (id != null || service == null) return id;

            try
            {
                var response = service.Execute(new RetrieveCurrentOrganizationRequest()) as RetrieveCurrentOrganizationResponse;
                return NormalizeEnvironmentId(response?.Detail?.EnvironmentId);
            }
            catch (Exception)
            {
                // No environment id means no portal link, which the UI explains;
                // it is never a reason to fail.
                return null;
            }
        }

        /// <summary>
        /// The flow's details page in the Power Automate portal.
        /// <paramref name="environmentId"/> is what <see cref="ResolveEnvironmentId"/> returned.
        /// </summary>
        public static string FlowUrl(string makerHost, string environmentId, Guid workflowIdUnique)
        {
            if (string.IsNullOrWhiteSpace(environmentId)) throw new ArgumentNullException(nameof(environmentId));
            return (makerHost ?? CommercialMakerHost).TrimEnd('/') +
                   "/environments/" + Uri.EscapeDataString(environmentId.Trim()) +
                   "/flows/" + workflowIdUnique.ToString("D") + "/details";
        }

        private const string DefaultEnvironmentPrefix = "Default-";

        // An environment id is a GUID, or "Default-" and the tenant's GUID. Anything
        // else is not one, and must not reach a URL.
        private static string NormalizeEnvironmentId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            bool isDefault = value.StartsWith(DefaultEnvironmentPrefix, StringComparison.OrdinalIgnoreCase);
            var guid = isDefault ? value.Substring(DefaultEnvironmentPrefix.Length) : value;
            Guid id;
            // 36 characters exactly: TryParseExact still forgives surrounding spaces.
            if (guid.Length != 36 || !Guid.TryParseExact(guid, "D", out id) || id == Guid.Empty)
                return null;
            return (isDefault ? DefaultEnvironmentPrefix : "") + id.ToString("D");
        }

        /// <summary>The classic workflow designer for the workflow, under the org's web application URL.</summary>
        public static string ClassicWorkflowUrl(string orgUrl, Guid workflowId)
            => (orgUrl ?? "").Trim().TrimEnd('/') + "/sfa/workflow/edit.aspx?id=" + workflowId.ToString("D");

        private static string HostOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            // A bare "org.crm9.dynamics.com" is a URL too.
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "https://" + url;
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : null;
        }
    }
}
