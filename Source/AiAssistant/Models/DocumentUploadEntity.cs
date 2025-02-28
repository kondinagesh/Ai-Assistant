// Path: /Models/DocumentUploadEntity.cs
using Azure;
using Azure.Data.Tables;

namespace DotNetOfficeAzureApp.Models
{
    public class DocumentUploadEntity : ITableEntity
    {
        public string PartitionKey { get; set; }  // Channel name
        public string RowKey { get; set; }        // Unique ID
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string UserName { get; set; }
        public string UserEmail { get; set; }
        public string FileName { get; set; }
        public string ContainerName { get; set; }
        public DateTime UploadDateTime { get; set; }
    }
}