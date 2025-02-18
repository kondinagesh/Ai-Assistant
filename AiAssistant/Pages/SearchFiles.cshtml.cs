using DotNetOfficeAzureApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DotNetOfficeAzureApp.Pages
{
    public class SearchFilesModel : PageModel
    {
        private readonly IAzureAISearchService _aiSearchService;
        private readonly IAzureBlobStorageService _blobService;
        private readonly IAccessControlService _accessControlService;
        private readonly ILogger<SearchFilesModel> _logger;

        public List<SearchEntry> SearchHistory { get; set; }
        public List<string> Containers { get; set; }

        [BindProperty]
        public string SelectedChannel { get; set; } = "general";

        public SearchFilesModel(
            IAzureAISearchService aiSearchService,
            IAzureBlobStorageService blobService,
            IAccessControlService accessControlService,
            ILogger<SearchFilesModel> logger)
        {
            _aiSearchService = aiSearchService;
            _blobService = blobService;
            _accessControlService = accessControlService;
            _logger = logger;
            SearchHistory = new List<SearchEntry>();
            Containers = new List<string>();
        }

        public virtual async Task<IActionResult> OnGetAsync()
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var userName = HttpContext.Session.GetString("UserName");
            bool isAuthenticated = !string.IsNullOrEmpty(userEmail) && !string.IsNullOrEmpty(userName);

            if (!isAuthenticated)
            {
                return RedirectToPage("/Login");
            }

            try
            {
                Containers = await _accessControlService.GetAccessibleContainers(userEmail);
                _logger.LogInformation($"Loaded {Containers.Count} accessible containers for user {userEmail}");

                if (string.IsNullOrEmpty(SelectedChannel) || !Containers.Contains(SelectedChannel))
                {
                    SelectedChannel = Containers.FirstOrDefault() ?? "General";
                }

                SearchHistory = new List<SearchEntry>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading page data");
                Containers = new List<string> { "General" };
                SelectedChannel = "General";
            }

            return Page();
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

                // Convert the selected channel to lowercase for storage operations
                string containerName = selectedChannel.ToLower().Replace(" ", "-");

                var response = await _aiSearchService.SearchResultByOpenAI(searchInput, containerName);

                if (response?.Value?.Choices != null && response.Value.Choices.Count > 0)
                {
                    string answer = response.Value.Choices[0].Message.Content;
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
}