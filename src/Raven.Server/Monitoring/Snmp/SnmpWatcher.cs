﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Pipeline;
using Lextm.SharpSnmpLib.Security;
using Raven.Client;
using Raven.Client.Util;
using Raven.Server.Monitoring.Snmp.Objects.Documents;
using Raven.Server.Monitoring.Snmp.Objects.Server;
using Raven.Server.ServerWide.Commands.Monitoring.Snmp;
using Raven.Server.ServerWide.Context;
using Sparrow.Collections.LockFree;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;

namespace Raven.Server.Monitoring.Snmp
{
    public class SnmpWatcher
    {
        private readonly ConcurrentDictionary<string, SnmpDatabase> _loadedDatabases = new ConcurrentDictionary<string, SnmpDatabase>(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _locker = new SemaphoreSlim(1, 1);

        private static readonly Logger Logger = LoggingSource.Instance.GetLogger<RavenServer>(nameof(SnmpWatcher));

        private readonly RavenServer _server;

        private ObjectStore _objectStore;

        private SnmpEngine _snmpEngine;

        public SnmpWatcher(RavenServer server)
        {
            _server = server;
        }

        public void Execute()
        {
            if (_server.Configuration.Monitoring.Snmp.Enabled == false)
                return;

            // validate license here

            _objectStore = CreateStore(_server);

            _snmpEngine = CreateSnmpEngine(_server, _objectStore);
            _snmpEngine.Start();

            _server.ServerStore.DatabasesLandlord.OnDatabaseLoaded += AddDatabaseIfNecessary;

            AsyncHelpers.RunSync(AddDatabases);
        }

        private void AddDatabaseIfNecessary(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
                return;

            Task.Factory.StartNew(async () =>
            {
                _locker.Wait();

                try
                {
                    using (_server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                    {
                        context.OpenReadTransaction();

                        var mapping = LoadMapping(context);
                        if (mapping.ContainsKey(databaseName) == false)
                        {
                            var result = await _server.ServerStore.SendToLeaderAsync(new AddDatabasesToSnmpMappingCommand(new List<string> { databaseName }));
                            await _server.ServerStore.Cluster.WaitForIndexNotification(result.Index);

                            context.CloseTransaction();
                            context.OpenReadTransaction();

                            mapping = LoadMapping(context);
                        }

                        LoadDatabase(databaseName, mapping[databaseName]);
                    }
                }
                finally
                {
                    _locker.Release();
                }
            });
        }

        private static SnmpEngine CreateSnmpEngine(RavenServer server, ObjectStore objectStore)
        {
            var v2MembershipProvider = new Version2MembershipProvider(new OctetString(server.Configuration.Monitoring.Snmp.Community), new OctetString(server.Configuration.Monitoring.Snmp.Community));
            var v3MembershipProvider = new Version3MembershipProvider();
            var membershipProvider = new ComposedMembershipProvider(new IMembershipProvider[] { v2MembershipProvider, v3MembershipProvider });

            var handlers = new[]
            {
                new HandlerMapping("V2,V3", "GET", new GetMessageHandler()),
                new HandlerMapping("V2,V3", "GETNEXT", new GetNextMessageHandler()),
                new HandlerMapping("V2,V3", "GETBULK", new GetBulkMessageHandler())
            };

            var messageHandlerFactory = new MessageHandlerFactory(handlers);

            var factory = new SnmpApplicationFactory(new SnmpLogger(Logger), objectStore, membershipProvider, messageHandlerFactory);

            var listener = new Listener();
            listener.Users.Add(new OctetString("ravendb"), new DefaultPrivacyProvider(new SHA1AuthenticationProvider(new OctetString(server.Configuration.Monitoring.Snmp.Community))));

            var engineGroup = new EngineGroup();

            var engine = new SnmpEngine(factory, listener, engineGroup);
            engine.Listener.AddBinding(new IPEndPoint(IPAddress.Any, server.Configuration.Monitoring.Snmp.Port));
            engine.Listener.ExceptionRaised += (sender, e) =>
            {
                if (Logger.IsOperationsEnabled)
                    Logger.Operations("SNMP error: " + e.Exception.Message, e.Exception);
            };

            return engine;
        }

        private static ObjectStore CreateStore(RavenServer server)
        {
            var store = new ObjectStore();

            store.Add(new ServerUrl(server.Configuration));
            store.Add(new ServerPublicUrl(server.Configuration));
            store.Add(new ServerTcpUrl(server.Configuration));
            store.Add(new ServerPublicTcpUrl(server.Configuration));

            store.Add(new ServerVersion());
            store.Add(new ServerFullVersion());

            store.Add(new ServerUpTime(server.Statistics));
            store.Add(new ServerUpTimeGlobal(server.Statistics));

            store.Add(new ServerPid());

            store.Add(new ServerConcurrentRequests(server.Metrics));
            store.Add(new ServerTotalRequests(server.Metrics));
            store.Add(new ServerRequestsPerSecond(server.Metrics));

            store.Add(new ServerCpu());
            store.Add(new ServerTotalMemory());

            store.Add(new ServerLastRequestTime(server.Statistics));

            store.Add(new DatabaseLoadedCount(server.ServerStore.DatabasesLandlord));
            store.Add(new DatabaseTotalCount(server.ServerStore));

            return store;
        }

        private async Task AddDatabases()
        {
            await _locker.WaitAsync();

            try
            {
                using (_server.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                {
                    context.OpenReadTransaction();

                    var databases = _server
                        .ServerStore
                        .Cluster
                        .ItemKeysStartingWith(context, Constants.Documents.Prefix, 0, int.MaxValue)
                        .Select(x => x.Substring(Constants.Documents.Prefix.Length))
                        .ToList();

                    if (databases.Count == 0)
                        return;

                    var mapping = LoadMapping(context);

                    var missingDatabases = new List<string>();
                    foreach (var database in databases)
                    {
                        if (mapping.ContainsKey(database) == false)
                            missingDatabases.Add(database);
                    }

                    if (missingDatabases.Count > 0)
                    {
                        var result = await _server.ServerStore.SendToLeaderAsync(new AddDatabasesToSnmpMappingCommand(missingDatabases));
                        await _server.ServerStore.Cluster.WaitForIndexNotification(result.Index);

                        context.CloseTransaction();
                        context.OpenReadTransaction();

                        mapping = LoadMapping(context);
                    }

                    foreach (var database in databases)
                        LoadDatabase(database, mapping[database]);
                }
            }
            finally
            {
                _locker.Release();
            }
        }

        private void LoadDatabase(string databaseName, long databaseIndex)
        {
            _loadedDatabases.GetOrAdd(databaseName, _ => new SnmpDatabase(_server.ServerStore.DatabasesLandlord, _objectStore, databaseName, (int)databaseIndex));
        }

        private List<(string DatabaseName, int DatabaseIndex)> AssignIndexes(IEnumerable<string> databases, BlittableJsonReaderObject mapping)
        {
            var results = new List<(string DatabaseName, int DatabaseIndex)>();
            foreach (var database in databases)
                results.Add((database, GetOrAddDatabaseIndex(mapping, database)));

            return results;
        }

        private Dictionary<string, long> LoadMapping(TransactionOperationContext context)
        {
            var json = _server.ServerStore.Cluster.Read(context, Constants.Monitoring.Snmp.MappingKey);

            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (json == null)
                return result;

            var propertyDetails = new BlittableJsonReaderObject.PropertyDetails();
            foreach (var index in json.GetPropertiesByInsertionOrder())
            {
                json.GetPropertyByIndex(index, ref propertyDetails);

                result[propertyDetails.Name] = (long)propertyDetails.Value;
            }

            return result;
        }

        private int GetOrAddDatabaseIndex(BlittableJsonReaderObject mappingJson, string databaseName)
        {
            if (databaseName == null)
                throw new ArgumentNullException(nameof(databaseName));

            if (mappingJson.TryGet(databaseName, out int index))
                return index;

            if (mappingJson.Modifications == null)
                mappingJson.Modifications = new DynamicJsonValue();

            index = mappingJson.Count + mappingJson.Modifications.Properties.Count + 1;
            mappingJson.Modifications[databaseName] = index;

            return index;
        }

        private class SnmpLogger : ILogger
        {
            private readonly Logger _logger;

            public SnmpLogger(Logger logger)
            {
                _logger = logger;
            }

            public void Log(ISnmpContext context)
            {
#if DEBUG
                if (_logger.IsInfoEnabled)
                    return;

                var builder = new StringBuilder();
                builder.AppendLine("SNMP:");
                var requestedOids = context.Request.Scope.Pdu.Variables.Select(x => x.Id);
                foreach (var oid in requestedOids)
                {
                    if (context.Response == null)
                    {
                        builder.AppendLine(string.Format("OID: {0}. Response: null", oid));
                        continue;
                    }

                    var responseData = context.Response.Scope.Pdu.Variables
                        .Where(x => x.Id == oid)
                        .Select(x => x.Data)
                        .FirstOrDefault();

                    builder.AppendLine(string.Format("OID: {0}. Response: {1}", oid, responseData != null ? responseData.ToString() : null));
                }

                _logger.Info(builder.ToString());
#endif
            }
        }
    }
}