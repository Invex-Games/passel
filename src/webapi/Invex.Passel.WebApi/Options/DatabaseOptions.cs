namespace Invex.Passel.WebApi.Options;

/// <summary>
///     What the <c>db-migrate</c> command grants, bound from the <c>Database</c> configuration section. The running
///     API needs none of it.
/// </summary>
internal sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    ///     The database role the running API logs in as. <c>db-migrate</c> makes it a member of the DML-only
    ///     <see cref="DatabaseMigrator.ApplicationRole" />; without one, only the schema is applied.
    /// </summary>
    public string? ApiRole { get; set; }

    /// <summary>
    ///     The Microsoft Entra object id of the managed identity behind <see cref="ApiRole" />. When set, the role is
    ///     created as an Entra principal of that identity if it does not exist, so the API can sign in with tokens;
    ///     when not, the role must already exist (a password login, as in the tests).
    /// </summary>
    public string? ApiRoleObjectId { get; set; }
}
