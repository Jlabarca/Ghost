using Ghost2.Infrastructure.Storage;
namespace Ghost2.Infrastructure.Data;

public interface IPermissionsManager
{
    Task EnsureCanRead(string key);
    Task EnsureCanWrite(string key);
    Task EnsureCanDelete(string key);
    Task GrantPermission(string key, string userId, Permission permission);
    Task RevokePermission(string key, string userId, Permission permission);
}

[Flags]
public enum Permission
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    Admin = 8,
    All = Read | Write | Delete | Admin
}

public class PermissionsManager : IPermissionsManager
{
    private readonly IPostgresClient _db;
    private readonly IRedisClient _cache;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public PermissionsManager(IPostgresClient db, IRedisClient cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task EnsureCanRead(string key)
    {
        if (!await HasPermission(key, Permission.Read))
        {
            throw new GhostException(
                "Access denied: Missing read permission",
                ErrorCode.UnauthorizedAccess);
        }
    }

    public async Task EnsureCanWrite(string key)
    {
        if (!await HasPermission(key, Permission.Write))
        {
            throw new GhostException(
                "Access denied: Missing write permission",
                ErrorCode.UnauthorizedAccess);
        }
    }

    public async Task EnsureCanDelete(string key)
    {
        if (!await HasPermission(key, Permission.Delete))
        {
            throw new GhostException(
                "Access denied: Missing delete permission",
                ErrorCode.UnauthorizedAccess);
        }
    }

    public async Task GrantPermission(string key, string userId, Permission permission)
    {
        // First check if user has admin rights
        if (!await HasPermission(key, Permission.Admin))
        {
            throw new GhostException(
                "Access denied: Must have admin rights to grant permissions",
                ErrorCode.UnauthorizedAccess);
        }

        await using var transaction = await _db.BeginTransactionAsync();
        try
        {
            var sql = @"
                INSERT INTO permissions (key, user_id, permission)
                VALUES (@key, @userId, @permission)
                ON CONFLICT (key, user_id) 
                DO UPDATE SET permission = permissions.permission | @permission";

            await _db.ExecuteAsync(sql, new { key, userId, permission = (int)permission });

            // Invalidate cache
            await InvalidatePermissionsCache(key);

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            throw new GhostException(
                "Failed to grant permission",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task RevokePermission(string key, string userId, Permission permission)
    {
        // First check if user has admin rights
        if (!await HasPermission(key, Permission.Admin))
        {
            throw new GhostException(
                "Access denied: Must have admin rights to revoke permissions",
                ErrorCode.UnauthorizedAccess);
        }

        await using var transaction = await _db.BeginTransactionAsync();
        try
        {
            var sql = @"
                UPDATE permissions 
                SET permission = permission & ~@permission
                WHERE key = @key AND user_id = @userId";

            await _db.ExecuteAsync(sql, new { key, userId, permission = (int)permission });

            // Invalidate cache
            await InvalidatePermissionsCache(key);

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            throw new GhostException(
                "Failed to revoke permission",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    private async Task<bool> HasPermission(string key, Permission requiredPermission)
    {
        var cacheKey = $"perm:{key}";
        
        // Try cache first
        var cachedPermissions = await _cache.GetAsync<Dictionary<string, Permission>>(cacheKey);
        if (cachedPermissions != null)
        {
            var currentUserId = GetCurrentUserId();
            return cachedPermissions.TryGetValue(currentUserId, out var userPermissions) &&
                   (userPermissions & requiredPermission) == requiredPermission;
        }

        // Cache miss, load from database
        var sql = @"
            SELECT user_id, permission 
            FROM permissions 
            WHERE key = @key";

        var permissions = await _db.QueryAsync<(string userId, Permission permission)>(sql, new { key });
        
        // Build cache dictionary
        var permissionsDict = permissions.ToDictionary(
            p => p.userId,
            p => p.permission);

        // Cache for future use
        await _cache.SetAsync(cacheKey, permissionsDict, _cacheExpiry);

        // Check permission
        var userId = GetCurrentUserId();
        return permissionsDict.TryGetValue(userId, out var perm) &&
               (perm & requiredPermission) == requiredPermission;
    }

    private async Task InvalidatePermissionsCache(string key)
    {
        var cacheKey = $"perm:{key}";
        await _cache.DeleteAsync(cacheKey);
    }

    private string GetCurrentUserId()
    {
        // This would typically come from your authentication system
        // For now, we'll use a placeholder
        return Thread.CurrentPrincipal?.Identity?.Name ?? "anonymous";
    }
}
