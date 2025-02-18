using DotNetOfficeAzureApp.Models;

namespace DotNetOfficeAzureApp.Services
{
    public interface IAccessControlService
    {
        Task UpdateAccessControl(string fileName, string containerName, string originalChannelName, AccessLevel level, List<string> selectedUsers);
        Task DeleteExistingAccessControl(string fileName, string containerName);
        Task<AccessControl> GetAccessControl(string fileName, string containerName);
        Task<List<string>> GetAccessibleContainers(string userEmail);
    }
}