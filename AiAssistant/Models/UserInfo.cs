using System.Text.Json.Serialization;

namespace DotNetOfficeAzureApp.Models
{
    public class UserInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; }
    }
}