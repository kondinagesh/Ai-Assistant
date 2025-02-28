using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace DotNetOfficeAzureApp.Pages
{
    public class LoginModel : PageModel
    {
        [BindProperty]
        public bool Loading { get; set; }

        [BindProperty]
        public string ReturnUrl { get; set; } = "/Home";  // Default redirect URL

        private readonly ILogger<LoginModel> _logger;

        public LoginModel(ILogger<LoginModel> logger)
        {
            _logger = logger;
        }

        public IActionResult OnGet()
        {
            if (User.Identity?.IsAuthenticated ?? false)
            {
                _logger.LogInformation($"User {User.Identity.Name} is already authenticated. Redirecting to /Home.");
                return RedirectToPage("/Home");
            }

            _logger.LogInformation("User is not authenticated, showing login page.");
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                var properties = new AuthenticationProperties
                {
                    RedirectUri = "/Home",  // Ensure redirect to Home after login
                    IsPersistent = true
                };

                properties.Items["prompt"] = "select_account";
                properties.Items["scope"] = "openid profile email";

                _logger.LogInformation("Initiating Microsoft authentication challenge");
                return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Login failed: {ex.Message}");
                return RedirectToPage("/Error");
            }
        }
    }
}
