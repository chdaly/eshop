using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace eShop.Catalog.FunctionalTests;

/// <summary>
/// Test middleware that conditionally authenticates requests.
/// Requests with the <see cref="UserIdHeaderName"/> header are authenticated with that user ID.
/// Requests without the header remain anonymous (unauthenticated).
/// </summary>
class AutoAuthorizeMiddleware
{
    public const string IDENTITY_ID = "9e3163b9-1ae6-4652-9dc6-7898ab7b7a00";
    public const string UserIdHeaderName = "X-Test-UserId";

    private readonly RequestDelegate _next;

    public AutoAuthorizeMiddleware(RequestDelegate rd)
    {
        _next = rd;
    }

    public async Task Invoke(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue(UserIdHeaderName, out var userIdValues))
        {
            var userId = userIdValues.FirstOrDefault() ?? IDENTITY_ID;
            var identity = new ClaimsIdentity("cookies");

            identity.AddClaim(new Claim("sub", userId));
            identity.AddClaim(new Claim("unique_name", userId));
            identity.AddClaim(new Claim(ClaimTypes.Name, userId));

            httpContext.User.AddIdentity(identity);
        }

        await _next.Invoke(httpContext);
    }
}
