using System.Security.Claims;

namespace Mrm.Api.Auth;

public static class AuthPolicies
{
    public const string StudioAdmin = "StudioAdminPolicy";
    public const string ProductionManager = "ProductionManagerPolicy";
    public const string SystemAdmin = "SystemAdminPolicy";
}

public static class ClaimNames
{
    public const string StudioId = "studioId";
    public const string Role = ClaimTypes.Role;
}
