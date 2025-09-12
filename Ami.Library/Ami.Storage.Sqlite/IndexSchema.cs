using Microsoft.Data.Sqlite;

namespace Ami.Core.Storage;

public static class IndexSchema
{
    public static void Ensure(SqliteConnection connection, bool enableFts5)
    {
        connection.Open();
        using var cmd = connection.CreateCommand();

        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
        ";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS manuscripts (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                path TEXT NOT NULL UNIQUE,
                parent_id TEXT,
                depth INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL,
                mtime_utc TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                version TEXT,
                properties TEXT,
                FOREIGN KEY (parent_id) REFERENCES manuscripts(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS unresolved_parents (
                child_id TEXT PRIMARY KEY,
                declared_parent_id TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS nodes (
                id INTEGER PRIMARY KEY,
                manuscript_id TEXT NOT NULL,
                node_type TEXT NOT NULL,
                key TEXT,
                group_name TEXT,
                field_name TEXT,
                object_name TEXT,
                value_text TEXT,
                value_xml TEXT,
                value_attrs TEXT,
                attributes TEXT,
                value_analysis TEXT,
                xpath TEXT,
                line INTEGER,
                column INTEGER,
                FOREIGN KEY (manuscript_id) REFERENCES manuscripts(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_nodes_key_manu ON nodes(key, manuscript_id);
            CREATE INDEX IF NOT EXISTS idx_nodes_type ON nodes(node_type);
            CREATE INDEX IF NOT EXISTS idx_nodes_object ON nodes(object_name);
            CREATE INDEX IF NOT EXISTS idx_nodes_manu ON nodes(manuscript_id);
        ";
        cmd.ExecuteNonQuery();

        if (enableFts5)
        {
            cmd.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(
                    key,
                    value_text,
                    value_xml,
                    content='nodes',
                    content_rowid='id'
                );

                CREATE TRIGGER IF NOT EXISTS nodes_ai AFTER INSERT ON nodes BEGIN
                  INSERT INTO nodes_fts(rowid, key, value_text, value_xml) VALUES (new.id, new.key, new.value_text, new.value_xml);
                END;
                CREATE TRIGGER IF NOT EXISTS nodes_ad AFTER DELETE ON nodes BEGIN
                  INSERT INTO nodes_fts(nodes_fts, rowid, key, value_text, value_xml) VALUES ('delete', old.id, old.key, old.value_text, old.value_xml);
                END;
                CREATE TRIGGER IF NOT EXISTS nodes_au AFTER UPDATE ON nodes BEGIN
                  INSERT INTO nodes_fts(nodes_fts, rowid, key, value_text, value_xml) VALUES ('delete', old.id, old.key, old.value_text, old.value_xml);
                  INSERT INTO nodes_fts(rowid, key, value_text, value_xml) VALUES (new.id, new.key, new.value_text, new.value_xml);
                END;
            ";
            cmd.ExecuteNonQuery();
        }
    }
}