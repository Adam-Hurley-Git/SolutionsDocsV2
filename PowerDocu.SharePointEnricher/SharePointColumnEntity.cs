using System.Collections.Generic;

namespace PowerDocu.SharePointEnricher
{
    public class SharePointColumnEntity
    {
        public string InternalName;
        public string DisplayName;
        public string TypeAsString;
        public bool Required;
        public List<string> Choices = new List<string>();
    }
}
