
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ami.Core.Abstractions;
using Ami.Core.Model;
using Ami.Core.Util;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Ami.Core.Storage;

public sealed class SqliteRepository : IAmiRepository
{
    public string ConnectionString { get; }
    private readonly bool _enableFts5;
    private readonly ILogger<SqliteRepository> _logger;
    private static readonly SemaphoreSlim _nodeWriteGate = new(1, 1);

    public SqliteRepository(string databasePath, bool enableFts5, ILogger<SqliteRepository> logger)
    {
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _enableFts5 = enableFts5;
        _logger = logger;

        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            IndexSchema.Ensure(connection, _enableFts5);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to initialize the SQLite database schema at {Path}.", databasePath);
            throw new AmiException("Database schema initialization failed.", ex);
        }
    }

    private SqliteConnection CreateConnection() => new(ConnectionString);

    public async Task UpsertManuscriptAsync(Manuscript m, CancellationToken ct = default)
    {
        using var activity = AmiInstrumentation.Source.StartActivity("UpsertManuscript");
        activity?.SetTag("ami.manuscript.id", m.Id);

        await using var db = CreateConnection();
        await db.OpenAsync(ct);

        await using var tx = db.BeginTransaction();

        // Normalize early
        var normalizedParentId = string.IsNullOrWhiteSpace(m.ParentId) ? null : m.ParentId.Trim();

        // Check parent existence only when non-null/non-empty
        bool parentExists = false;
        if (normalizedParentId != null)
        {
            await using var checkParentCmd = db.CreateCommand();
            checkParentCmd.Transaction = tx;
            checkParentCmd.CommandText = "SELECT 1 FROM manuscripts WHERE id = $pid LIMIT 1;";
            checkParentCmd.Parameters.AddWithValue("$pid", normalizedParentId);
            parentExists = (await checkParentCmd.ExecuteScalarAsync(ct)) != null;
        }

        // Build upsert and always bind ALL params
        await using var upsertCmd = db.CreateCommand();
        upsertCmd.Transaction = tx;
        upsertCmd.CommandText = @"
    INSERT INTO manuscripts (id, name, path, parent_id, depth, size_bytes, mtime_utc, sha256, version, properties)
    VALUES ($id, $name, $path, $parent_id, $depth, $size, $mtime, $sha256, $version, $props)
    ON CONFLICT(id) DO UPDATE SET
        name=excluded.name,
        path=excluded.path,
        parent_id=excluded.parent_id,
        depth=excluded.depth,
        size_bytes=excluded.size_bytes,
        mtime_utc=excluded.mtime_utc,
        sha256=excluded.sha256,
        version=excluded.version,
        properties=excluded.properties;
";

        // Add parameters (use your AddManuscriptParameters, but pass normalizedParentId)
        AddManuscriptParameters(upsertCmd, m);

        // IMPORTANT: override $parent_id to NULL when missing OR when parent doesn't exist yet
        upsertCmd.Parameters["$parent_id"].Value =
            (object?)(parentExists ? normalizedParentId : null) ?? DBNull.Value;

        await upsertCmd.ExecuteNonQueryAsync(ct);

        // Only enqueue unresolved when we actually have a non-empty parent that wasn't found
        if (normalizedParentId != null && !parentExists)
        {
            _logger.LogWarning("Manuscript {ChildId} declared a parent '{ParentId}' which does not exist yet. Adding to unresolved queue.",
                               m.Id, normalizedParentId);

            await using var unresolvedCmd = db.CreateCommand();
            unresolvedCmd.Transaction = tx;
            unresolvedCmd.CommandText = @"
        INSERT INTO unresolved_parents (child_id, declared_parent_id, created_utc)
        VALUES ($child_id, $parent_id, $now)
        ON CONFLICT(child_id) DO UPDATE SET declared_parent_id=excluded.declared_parent_id,
                                            created_utc=excluded.created_utc;
    ";
            unresolvedCmd.Parameters.AddWithValue("$child_id", m.Id);
            unresolvedCmd.Parameters.AddWithValue("$parent_id", normalizedParentId);
            unresolvedCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            await unresolvedCmd.ExecuteNonQueryAsync(ct);
        }



        var healCmd = db.CreateCommand();
        healCmd.Transaction = tx;
        healCmd.CommandText = "SELECT child_id FROM unresolved_parents WHERE declared_parent_id = $id;";
        healCmd.Parameters.AddWithValue("$id", m.Id);
        var childrenToHeal = new List<string>();
        await using (var reader = await healCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) childrenToHeal.Add(reader.GetString(0));
        }

        if (childrenToHeal.Any())
        {
            _logger.LogInformation("Manuscript {ManuscriptId} appeared, healing {Count} unresolved children.", m.Id, childrenToHeal.Count);
            var deleteHealedCmd = db.CreateCommand();
            deleteHealedCmd.Transaction = tx;
            deleteHealedCmd.CommandText = "DELETE FROM unresolved_parents WHERE declared_parent_id = $id;";
            deleteHealedCmd.Parameters.AddWithValue("$id", m.Id);
            await deleteHealedCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
    
    public async Task BulkReplaceNodesAsync_old(string manuscriptId, IAsyncEnumerable<NodeRecord> nodes, CancellationToken ct)
    {
        using var activity = AmiInstrumentation.Source.StartActivity("BulkReplaceNodes");
        activity?.SetTag("ami.manuscript.id", manuscriptId);

        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        await using var tx = db.BeginTransaction();

        var deleteCmd = db.CreateCommand();
        deleteCmd.Transaction = tx;
        deleteCmd.CommandText = "DELETE FROM nodes WHERE manuscript_id = $id;";
        deleteCmd.Parameters.AddWithValue("$id", manuscriptId);
        await deleteCmd.ExecuteNonQueryAsync(ct);

        var insertCmd = db.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = @"
            INSERT INTO nodes (manuscript_id, node_type, key, group_name, field_name, object_name, value_text, value_xml, value_attrs, attributes, value_analysis, line, column)
            VALUES ($mid, $type, $key, $group, $field, $object, $vtext, $vxml, $vattrs, $attrs, $vanalysis, $line, $col);
        ";
        insertCmd.Parameters.Add("$mid", SqliteType.Text).Value = manuscriptId;
        var pType = insertCmd.Parameters.Add("$type", SqliteType.Text);
        var pKey = insertCmd.Parameters.Add("$key", SqliteType.Text);
        var pGroup = insertCmd.Parameters.Add("$group", SqliteType.Text);
        var pField = insertCmd.Parameters.Add("$field", SqliteType.Text);
        var pObject = insertCmd.Parameters.Add("$object", SqliteType.Text);
        var pVText = insertCmd.Parameters.Add("$vtext", SqliteType.Text);
        var pVXml = insertCmd.Parameters.Add("$vxml", SqliteType.Text);
        var pVAttrs = insertCmd.Parameters.Add("$vattrs", SqliteType.Text);
        var pAttrs = insertCmd.Parameters.Add("$attrs", SqliteType.Text);
        var pVAnalysis = insertCmd.Parameters.Add("$vanalysis", SqliteType.Text);
        var pLine = insertCmd.Parameters.Add("$line", SqliteType.Integer);
        var pCol = insertCmd.Parameters.Add("$col", SqliteType.Integer);
        await insertCmd.PrepareAsync(ct);

        long insertedCount = 0;
        await foreach (var node in nodes.WithCancellation(ct))
        {
            pType.Value = node.NodeType;
            pKey.Value = (object?)node.Key ?? DBNull.Value;
            pGroup.Value = (object?)node.GroupName ?? DBNull.Value;
            pField.Value = (object?)node.FieldName ?? DBNull.Value;
            pObject.Value = (object?)node.ObjectName ?? DBNull.Value;
            pVText.Value = (object?)node.ValueText ?? DBNull.Value;
            pVXml.Value = (object?)node.ValueXml ?? DBNull.Value;
            pVAttrs.Value = node.ValueAttrsJson;
            pAttrs.Value = node.AttributesJson;
            pVAnalysis.Value = (object?)node.ValueAnalysisJson ?? DBNull.Value;
            pLine.Value = (object?)node.Line ?? DBNull.Value;
            pCol.Value = (object?)node.Column ?? DBNull.Value;
            await insertCmd.ExecuteNonQueryAsync(ct);
            insertedCount++;
        }

        await tx.CommitAsync(ct);

        AmiInstrumentation.NodesInserted.Add(insertedCount);
        activity?.SetTag("ami.nodes.inserted", insertedCount);
        _logger.LogDebug("Replaced {NodeCount} nodes for manuscript {ManuscriptId}.", insertedCount, manuscriptId);
    }


    public async Task BulkReplaceNodesAsync(string manuscriptId, IAsyncEnumerable<NodeRecord> nodes, CancellationToken ct)
    {
        using var activity = AmiInstrumentation.Source.StartActivity("BulkReplaceNodes");
        activity?.SetTag("ami.manuscript.id", manuscriptId);

        // Ensure only one writer touches `nodes` at a time
        await _nodeWriteGate.WaitAsync(ct);
        try
        {
            await using var db = CreateConnection();
            await db.OpenAsync(ct);

            // Pragmas to reduce lock failures and improve throughput
            await using (var pragmas = db.CreateCommand())
            {
                pragmas.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA busy_timeout = 5000; -- wait up to 5s for locks
                ";
                await pragmas.ExecuteNonQueryAsync(ct);
            }

            // Begin a transaction for delete+insert batch
            await using var tx = db.BeginTransaction();

            // 1) Delete existing nodes for this manuscript
            await using (var deleteCmd = db.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = "DELETE FROM nodes WHERE manuscript_id = $id;";
                deleteCmd.Parameters.AddWithValue("$id", manuscriptId);
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            // 2) Insert new nodes using a prepared statement
            await using (var insertCmd = db.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT INTO nodes (
                        manuscript_id, node_type, key, group_name, field_name, object_name,
                        value_text, value_xml, value_attrs, attributes, value_analysis,
                        line, column
                    )
                    VALUES (
                        $mid, $type, $key, $group, $field, $object,
                        $vtext, $vxml, $vattrs, $attrs, $vanalysis,
                        $line, $col
                    );
                ";

                insertCmd.Parameters.Add("$mid", SqliteType.Text).Value = manuscriptId;
                var pType = insertCmd.Parameters.Add("$type", SqliteType.Text);
                var pKey = insertCmd.Parameters.Add("$key", SqliteType.Text);
                var pGroup = insertCmd.Parameters.Add("$group", SqliteType.Text);
                var pField = insertCmd.Parameters.Add("$field", SqliteType.Text);
                var pObject = insertCmd.Parameters.Add("$object", SqliteType.Text);
                var pVText = insertCmd.Parameters.Add("$vtext", SqliteType.Text);
                var pVXml = insertCmd.Parameters.Add("$vxml", SqliteType.Text);
                var pVAttrs = insertCmd.Parameters.Add("$vattrs", SqliteType.Text);
                var pAttrs = insertCmd.Parameters.Add("$attrs", SqliteType.Text);
                var pVAnalysis = insertCmd.Parameters.Add("$vanalysis", SqliteType.Text);
                var pLine = insertCmd.Parameters.Add("$line", SqliteType.Integer);
                var pCol = insertCmd.Parameters.Add("$col", SqliteType.Integer);

                await insertCmd.PrepareAsync(ct);

                long insertedCount = 0;

                await foreach (var node in nodes.WithCancellation(ct))
                {
                    ct.ThrowIfCancellationRequested();

                    pType.Value = (object?)node.NodeType ?? DBNull.Value;
                    pKey.Value = (object?)node.Key ?? DBNull.Value;
                    pGroup.Value = (object?)node.GroupName ?? DBNull.Value;
                    pField.Value = (object?)node.FieldName ?? DBNull.Value;
                    pObject.Value = (object?)node.ObjectName ?? DBNull.Value;
                    pVText.Value = (object?)node.ValueText ?? DBNull.Value;
                    pVXml.Value = (object?)node.ValueXml ?? DBNull.Value;
                    pVAttrs.Value = (object?)node.ValueAttrsJson ?? DBNull.Value;
                    pAttrs.Value = (object?)node.AttributesJson ?? DBNull.Value;
                    pVAnalysis.Value = (object?)node.ValueAnalysisJson ?? DBNull.Value;
                    pLine.Value = node.Line.HasValue ? node.Line.Value : (object)DBNull.Value;
                    pCol.Value = node.Column.HasValue ? node.Column.Value : (object)DBNull.Value;

                    await insertCmd.ExecuteNonQueryAsync(ct);
                    insertedCount++;
                }

                await tx.CommitAsync(ct);

                AmiInstrumentation.NodesInserted.Add(insertedCount);
                activity?.SetTag("ami.nodes.inserted", insertedCount);
                _logger.LogDebug("Replaced {NodeCount} nodes for manuscript {ManuscriptId}.", insertedCount, manuscriptId);
            }
        }
        finally
        {
            _nodeWriteGate.Release();
        }
    }

    public async Task<IReadOnlyList<Manuscript>> ListManuscriptsAsync(CancellationToken ct = default)
        {
            await using var db = CreateConnection();
            await db.OpenAsync(ct);

            var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, name, path, parent_id, depth, size_bytes, mtime_utc, sha256, version, properties FROM manuscripts;";

            var results = new List<Manuscript>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(MapReaderToManuscript(reader));
            }
            return results;
        }

    public async Task<ResolvedValue?> ResolveAsync(string manuscriptId, string key, CancellationToken ct)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        var cmd = db.CreateCommand();
        cmd.CommandText = @"
            WITH RECURSIVE Ancestry(manuscript_id, parent_id, depth) AS (
                SELECT id, parent_id, depth FROM manuscripts WHERE id = $start_id
                UNION ALL
                SELECT m.id, m.parent_id, m.depth
                FROM manuscripts m JOIN Ancestry a ON m.id = a.parent_id
            )
            SELECT n.value_text, n.value_xml, n.manuscript_id, n.id
            FROM nodes n JOIN Ancestry a ON n.manuscript_id = a.manuscript_id
            WHERE n.key = $key
            ORDER BY a.depth ASC
            LIMIT 1;
        ";
        cmd.Parameters.AddWithValue("$start_id", manuscriptId);
        cmd.Parameters.AddWithValue("$key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new ResolvedValue(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)
            );
        }
        return null;
    }

    public async Task<IReadOnlyList<LineageHit>> TraceAsync(string manuscriptId, string key, CancellationToken ct)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        var cmd = db.CreateCommand();
        cmd.CommandText = @"
            WITH RECURSIVE Ancestry(manuscript_id, parent_id, depth) AS (
                SELECT id, parent_id, depth FROM manuscripts WHERE id = $start_id
                UNION ALL
                SELECT m.id, m.parent_id, m.depth
                FROM manuscripts m JOIN Ancestry a ON m.id = a.parent_id
            )
            SELECT n.manuscript_id, a.depth, n.value_text, n.value_xml, n.id
            FROM nodes n JOIN Ancestry a ON n.manuscript_id = a.manuscript_id
            WHERE n.key = $key
            ORDER BY a.depth ASC;
        ";
        cmd.Parameters.AddWithValue("$start_id", manuscriptId);
        cmd.Parameters.AddWithValue("$key", key);

        var results = new List<LineageHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new LineageHit(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4)
            ));
        }
        return results;
    }

    public async Task<IReadOnlyList<Node>> FindByKeyAsync(string key, CancellationToken ct) =>
        await SearchAsync(new NodeQuery(Key: key), ct);

    public async Task<IReadOnlyList<Node>> SearchAsync(NodeQuery q, CancellationToken ct)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        var cmd = db.CreateCommand();

        var sb = new StringBuilder("SELECT id, manuscript_id, node_type, key, group_name, field_name, object_name, value_text, value_xml, value_attrs, attributes, value_analysis, xpath, line, column FROM nodes WHERE 1=1");

        if (!string.IsNullOrEmpty(q.Key))
        {
            sb.Append(" AND key = $key");
            cmd.Parameters.AddWithValue("$key", q.Key);
        }
        if (!string.IsNullOrEmpty(q.ObjectName))
        {
            sb.Append(" AND object_name = $obj");
            cmd.Parameters.AddWithValue("$obj", q.ObjectName);
        }
        if (!string.IsNullOrEmpty(q.Contains))
        {
            sb.Append(" AND value_text LIKE $contains");
            cmd.Parameters.AddWithValue("$contains", $"%{q.Contains}%");
        }
        if (!string.IsNullOrEmpty(q.NodeType))
        {
            sb.Append(" AND node_type = $type");
            cmd.Parameters.AddWithValue("$type", q.NodeType);
        }
        if (q.HasComparison == true)
        {
            sb.Append(" AND value_analysis LIKE '%\"Kind\":\"Comparison\"%'");
        }

        sb.Append(" LIMIT $limit;");
        cmd.Parameters.AddWithValue("$limit", q.Limit);

        cmd.CommandText = sb.ToString();

        var results = new List<Node>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapReaderToNode(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<Node>> FtsAsync(string matchQuery, int limit, CancellationToken ct)
    {
        if (!_enableFts5)
        {
            _logger.LogWarning("FTS5 search was attempted, but FTS5 is not enabled in the configuration.");
            return Array.Empty<Node>();
        }

        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        var cmd = db.CreateCommand();
        cmd.CommandText = @"
            SELECT n.id, n.manuscript_id, n.node_type, n.key, n.group_name, n.field_name, n.object_name, n.value_text, n.value_xml, n.value_attrs, n.attributes, n.value_analysis, n.xpath, n.line, n.column
            FROM nodes_fts f
            JOIN nodes n ON f.rowid = n.id
            WHERE f.nodes_fts MATCH $query
            ORDER BY rank
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$query", matchQuery);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<Node>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapReaderToNode(reader));
        }
        return results;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        // Optional preferences:
        // PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static void AddManuscriptParameters(SqliteCommand cmd, Manuscript m)
    {
        cmd.Parameters.Clear();
        // Required / non-nullables
        cmd.Parameters.Add("$id", SqliteType.Text).Value = m.Id;                                 // TEXT PK
        cmd.Parameters.Add("$name", SqliteType.Text).Value = m.Name ?? string.Empty;             // TEXT
        cmd.Parameters.Add("$path", SqliteType.Text).Value = m.Path ?? string.Empty;             // TEXT
        cmd.Parameters.Add("$depth", SqliteType.Integer).Value = m.Depth;                        // INTEGER
        cmd.Parameters.Add("$version", SqliteType.Integer).Value = m.Version;                    // INTEGER

        // Parent: write NULL when missing/blank
        var parentValue = string.IsNullOrWhiteSpace(m.ParentId) ? (object)DBNull.Value : m.ParentId!.Trim();
        cmd.Parameters.Add("$parent_id", SqliteType.Text).Value = parentValue;

        // Optional fields: use DBNull.Value when missing
        cmd.Parameters.Add("$size", SqliteType.Integer).Value = (object?)m.SizeBytes ?? DBNull.Value;                    // INTEGER
        cmd.Parameters.Add("$mtime", SqliteType.Text).Value = (object?)m.MtimeUtc ?? DBNull.Value;                       // ISO-8601 TEXT
        cmd.Parameters.Add("$sha256", SqliteType.Text).Value = (object?)m.Sha256 ?? DBNull.Value;                        // TEXT

        string? propsJson = null;
        if (m.Properties is not null)
            propsJson = JsonSerializer.Serialize(m.Properties, JsonOpts);

        cmd.Parameters.Add("$props", SqliteType.Text).Value = (object?)propsJson ?? DBNull.Value;

    }

    private static Node MapReaderToNode(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetInt32(13), reader.IsDBNull(14) ? null : reader.GetInt32(14)
    );

    private static Manuscript MapReaderToManuscript(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt64(5),
        DateTime.Parse(reader.GetString(6)), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(reader.GetString(9))
    );

    public void Dispose() { }
    public async ValueTask DisposeAsync() { await Task.CompletedTask; }
}
