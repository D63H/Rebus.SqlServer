﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Rebus.Auditing.Sagas;
using Rebus.Logging;
using Rebus.Sagas;
using Rebus.Serialization;

namespace Rebus.SqlServer.Sagas
{
    /// <summary>
    /// Implementation of <see cref="ISagaSnapshotStorage"/> that uses a table in SQL Server to store saga snapshots
    /// </summary>
    public class SqlServerSagaSnapshotStorage : ISagaSnapshotStorage
    {
        readonly IDbConnectionProvider _connectionProvider;
        readonly string _tableName;
        readonly ILog _log;

        static readonly ObjectSerializer DataSerializer = new ObjectSerializer();
        static readonly HeaderSerializer MetadataSerializer = new HeaderSerializer();

        /// <summary>
        /// Constructs the snapshot storage
        /// </summary>
        public SqlServerSagaSnapshotStorage(IDbConnectionProvider connectionProvider, string tableName, IRebusLoggerFactory rebusLoggerFactory)
        {
            _log = rebusLoggerFactory.GetCurrentClassLogger();
            _connectionProvider = connectionProvider;
            _tableName = tableName;
        }

        /// <summary>
        /// Creates the subscriptions table if necessary
        /// </summary>
        public void EnsureTableIsCreated()
        {
            using (var connection = _connectionProvider.GetConnection().Result)
            {
                var tableNames = connection.GetTableNames();

                if (tableNames.Contains(_tableName, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                _log.Info("Table '{0}' does not exist - it will be created now", _tableName);

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = string.Format(@"
CREATE TABLE [dbo].[{0}] (
	[id] [uniqueidentifier] NOT NULL,
	[revision] [int] NOT NULL,
	[data] [nvarchar](max) NOT NULL,
	[metadata] [nvarchar](max) NOT NULL,
    CONSTRAINT [PK_{0}] PRIMARY KEY CLUSTERED 
    (
	    [id] ASC,
        [revision] ASC
    )
)
", _tableName);
                    command.ExecuteNonQuery();
                }

                connection.Complete();
            }
        }

        /// <summary>
        /// Saves a snapshot of the saga data along with the given metadata
        /// </summary>
        public async Task Save(ISagaData sagaData, Dictionary<string, string> sagaAuditMetadata)
        {
            using (var connection = await _connectionProvider.GetConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $@"

INSERT INTO [{_tableName}] (
    [id],
    [revision],
    [data],
    [metadata]
) VALUES (
    @id, 
    @revision, 
    @data,
    @metadata
)

";
                    command.Parameters.Add("id", SqlDbType.UniqueIdentifier).Value = sagaData.Id;
                    command.Parameters.Add("revision", SqlDbType.Int).Value = sagaData.Revision;
                    command.Parameters.Add("data", SqlDbType.NVarChar).Value = DataSerializer.SerializeToString(sagaData);
                    command.Parameters.Add("metadata", SqlDbType.NVarChar).Value = MetadataSerializer.SerializeToString(sagaAuditMetadata);

                    await command.ExecuteNonQueryAsync();
                }

                await connection.Complete();
            }
        }
    }
}