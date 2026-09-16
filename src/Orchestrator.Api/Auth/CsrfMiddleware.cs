using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Api.Auth;

/// <summary>
/// Double-submit CSRF protection for the JSON API. Unsafe verbs to
/// <c>/api/*</c> must carry an <c>X-CSRF-Token</c> header matching the readable
/// <c>orchestrator_csrf</c> cookie issued at login. Webhook routes live under
/// <c>/webhooks/*</c> and authenticate by signature instead, so they are exempt.
/// Anonymous requests carry no ambient authority and are left alone; the check
/// only applies once a session is authenticated.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CSRF-Token";
    public const string CookieName = "orchestrator_csrf";
    public const string LoginPath = "/api/auth/login";

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresToken(context) && !TokenValid(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool RequiresToken(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method)
            || HttpMethods.IsTrace(context.Request.Method))
            return false;

        if (context.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (context.GetEndpoint() is null)
            return false;

        return context.User.Identity?.IsAuthenticated is true;
    }

    private static bool TokenValid(HttpContext context)
    {
        var header = context.Request.Headers[HeaderName].ToString();
        var cookie = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(cookie))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(cookie));
    }
}
