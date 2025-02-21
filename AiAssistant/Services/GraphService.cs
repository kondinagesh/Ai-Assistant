using Microsoft.Graph;
using Azure.Identity;
using DotNetOfficeAzureApp.Models;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DotNetOfficeAzureApp.Services
{
    /// <summary>
    /// Service for interacting with Microsoft Graph API to retrieve user information
    /// across multiple tenants.
    /// </summary>
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

        /// <summary>
        /// Retrieves user information from Microsoft Graph API for the current tenant.
        /// </summary>
        /// <returns>A list of user information objects</returns>
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

        /// <summary>
        /// Creates a Microsoft Graph client with the appropriate credentials.
        /// </summary>
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

        /// <summary>
        /// Fetches users from Microsoft Graph API with selected properties.
        /// </summary>
        private async Task<Microsoft.Graph.Models.UserCollectionResponse> FetchUsersFromGraph(
            GraphServiceClient graphClient)
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

        /// <summary>
        /// Maps Microsoft Graph users to application UserInfo objects,
        /// using UserPrincipalName as fallback for email addresses.
        /// </summary>
        private List<UserInfo> MapAndFilterUsers(IEnumerable<Microsoft.Graph.Models.User> graphUsers)
        {
            return graphUsers
                .Select(user => new UserInfo
                {
                    UserId = user.Id,
                    // Use Mail if available, otherwise fallback to UserPrincipalName
                    Email = !string.IsNullOrEmpty(user.Mail) ? user.Mail : user.UserPrincipalName,
                    Name = !string.IsNullOrEmpty(user.DisplayName)
                        ? user.DisplayName
                        : FormatNameFromParts(user.GivenName, user.Surname)
                })
                .Where(user => !string.IsNullOrEmpty(user.Email)) // Filter out users without email
                .OrderBy(user => user.Name)
                .ToList();
        }

        /// <summary>
        /// Formats a display name from given name and surname parts.
        /// </summary>
        private string FormatNameFromParts(string firstName, string lastName)
        {
            var parts = new[] { firstName, lastName }
                .Where(part => !string.IsNullOrEmpty(part));

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Logs summary information about retrieved users.
        /// </summary>
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

        /// <summary>
        /// Gets the effective tenant ID from user claims or session.
        /// </summary>
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

        /// <summary>
        /// Attempts to get the tenant ID from the user's claims.
        /// </summary>
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

        /// <summary>
        /// Attempts to get the tenant ID from the session.
        /// </summary>
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