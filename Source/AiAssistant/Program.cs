using DotNetOfficeAzureApp.Services;
using Microsoft.Extensions.Azure;
using Azure.Identity;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web.UI;
using Microsoft.Graph;
using System.Net;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Configure HTTP client with timeout
builder.Services.AddHttpClient("AzureAD").ConfigureHttpClient(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});

// Add session support
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorPages(options => {
    options.RootDirectory = "/Pages";
});

// Configure cookie policy
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.None;
    options.HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always;
    options.Secure = CookieSecurePolicy.Always;
});

// Configure forwarded headers
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.All;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddMicrosoftIdentityWebApp(options =>
{
    builder.Configuration.GetSection("AzureAd").Bind(options);

    // Configure secure cookies
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.CorrelationCookie.SameSite = SameSiteMode.None;
    options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.NonceCookie.SameSite = SameSiteMode.None;

    // Ensure Authority is set correctly  
    options.Authority = $"https://login.microsoftonline.com/{builder.Configuration["AzureAd:TenantId"]}/v2.0";
    options.MetadataAddress = options.Authority + "/.well-known/openid-configuration";

    // Enable token storage
    options.SaveTokens = true;
    options.UseTokenLifetime = true;
    options.GetClaimsFromUserInfoEndpoint = true;

    // Relax token validation
    options.TokenValidationParameters.ValidateIssuer = false;

    // Update events
    options.Events = new OpenIdConnectEvents
    {
        OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = $"{context.Request.Scheme}://{context.Request.Host}/signin-oidc";

            // Manually set State and Nonce
            context.ProtocolMessage.State = Guid.NewGuid().ToString();
            context.ProtocolMessage.Nonce = Guid.NewGuid().ToString();

            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            context.Response.Redirect("/");
            context.HandleResponse();
            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            // Persist user info in session
            if (context.Principal?.Identity is System.Security.Claims.ClaimsIdentity identity)
            {
                var email = identity.FindFirst("preferred_username")?.Value;
                var name = identity.FindFirst("name")?.Value;
                var tenantId = identity.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
                               ?? identity.FindFirst("tid")?.Value;

                context.HttpContext.Session.SetString("UserEmail", email ?? "");
                context.HttpContext.Session.SetString("UserName", name ?? "");

                if (!string.IsNullOrEmpty(tenantId))
                {
                    context.HttpContext.Session.SetString("UserTenantId", tenantId);
                }

                // Redirect to Home after token validation
                context.Properties.RedirectUri = "/Home";
            }
            await Task.CompletedTask;
        }
    };
});

// Configure cookie authentication
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(24);
    options.SlidingExpiration = true;
});

// Add services
builder.Services.AddRazorPages().AddMicrosoftIdentityUI();
builder.Services.AddScoped<IAzureBlobStorageService, AzureBlobStorageService>();
builder.Services.AddScoped<IAzureAISearchService, AzureAISearchService>();
builder.Services.AddScoped<AzureSearchVectorizationService>();
builder.Services.AddScoped<IAccessControlService, AccessControlService>();
builder.Services.AddScoped<IDocumentTrackingService, DocumentTrackingService>();
builder.Services.AddScoped<IGraphService, GraphService>();

// Configure DNS resolution for private endpoints
builder.Services.AddHttpClient("PrivateEndpoints")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

// Add Azure clients with simplified configuration
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.AddBlobServiceClient(builder.Configuration["Storage:connectionString"])
        .WithName("StorageConnection");
    clientBuilder.AddQueueServiceClient(builder.Configuration["Storage:connectionString"])
        .WithName("StorageConnection");
    clientBuilder.AddTableServiceClient(builder.Configuration["Storage:connectionString"])
        .WithName("StorageConnection");
});

var app = builder.Build();

// Apply forwarded headers middleware BEFORE other middleware
app.UseForwardedHeaders();

// Use session before authentication
app.UseSession();

// Handle X-Forwarded-Proto header
app.Use((context, next) =>
{
    if (context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
    {
        context.Request.Scheme = context.Request.Headers["X-Forwarded-Proto"].ToString();
    }
    return next();
});

// Apply cookie policy  
app.UseCookiePolicy();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Add default route to redirect to login
app.MapGet("/", context =>
{
    context.Response.Redirect("/Home");
    return Task.CompletedTask;
});

// Create a scope to register service provider
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    // Register service provider so it can be accessed from services
    app.Use(async (context, next) =>
    {
        // Add service provider to HttpContext items so it can be retrieved in services
        context.Items["ServiceProvider"] = app.Services;
        await next.Invoke();
    });
}

app.MapRazorPages();

app.Run();