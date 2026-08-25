using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PowerDocu.SharePointEnricher
{
    /// <summary>
    /// Shells out to Resources\FetchSharePointData.ps1 (real pwsh.exe + PnP.PowerShell,
    /// real interactive site login) and deserializes its stdout against the contract
    /// documented in SHAREPOINT-DATA-CONTRACT.md. On any failure for a given site
    /// (module missing, auth cancelled, list not found), logs clearly and continues
    /// without that site rather than aborting the whole run.
    /// </summary>
    public class SharePointDataFetcher
    {
        private readonly string scriptPath;
        private readonly int sampleLimit;

        public SharePointDataFetcher(string scriptPath, int sampleLimit = 20)
        {
            this.scriptPath = scriptPath;
            this.sampleLimit = sampleLimit;
        }

        /// <summary>
        /// Runs the real fetch script against pwsh.exe for the given (siteUrl, listId) pairs.
        /// </summary>
        public List<SharePointSiteEntity> FetchLive(IEnumerable<(string SiteUrl, string ListId)> requests)
        {
            var requestArray = requests.Select(r => new { siteUrl = r.SiteUrl, listId = r.ListId }).ToList();
            string requestsJson = JsonConvert.SerializeObject(requestArray);

            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Requests {EscapeArg(requestsJson)} -SampleLimit {sampleLimit}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using Process process = Process.Start(psi);
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Console.Error.WriteLine("[SharePointEnricher] " + stderr.Trim());
                }

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    Console.Error.WriteLine("[SharePointEnricher] Fetch script produced no output; continuing without SharePoint data.");
                    return new List<SharePointSiteEntity>();
                }

                using StringReader reader = new StringReader(stdout);
                return ParseFromContract(reader);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[SharePointEnricher] Could not run FetchSharePointData.ps1 (is pwsh.exe on PATH?): " + ex.Message);
                return new List<SharePointSiteEntity>();
            }
        }

        /// <summary>
        /// Deserializes the JSON contract from any TextReader — the seam that keeps this
        /// class testable independent of a live pwsh.exe process. Real validation still
        /// means exercising FetchLive against a real script/site, not substituting fixture
        /// data for that validation, per the plan's "no fake test data" requirement.
        /// </summary>
        public static List<SharePointSiteEntity> ParseFromContract(TextReader reader)
        {
            var result = new List<SharePointSiteEntity>();
            string json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json)) return result;

            JArray sites = JArray.Parse(json);
            foreach (JToken siteToken in sites)
            {
                var site = new SharePointSiteEntity
                {
                    SiteUrl = siteToken["siteUrl"]?.ToString()
                };

                foreach (JToken listToken in siteToken["lists"] ?? new JArray())
                {
                    var list = new SharePointListEntity
                    {
                        Title = listToken["title"]?.ToString(),
                        Id = listToken["id"]?.ToString()
                    };

                    foreach (JToken columnToken in listToken["columns"] ?? new JArray())
                    {
                        list.Columns.Add(new SharePointColumnEntity
                        {
                            InternalName = columnToken["internalName"]?.ToString(),
                            DisplayName = columnToken["displayName"]?.ToString(),
                            TypeAsString = columnToken["type"]?.ToString(),
                            Required = columnToken["required"]?.ToObject<bool>() ?? false,
                            Choices = (columnToken["choices"] as JArray)?.Select(c => c.ToString()).ToList() ?? new List<string>()
                        });
                    }

                    foreach (JToken itemToken in listToken["sampleItems"] ?? new JArray())
                    {
                        var row = new Dictionary<string, object>();
                        foreach (JProperty prop in ((JObject)itemToken).Properties())
                        {
                            row[prop.Name] = prop.Value?.ToString();
                        }
                        list.SampleItems.Add(row);
                    }

                    site.Lists.Add(list);
                }

                result.Add(site);
            }

            return result;
        }

        private static string EscapeArg(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
