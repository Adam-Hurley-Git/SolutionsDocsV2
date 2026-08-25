using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PowerDocu.Shell
{
    // Mirror nav.js's exact expected JSON shape (window.NAV_TREE / window.SEARCH_INDEX) -
    // see mockup-v2/assets/nav.js's header comment for the contract this must match.
    public class NavTreeDto
    {
        public List<NavGroupDto> Groups { get; set; } = new();
    }

    public class NavGroupDto
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Label { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Count { get; set; }
        public List<NavDocDto> Docs { get; set; } = new();
    }

    public class NavDocDto
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Href { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Meta { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<NavTabDto> Tabs { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<NavKidDto> Kids { get; set; }
    }

    public class NavTabDto
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string File { get; set; }
    }

    public class NavKidDto
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Href { get; set; }
    }

    public class SearchEntryDto
    {
        public string N { get; set; }
        public string P { get; set; }
        public string H { get; set; }
    }
}
