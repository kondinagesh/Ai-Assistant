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
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.None;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
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

    // Configure cookies for App Service environment
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.CorrelationCookie.SameSite = SameSiteMode.None;
    options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.NonceCookie.SameSite = SameSiteMode.None;

    // NOTE: Removed options.GenerateNonce = true; because
    //       'GenerateNonce' does not exist in MicrosoftIdentityOptions.

    // Support multiple tenants
    if (builder.Environment.IsProduction())
    {
        options.Authority = "https://login.microsoftonline.com/organizations/v2.0";
    }
    else
    {
        options.Authority = $"https://login.microsoftonline.com/{builder.Configuration["AzureAd:TenantId"]}/v2.0";
    }

    // Set metadata address
    options.MetadataAddress = options.Authority + "/.well-known/openid-configuration";

    // Configure retry and timeout settings
    options.BackchannelHttpHandler = new HttpClientHandler();
    options.BackchannelTimeout = TimeSpan.FromMinutes(2);

    // Store tokens for API access
    options.SaveTokens = true;
    options.UseTokenLifetime = true;
    options.GetClaimsFromUserInfoEndpoint = true;

    // Update to save tenant ID in session
    options.Events = new OpenIdConnectEvents
    {
        OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = $"{context.Request.Scheme}://{context.Request.Host}/signin-oidc";
            // The OpenID Connect middleware automatically generates a nonce. 
            // Below is optional if you want to explicitly set it:
            if (string.IsNullOrEmpty(context.ProtocolMessage.Nonce))
            {
                context.ProtocolMessage.Nonce = Guid.NewGuid().ToString();
            }
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

                // Redirect to Home page after successful token validation
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Apply forwarded headers middleware BEFORE other middleware
app.UseForwardedHeaders();

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
app.UseSession();

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

app.MapRazorPages();

app.Run();
