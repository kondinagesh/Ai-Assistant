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
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddRazorPages(options => {
    options.RootDirectory = "/Pages";
});

// Configure authentication
builder.Services.AddAuthentication(options => {
    options.DefaultScheme = "Cookies";
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddMicrosoftIdentityWebApp(options => {
    builder.Configuration.GetSection("AzureAd").Bind(options);
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

    // Set metadata address
    options.MetadataAddress = $"https://login.microsoftonline.com/{builder.Configuration["AzureAd:TenantId"]}/v2.0/.well-known/openid-configuration";

    // Configure retry and timeout settings
    options.BackchannelHttpHandler = new HttpClientHandler();
    options.BackchannelTimeout = TimeSpan.FromMinutes(2);

    options.Events = new OpenIdConnectEvents
    {
        OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = $"{context.Request.Scheme}://{context.Request.Host}/signin-oidc";
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

                context.HttpContext.Session.SetString("UserEmail", email ?? "");
                context.HttpContext.Session.SetString("UserName", name ?? "");

                // Redirect to Home page after successful token validation
                context.Properties.RedirectUri = "/Home";
            }
            await Task.CompletedTask;
        }
    };
});

// Add services
builder.Services.AddRazorPages().AddMicrosoftIdentityUI();
builder.Services.AddScoped<IAzureBlobStorageService, AzureBlobStorageService>();
builder.Services.AddScoped<IAzureAISearchService, AzureAISearchService>();
builder.Services.AddScoped<AzureSearchVectorizationService>();
builder.Services.AddScoped<IAccessControlService, AccessControlService>();
builder.Services.AddScoped<IDocumentTrackingService, DocumentTrackingService>();
builder.Services.AddScoped<IGraphService, GraphService>();

// Add Azure clients
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

// Configure forwarded headers if needed
app.Use((context, next) =>
{
    if (context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
    {
        context.Request.Scheme = context.Request.Headers["X-Forwarded-Proto"].ToString();
    }
    return next();
});

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