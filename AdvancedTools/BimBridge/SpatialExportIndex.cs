using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AdvancedTools.BimBridge
{
    // Disk-backed mesh deduplication and traversal queue. Neither a million mesh
    // keys nor a wide subtree's pending nodes need a managed in-memory HashSet.
    public sealed class SpatialExportIndex:IDisposable
    {
        private readonly string _path;
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        public SpatialExportIndex(string directory,long quota)
        {
            Directory.CreateDirectory(directory);
            _path=Path.Combine(directory,Guid.NewGuid().ToString("N")+".spatial-index.db");
            SQLitePCL.Batteries_V2.Init();
            _connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=_path,Pooling=false}.ToString());_connection.Open();
            Execute("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-2048; PRAGMA temp_store=FILE; PRAGMA max_page_count="+Math.Max(1,quota/4096)+";");
            Execute("CREATE TABLE meshes(key TEXT PRIMARY KEY) WITHOUT ROWID; CREATE TABLE queue(seq INTEGER PRIMARY KEY,part TEXT,element TEXT,done INTEGER DEFAULT 0,UNIQUE(part,element)); CREATE INDEX pending ON queue(done,seq);");
            _transaction=_connection.BeginTransaction();
        }
        private void Execute(string sql)
        {using(var command=_connection.CreateCommand()) {command.CommandText=sql;command.Transaction=_transaction;command.ExecuteNonQuery();}}
        public bool AddMesh(string key)
        {
            using(var command=_connection.CreateCommand())
            {
                command.Transaction=_transaction;command.CommandText="INSERT OR IGNORE INTO meshes(key) VALUES($key)";
                command.Parameters.AddWithValue("$key",key);return command.ExecuteNonQuery()==1;
            }
        }
        public void BeginRoot(ElementKey key) {Execute("DELETE FROM queue");AddNode(key);}
        public void AddNode(ElementKey key)
        {
            using(var command=_connection.CreateCommand())
            {
                command.Transaction=_transaction;command.CommandText="INSERT OR IGNORE INTO queue(part,element) VALUES($part,$element)";
                command.Parameters.AddWithValue("$part",key.ModelPartId.ToString("D"));command.Parameters.AddWithValue("$element",key.ElementId.ToString("D"));command.ExecuteNonQuery();
            }
        }
        public ElementKey NextNode()
        {
            long sequence;ElementKey result;
            using(var command=_connection.CreateCommand())
            {
                command.Transaction=_transaction;command.CommandText="SELECT seq,part,element FROM queue WHERE done=0 ORDER BY seq LIMIT 1";
                using(var reader=command.ExecuteReader())
                {
                    if(!reader.Read())return null;
                    sequence=reader.GetInt64(0);result=new ElementKey {ModelPartId=Guid.Parse(reader.GetString(1)),ElementId=Guid.Parse(reader.GetString(2))};
                }
            }
            Execute("UPDATE queue SET done=1 WHERE seq="+sequence);return result;
        }
        public void Dispose()
        {
            _transaction?.Dispose();_connection?.Dispose();
            TemporaryFileCleaner.TryDelete(_path);
        }
    }
}
