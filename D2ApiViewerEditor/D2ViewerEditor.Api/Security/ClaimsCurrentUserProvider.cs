using System.Security.Claims;
using D2ViewerEditor.Application.Common.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace D2ViewerEditor.Api.Security;

public sealed class ClaimsCurrentUserProvider : ICurrentUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AzureAdOptions _options;
    private readonly ILogger<ClaimsCurrentUserProvider> _logger;

    public ClaimsCurrentUserProvider(
        IHttpContextAccessor httpContextAccessor,
        IOptions<AzureAdOptions> options,
        ILogger<ClaimsCurrentUserProvider> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value;
        _logger = logger;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public string? CorporateKey
    {
        get
        {
            var user = User;
            
            var value = user?.Claims
                .FirstOrDefault(c => string.Equals(c.Type, _options.CorporateKeyClaim, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                
                var claimTypes = user?.Claims.Select(c => c.Type).Distinct() ?? [];
                _logger.LogWarning(
                    "CorporateKey claim '{ExpectedClaim}' nieobecny lub pusty. IsAuthenticated={IsAuth}. Dostępne typy claimów: {ClaimTypes}",
                    _options.CorporateKeyClaim,
                    user?.Identity?.IsAuthenticated ?? false,
                    string.Join(", ", claimTypes));
                return null;
            }

            return value.Trim();
        }
    }

    public bool IsAdmin => User?.IsInRole(_options.AdminRole) ?? false;
}
