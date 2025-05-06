
namespace Ghost.Core.Data;

/// <summary>
/// Provides core data operations for the Ghost environment.
/// Implements a unified interface for key-value operations and SQL operations.
/// </summary>
public interface IGhostData : IAsyncDisposable
{
        #region Key-Value Operations

    /// <summary>
    /// Retrieves a value by key.
    /// </summary>
    /// <typeparam name="T">The type of the value to retrieve.</typeparam>
    /// <param name="key">The key of the item to retrieve.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The value if found; otherwise, default(T).</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);

    /// <summary>
    /// Stores a value with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the value to store.</typeparam>
    /// <param name="key">The key to store the value under.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="expiry">Optional expiration time for the value.</param>
    /// <param name="ct">Optional cancellation token.</param>
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes a value by key.
    /// </summary>
    /// <param name="key">The key of the item to delete.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>True if the item was deleted; otherwise, false.</returns>
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Checks if a key exists.
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>True if the key exists; otherwise, false.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

        #endregion

        #region Batch Key-Value Operations

    /// <summary>
    /// Retrieves multiple values by their keys.
    /// </summary>
    /// <typeparam name="T">The type of the values to retrieve.</typeparam>
    /// <param name="keys">The keys of the items to retrieve.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A dictionary mapping keys to values. Keys without values will map to default(T).</returns>
    Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default);

    /// <summary>
    /// Stores multiple values with their specified keys.
    /// </summary>
    /// <typeparam name="T">The type of the values to store.</typeparam>
    /// <param name="items">A dictionary mapping keys to values.</param>
    /// <param name="expiry">Optional expiration time for the values.</param>
    /// <param name="ct">Optional cancellation token.</param>
    Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes multiple values by their keys.
    /// </summary>
    /// <param name="keys">The keys of the items to delete.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The number of items that were deleted.</returns>
    Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default);

        #endregion

        #region SQL Operations

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
    /// Executes multiple SQL commands in a batch.
    /// </summary>
    /// <param name="commands">The SQL commands to execute with their parameters.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The total number of rows affected.</returns>
    Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default);

        #endregion

        #region Transaction Support

    /// <summary>
    /// Begins a new transaction.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A transaction object that can be used to perform operations within the transaction.</returns>
    Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default);

        #endregion

        #region Schema Operations

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

        #endregion

        #region Database Properties

    /// <summary>
    /// Gets the database client used by this data provider.
    /// </summary>
    /// <returns>The database client.</returns>
    IDatabaseClient GetDatabaseClient();

        #endregion
}