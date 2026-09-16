using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Api.Auth;

public sealed record LoginRequest(string LoginName, string Password);

/// <summary>
/// Session endpoints for the single administrator account store. There is no
/// registration endpoint by design: accounts are created only by the
/// provision-admin CLI (Task 11).
/// </summary>
public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/auth");
        group.MapPost("/login", (Delegate)Login).AllowAnonymous();
        group.MapPost("/logout", (Delegate)Logout).RequireAuthorization();
        group.MapGet("/me", (Delegate)Me).RequireAuthorization();
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        OrchestratorDbContext db,
        IPasswordHasher hasher,
        IAuditWriter audit,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var loginName = request.LoginName.Trim().ToLowerInvariant();
        var account = await db.AdminAccounts
            .SingleOrDefaultAsync(a => a.LoginName == loginName, ct);

        // Always run exactly one verification so the response time does not
        // reveal whether the login exists.
        var dummyHash = account is null ? hasher.Hash("orchestrator-nonexistent-login-dummy") : null;
        var verified = hasher.Verify(account?.PasswordHash ?? dummyHash!, request.Password);

        var now = clock.GetUtcNow();
        var correlationId = Guid.CreateVersion7().ToString();

        if (account is null || !account.IsActive || verified == PasswordVerificationResult.Fail)
        {
            await audit.RecordAsync(new AuditEntry(
                Actor: $"auth:{loginName}",
                TaskId: null,
                Action: "auth.login",
                Reason: "Invalid login, wrong password, or deactivated account.",
                Outcome: "Failed",
                CorrelationId: correlationId,
                Details: new Dictionary<string, string> { ["loginName"] = loginName }), ct);
            return Results.Json(new { message = "Invalid login or password." }, statusCode: 401);
        }

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            account.ChangePasswordHash(hasher.Hash(request.Password), now);
        account.RecordLogin(now);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: $"auth:{loginName}",
            TaskId: null,
            Action: "auth.login",
            Reason: "Administrator signed in.",
            Outcome: "Success",
            CorrelationId: correlationId,
            Details: new Dictionary<string, string> { ["loginName"] = loginName }), ct);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, account.LoginName),
            new Claim("admin", "true"),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = now + TimeSpan.FromHours(8) });

        var csrfToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(CsrfMiddleware.CookieName, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromHours(8),
        });

        return Results.NoContent();
    }

    private static async Task<IResult> Logout(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        http.Response.Cookies.Delete(CsrfMiddleware.CookieName);
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal user)
    {
        var loginName = user.Identity?.Name ?? string.Empty;
        var isAdministrator = user.HasClaim("admin", "true");
        return Results.Json(new { loginName, isAdministrator });
    }
}
