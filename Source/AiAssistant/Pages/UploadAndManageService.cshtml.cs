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
        private readonly IDocumentTrackingService _documentTrackingService;
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
            IDocumentTrackingService documentTrackingService,
            ILogger<UploadAndManageService> logger)
        {
            _blobService = blobService;
            _vectorizationService = vectorizationService;
            _accessControlService = accessControlService;
            _graphService = graphService;
            _documentTrackingService = documentTrackingService;
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
                var userEmail = HttpContext.Session.GetString("UserEmail");

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
                                // Parse the selected users
                                usersList = SelectedUsers.Split(',')
                                    .Select(email => email.Trim())
                                    .Where(email => !string.IsNullOrEmpty(email))
                                    .ToList();

                                // Ensure the current user is included
                                if (!string.IsNullOrEmpty(userEmail) && !usersList.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
                                {
                                    usersList.Add(userEmail);
                                }
                            }
                            else if (AccessLevel == AccessLevel.Private)
                            {
                                // Private access is only for the current user
                                if (!string.IsNullOrEmpty(userEmail))
                                {
                                    usersList = new List<string> { userEmail };
                                }
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
                string userEmail = HttpContext.Session.GetString("UserEmail");

                if (string.IsNullOrEmpty(userEmail))
                {
                    TempData["ErrorMessage"] = "You must be logged in to delete files";
                    return RedirectToPage(new { handler = "Containers", selectedChannel = SelectedChannel });
                }

                // Check if the user has access to the file before allowing deletion
                var accessControl = await _accessControlService.GetAccessControl(fileName, containerName);
                bool hasAccess = false;

                if (accessControl.IsOpen)
                {
                    // Organization-level access - still need to check if the user is the owner
                    // This implementation assumes only the owner can delete files with org-level access
                    var userUploads = await _documentTrackingService.GetContainerUploadsAsync(containerName);
                    var fileUpload = userUploads.FirstOrDefault(u => u.FileName == fileName);
                    hasAccess = fileUpload != null && fileUpload.UserEmail.Equals(userEmail, StringComparison.OrdinalIgnoreCase);
                }
                else if (accessControl.Acl != null && accessControl.Acl.Count > 0)
                {
                    // Check if user is in the access control list
                    hasAccess = accessControl.Acl.Contains(userEmail, StringComparer.OrdinalIgnoreCase);

                    // If user is just in the ACL but not the owner, they shouldn't delete
                    // Check if the user is the actual owner of the file
                    if (hasAccess)
                    {
                        var userUploads = await _documentTrackingService.GetContainerUploadsAsync(containerName);
                        var fileUpload = userUploads.FirstOrDefault(u => u.FileName == fileName);
                        hasAccess = fileUpload != null && fileUpload.UserEmail.Equals(userEmail, StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (!hasAccess)
                {
                    _logger.LogWarning($"User {userEmail} attempted to delete file {fileName} without permission");
                    TempData["ErrorMessage"] = "You don't have permission to delete this file";
                    return RedirectToPage(new { handler = "Containers", selectedChannel = SelectedChannel });
                }

                // User has permission to delete, proceed with deletion
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

            // Fix: Pass the selected channel as a route value instead of model binding
            return RedirectToPage(new { handler = "Containers", selectedChannel = SelectedChannel });
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

        public async Task<IActionResult> OnGetUsersAsync([FromQuery] string search)
        {
            try
            {
                if (string.IsNullOrEmpty(search) || search.Length < 1)
                {
                    return new JsonResult(new List<UserInfo>());
                }

                var users = await _graphService.GetUsersAsync(search, 10);
                return new JsonResult(users);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching users with search: {Search}", search);
                return new JsonResult(new { error = "Failed to fetch users" });
            }
        }
    }
}