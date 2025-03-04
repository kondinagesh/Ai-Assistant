using Azure.AI.OpenAI;
using Azure;

public interface IAzureAISearchService
{
    bool UpdateIndexer();
    Task<bool> UpdateIndexerForContainer(string containerName);
    Task<Response<ChatCompletions>> SearchResultByOpenAI(string chatInput, string containerName = "general");
    Task<(string Content, List<CitationSourceInfo> Citations)> SearchResultByOpenAIWithFullCitations(
        string chatInput, string containerName = "general", string userEmail = null);
}

// Add this class for citation information
public class CitationSourceInfo
{
    public string Source { get; set; }
    public string Content { get; set; }
    public int Index { get; set; }
    public string Url { get; set; }
}