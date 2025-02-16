using Ghost.Core.Data;
using System.Data;

namespace Ghost.Core.Storage;

public static class PostgresBulkExtensions
{
  public static async Task<int> BulkCopyAsync(
      this PostgresDatabase client,
      DataTable data,
      string tableName)
  {
    var connection = await client.GetConnectionAsync();
    if (connection == null)
      throw new InvalidOperationException("Invalid connection type");

    // Ensure we're in the public schema
    tableName = tableName.Contains(".") ? tableName : $"public.{tableName}";

    using var writer = connection.BeginBinaryImport(
        $"COPY {tableName} ({string.Join(", ", data.Columns.Cast<DataColumn>().Select(c => c.ColumnName))}) " +
        "FROM STDIN (FORMAT BINARY)");

    foreach (DataRow row in data.Rows)
    {
      await writer.StartRowAsync();
      foreach (DataColumn col in data.Columns)
      {
        var value = row[col];
        if (value == DBNull.Value)
          await writer.WriteNullAsync();
        else
          await writer.WriteAsync(value);
      }
    }

    return (int)await writer.CompleteAsync();
  }
}

