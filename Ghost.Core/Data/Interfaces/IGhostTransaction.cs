namespace Ghost.Data;

/// <summary>
///     Represents a database transaction that can be committed or rolled back.
///     Provides transaction-scoped versions of the IGhostData query and execution methods.
/// </summary>
public interface IGhostTransaction : IAsyncDisposable
{
    /// <summary>
    ///     Commits the transaction.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    Task CommitAsync(CancellationToken ct = default(CancellationToken));

    /// <summary>
    ///     Rolls back the transaction.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    Task RollbackAsync(CancellationToken ct = default(CancellationToken));

    /// <summary>
    ///     Executes a SQL query that returns a single result within the transaction.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="param">Optional parameters for the query.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The result of the query.</returns>
    Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken));

    /// <summary>
    ///     Executes a SQL query that returns multiple results within the transaction.
    /// </summary>
    /// <typeparam name="T">The type of the results.</typeparam>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="param">Optional parameters for the query.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The results of the query.</returns>
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken));

    /// <summary>
    ///     Executes a SQL command that doesn't return a result within the transaction.
    /// </summary>
    /// <param name="sql">The SQL command to execute.</param>
    /// <param name="param">Optional parameters for the command.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The number of rows affected.</returns>
    Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken));

    /// <summary>
    ///     Executes multiple SQL commands in a batch within the transaction.
    /// </summary>
    /// <param name="commands">The SQL commands to execute with their parameters.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The total number of rows affected.</returns>
    Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken));
}
