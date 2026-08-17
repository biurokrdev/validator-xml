using D2ViewerEditor.Application.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace D2ViewerEditor.Api.Security;

public static class ConfigureAuthentication
{
    
    public static IServiceCollection AddDevBypassAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(DevAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.RequireAppOperator, p => p.RequireAuthenticatedUser());
            options.AddPolicy(AuthorizationPolicies.RequireAppAdmin, p => p.RequireAuthenticatedUser());
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserProvider, HttpHeaderCurrentUserProvider>();
        services.AddSingleton<IGraphUserService, DisabledGraphUserService>();
        return services;
    }

    public const string WebAppScheme = "MyAzureAdScheme";

    public static IServiceCollection AddEntraIdAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var azureAd = new AzureAdOptions();
        configuration.GetSection(AzureAdOptions.SectionName).Bind(azureAd);
        services.Configure<AzureAdOptions>(configuration.GetSection(AzureAdOptions.SectionName));

        Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = !environment.IsProduction();

        var isLocalDev = Environment.GetEnvironmentVariable("IS_LOCAL_DEV") == "true";

        HttpClientHandler CreateHttpHandler()
        {
            if (isLocalDev)
                return new HttpClientHandler { UseProxy = false };

            var businessProxy = EntraBackchannel.CreateProxy(azureAd);
            if (businessProxy is null)
                return new HttpClientHandler();

            HttpClient.DefaultProxy = businessProxy;
            return new HttpClientHandler { UseProxy = true, Proxy = businessProxy };
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(
                jwtOptions =>
                {
                    configuration.Bind(AzureAdOptions.SectionName, jwtOptions);
                    if (!isLocalDev)
                        jwtOptions.BackchannelHttpHandler = CreateHttpHandler();
                    jwtOptions.IncludeErrorDetails = !environment.IsProduction();
                },
                identityOptions => configuration.Bind(AzureAdOptions.SectionName, identityOptions));

        services.AddAuthentication()
            .AddMicrosoftIdentityWebApp(
                oidcOptions =>
                {
                    oidcOptions.BackchannelHttpHandler = CreateHttpHandler();
                    oidcOptions.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    oidcOptions.Instance = azureAd.Instance;
                    oidcOptions.TenantId = azureAd.TenantId;
                    oidcOptions.ClientId = azureAd.ClientId;
                    oidcOptions.ClientSecret = azureAd.ClientSecret;
                    oidcOptions.ResponseType = OpenIdConnectResponseType.Code;
                    oidcOptions.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                    oidcOptions.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                    oidcOptions.Scope.Add("offline_access");
                    oidcOptions.Scope.Add("email");
                    oidcOptions.TokenValidationParameters.ValidateIssuerSigningKey = true;
                },
                configureCookieAuthenticationOptions: null,
                openIdConnectScheme: WebAppScheme,
                cookieScheme: null)
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches();

        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.TokenValidationParameters.RoleClaimType = "roles";
            options.TokenValidationParameters.NameClaimType = "name";
        });

        services.Configure<RolesOptions>(configuration.GetSection(RolesOptions.SectionName));
        services.AddScoped<IClaimsTransformation, ClaimsTransformer>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthorizationPolicies.RequireAppOperator, policy =>
                policy.RequireRole(azureAd.OperatorRole, azureAd.AdminRole)); 
            options.AddPolicy(AuthorizationPolicies.RequireAppAdmin, policy =>
                policy.RequireRole(azureAd.AdminRole));
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserProvider, ClaimsCurrentUserProvider>();

        if (!string.IsNullOrWhiteSpace(azureAd.ClientSecret)
            && !string.IsNullOrWhiteSpace(azureAd.ClientId)
            && !string.IsNullOrWhiteSpace(azureAd.TenantId))
        {
            services.AddSingleton<IGraphUserService, GraphUserService>();
        }
        else
        {
            services.AddSingleton<IGraphUserService, DisabledGraphUserService>();
        }

        return services;
    }
}
