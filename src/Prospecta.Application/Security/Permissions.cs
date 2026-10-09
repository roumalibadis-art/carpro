namespace Prospecta.Application.Security;

/// <summary>Explicit permission constants. Each one maps 1:1 to an authorization policy and a role claim.</summary>
public static class Permissions
{
    public const string ClaimType = "permission";

    public const string BusinessView = "Business.View";           // own assigned businesses
    public const string BusinessViewAll = "Business.ViewAll";     // whole organization
    public const string BusinessCreate = "Business.Create";
    public const string BusinessEdit = "Business.Edit";
    public const string BusinessDelete = "Business.Delete";
    public const string BusinessVerify = "Business.Verify";
    public const string BusinessAssign = "Business.Assign";
    public const string BusinessImport = "Business.Import";
    public const string BusinessExport = "Business.Export";
    public const string DuplicateManage = "Duplicate.Manage";
    public const string ReferenceManage = "Reference.Manage";     // geography, activities, statuses
    public const string UserManage = "User.Manage";
    public const string RoleManage = "Role.Manage";
    public const string AuditView = "Audit.View";

    public static readonly IReadOnlyList<string> All =
    [
        BusinessView, BusinessViewAll, BusinessCreate, BusinessEdit, BusinessDelete, BusinessVerify, BusinessAssign,
        BusinessImport, BusinessExport, DuplicateManage, ReferenceManage, UserManage, RoleManage, AuditView,
    ];
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string SalesManager = "SalesManager";
    public const string Salesperson = "Salesperson";

    public static readonly IReadOnlyList<string> All = [Admin, SalesManager, Salesperson];

    /// <summary>Default grants; an admin can change them afterwards from the UI (stored as role claims).</summary>
    public static IReadOnlyDictionary<string, string[]> DefaultPermissions { get; } = new Dictionary<string, string[]>
    {
        [Admin] = [.. Permissions.All],
        [SalesManager] =
        [
            Permissions.BusinessView, Permissions.BusinessViewAll, Permissions.BusinessCreate, Permissions.BusinessEdit,
            Permissions.BusinessVerify, Permissions.BusinessAssign, Permissions.BusinessImport, Permissions.BusinessExport,
            Permissions.DuplicateManage,
        ],
        [Salesperson] = [Permissions.BusinessView, Permissions.BusinessEdit, Permissions.BusinessCreate],
    };
}
