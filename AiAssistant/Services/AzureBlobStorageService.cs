using Azure.Storage.Blobs;
using DotNetOfficeAzureApp.Services;

public class AzureBlobStorageService : IAzureBlobStorageService
{
    private readonly IConfiguration _configuration;
    private readonly IConfigurationSection _configStorage;
    private readonly ILogger<AzureBlobStorageService> _logger;
    private readonly AzureSearchVectorizationService _vectorizationService;

    public AzureBlobStorageService(
        IConfiguration configuration,
        ILogger<AzureBlobStorageService> logger,
        AzureSearchVectorizationService vectorizationService)
    {
        _configuration = configuration;
        _configStorage = _configuration.GetSection("Storage");
        _logger = logger;
        _vectorizationService = vectorizationService;
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

            await containerClient.CreateAsync();
            await _vectorizationService.SetupVectorSearch(formattedContainerName);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating container");
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

            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(containerName.ToLower());
            await containerClient.CreateIfNotExistsAsync();

            BlobClient blobClient = containerClient.GetBlobClient(file.FileName);
            using (Stream stream = file.OpenReadStream())
            {
                await blobClient.UploadAsync(stream, true);
            }

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