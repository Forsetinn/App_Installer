namespace AppInstaller.Models
{
    public class SearchResult
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Source { get; set; }
        public string Version { get; set; }

        // AI‐computed relevance score (higher = more relevant). 
        // We won't bind this directly to UI; it’s only for sorting.
        public int RelevanceScore { get; set; }
    }
}
