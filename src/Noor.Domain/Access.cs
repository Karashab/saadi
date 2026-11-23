namespace Noor.Domain;

public enum MemberRole { SystemAdmin, CompanyAdmin, ProjectManager, Executor, Viewer }

public enum Permission
{
    ViewTask, CreateTask, UpdateTask, DeleteTask,
    ViewDocument, UploadDocument, DeleteDocument,
    ViewReports, ManageMembers, ViewAuditLog
}

public sealed record Membership(Guid UserId, Guid ProjectId, Guid? OrganizationId, MemberRole Role);

/// <summary>A resource in a project; a null organization means it has no owning company yet.</summary>
public sealed record ResourceScope(Guid ProjectId, Guid? OrganizationId);

public static class AccessPolicy
{
    private static readonly IReadOnlyDictionary<MemberRole, HashSet<Permission>> Matrix =
        new Dictionary<MemberRole, HashSet<Permission>>
        {
            [MemberRole.SystemAdmin] = [.. Enum.GetValues<Permission>()],
            [MemberRole.ProjectManager] = [.. Enum.GetValues<Permission>()],
            [MemberRole.CompanyAdmin] =
            [
                Permission.ViewTask, Permission.CreateTask, Permission.UpdateTask, Permission.DeleteTask,
                Permission.ViewDocument, Permission.UploadDocument, Permission.DeleteDocument,
                Permission.ViewReports, Permission.ManageMembers
            ],
            [MemberRole.Executor] =
            [
                Permission.ViewTask, Permission.UpdateTask,
                Permission.ViewDocument, Permission.UploadDocument
            ],
            [MemberRole.Viewer] = [Permission.ViewTask, Permission.ViewDocument, Permission.ViewReports],
        };

    public static bool HasProjectWideScope(MemberRole role) =>
        role is MemberRole.SystemAdmin or MemberRole.ProjectManager;

    public static bool Can(Membership? member, Permission permission, ResourceScope resource)
    {
        if (member is null || member.ProjectId != resource.ProjectId) return false;
        if (!Matrix[member.Role].Contains(permission)) return false;
        return HasProjectWideScope(member.Role)
            || (member.OrganizationId is { } company && resource.OrganizationId == company);
    }
}
