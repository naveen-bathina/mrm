namespace Mrm.Infrastructure.Entities;

public enum UserRole
{
    StudioAdmin,
    ProductionManager,
    SystemAdmin,
}

public class User
{
    public Guid Id { get; set; }
    public Guid? StudioId { get; set; } // null = SystemAdmin
    public UserRole Role { get; set; }
    public string Email { get; set; } = string.Empty;
}
