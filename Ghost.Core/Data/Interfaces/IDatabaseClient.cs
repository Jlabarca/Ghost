namespace Ghost.Core.Data;

/// <summary>
/// Low-level database client interface for PostgreSQL operations.
/// </summary>
public interface IDatabaseClient : IAsyncDisposable
{
    /// <summary>
    /// Executes a SQL query that returns a single result.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="param">Optional parameters for the query.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The result of the query.</returns>
    Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    /// <summary>
    /// Executes a SQL query that returns multiple results.
    /// </summary>
    /// <typeparam name="T">The type of the results.</typeparam>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="param">Optional parameters for the query.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The results of the query.</returns>
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    /// <summary>
    /// Executes a SQL command that doesn't return a result.
    /// </summary>
    /// <param name="sql">The SQL command to execute.</param>
    /// <param name="param">Optional parameters for the command.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The number of rows affected.</returns>
    Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default);

    /// <summary>
    /// Begins a new transaction.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A transaction object that can be used to perform operations within the transaction.</returns>
    Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if a table exists in the database.
    /// </summary>
    /// <param name="tableName">The name of the table to check.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>True if the table exists; otherwise, false.</returns>
    Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Gets the names of all tables in the database.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The names of all tables in the database.</returns>
    Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if the database connection is available.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>True if the connection is available, otherwise false.</returns>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the connection string being used by this client.
    /// </summary>
    string ConnectionString { get; }
}