public interface IAzureBlobStorageService
{
    Task<string> UploadFile(IFormFile formFile, string containerName = "general");
    List<string> GetBlobFileNames(string containerName = "general");
    bool deleteBlobName(string blobName, string containerName = "general");
    List<string> GetContainers();
    string GetConnectionString();
    Task<bool> CreateContainer(string containerName);
}