// Path: /Services/DocumentTrackingService.cs
using Azure.Data.Tables;
using DotNetOfficeAzureApp.Models;
using Microsoft.Extensions.Configuration;

namespace DotNetOfficeAzureApp.Services
{
    public class DocumentTrackingService : IDocumentTrackingService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<DocumentTrackingService> _logger;
        private const string TableName = "DocumentUploads";

        public DocumentTrackingService(IConfiguration configuration, ILogger<DocumentTrackingService> logger)
        {
            _logger = logger;
            try
            {
                var connectionString = configuration.GetSection("Storage")["connectionString"];
                var tableServiceClient = new TableServiceClient(connectionString);
                tableServiceClient.CreateTableIfNotExists(TableName);
                _tableClient = tableServiceClient.GetTableClient(TableName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing DocumentTrackingService");
                throw;
            }
        }

        public async Task TrackDocumentUploadAsync(string userName, string userEmail, string fileName, string containerName)
        {
            try
            {
                var entity = new DocumentUploadEntity
                {
                    PartitionKey = containerName.ToLower(),
                    RowKey = Guid.NewGuid().ToString(),
                    UserName = userName ?? "Unknown",
                    UserEmail = userEmail ?? "Unknown",
                    FileName = fileName,
                    ContainerName = containerName,
                    UploadDateTime = DateTime.UtcNow
                };

                await _tableClient.AddEntityAsync(entity);
                _logger.LogInformation($"Tracked upload of {fileName} by {userEmail} to {containerName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error tracking document upload");
                throw;
            }
        }

        public async Task<List<DocumentUploadEntity>> GetUserUploadsAsync(string userEmail)
        {
            try
            {
                var uploads = new List<DocumentUploadEntity>();
                var queryResults = _tableClient.QueryAsync<DocumentUploadEntity>(ent => ent.UserEmail == userEmail);

                await foreach (var entity in queryResults)
                {
                    uploads.Add(entity);
                }

                return uploads;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user uploads");
                throw;
            }
        }

        public async Task<List<DocumentUploadEntity>> GetContainerUploadsAsync(string containerName)
        {
            try
            {
                var uploads = new List<DocumentUploadEntity>();
                var queryResults = _tableClient.QueryAsync<DocumentUploadEntity>(
                    filter: $"PartitionKey eq '{containerName.ToLower()}'");

                await foreach (var entity in queryResults)
                {
                    uploads.Add(entity);
                }

                return uploads;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving container uploads");
                throw;
            }
        }
    }
}