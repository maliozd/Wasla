namespace Wasla.Domain.Enums;

public enum UserRole
{
    Owner = 1,
    Manager = 2,
    Kitchen = 3,
    Cashier = 4,
    Viewer = 5,

    [Obsolete("Use Kitchen, Cashier, or Viewer for new tenant users.")]
    Staff = 100
}
