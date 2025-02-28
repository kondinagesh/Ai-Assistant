using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using DotNetOfficeAzureApp.Models;

namespace DotNetOfficeAzureApp.Services
{
    public class AccessControlService : IAccessControlService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<AccessControlService> _logger;
        private const string TableName = "DocumentAccessControl";

        public AccessControlService(IConfiguration configuration, ILogger<AccessControlService> logger)
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
                _logger.LogError(ex, "Error initializing AccessControlService");
                throw;
            }
        }

        public async Task UpdateAccessControl(string fileName, string containerName, string originalChannelName, AccessLevel level, List<string> selectedUsers)
        {
            try
            {
                selectedUsers ??= new List<string>();

                var entity = new AccessControlEntity
                {
                    PartitionKey = containerName.ToLower(),
                    RowKey = Guid.NewGuid().ToString(),
                    FileName = fileName,
                    OriginalChannelName = originalChannelName,
                    IsOpen = level == AccessLevel.Organization
                };

                switch (level)
                {
                    case AccessLevel.Selected:
                        entity.AccessList = string.Join(",", selectedUsers);
                        break;
                    case AccessLevel.Private:
                        entity.AccessList = selectedUsers.FirstOrDefault() ?? "";
                        break;
                    case AccessLevel.Organization:
                        entity.AccessList = "";
                        break;
                }

                await DeleteExistingAccessControl(fileName, containerName);
                await _tableClient.AddEntityAsync(entity);
                _logger.LogInformation($"Updated access control for {fileName} in {containerName}. Level: {level}, Users: {entity.AccessList}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating access control");
                throw;
            }
        }

        public async Task DeleteExistingAccessControl(string fileName, string containerName)
        {
            try
            {
                var query = _tableClient.QueryAsync<AccessControlEntity>(
                    filter: $"PartitionKey eq '{containerName.ToLower()}' and FileName eq '{fileName}'");

                await foreach (var entity in query)
                {
                    try
                    {
                        await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag);
                        _logger.LogInformation($"Deleted access control entry for file {fileName} in container {containerName}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deleting access control entry for file {fileName}");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting existing access control");
                throw;
            }
        }

        public async Task<AccessControl> GetAccessControl(string fileName, string containerName)
        {
            try
            {
                var query = _tableClient.QueryAsync<AccessControlEntity>(
                    filter: $"PartitionKey eq '{containerName.ToLower()}' and FileName eq '{fileName}'");

                AccessControlEntity entity = null;
                await foreach (var item in query)
                {
                    entity = item;
                    break;
                }

                if (entity == null)
                {
                    return new AccessControl
                    {
                        IsOpen = false,
                        Acl = new List<string>()
                    };
                }

                return new AccessControl
                {
                    IsOpen = entity.IsOpen,
                    Acl = string.IsNullOrEmpty(entity.AccessList) ?
                        new List<string>() :
                        entity.AccessList.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving access control");
                return new AccessControl
                {
                    IsOpen = false,
                    Acl = new List<string>()
                };
            }
        }

        public async Task<List<string>> GetAccessibleContainers(string userEmail)
        {
            try
            {
                var accessibleContainers = new HashSet<KeyValuePair<string, string>>();

                var queryResults = _tableClient.QueryAsync<AccessControlEntity>();

                await foreach (var entity in queryResults)
                {
                    bool hasAccess = false;

                    if (entity.IsOpen)
                    {
                        hasAccess = true;
                    }
                    else if (!string.IsNullOrEmpty(entity.AccessList))
                    {
                        var allowedUsers = entity.AccessList.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        hasAccess = allowedUsers.Contains(userEmail);
                    }

                    if (hasAccess)
                    {
                        accessibleContainers.Add(new KeyValuePair<string, string>(
                            entity.PartitionKey,
                            string.IsNullOrEmpty(entity.OriginalChannelName) ? entity.PartitionKey : entity.OriginalChannelName
                        ));
                    }
                }

                // Add "General" container by default
                accessibleContainers.Add(new KeyValuePair<string, string>("general", "General"));

                return accessibleContainers
                    .Select(x => x.Value)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving accessible containers");
                return new List<string> { "General" };
            }
        }
    }
}