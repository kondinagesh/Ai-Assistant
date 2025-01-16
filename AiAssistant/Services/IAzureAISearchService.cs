using Azure.AI.OpenAI;
using Azure;

public interface IAzureAISearchService
{
    bool UpdateIndexer();
    Task<bool> UpdateIndexerForContainer(string containerName);
    Task<Response<ChatCompletions>> SearchResultByOpenAI(string chatInput, string containerName = "general");
}