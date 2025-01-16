using DotNetOfficeAzureApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using System.Text.Json;

namespace DotNetOfficeAzureApp.Pages
{
    public class SearchFilesModel : PageModel
    {
        private readonly IAzureAISearchService _aiSearchService;
        private readonly IAzureBlobStorageService _blobService;
        private readonly ILogger<SearchFilesModel> _logger;

        public List<SearchEntry> SearchHistory { get; set; }
        public List<string> Containers { get; set; }

        [BindProperty]
        public string SelectedChannel { get; set; } = "general";

        public SearchFilesModel(
            IAzureAISearchService aiSearchService,
            IAzureBlobStorageService blobService,
            ILogger<SearchFilesModel> logger)
        {
            _aiSearchService = aiSearchService;
            _blobService = blobService;
            _logger = logger;
            SearchHistory = new List<SearchEntry>();
            Containers = new List<string>();
        }

        public void OnGet()
        {
            try
            {
                // Load all available containers
                Containers = _blobService.GetContainers();
                _logger.LogInformation($"Loaded {Containers.Count} containers");

                if (string.IsNullOrEmpty(SelectedChannel) || !Containers.Contains(SelectedChannel))
                {
                    SelectedChannel = Containers.FirstOrDefault() ?? "general";
                }

                // Load search history
                if (TempData["SearchHistory"] != null)
                {
                    SearchHistory = TempData.Get<List<SearchEntry>>("SearchHistory") ?? new List<SearchEntry>();
                    TempData.Keep("SearchHistory");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading page data");
            }
        }

        public async Task<IActionResult> OnPostSearchAsync(string searchInput, string selectedChannel)
        {
            try
            {
                if (string.IsNullOrEmpty(searchInput))
                {
                    return new JsonResult(new { success = false, message = "No input provided" });
                }

                _logger.LogInformation($"Processing search request - Query: {searchInput}, Channel: {selectedChannel}");

                var response = await _aiSearchService.SearchResultByOpenAI(searchInput, selectedChannel);

                if (response?.Value?.Choices != null && response.Value.Choices.Count > 0)
                {
                    string answer = response.Value.Choices[0].Message.Content;

                    // Add to history
                    SearchHistory = TempData.Get<List<SearchEntry>>("SearchHistory") ?? new List<SearchEntry>();
                    SearchHistory.Add(new SearchEntry
                    {
                        Query = searchInput,
                        Response = answer,
                        Container = selectedChannel,
                        Timestamp = DateTime.UtcNow
                    });

                    if (SearchHistory.Count > 10)
                    {
                        SearchHistory = SearchHistory
                            .OrderByDescending(x => x.Timestamp)
                            .Take(10)
                            .ToList();
                    }

                    TempData.Put("SearchHistory", SearchHistory);

                    return new JsonResult(new { success = true, response = answer });
                }
                else
                {
                    _logger.LogWarning("No response from AI service");
                    return new JsonResult(new
                    {
                        success = false,
                        response = "I apologize, but I couldn't process your request at this time. Please try again."
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing search request");
                return new JsonResult(new
                {
                    success = false,
                    response = "An error occurred while processing your request. Please try again."
                });
            }
        }
    }

    public class SearchEntry
    {
        public string Query { get; set; }
        public string Response { get; set; }
        public string Container { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public static class TempDataExtensions
    {
        public static void Put<T>(this ITempDataDictionary tempData, string key, T value) where T : class
        {
            tempData[key] = JsonSerializer.Serialize(value);
        }

        public static T Get<T>(this ITempDataDictionary tempData, string key) where T : class
        {
            tempData.TryGetValue(key, out var value);
            return value == null ? null : JsonSerializer.Deserialize<T>((string)value);
        }
    }
}