using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using DotNetOfficeAzureApp.Services;

namespace DotNetOfficeAzureApp.Pages
{
    public class UploadAndManageService : PageModel
    {
        private readonly IAzureBlobStorageService _blobService;
        private readonly AzureSearchVectorizationService _vectorizationService;
        private readonly ILogger<UploadAndManageService> _logger;

        public List<string> BlobFileNames { get; private set; } = new List<string>();
        public List<string> Containers { get; private set; } = new List<string>();

        [BindProperty]
        public string SelectedChannel { get; set; } = "general";

        [TempData]
        public string StatusMessage { get; set; }

        public UploadAndManageService(
            IAzureBlobStorageService blobService,
            AzureSearchVectorizationService vectorizationService,
            ILogger<UploadAndManageService> logger)
        {
            _blobService = blobService;
            _vectorizationService = vectorizationService;
            _logger = logger;
        }

        public void OnGet()
        {
            LoadData();
        }

        public void OnGetContainers(string selectedChannel = "general")
        {
            SelectedChannel = selectedChannel;
            LoadData();
        }

        private void LoadData()
        {
            try
            {
                Containers = _blobService.GetContainers();
                BlobFileNames = _blobService.GetBlobFileNames(SelectedChannel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading data");
                TempData["ErrorMessage"] = "Error loading data: " + ex.Message;
            }
        }

        public async Task<IActionResult> OnPostAsync(IFormFile file)
        {
            try
            {
                if (file == null)
                {
                    TempData["ErrorMessage"] = "No file selected for upload";
                    return RedirectToPage();
                }

                string containerToUse = SelectedChannel;

                if (!string.IsNullOrWhiteSpace(containerToUse))
                {
                    await _blobService.CreateContainer(containerToUse);
                    containerToUse = containerToUse.ToLower().Replace(" ", "-");
                }

                string uploadedFileName = await _blobService.UploadFile(file, containerToUse);
                if (!string.IsNullOrEmpty(uploadedFileName))
                {
                    // Set up vector search components after successful file upload
                    await _vectorizationService.SetupVectorSearch(containerToUse);
                    TempData["SuccessMessage"] = "File uploaded successfully and search components created";
                }

                return RedirectToPage(new { handler = "Containers", SelectedChannel = containerToUse });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file");
                TempData["ErrorMessage"] = "Error uploading file: " + ex.Message;
                return RedirectToPage();
            }
        }

        public async Task<IActionResult> OnPostDelete(string fileName)
        {
            try
            {
                if (_blobService.deleteBlobName(fileName, SelectedChannel))
                {
                    TempData["SuccessMessage"] = "File deleted successfully";
                }
                else
                {
                    TempData["ErrorMessage"] = "Failed to delete file";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting file");
                TempData["ErrorMessage"] = "Error deleting file: " + ex.Message;
            }

            return RedirectToPage();
        }
    }
}