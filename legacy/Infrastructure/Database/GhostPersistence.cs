using System.Data.Common;
namespace Ghost.Legacy.Infrastructure.Database;

public interface IDbProvider
{
    DbConnection CreateConnection();
    void Initialize();
    string GetTablePrefix();
}

// Core models that represent our domain
public record ProcessInfo(
    string Id,
    string Name,
    string Status,
    int? Pid,
    int? Port,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record ConfigEntry(
    string Key,
    string Value,
    string AppId,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record EventEntry(
    long Id,
    string Type,
    string AppId,
    string Payload,
    bool Processed,
    DateTime CreatedAt
);