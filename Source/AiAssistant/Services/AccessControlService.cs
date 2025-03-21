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

                // First, check if there's already an access control entity for this file
                var existingEntities = new List<AccessControlEntity>();
                var query = _tableClient.QueryAsync<AccessControlEntity>(
                    filter: $"PartitionKey eq '{containerName.ToLower()}' and FileName eq '{fileName}'");

                await foreach (var entity in query)
                {
                    existingEntities.Add(entity);
                }

                // If we're setting Organization level access, we can delete any existing entries
                // as everyone will have access anyway
                if (level == AccessLevel.Organization)
                {
                    // Delete any existing entries
                    foreach (var entity in existingEntities)
                    {
                        await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag);
                    }

                    // Create new Organization-level access
                    var newEntity = new AccessControlEntity
                    {
                        PartitionKey = containerName.ToLower(),
                        RowKey = Guid.NewGuid().ToString(),
                        FileName = fileName,
                        OriginalChannelName = originalChannelName,
                        IsOpen = true,
                        AccessList = ""
                    };

                    await _tableClient.AddEntityAsync(newEntity);
                    _logger.LogInformation($"Updated access control for {fileName} to Organization level");
                    return;
                }

                // For Private or Selected access, we need to handle it differently
                if (existingEntities.Count == 0)
                {
                    // No existing access control, create new
                    var entity = new AccessControlEntity
                    {
                        PartitionKey = containerName.ToLower(),
                        RowKey = Guid.NewGuid().ToString(),
                        FileName = fileName,
                        OriginalChannelName = originalChannelName,
                        IsOpen = false
                    };

                    switch (level)
                    {
                        case AccessLevel.Selected:
                            entity.AccessList = string.Join(",", selectedUsers);
                            break;
                        case AccessLevel.Private:
                            entity.AccessList = selectedUsers.FirstOrDefault() ?? "";
                            break;
                    }

                    await _tableClient.AddEntityAsync(entity);
                    _logger.LogInformation($"Created new access control for {fileName}. Level: {level}, Users: {entity.AccessList}");
                }
                else
                {
                    // There are existing access control entries
                    // Get the current access control
                    var currentEntity = existingEntities.First();

                    // If current is Organization-level, and we're changing to Private/Selected,
                    // delete the current and create a new one
                    if (currentEntity.IsOpen && level != AccessLevel.Organization)
                    {
                        await _tableClient.DeleteEntityAsync(currentEntity.PartitionKey, currentEntity.RowKey, currentEntity.ETag);

                        var newEntity = new AccessControlEntity
                        {
                            PartitionKey = containerName.ToLower(),
                            RowKey = Guid.NewGuid().ToString(),
                            FileName = fileName,
                            OriginalChannelName = originalChannelName,
                            IsOpen = false,
                            AccessList = level == AccessLevel.Private
                                ? (selectedUsers.FirstOrDefault() ?? "")
                                : string.Join(",", selectedUsers)
                        };

                        await _tableClient.AddEntityAsync(newEntity);
                        _logger.LogInformation($"Replaced Organization access with {level} access for {fileName}");
                    }
                    else if (!currentEntity.IsOpen)
                    {
                        // Current is Private or Selected, update the access list
                        // If we're updating to Private, respect the current user's selection
                        if (level == AccessLevel.Private)
                        {
                            // For Private, we only add the current user if they're not already there
                            var currentUser = selectedUsers.FirstOrDefault() ?? "";
                            if (!string.IsNullOrEmpty(currentUser))
                            {
                                // Merge with existing users to maintain their access
                                var existingUsers = string.IsNullOrEmpty(currentEntity.AccessList)
                                    ? new List<string>()
                                    : currentEntity.AccessList.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                                if (!existingUsers.Contains(currentUser, StringComparer.OrdinalIgnoreCase))
                                {
                                    existingUsers.Add(currentUser);
                                }

                                currentEntity.AccessList = string.Join(",", existingUsers);
                            }
                        }
                        else if (level == AccessLevel.Selected)
                        {
                            // For Selected, merge the new users with existing ones
                            var existingUsers = string.IsNullOrEmpty(currentEntity.AccessList)
                                ? new List<string>()
                                : currentEntity.AccessList.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                            foreach (var user in selectedUsers)
                            {
                                if (!string.IsNullOrEmpty(user) && !existingUsers.Contains(user, StringComparer.OrdinalIgnoreCase))
                                {
                                    existingUsers.Add(user);
                                }
                            }

                            currentEntity.AccessList = string.Join(",", existingUsers);
                        }

                        await _tableClient.UpdateEntityAsync(currentEntity, currentEntity.ETag, TableUpdateMode.Replace);
                        _logger.LogInformation($"Updated access list for {fileName}. New access list: {currentEntity.AccessList}");
                    }
                }
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