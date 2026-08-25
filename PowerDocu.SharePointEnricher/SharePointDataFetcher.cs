using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SharePoint.Client;
using PnP.Framework;

namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// Fetches SharePoint list schema + a capped sample of rows directly via CSOM,
    /// authenticating interactively through PnP.Framework/MSAL - no pwsh.exe, no
    /// PnP.PowerShell module, nothing to install to run this exe.
    ///
    /// One requirement this can't remove, regardless of language: since September
    /// 2024 Microsoft retired the shared multi-tenant "PnP Management Shell" app
    /// that let Connect-PnPOnline -Interactive work with zero setup. Every tenant
    /// now needs its own Entra ID app registration (a one-time admin action - see
    /// HANDOFF.md for the exact steps) and its Client ID, supplied here as
    /// <see cref="clientId"/>. This is a Microsoft platform requirement, identical
    /// whether the caller is PowerShell or C#.
    ///
    /// On any failure for a given site (auth cancelled, list not found, no access),
    /// logs clearly and continues without that site rather than aborting the run.
    /// </summary>
    public class SharePointDataFetcher
    {
        private readonly string clientId;
        private readonly string redirectUrl;
        private readonly int sampleLimit;

        public SharePointDataFetcher(string clientId, int sampleLimit = 20, string redirectUrl = "http://localhost")
        {
            this.clientId = clientId;
            this.redirectUrl = redirectUrl;
            this.sampleLimit = sampleLimit;
        }

        public List<SharePointSiteEntity> FetchLive(IEnumerable<(string SiteUrl, string ListId)> requests)
        {
            return FetchLiveAsync(requests).GetAwaiter().GetResult();
        }

        private async Task<List<SharePointSiteEntity>> FetchLiveAsync(IEnumerable<(string SiteUrl, string ListId)> requests)
        {
            var result = new List<SharePointSiteEntity>();
            var bySite = requests.GroupBy(r => r.SiteUrl);

            foreach (var siteGroup in bySite)
            {
                string siteUrl = siteGroup.Key;
                ClientContext context;
                try
                {
                    var authManager = AuthenticationManager.CreateWithInteractiveLogin(clientId, redirectUrl);
                    context = await authManager.GetContextAsync(siteUrl);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SharePointEnricher] Failed to connect to '{siteUrl}': {ex.Message}");
                    continue;
                }

                using (context)
                {
                    var site = new SharePointSiteEntity { SiteUrl = siteUrl };

                    foreach (var req in siteGroup)
                    {
                        SharePointListEntity list;
                        try
                        {
                            list = FetchList(context, req.ListId);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SharePointEnricher] List '{req.ListId}' not found on '{siteUrl}': {ex.Message}");
                            continue;
                        }
                        site.Lists.Add(list);
                    }

                    result.Add(site);
                }
            }

            return result;
        }

        private SharePointListEntity FetchList(ClientContext context, string listIdOrPath)
        {
            List spList = Guid.TryParse(listIdOrPath, out Guid listGuid)
                ? context.Web.Lists.GetById(listGuid)
                : context.Web.GetList(listIdOrPath); // legacy low-confidence shape: a server-relative path, not a GUID

            context.Load(spList, l => l.Title, l => l.Id, l => l.Fields);
            context.ExecuteQuery();

            var columns = new List<SharePointColumnEntity>();
            foreach (Field field in spList.Fields)
            {
                if (field.Hidden) continue;

                var column = new SharePointColumnEntity
                {
                    InternalName = field.InternalName,
                    DisplayName = field.Title,
                    TypeAsString = field.TypeAsString,
                    Required = field.Required,
                    Choices = new List<string>()
                };

                if (field.TypeAsString == "Choice" || field.TypeAsString == "MultiChoice")
                {
                    FieldChoice choiceField = context.CastTo<FieldChoice>(field);
                    context.Load(choiceField, cf => cf.Choices);
                    context.ExecuteQuery();
                    column.Choices = choiceField.Choices?.ToList() ?? new List<string>();
                }

                columns.Add(column);
            }

            // RowLimit, not a client-side Take(): caps what the server actually returns,
            // same reasoning as the original PowerShell script (see the removed
            // SHAREPOINT-DATA-CONTRACT.md note this carries forward).
            var camlQuery = new CamlQuery { ViewXml = $"<View><RowLimit>{sampleLimit}</RowLimit></View>" };
            ListItemCollection items = spList.GetItems(camlQuery);
            context.Load(items);
            context.ExecuteQuery();

            var sampleItems = new List<Dictionary<string, object>>();
            foreach (ListItem item in items)
            {
                var row = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in item.FieldValues)
                {
                    row[kv.Key] = kv.Value?.ToString();
                }
                sampleItems.Add(row);
            }

            return new SharePointListEntity
            {
                Title = spList.Title,
                Id = spList.Id.ToString(),
                Columns = columns,
                SampleItems = sampleItems
            };
        }
    }
}
