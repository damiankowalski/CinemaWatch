using System.Security.Claims;
using CinemaWatch.Server;
using CinemaWatch.Server.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Web;

public sealed class OwnerRequirement : IAuthorizationRequirement;

/// <summary>The owner is the account whose e-mail equals Saas:OwnerEmail; only the owner may invite people.</summary>
public sealed class OwnerAuthorizationHandler(IOptions<SaasOptions> options) : AuthorizationHandler<OwnerRequirement>
{
    public const string Policy = "Owner";

    public static bool IsOwner(ClaimsPrincipal user, SaasOptions options) =>
        user.Identity?.IsAuthenticated == true
        && AccountService.IsOwnerEmail(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity.Name, options);

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnerRequirement requirement)
    {
        if (IsOwner(context.User, options.Value))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
