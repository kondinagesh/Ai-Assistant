// Path: /Services/AzureBlobStorageService.cs
using Azure.Storage.Blobs;
using DotNetOfficeAzureApp.Services;
using Microsoft.Extensions.Configuration;

public class AzureBlobStorageService : IAzureBlobStorageService
{
    private readonly IConfiguration _configuration;
    private readonly IConfigurationSection _configStorage;
    private readonly ILogger<AzureBlobStorageService> _logger;
    private readonly AzureSearchVectorizationService _vectorizationService;
    private readonly IDocumentTrackingService _documentTrackingService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AzureBlobStorageService(
        IConfiguration configuration,
        ILogger<AzureBlobStorageService> logger,
        AzureSearchVectorizationService vectorizationService,
        IDocumentTrackingService documentTrackingService,
        IHttpContextAccessor httpContextAccessor)
    {
        _configuration = configuration;
        _configStorage = _configuration.GetSection("Storage");
        _logger = logger;
        _vectorizationService = vectorizationService;
        _documentTrackingService = documentTrackingService;
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetConnectionString()
    {
        return _configStorage.GetValue<string>("connectionString");
    }

    public async Task<bool> CreateContainer(string containerName)
    {
        try
        {
            string connectionString = GetConnectionString();
            BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

            string formattedContainerName = containerName.ToLower().Replace(" ", "-");
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(formattedContainerName);

            if (!await containerClient.ExistsAsync())
            {
                await containerClient.CreateAsync();
                _logger.LogInformation($"Created new container: {formattedContainerName}");

                // Setup vector search for new container
                await _vectorizationService.SetupVectorSearch(formattedContainerName);
            }
            else
            {
                _logger.LogInformation($"Container {formattedContainerName} already exists");
                // Run the indexer for existing container
                await _vectorizationService.RunExistingIndexer(formattedContainerName);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating/using container");
            throw;
        }
    }

    public async Task<string> UploadFile(IFormFile file, string containerName = "general")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(containerName))
                containerName = "general";

            string connectionString = GetConnectionString();
            BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

            // Get or create container
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(containerName.ToLower());

            if (!await containerClient.ExistsAsync())
            {
                await containerClient.CreateIfNotExistsAsync();
                _logger.LogInformation($"Created new container: {containerName}");
            }
            else
            {
                _logger.LogInformation($"Using existing container: {containerName}");
            }

            BlobClient blobClient = containerClient.GetBlobClient(file.FileName);
            using (Stream stream = file.OpenReadStream())
            {
                await blobClient.UploadAsync(stream, true);
            }

            // Get user info from session
            var httpContext = _httpContextAccessor.HttpContext;
            var userName = httpContext?.Session.GetString("UserName");
            var userEmail = httpContext?.Session.GetString("UserEmail");

            // Track the upload
            await _documentTrackingService.TrackDocumentUploadAsync(
                userName,
                userEmail,
                file.FileName,
                containerName);

            _logger.LogInformation($"File {file.FileName} uploaded to container {containerName} by {userEmail}");
            return file.FileName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file");
            throw;
        }
    }

    public List<string> GetBlobFileNames(string containerName = "general")
    {
        List<string> fileNames = new List<string>();
        string connectionString = GetConnectionString();
        BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

        var containerClient = blobServiceClient.GetBlobContainerClient(containerName.ToLower());
        if (containerClient.Exists())
        {
            var blobs = containerClient.GetBlobs();
            foreach (var blob in blobs)
            {
                fileNames.Add(blob.Name);
            }
        }

        return fileNames;
    }

    public bool deleteBlobName(string blobName, string containerName = "general")
    {
        try
        {
            string connectionString = GetConnectionString();
            BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName.ToLower());
            return containerClient.DeleteBlobIfExists(blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting blob");
            return false;
        }
    }

    public List<string> GetContainers()
    {
        var containerNames = new List<string>();
        string connectionString = GetConnectionString();
        BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

        try
        {
            var containers = blobServiceClient.GetBlobContainers();
            foreach (var container in containers)
            {
                containerNames.Add(container.Name);
            }

            if (!containerNames.Contains("general"))
            {
                var containerClient = blobServiceClient.GetBlobContainerClient("general");
                containerClient.CreateIfNotExists();
                containerNames.Add("general");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting containers");
        }

        return containerNames.OrderBy(c => c).ToList();
    }
}