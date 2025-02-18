using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using DotNetOfficeAzureApp.Services;
using DotNetOfficeAzureApp.Models;

namespace DotNetOfficeAzureApp.Pages
{
    public class UploadAndManageService : PageModel
    {
        private readonly IAzureBlobStorageService _blobService;
        private readonly AzureSearchVectorizationService _vectorizationService;
        private readonly IAccessControlService _accessControlService;
        private readonly IGraphService _graphService;
        private readonly ILogger<UploadAndManageService> _logger;

        public List<string> BlobFileNames { get; private set; } = new List<string>();
        public List<string> Containers { get; private set; } = new List<string>();

        [BindProperty]
        public string SelectedChannel { get; set; } = "General";

        [BindProperty]
        public AccessLevel AccessLevel { get; set; }

        [BindProperty]
        public string SelectedUsers { get; set; }

        public UploadAndManageService(
            IAzureBlobStorageService blobService,
            AzureSearchVectorizationService vectorizationService,
            IAccessControlService accessControlService,
            IGraphService graphService,
            ILogger<UploadAndManageService> logger)
        {
            _blobService = blobService;
            _vectorizationService = vectorizationService;
            _accessControlService = accessControlService;
            _graphService = graphService;
            _logger = logger;
        }

        public async Task<IActionResult> OnGet()
        {
            var userEmail = HttpContext.Session.GetString("UserEmail");
            var userName = HttpContext.Session.GetString("UserName");
            bool isAuthenticated = !string.IsNullOrEmpty(userEmail) && !string.IsNullOrEmpty(userName);

            if (!isAuthenticated)
            {
                return RedirectToPage("/Login");
            }

            await LoadData();
            return Page();
        }

        private async Task LoadData()
        {
            try
            {
                var userEmail = HttpContext.Session.GetString("UserEmail");
                // Use the access control service to get original channel names
                Containers = await _accessControlService.GetAccessibleContainers(userEmail);
                if (string.IsNullOrEmpty(SelectedChannel) || !Containers.Contains(SelectedChannel))
                {
                    SelectedChannel = Containers.FirstOrDefault() ?? "General";
                }
                // Use the lowercase version for getting blob files
                BlobFileNames = _blobService.GetBlobFileNames(SelectedChannel.ToLower().Replace(" ", "-"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading data");
                TempData["ErrorMessage"] = "Error loading data: " + ex.Message;
            }
        }

        public async Task<IActionResult> OnGetAccessControlAsync(string fileName, string containerName)
        {
            try
            {
                var accessControl = await _accessControlService.GetAccessControl(fileName, containerName.ToLower().Replace(" ", "-"));
                return new JsonResult(accessControl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting access control");
                return new JsonResult(new { error = "Failed to get access control" });
            }
        }

        public async Task<IActionResult> OnPostAsync(List<IFormFile> files)
        {
            try
            {
                if (files == null || files.Count == 0)
                {
                    return new JsonResult(new
                    {
                        success = false,
                        message = "No files selected for upload"
                    });
                }

                string containerToUse = SelectedChannel.ToLower().Replace(" ", "-");
                string originalChannelName = SelectedChannel;  // Preserve original name

                if (!string.IsNullOrWhiteSpace(containerToUse))
                {
                    await _blobService.CreateContainer(containerToUse);
                }

                var successfulUploads = 0;
                var failedUploads = 0;

                foreach (var file in files)
                {
                    try
                    {
                        string uploadedFileName = await _blobService.UploadFile(file, containerToUse);

                        if (!string.IsNullOrEmpty(uploadedFileName))
                        {
                            await _vectorizationService.SetupVectorSearch(containerToUse);

                            List<string> usersList = new List<string>();

                            if (AccessLevel == AccessLevel.Selected && !string.IsNullOrEmpty(SelectedUsers))
                            {
                                usersList = SelectedUsers.Split(',')
                                    .Select(email => email.Trim())
                                    .Where(email => !string.IsNullOrEmpty(email))
                                    .ToList();
                            }
                            else if (AccessLevel == AccessLevel.Private)
                            {
                                var userEmail = HttpContext.Session.GetString("UserEmail");
                                usersList = new List<string> { userEmail };
                            }

                            await _accessControlService.UpdateAccessControl(
                                uploadedFileName,
                                containerToUse,
                                originalChannelName,  // Pass original name
                                AccessLevel,
                                usersList
                            );

                            successfulUploads++;
                        }
                        else
                        {
                            failedUploads++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error uploading file {file.FileName}");
                        failedUploads++;
                    }
                }

                return new JsonResult(new
                {
                    success = successfulUploads > 0,
                    message = successfulUploads > 0
                        ? $"Successfully uploaded {successfulUploads} file(s)"
                        : "No files were uploaded successfully",
                    successfulUploads,
                    failedUploads
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in file upload");
                return new JsonResult(new
                {
                    success = false,
                    message = "Error uploading files: " + ex.Message
                });
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(string fileName)
        {
            try
            {
                string containerName = SelectedChannel.ToLower().Replace(" ", "-");
                if (_blobService.deleteBlobName(fileName, containerName))
                {
                    await _accessControlService.DeleteExistingAccessControl(fileName, containerName);
                    await _vectorizationService.RunExistingIndexer(containerName);
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

            return RedirectToPage(new { SelectedChannel });
        }

        public async Task<IActionResult> OnGetContainers(string selectedChannel)
        {
            if (!string.IsNullOrEmpty(selectedChannel))
            {
                SelectedChannel = selectedChannel;
            }
            await LoadData();
            return Page();
        }

        public async Task<IActionResult> OnGetUsersAsync()
        {
            try
            {
                var users = await _graphService.GetUsersAsync();
                return new JsonResult(users);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching users");
                return new JsonResult(new { error = "Failed to fetch users" });
            }
        }
    }
}