// Models/AccessControl.cs
using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;

namespace DotNetOfficeAzureApp.Models
{
    public class AccessControl
    {
        public bool IsOpen { get; set; }
        public List<string> Acl { get; set; } = new List<string>();
    }

    public enum AccessLevel
    {
        Private,
        Organization,
        Selected
    }

    public class AccessControlEntity : ITableEntity
    {
        public string PartitionKey { get; set; }  // ContainerName (lowercase)
        public string RowKey { get; set; }        // GUID
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public bool IsOpen { get; set; }
        public string AccessList { get; set; }    // Comma-separated list of emails
        public string FileName { get; set; }      // Store filename separately
        public string OriginalChannelName { get; set; }  // Add this new property
    }
}