using DotNetOfficeAzureApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DotNetOfficeAzureApp.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        IAzureBlobStorageService _service;
        private readonly IAzureAISearchService _azureAIService;

        // Properties for user info
        public string UserName { get; set; }
        public string UserEmail { get; set; }
        public bool IsAuthenticated { get; set; }

        public List<string> blobFileNames { get; set; } = new List<string>();
        public List<string> Containers { get; set; } = new List<string>();

        public IndexModel(ILogger<IndexModel> logger, IAzureBlobStorageService service, IAzureAISearchService azureAIService)
        {
            _logger = logger;
            _service = service;
            _azureAIService = azureAIService;
        }

        public IActionResult OnGet()
        {
            _logger.LogInformation("Checking authentication status on Index page...");

            // Force check if the user is authenticated
            if (User.Identity?.IsAuthenticated ?? false)
            {
                _logger.LogInformation("User is authenticated, redirecting to /Home");
                return RedirectToPage("/Home");
            }

            // Get user info from session
            UserName = HttpContext.Session.GetString("UserName");
            UserEmail = HttpContext.Session.GetString("UserEmail");
            IsAuthenticated = !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(UserEmail);

            if (!IsAuthenticated)
            {
                _logger.LogWarning("User is not authenticated, redirecting to /Login");
                return RedirectToPage("/Login");
            }

            _logger.LogInformation("User is authenticated but not redirected. Possible issue.");
            return Page();
        }


        public void OnGetContainers()
        {
            Containers = _service.GetContainers();
        }

        public async Task<IActionResult> OnPostAsync(IFormFile file)
        {
            try
            {
                if (file == null)
                {
                    return RedirectToPage();
                }

                string uploadedFileName = await _service.UploadFile(file);
                if (!string.IsNullOrEmpty(uploadedFileName))
                {
                    bool isIndexerSuccess = _azureAIService.UpdateIndexer();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, message: ex.Message);
            }
            return RedirectToPage();
        }

        public IActionResult OnPostDelete(string blobName)
        {
            _service.deleteBlobName(blobName);
            return RedirectToPage();
        }
    }
}