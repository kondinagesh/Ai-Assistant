using Microsoft.Graph;
using Azure.Identity;
using DotNetOfficeAzureApp.Models;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Microsoft.Graph.Models;

namespace DotNetOfficeAzureApp.Services
{
    public class GraphService : IGraphService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<GraphService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private const string DefaultScope = "https://graph.microsoft.com/.default";
        private const string TenantIdClaimType = "http://schemas.microsoft.com/identity/claims/tenantid";
        private const string TenantIdClaimAlias = "tid";
        private const string UserTenantIdSessionKey = "UserTenantId";

        public GraphService(
            IConfiguration configuration,
            ILogger<GraphService> logger,
            IHttpContextAccessor httpContextAccessor)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        }

        public async Task<List<UserInfo>> GetUsersAsync()
        {
            using var logScope = _logger.BeginScope("GetUsersAsync");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                string tenantId = GetEffectiveTenantId();
                _logger.LogInformation("Using tenant ID: {TenantId}", tenantId);

                var graphClient = CreateGraphClient(tenantId);
                var users = await FetchUsersFromGraph(graphClient);

                if (users?.Value == null || !users.Value.Any())
                {
                    _logger.LogWarning("No users retrieved from Microsoft Graph API");
                    return new List<UserInfo>();
                }

                var result = MapAndFilterUsers(users.Value);
                LogUserResults(result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve users from Microsoft Graph API");
                return new List<UserInfo>();
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogInformation("Operation completed in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
            }
        }

        public async Task<List<UserInfo>> GetUsersAsync(string searchQuery, int maxResults)
        {
            using var logScope = _logger.BeginScope("GetUsersAsync with search");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                string tenantId = GetEffectiveTenantId();
                var graphClient = CreateGraphClient(tenantId);
                var users = await FetchUsersFromGraphWithFilter(graphClient, searchQuery, maxResults);

                if (users?.Value == null || !users.Value.Any())
                {
                    _logger.LogWarning("No users found matching search criteria");
                    return new List<UserInfo>();
                }

                var result = MapAndFilterUsers(users.Value)
                    .OrderBy(u => u.Email)
                    .Take(maxResults)
                    .ToList();

                _logger.LogInformation("Found {Count} users matching search criteria", result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search users from Microsoft Graph API. Search: {SearchQuery}", searchQuery);
                return new List<UserInfo>();
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogInformation("Search completed in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
            }
        }

        private GraphServiceClient CreateGraphClient(string tenantId)
        {
            var clientId = _configuration["AzureAd:ClientId"];
            var clientSecret = _configuration["AzureAd:ClientSecret"];

            _logger.LogDebug("Creating Graph client for tenant {TenantId} with client ID {ClientId}",
                tenantId, clientId);

            var credential = new ClientSecretCredential(
                tenantId,
                clientId,
                clientSecret);

            return new GraphServiceClient(credential, new[] { DefaultScope });
        }

        private async Task<UserCollectionResponse> FetchUsersFromGraph(GraphServiceClient graphClient)
        {
            _logger.LogInformation("Fetching users from Microsoft Graph API");

            return await graphClient.Users
                .GetAsync(options =>
                {
                    options.QueryParameters.Select = new[]
                    {
                        "id",
                        "displayName",
                        "mail",
                        "userPrincipalName",
                        "givenName",
                        "surname",
                        "accountEnabled"
                    };
                    options.QueryParameters.Top = 999; // Maximum allowed page size
                });
        }

        private async Task<UserCollectionResponse> FetchUsersFromGraphWithFilter(
            GraphServiceClient graphClient, string searchQuery, int maxResults)
        {
            _logger.LogInformation("Fetching users with filter. Search: {SearchQuery}, MaxResults: {MaxResults}",
                searchQuery, maxResults);

            try
            {
                // Sanitize the search query to prevent injection
                searchQuery = searchQuery.Replace("'", "''");

                return await graphClient.Users
                    .GetAsync(options =>
                    {
                        options.QueryParameters.Select = new[]
                        {
                            "id",
                            "displayName",
                            "mail",
                            "userPrincipalName",
                            "givenName",
                            "surname"
                        };
                        options.QueryParameters.Filter = $"startsWith(mail,'{searchQuery}') or startsWith(userPrincipalName,'{searchQuery}')";
                        options.QueryParameters.Top = maxResults;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching users with filter");
                throw;
            }
        }

        private List<UserInfo> MapAndFilterUsers(IEnumerable<Microsoft.Graph.Models.User> graphUsers)
        {
            return graphUsers
                .Where(user => !string.IsNullOrEmpty(user.Mail) || !string.IsNullOrEmpty(user.UserPrincipalName))
                .Select(user => new UserInfo
                {
                    UserId = user.Id,
                    Email = !string.IsNullOrEmpty(user.Mail) ? user.Mail : user.UserPrincipalName,
                    Name = !string.IsNullOrEmpty(user.DisplayName)
                        ? user.DisplayName
                        : FormatNameFromParts(user.GivenName, user.Surname)
                })
                .ToList();
        }

        private string FormatNameFromParts(string firstName, string lastName)
        {
            var parts = new[] { firstName, lastName }
                .Where(part => !string.IsNullOrEmpty(part));
            return string.Join(" ", parts);
        }

        private void LogUserResults(List<UserInfo> users)
        {
            _logger.LogInformation("Successfully mapped {Count} users with valid email addresses",
                users.Count);

            if (users.Any())
            {
                var sampleEmails = string.Join(", ",
                    users.Take(Math.Min(3, users.Count))
                         .Select(user => user.Email));

                _logger.LogInformation("Sample user emails: {SampleEmails}", sampleEmails);
            }
        }

        private string GetEffectiveTenantId()
        {
            string tenantId = GetTenantIdFromClaims();

            if (string.IsNullOrEmpty(tenantId))
            {
                tenantId = GetTenantIdFromSession();
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogWarning("No tenant ID found in claims or session. Using configured tenant.");
                tenantId = _configuration["AzureAd:TenantId"];
            }

            return tenantId;
        }

        private string GetTenantIdFromClaims()
        {
            if (_httpContextAccessor.HttpContext?.User?.Identity is not ClaimsIdentity identity)
            {
                return null;
            }

            var tenantIdClaim = identity.FindFirst(TenantIdClaimType) ??
                               identity.FindFirst(TenantIdClaimAlias);

            if (tenantIdClaim != null)
            {
                _logger.LogDebug("Found tenant ID in claims: {TenantId}", tenantIdClaim.Value);
                return tenantIdClaim.Value;
            }

            _logger.LogDebug("Tenant ID not found in user claims");
            return null;
        }

        private string GetTenantIdFromSession()
        {
            var tenantId = _httpContextAccessor.HttpContext?.Session.GetString(UserTenantIdSessionKey);

            if (!string.IsNullOrEmpty(tenantId))
            {
                _logger.LogDebug("Using tenant ID from session: {TenantId}", tenantId);
                return tenantId;
            }

            return null;
        }
    }
}