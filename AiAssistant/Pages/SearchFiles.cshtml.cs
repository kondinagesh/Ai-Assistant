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
        public string ErrorMessage { get; set; }

        [BindProperty]
        public string SelectedContainer { get; set; } = "general";

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
                // Load containers
                Containers = _blobService.GetContainers();
                if (!string.IsNullOrEmpty(SelectedContainer) && !Containers.Contains(SelectedContainer))
                {
                    SelectedContainer = Containers.FirstOrDefault() ?? "general";
                }

                // Load search history
                if (TempData["SearchHistory"] != null)
                {
                    SearchHistory = TempData.Get<List<SearchEntry>>("SearchHistory") ?? new List<SearchEntry>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading page data");
                ErrorMessage = "Error loading page data: " + ex.Message;
            }
        }

        public async Task<IActionResult> OnPostSearchAsync(string searchInput, string selectedContainer)
        {
            try
            {
                Containers = _blobService.GetContainers();

                if (string.IsNullOrEmpty(searchInput))
                {
                    ErrorMessage = "Please enter a search query.";
                    return Page();
                }

                if (string.IsNullOrEmpty(selectedContainer))
                {
                    selectedContainer = "general";
                }

                SelectedContainer = selectedContainer;
                _logger.LogInformation($"Searching in container: {selectedContainer}");

                var response = await _aiSearchService.SearchResultByOpenAI(searchInput, selectedContainer);

                if (response?.Value?.Choices == null || response.Value.Choices.Count == 0)
                {
                    ErrorMessage = "No response received from the AI service.";
                    return Page();
                }

                string answer = response.Value.Choices[0].Message.Content;

                // Add to history
                SearchHistory = TempData.Get<List<SearchEntry>>("SearchHistory") ?? new List<SearchEntry>();
                SearchHistory.Add(new SearchEntry
                {
                    Query = searchInput,
                    Response = answer,
                    Container = selectedContainer,
                    Timestamp = DateTime.UtcNow
                });

                // Keep only last 10 entries
                if (SearchHistory.Count > 10)
                {
                    SearchHistory = SearchHistory.OrderByDescending(x => x.Timestamp)
                                               .Take(10)
                                               .ToList();
                }

                TempData.Put("SearchHistory", SearchHistory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during AI search");
                ErrorMessage = $"Error processing your query: {ex.Message}";
            }

            return Page();
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