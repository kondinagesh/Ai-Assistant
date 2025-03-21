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
        public string SelectedChannel { get; set; } = "General";

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
                // Get accessible containers and ensure General is always included
                var accessibleContainers = await _accessControlService.GetAccessibleContainers(userEmail);

                // Make sure "General" is always first by handling it separately in the view
                // Just keep it in the list for other operations
                if (!accessibleContainers.Contains("General", StringComparer.OrdinalIgnoreCase))
                {
                    accessibleContainers.Insert(0, "General");
                }

                // Sort the containers alphabetically (General will be displayed specially in the view)
                Containers = accessibleContainers.OrderBy(c => c).ToList();

                _logger.LogInformation($"Loaded {Containers.Count} accessible containers for user {userEmail}");

                // Default to General if no channel is selected
                if (string.IsNullOrEmpty(SelectedChannel) || !Containers.Contains(SelectedChannel, StringComparer.OrdinalIgnoreCase))
                {
                    SelectedChannel = "General";
                }
                // Convert (General) to General if needed
                else if (SelectedChannel == "(General)")
                {
                    SelectedChannel = "General";
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
                // Convert (General) to General if needed
                if (selectedChannel == "(General)")
                {
                    selectedChannel = "General";
                }

                if (string.IsNullOrEmpty(searchInput))
                {
                    return new JsonResult(new { success = false, message = "No input provided" });
                }

                _logger.LogInformation($"Processing search request - Query: {searchInput}, Channel: {selectedChannel}");

                // Get current user email for access control
                var userEmail = HttpContext.Session.GetString("UserEmail");
                if (string.IsNullOrEmpty(userEmail))
                {
                    return new JsonResult(new
                    {
                        success = false,
                        response = "You must be logged in to search documents.",
                        citations = new List<object>(),
                        citationCount = 0
                    });
                }

                // Convert the selected channel to lowercase for storage operations
                string containerName = selectedChannel.ToLower().Replace(" ", "-");

                // Use the full citation method with user email for access control
                var (content, citations) = await _aiSearchService.SearchResultByOpenAIWithFullCitations(
                    searchInput, containerName, userEmail);

                if (!string.IsNullOrEmpty(content))
                {
                    return new JsonResult(new
                    {
                        success = true,
                        response = content,
                        citations = citations.Select(c => new {
                            source = c.Source,
                            content = c.Content,
                            index = c.Index
                        }).ToList(),
                        citationCount = citations.Count
                    });
                }
                else
                {
                    _logger.LogWarning("No response from AI service");
                    return new JsonResult(new
                    {
                        success = false,
                        response = "I apologize, but I couldn't process your request at this time. Please try again.",
                        citations = new List<object>(),
                        citationCount = 0
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing search request");
                return new JsonResult(new
                {
                    success = false,
                    response = "An error occurred while processing your request. Please try again.",
                    citations = new List<object>(),
                    citationCount = 0
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