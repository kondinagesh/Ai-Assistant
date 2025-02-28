using DotNetOfficeAzureApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DotNetOfficeAzureApp.Pages
{
    public class HomeModel : PageModel
    {
        private readonly ILogger<HomeModel> _logger;
        IAzureBlobStorageService _service;
        private readonly IAzureAISearchService _azureAIService;

        // Properties for user info
        public string UserName { get; set; }
        public string UserEmail { get; set; }
        public bool IsAuthenticated { get; set; }

        public List<string> blobFileNames { get; set; } = new List<string>();
        public List<string> Containers { get; set; } = new List<string>();

        public HomeModel(ILogger<HomeModel> logger, IAzureBlobStorageService service, IAzureAISearchService azureAIService)
        {
            _logger = logger;
            _service = service;
            _azureAIService = azureAIService;
        }

        public IActionResult OnGet()
        {
            // Get user info from session
            UserName = HttpContext.Session.GetString("UserName");
            UserEmail = HttpContext.Session.GetString("UserEmail");
            IsAuthenticated = !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(UserEmail);

            // If not authenticated, redirect to login
            if (!IsAuthenticated)
            {
                return RedirectToPage("/Login");
            }

            try
            {
                blobFileNames = _service.GetBlobFileNames();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting blob files");
                blobFileNames = new List<string>();
            }

            return Page();
        }

        public void OnGetContainers()
        {
            Containers = _service.GetContainers();
        }
    }
}