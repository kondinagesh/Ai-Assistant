using DotNetOfficeAzureApp.Models;

namespace DotNetOfficeAzureApp.Services
{
    public interface IGraphService
    {
        Task<List<UserInfo>> GetUsersAsync();
    }
}