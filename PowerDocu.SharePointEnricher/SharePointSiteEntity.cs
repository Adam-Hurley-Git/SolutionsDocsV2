using System.Collections.Generic;

namespace PowerDocu.SharePointEnricher
{
    public class SharePointSiteEntity
    {
        public string SiteUrl;
        public List<SharePointListEntity> Lists = new List<SharePointListEntity>();
    }
}
