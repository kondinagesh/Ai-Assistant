using Microsoft.Graph;
using Azure.Identity;
using DotNetOfficeAzureApp.Models;

namespace DotNetOfficeAzureApp.Services
{
    public class GraphService : IGraphService
    {
        private readonly IConfiguration _configuration;
        private readonly GraphServiceClient _graphClient;
        private readonly ILogger<GraphService> _logger;

        public GraphService(IConfiguration configuration, ILogger<GraphService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            var clientSecretCredential = new ClientSecretCredential(
                _configuration["AzureAd:TenantId"],
                _configuration["AzureAd:ClientId"],
                _configuration["AzureAd:ClientSecret"]);
            _graphClient = new GraphServiceClient(clientSecretCredential);
        }

        public async Task<List<UserInfo>> GetUsersAsync()
        {
            try
            {
                var users = await _graphClient.Users
                    .GetAsync(requestConfiguration => {
                        requestConfiguration.QueryParameters.Select = new[] { "mail", "displayName", "id" };
                    });

                return users?.Value?.Select(u => new UserInfo
                {
                    Email = u.Mail,
                    Name = u.DisplayName,
                    UserId = u.Id
                }).ToList() ?? new List<UserInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching users from Graph API");
                return new List<UserInfo>();
            }
        }
    }
}