using DotNetOfficeAzureApp.Models;

namespace DotNetOfficeAzureApp.Services
{
    public interface IDocumentTrackingService
    {
        Task TrackDocumentUploadAsync(string userName, string userEmail, string fileName, string containerName);
        Task<List<DocumentUploadEntity>> GetUserUploadsAsync(string userEmail);
        Task<List<DocumentUploadEntity>> GetContainerUploadsAsync(string containerName);
    }
}