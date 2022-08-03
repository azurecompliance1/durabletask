﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

namespace DurableTask.AzureStorage.Tracking
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.Serialization;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure;
    using Azure.Data.Tables;
    using DurableTask.AzureStorage.Monitoring;
    using DurableTask.AzureStorage.Storage;
    using DurableTask.Core;
    using DurableTask.Core.History;

    /// <summary>
    /// Tracking store for use with <see cref="AzureStorageOrchestrationService"/>. Uses Azure Tables and Azure Blobs to store runtime state.
    /// </summary>
    class AzureTableTrackingStore : TrackingStoreBase
    {
        const string NameProperty = "Name";
        const string InputProperty = "Input";
        const string ResultProperty = "Result";
        const string OutputProperty = "Output";
        const string RowKeyProperty = "RowKey";
        const string PartitionKeyProperty = "PartitionKey";
        const string SentinelRowKey = "sentinel";
        const string IsCheckpointCompleteProperty = "IsCheckpointComplete";
        const string CheckpointCompletedTimestampProperty = "CheckpointCompletedTimestamp";

        // See https://docs.microsoft.com/en-us/rest/api/storageservices/understanding-the-table-service-data-model#property-types
        const int MaxTablePropertySizeInBytes = 60 * 1024; // 60KB to give buffer

        static readonly string[] VariableSizeEntityProperties = new[]
        {
            NameProperty,
            InputProperty,
            ResultProperty,
            OutputProperty,
            "Reason",
            "Details",
            "Correlation",
            "FailureDetails",
        };

        readonly string storageAccountName;
        readonly string taskHubName;
        readonly AzureStorageClient azureStorageClient;
        readonly AzureStorageOrchestrationServiceSettings settings;
        readonly AzureStorageOrchestrationServiceStats stats;
        readonly TableEntityConverter tableEntityConverter;
        readonly IReadOnlyDictionary<EventType, Type> eventTypeMap;
        readonly MessageManager messageManager;

        public AzureTableTrackingStore(
            AzureStorageClient azureStorageClient,
            MessageManager messageManager)
        {
            this.azureStorageClient = azureStorageClient;
            this.messageManager = messageManager;
            this.settings = this.azureStorageClient.Settings;
            this.stats = this.azureStorageClient.Stats;
            this.tableEntityConverter = new TableEntityConverter();
            this.taskHubName = settings.TaskHubName;

            this.storageAccountName = this.azureStorageClient.TableAccountName;

            string historyTableName = settings.HistoryTableName;
            string instancesTableName = settings.InstanceTableName;

            this.HistoryTable = this.azureStorageClient.GetTableReference(historyTableName);
            this.InstancesTable = this.azureStorageClient.GetTableReference(instancesTableName);

            // Use reflection to learn all the different event types supported by DTFx.
            // This could have been hardcoded, but I generally try to avoid hardcoding of point-in-time DTFx knowledge.
            Type historyEventType = typeof(HistoryEvent);

            IEnumerable<Type> historyEventTypes = historyEventType.Assembly.GetTypes().Where(
                t => !t.IsAbstract && t.IsSubclassOf(historyEventType));

            PropertyInfo eventTypeProperty = historyEventType.GetProperty(nameof(HistoryEvent.EventType));
            this.eventTypeMap = historyEventTypes.ToDictionary(
                type => ((HistoryEvent)FormatterServices.GetUninitializedObject(type)).EventType);
        }

        // For testing
        internal AzureTableTrackingStore(
            AzureStorageOrchestrationServiceStats stats,
            Table instancesTable)
        {
            this.stats = stats;
            this.InstancesTable = instancesTable;
            this.settings = new AzureStorageOrchestrationServiceSettings
            {
                // Have to set FetchLargeMessageDataEnabled to false, as no MessageManager is 
                // instantiated for this test.
                FetchLargeMessageDataEnabled = false,
            };
        }

        internal Table HistoryTable { get; }

        internal Table InstancesTable { get; }

        /// <inheritdoc />
        public override Task CreateAsync(CancellationToken cancellationToken = default)
        {
            return Task.WhenAll(new Task[]
            {
                this.HistoryTable.CreateIfNotExistsAsync(cancellationToken),
                this.InstancesTable.CreateIfNotExistsAsync(cancellationToken)
            });
        }

        /// <inheritdoc />
        public override Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            return Task.WhenAll(new Task[]
            {
                this.HistoryTable.DeleteIfExistsAsync(cancellationToken),
                this.InstancesTable.DeleteIfExistsAsync(cancellationToken)
            });
        }

        /// <inheritdoc />
        public override async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            return this.HistoryTable != null && this.InstancesTable != null && await this.HistoryTable.ExistsAsync(cancellationToken) && await this.InstancesTable.ExistsAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override async Task<OrchestrationHistory> GetHistoryEventsAsync(string instanceId, string expectedExecutionId, CancellationToken cancellationToken = default)
        {
            TableQueryResults<TableEntity> results = await this
                .GetHistoryEntitiesResponseInfoAsync(instanceId, expectedExecutionId, null, cancellationToken)
                .GetResultsAsync(cancellationToken: cancellationToken);

            IList<HistoryEvent> historyEvents;
            string executionId;
            TableEntity sentinel = null;
            if (results.Entities.Count > 0)
            {
                // The most recent generation will always be in the first history event.
                executionId = results.Entities[0].GetString("ExecutionId");

                // Convert the table entities into history events.
                var events = new List<HistoryEvent>(results.Entities.Count);

                foreach (TableEntity entity in results.Entities)
                {
                    if (entity.GetString("ExecutionId") != executionId)
                    {
                        // The remaining entities are from a previous generation and can be discarded.
                        break;
                    }

                    // The sentinel row does not contain any history events, so save it for later
                    // and continue
                    if (entity.RowKey == SentinelRowKey)
                    {
                        sentinel = entity;
                        continue;
                    }

                    // Some entity properties may be stored in blob storage.
                    await this.DecompressLargeEntityProperties(entity, cancellationToken);

                    events.Add(this.tableEntityConverter.ConvertFromTableEntity<HistoryEvent>(entity, GetTypeForTableEntity));
                }

                historyEvents = events;
            }
            else
            {
                historyEvents = EmptyHistoryEventList;
                executionId = expectedExecutionId;
            }

            // Read the checkpoint completion time from the sentinel row, which should always be the last row.
            // A sentinel won't exist only if no instance of this ID has ever existed or the instance history
            // was purged.The IsCheckpointCompleteProperty was newly added _after_ v1.6.4.
            DateTime checkpointCompletionTime = DateTime.MinValue;
            sentinel = sentinel ?? results.Entities.LastOrDefault(e => e.RowKey == SentinelRowKey);
            ETag? eTagValue = sentinel?.ETag;
            if (sentinel != null &&
                sentinel.TryGetValue(CheckpointCompletedTimestampProperty, out object timestampObj) &&
                timestampObj is DateTimeOffset timestampProperty)
            {
                checkpointCompletionTime = timestampProperty.DateTime;
            }

            int currentEpisodeNumber = Utils.GetEpisodeNumber(historyEvents);

            this.settings.Logger.FetchedInstanceHistory(
                this.storageAccountName,
                this.taskHubName,
                instanceId,
                executionId,
                historyEvents.Count,
                currentEpisodeNumber,
                results.RequestCount,
                results.ElapsedMilliseconds,
                eTagValue?.ToString(),
                checkpointCompletionTime);

            return new OrchestrationHistory(historyEvents, checkpointCompletionTime, eTagValue);
        }

        TableQueryResponse<TableEntity> GetHistoryEntitiesResponseInfoAsync(string instanceId, string expectedExecutionId, IList<string> projectionColumns, CancellationToken cancellationToken)
        {
            string filter = $"{nameof(TableEntity.PartitionKey)} eq '{KeySanitation.EscapePartitionKey(instanceId)}'";
            if (!string.IsNullOrEmpty(expectedExecutionId))
            {
                filter += $" and ({nameof(TableEntity.RowKey)} eq '{SentinelRowKey}' or ExecutionId eq '{expectedExecutionId}')";
            }

            return this.HistoryTable.ExecuteQueryAsync<TableEntity>(filter, projectionColumns, cancellationToken);
        }

        async Task<IReadOnlyList<TableEntity>> QueryHistoryAsync(string filter, string instanceId, CancellationToken cancellationToken)
        {
            TableQueryResults<TableEntity> results = await this
                .HistoryTable.ExecuteQueryAsync<TableEntity>(filter, cancellationToken: cancellationToken)
                .GetResultsAsync(cancellationToken: cancellationToken);

            IReadOnlyList<TableEntity> entities = results.Entities;

            string executionId = entities.FirstOrDefault()?.GetString("ExecutionId") ?? string.Empty;
            this.settings.Logger.FetchedInstanceHistory(
                this.storageAccountName,
                this.taskHubName,
                instanceId,
                executionId,
                entities.Count,
                episode: -1, // We don't have enough information to get the episode number. It's also not important to have for this particular trace.
                results.RequestCount,
                results.ElapsedMilliseconds,
                eTag: string.Empty,
                DateTime.MinValue);

            return entities;
        }

        public override async Task<IReadOnlyList<string>> RewindHistoryAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
            // REWIND ALGORITHM:
            // 1. Finds failed execution of specified orchestration instance to rewind
            // 2. Finds failure entities to clear and over-writes them (as well as corresponding trigger events)
            // 3. Identifies sub-orchestration failure(s) from parent instance and calls RewindHistoryAsync recursively on failed sub-orchestration child instance(s)
            // 4. Resets orchestration status of rewound instance in instance store table to prepare it to be restarted
            // 5. Returns "failedLeaves", a list of the deepest failed instances on each failed branch to revive with RewindEvent messages
            ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

            bool hasFailedSubOrchestrations = false;
            string partitionFilter = $"{nameof(TableEntity.PartitionKey)} eq '{KeySanitation.EscapePartitionKey(instanceId)}'";

            string orchestratorStartedFilter = $"{partitionFilter} and EventType eq '{nameof(EventType.OrchestratorStarted)}'";
            IReadOnlyList<TableEntity> orchestratorStartedEntities = await this.QueryHistoryAsync(orchestratorStartedFilter, instanceId, cancellationToken);

            // get most recent orchestratorStarted event
            string recentStartRowKey = orchestratorStartedEntities.Max(x => x.RowKey);
            var recentStartRow = orchestratorStartedEntities.Where(y => y.RowKey == recentStartRowKey).ToList();
            string executionId = recentStartRow[0].GetString("ExecutionId");
            DateTime instanceTimestamp = recentStartRow[0].Timestamp.GetValueOrDefault().DateTime;

            string executionIdFilter = $"ExecutionId eq '{executionId}'";

            var updateFilterBuilder = new StringBuilder();
            updateFilterBuilder.Append($"{partitionFilter}");
            updateFilterBuilder.Append($" and {executionIdFilter}");
            updateFilterBuilder.Append(" and (");
            updateFilterBuilder.Append("OrchestrationStatus eq 'Failed'");
            updateFilterBuilder.Append($" or EventType eq '{nameof(EventType.TaskFailed)}'");
            updateFilterBuilder.Append($" or EventType eq '{nameof(EventType.SubOrchestrationInstanceFailed)}'");
            updateFilterBuilder.Append(')');

            IReadOnlyList<TableEntity> entitiesToClear = await this.QueryHistoryAsync(updateFilterBuilder.ToString(), instanceId, cancellationToken);

            var failedLeaves = new List<string>();
            foreach (TableEntity entity in entitiesToClear.Where(x => x.RowKey == SentinelRowKey))
            {
                int? taskScheduledId = entity.GetInt32("TaskScheduledId");

                var eventFilterBuilder = new StringBuilder();
                eventFilterBuilder.Append($"{partitionFilter}");
                eventFilterBuilder.Append($" and {executionIdFilter}");
                eventFilterBuilder.Append($" and EventId eq '{taskScheduledId.GetValueOrDefault()}'");

                switch (entity.GetString("EventType"))
                {
                    // delete TaskScheduled corresponding to TaskFailed event
                    case nameof(EventType.TaskFailed):
                        eventFilterBuilder.Append($" and EventType eq '{nameof(EventType.TaskScheduled)}'");
                        IReadOnlyList<TableEntity> taskScheduledEntities = await QueryHistoryAsync(eventFilterBuilder.ToString(), instanceId, cancellationToken);

                        TableEntity tsEntity = taskScheduledEntities[0];
                        tsEntity["Reason"] = "Rewound: " + tsEntity.GetString("EventType");
                        tsEntity["EventType"] = nameof(EventType.GenericEvent);
                        await this.HistoryTable.ReplaceAsync(tsEntity, tsEntity.ETag, cancellationToken);
                        break;

                    // delete SubOrchestratorCreated corresponding to SubOrchestraionInstanceFailed event
                    case nameof(EventType.SubOrchestrationInstanceFailed):
                        hasFailedSubOrchestrations = true;

                        eventFilterBuilder.Append($" and EventType eq '{nameof(EventType.SubOrchestrationInstanceCreated)}'");
                        IReadOnlyList<TableEntity> subOrchesratrationEntities = await QueryHistoryAsync(eventFilterBuilder.ToString(), instanceId, cancellationToken);

                        // the SubOrchestrationCreatedEvent is still healthy and will not be overwritten, just marked as rewound
                        TableEntity soEntity = subOrchesratrationEntities[0];
                        soEntity["Reason"] = "Rewound: " + soEntity.GetString("EventType");
                        await this.HistoryTable.ReplaceAsync(soEntity, soEntity.ETag, cancellationToken);

                        // recursive call to clear out failure events on child instances
                        failedLeaves.AddRange(await this.RewindHistoryAsync(soEntity.GetString("InstanceId"), cancellationToken));
                        break;
                }

                // "clear" failure event by making RewindEvent: replay ignores row while dummy event preserves rowKey
                entity["Reason"] = "Rewound: " + entity.GetString("EventType");
                entity["EventType"] = nameof(EventType.GenericEvent);

                await this.HistoryTable.ReplaceAsync(entity, entity.ETag, cancellationToken);
            }

            // reset orchestration status in instance store table
            await this.UpdateStatusForRewindAsync(instanceId, cancellationToken);

            if (!hasFailedSubOrchestrations)
            {
                failedLeaves.Add(instanceId);
            }

            return failedLeaves;
        }

        /// <inheritdoc />
        public override async IAsyncEnumerable<OrchestrationState> GetStateAsync(string instanceId, bool allExecutions, bool fetchInput, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            InstanceStatus instanceStatus = await this.FetchInstanceStatusInternalAsync(instanceId, fetchInput, cancellationToken);
            if (instanceStatus != null)
            {
                yield return instanceStatus.State;
            }
        }
#nullable enable
        /// <inheritdoc />
        public override async Task<OrchestrationState?> GetStateAsync(string instanceId, string executionId, bool fetchInput, CancellationToken cancellationToken = default)
        {
            InstanceStatus? instanceStatus = await this.FetchInstanceStatusInternalAsync(instanceId, fetchInput, cancellationToken);
            return instanceStatus?.State;
        }

        /// <inheritdoc />
        public override Task<InstanceStatus?> FetchInstanceStatusAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            return this.FetchInstanceStatusInternalAsync(instanceId, fetchInput: false, cancellationToken);
        }

        /// <inheritdoc />
        async Task<InstanceStatus?> FetchInstanceStatusInternalAsync(string instanceId, bool fetchInput, CancellationToken cancellationToken)
        {
            if (instanceId == null)
            {
                throw new ArgumentNullException(nameof(instanceId));
            }

            var queryCondition = new OrchestrationInstanceStatusQueryCondition
            {
                InstanceId = instanceId,
                FetchInput = fetchInput,
            };

            (string? filter, IEnumerable<string>? select) = queryCondition.ToOData();
            TableQueryResults<TableEntity> results = await this.InstancesTable
                .ExecuteQueryAsync<TableEntity>(filter, select, cancellationToken)
                .GetResultsAsync(cancellationToken: cancellationToken);

            TableEntity tableEntity = results.Entities.FirstOrDefault();

            OrchestrationState? orchestrationState = null;
            if (tableEntity != null)
            {
                orchestrationState = await this.ConvertFromAsync(tableEntity, cancellationToken);
            }

            this.settings.Logger.FetchedInstanceStatus(
                this.storageAccountName,
                this.taskHubName,
                instanceId,
                orchestrationState?.OrchestrationInstance.ExecutionId ?? string.Empty,
                orchestrationState?.OrchestrationStatus.ToString() ?? "NotFound",
                results.ElapsedMilliseconds);

            if (tableEntity == null || orchestrationState == null)
            {
                return null;
            }

            return new InstanceStatus(orchestrationState, tableEntity.ETag);
        }
#nullable disable
        Task<OrchestrationState> ConvertFromAsync(TableEntity tableEntity, CancellationToken cancellationToken)
        {
            var orchestrationInstanceStatus = ConvertFromProperties(tableEntity);
            var instanceId = KeySanitation.UnescapePartitionKey(tableEntity.PartitionKey);
            return ConvertFromAsync(orchestrationInstanceStatus, instanceId, cancellationToken);
        }

        static OrchestrationInstanceStatus ConvertFromProperties(IDictionary<string, object> properties)
        {
            var orchestrationInstanceStatus = new OrchestrationInstanceStatus();

            var type = typeof(TableEntity);
            foreach (var pair in properties)
            {
                var property = type.GetProperty(pair.Key);
                if (property != null)
                {
                    var value = pair.Value;
                    if (value != null)
                    {
                        if (property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?))
                        {
                            property.SetValue(orchestrationInstanceStatus, value);
                        }
                        else if (property.PropertyType == typeof(int) || property.PropertyType == typeof(int?))
                        {
                            property.SetValue(orchestrationInstanceStatus, value);
                        }
                        else
                        {
                            property.SetValue(orchestrationInstanceStatus, value);
                        }
                    }
                }
            }

            return orchestrationInstanceStatus;
        }

        async Task<OrchestrationState> ConvertFromAsync(OrchestrationInstanceStatus orchestrationInstanceStatus, string instanceId, CancellationToken cancellationToken)
        {
            var orchestrationState = new OrchestrationState();
            if (!Enum.TryParse(orchestrationInstanceStatus.RuntimeStatus, out orchestrationState.OrchestrationStatus))
            {
                // This is not expected, but could happen if there is invalid data in the Instances table.
                orchestrationState.OrchestrationStatus = (OrchestrationStatus)(-1);
            }

            orchestrationState.OrchestrationInstance = new OrchestrationInstance
            {
                InstanceId = instanceId,
                ExecutionId = orchestrationInstanceStatus.ExecutionId,
            };

            orchestrationState.Name = orchestrationInstanceStatus.Name;
            orchestrationState.Version = orchestrationInstanceStatus.Version;
            orchestrationState.Status = orchestrationInstanceStatus.CustomStatus;
            orchestrationState.CreatedTime = orchestrationInstanceStatus.CreatedTime;
            orchestrationState.CompletedTime = orchestrationInstanceStatus.CompletedTime.GetValueOrDefault();
            orchestrationState.LastUpdatedTime = orchestrationInstanceStatus.LastUpdatedTime;
            orchestrationState.Input = orchestrationInstanceStatus.Input;
            orchestrationState.Output = orchestrationInstanceStatus.Output;
            orchestrationState.ScheduledStartTime = orchestrationInstanceStatus.ScheduledStartTime;
            orchestrationState.Generation = orchestrationInstanceStatus.Generation;

            if (this.settings.FetchLargeMessageDataEnabled)
            {
                orchestrationState.Input = await this.messageManager.FetchLargeMessageIfNecessary(orchestrationState.Input, cancellationToken);
                orchestrationState.Output = await this.messageManager.FetchLargeMessageIfNecessary(orchestrationState.Output, cancellationToken);
            }

            return orchestrationState;
        }

        /// <inheritdoc />
        public override async IAsyncEnumerable<OrchestrationState> GetStateAsync(IEnumerable<string> instanceIds, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (instanceIds == null)
            {
                yield break;
            }

            IEnumerable<Task<OrchestrationState>> instanceQueries = instanceIds.Select(instance => this.GetStateAsync(instance, allExecutions: true, fetchInput: false, cancellationToken).SingleAsync().AsTask());
            foreach (OrchestrationState state in await Task.WhenAll(instanceQueries))
            {
                if (state != null)
                {
                    yield return state;
                }
            }
        }

        /// <inheritdoc />
        public override IAsyncEnumerable<OrchestrationState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            return this.QueryStateAsync($"{nameof(TableEntity.RowKey)} eq ''", cancellationToken: cancellationToken);
        }

        public override IAsyncEnumerable<OrchestrationState> GetStateAsync(DateTime createdTimeFrom, DateTime? createdTimeTo, IEnumerable<OrchestrationStatus> runtimeStatus, CancellationToken cancellationToken = default)
        {
            (string filter, IEnumerable<string> select) = OrchestrationInstanceStatusQueryCondition.Parse(createdTimeFrom, createdTimeTo, runtimeStatus).ToOData();
            return this.QueryStateAsync(filter, select, cancellationToken);
        }

        public override IAsyncEnumerable<OrchestrationState> GetStateAsync(OrchestrationInstanceStatusQueryCondition condition, CancellationToken cancellationToken = default)
        {
            (string filter, IEnumerable<string> select) = condition.ToOData();
            return this.QueryStateAsync(filter, select, cancellationToken);
        }

        IAsyncEnumerable<OrchestrationState> QueryStateAsync(string filter = null, IEnumerable<string> select = null, CancellationToken cancellationToken = default)
        {
            return this.InstancesTable
                .ExecuteQueryAsync<OrchestrationInstanceStatus>(filter, select, cancellationToken)
                .SelectAwaitWithCancellation((s, t) => new ValueTask<OrchestrationState>(this.ConvertFromAsync(s, KeySanitation.UnescapePartitionKey(s.PartitionKey), t)));
        }

        async Task<PurgeHistoryResult> DeleteHistoryAsync(
            DateTime createdTimeFrom,
            DateTime? createdTimeTo,
            IEnumerable<OrchestrationStatus> runtimeStatus,
            CancellationToken cancellationToken)
        {
            var condition = OrchestrationInstanceStatusQueryCondition.Parse(
                createdTimeFrom,
                createdTimeTo,
                runtimeStatus);
            condition.FetchInput = false;
            condition.FetchOutput = false;

            (string filter, IEnumerable<string> select) = condition.ToOData();

            // Limit to batches of 100 to avoid excessive memory usage and table storage scanning
            int storageRequests = 0;
            int instancesDeleted = 0;
            int rowsDeleted = 0;

            var options = new ParallelOptions { MaxDegreeOfParallelism = this.settings.MaxStorageOperationConcurrency };
            AsyncPageable<OrchestrationInstanceStatus> entitiesPageable = this.InstancesTable.ExecuteQueryAsync<OrchestrationInstanceStatus>(filter, select, cancellationToken);
            await foreach (Page<OrchestrationInstanceStatus> page in entitiesPageable.AsPages(pageSizeHint: 100))
            {
                // The underlying client throttles
                await Task.WhenAll(page.Values.Select(async instance =>
                {
                    PurgeHistoryResult statisticsFromDeletion = await this.DeleteAllDataForOrchestrationInstance(instance, cancellationToken);
                    Interlocked.Add(ref instancesDeleted, statisticsFromDeletion.InstancesDeleted);
                    Interlocked.Add(ref storageRequests, statisticsFromDeletion.RowsDeleted);
                    Interlocked.Add(ref rowsDeleted, statisticsFromDeletion.RowsDeleted);
                }));
            }

            return new PurgeHistoryResult(storageRequests, instancesDeleted, rowsDeleted);
        }

        async Task<PurgeHistoryResult> DeleteAllDataForOrchestrationInstance(OrchestrationInstanceStatus orchestrationInstanceStatus, CancellationToken cancellationToken)
        {
            int storageRequests = 0;
            int rowsDeleted = 0;

            string sanitizedInstanceId = KeySanitation.UnescapePartitionKey(orchestrationInstanceStatus.PartitionKey);

            TableQueryResults<TableEntity> results = await this
                .GetHistoryEntitiesResponseInfoAsync(
                    instanceId: sanitizedInstanceId,
                    expectedExecutionId: null,
                    projectionColumns: new[] { RowKeyProperty },
                    cancellationToken)
                .GetResultsAsync(cancellationToken: cancellationToken);

            storageRequests += results.RequestCount;

            IReadOnlyList<TableEntity> historyEntities = results.Entities;

            var tasks = new List<Task>
            {
                Task.Run(async () =>
                {
                    int storageOperations = await this.messageManager.DeleteLargeMessageBlobs(sanitizedInstanceId, cancellationToken);
                    Interlocked.Add(ref storageRequests, storageOperations);
                }),
                Task.Run(async () =>
                {
                    var deletedEntitiesResponseInfo = await this.HistoryTable.DeleteBatchAsync(historyEntities, cancellationToken);
                    Interlocked.Add(ref rowsDeleted, deletedEntitiesResponseInfo.Responses.Count);
                    Interlocked.Add(ref storageRequests, deletedEntitiesResponseInfo.RequestCount);
                }),
                this.InstancesTable.DeleteAsync(new TableEntity(orchestrationInstanceStatus.PartitionKey, string.Empty), ETag.All, cancellationToken: cancellationToken)
            };

            await Task.WhenAll(tasks);

            // This is for the instances table deletion
            storageRequests++;

            return new PurgeHistoryResult(storageRequests, 1, rowsDeleted);
        }

        /// <inheritdoc />
        public override Task PurgeHistoryAsync(DateTime thresholdDateTimeUtc, OrchestrationStateTimeRangeFilterType timeRangeFilterType, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override async Task<PurgeHistoryResult> PurgeInstanceHistoryAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            string sanitizedInstanceId = KeySanitation.EscapePartitionKey(instanceId);

            string filter = $"{PartitionKeyProperty} eq '{sanitizedInstanceId}' and {RowKeyProperty} eq ''";
            var results = await this.InstancesTable
                .ExecuteQueryAsync<OrchestrationInstanceStatus>(filter: filter, cancellationToken: cancellationToken)
                .GetResultsAsync(cancellationToken: cancellationToken);

            OrchestrationInstanceStatus orchestrationInstanceStatus = results.Entities.FirstOrDefault();

            if (orchestrationInstanceStatus != null)
            {
                PurgeHistoryResult result = await this.DeleteAllDataForOrchestrationInstance(orchestrationInstanceStatus, cancellationToken);

                this.settings.Logger.PurgeInstanceHistory(
                    this.storageAccountName,
                    this.taskHubName,
                    instanceId,
                    DateTime.MinValue.ToString(),
                    DateTime.MinValue.ToString(),
                    string.Empty,
                    result.StorageRequests,
                    result.InstancesDeleted,
                    results.ElapsedMilliseconds);

                return result;
            }

            return new PurgeHistoryResult(0, 0, 0);
        }

        /// <inheritdoc />
        public override async Task<PurgeHistoryResult> PurgeInstanceHistoryAsync(
            DateTime createdTimeFrom,
            DateTime? createdTimeTo,
            IEnumerable<OrchestrationStatus> runtimeStatus,
            CancellationToken cancellationToken = default)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<OrchestrationStatus> runtimeStatusList =  runtimeStatus?.Where(
               status => status == OrchestrationStatus.Completed ||
                    status == OrchestrationStatus.Terminated ||
                    status == OrchestrationStatus.Canceled ||
                    status == OrchestrationStatus.Failed).ToList();

            PurgeHistoryResult result = await this.DeleteHistoryAsync(createdTimeFrom, createdTimeTo, runtimeStatusList, cancellationToken);

            this.settings.Logger.PurgeInstanceHistory(
                this.storageAccountName,
                this.taskHubName,
                string.Empty,
                createdTimeFrom.ToString(),
                createdTimeTo.ToString() ?? DateTime.MinValue.ToString(),
                runtimeStatus != null ?
                    string.Join(",", runtimeStatus.Select(x => x.ToString())) :
                    string.Empty,
                result.StorageRequests,
                result.InstancesDeleted,
                stopwatch.ElapsedMilliseconds);

            return result;
        }

        /// <inheritdoc />
        public override async Task<bool> SetNewExecutionAsync(
            ExecutionStartedEvent executionStartedEvent,
            ETag? eTag,
            string inputStatusOverride,
            CancellationToken cancellationToken = default)
        {
            string sanitizedInstanceId = KeySanitation.EscapePartitionKey(executionStartedEvent.OrchestrationInstance.InstanceId);
            TableEntity entity = new TableEntity(sanitizedInstanceId, "")
            {
                ["Input"] = inputStatusOverride ?? executionStartedEvent.Input,
                ["CreatedTime"] = executionStartedEvent.Timestamp,
                ["Name"] = executionStartedEvent.Name,
                ["Version"] = executionStartedEvent.Version,
                ["RuntimeStatus"] = OrchestrationStatus.Pending.ToString("G"),
                ["LastUpdatedTime"] = DateTime.UtcNow,
                ["TaskHubName"] = this.settings.TaskHubName,
                ["ScheduledStartTime"] = executionStartedEvent.ScheduledStartTime,
                ["ExecutionId"] = executionStartedEvent.OrchestrationInstance.ExecutionId,
                ["Generation"] = executionStartedEvent.Generation,
            };

            // It is possible that the queue message was small enough to be written directly to a queue message,
            // not a blob, but is too large to be written to a table property.
            await this.CompressLargeMessageAsync(entity, cancellationToken);

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                if (eTag == null)
                {
                    // This is the case for creating a new instance.
                    await this.InstancesTable.InsertAsync(entity, cancellationToken);
                }
                else
                {
                    // This is the case for overwriting an existing instance.
                    await this.InstancesTable.ReplaceAsync(entity, eTag.GetValueOrDefault(), cancellationToken);
                }
            }
            catch (DurableTaskStorageException e) when (
                e.HttpStatusCode == 409 /* Conflict */ ||
                e.HttpStatusCode == 412 /* Precondition failed */)
            {
                // Ignore. The main scenario for this is handling race conditions in status update.
                return false;
            }

            // Episode 0 means the orchestrator hasn't started yet.
            int currentEpisodeNumber = 0;

            this.settings.Logger.InstanceStatusUpdate(
                this.storageAccountName,
                this.taskHubName,
                executionStartedEvent.OrchestrationInstance.InstanceId,
                executionStartedEvent.OrchestrationInstance.ExecutionId,
                OrchestrationStatus.Pending,
                currentEpisodeNumber,
                stopwatch.ElapsedMilliseconds);

            return true;
        }

        /// <inheritdoc />
        public override async Task UpdateStatusForRewindAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            string sanitizedInstanceId = KeySanitation.EscapePartitionKey(instanceId);
            TableEntity entity = new TableEntity(sanitizedInstanceId, "")
            {
                ["RuntimeStatus"] = OrchestrationStatus.Pending.ToString("G"),
                ["LastUpdatedTime"] = DateTime.UtcNow,
            };

            Stopwatch stopwatch = Stopwatch.StartNew();
            await this.InstancesTable.MergeAsync(entity, ETag.All, cancellationToken);

            // We don't have enough information to get the episode number.
            // It's also not important to have for this particular trace.
            int currentEpisodeNumber = 0;

            this.settings.Logger.InstanceStatusUpdate(
                this.storageAccountName,
                this.taskHubName,
                instanceId,
                string.Empty,
                OrchestrationStatus.Pending,
                currentEpisodeNumber,
                stopwatch.ElapsedMilliseconds);
        }


        /// <inheritdoc />
        public override Task StartAsync(CancellationToken cancellationToken = default)
        {
            Uri historyUri = this.HistoryTable.Uri;
            Uri instanceUri = this.InstancesTable.Uri;

            if (historyUri != null)
            {
                ServicePointManager.FindServicePoint(historyUri).UseNagleAlgorithm = false;
            }

            if (instanceUri != null)
            {
                ServicePointManager.FindServicePoint(instanceUri).UseNagleAlgorithm = false;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override async Task<ETag?> UpdateStateAsync(
            OrchestrationRuntimeState newRuntimeState,
            OrchestrationRuntimeState oldRuntimeState,
            string instanceId,
            string executionId,
            ETag? eTagValue,
            CancellationToken cancellationToken = default)
        {
            int estimatedBytes = 0;
            IList<HistoryEvent> newEvents = newRuntimeState.NewEvents;
            IList<HistoryEvent> allEvents = newRuntimeState.Events;

            int episodeNumber = Utils.GetEpisodeNumber(newRuntimeState);

            var newEventListBuffer = new StringBuilder(4000);
            var historyEventBatch = new List<TableTransactionAction>();

            OrchestrationStatus runtimeStatus = OrchestrationStatus.Running;
            string sanitizedInstanceId = KeySanitation.EscapePartitionKey(instanceId);

            var instanceEntity = new TableEntity(sanitizedInstanceId, string.Empty)
            {
                // TODO: Translating null to "null" is a temporary workaround. We should prioritize 
                // https://github.com/Azure/durabletask/issues/477 so that this is no longer necessary.
                ["CustomStatus"] = newRuntimeState.Status ?? "null",
                ["ExecutionId"] = executionId,
                ["LastUpdatedTime"] = newEvents.Last().Timestamp,
            };
           
            for (int i = 0; i < newEvents.Count; i++)
            {
                bool isFinalEvent = i == newEvents.Count - 1;

                HistoryEvent historyEvent = newEvents[i];
                var historyEntity = this.tableEntityConverter.ConvertToTableEntity(historyEvent);
                historyEntity.PartitionKey = sanitizedInstanceId;

                newEventListBuffer.Append(historyEvent.EventType.ToString()).Append(',');

                // The row key is the sequence number, which represents the chronological ordinal of the event.
                long sequenceNumber = i + (allEvents.Count - newEvents.Count);
                historyEntity.RowKey = sequenceNumber.ToString("X16");
                historyEntity["ExecutionId"] = executionId;

                await this.CompressLargeMessageAsync(historyEntity, cancellationToken);

                // Replacement can happen if the orchestration episode gets replayed due to a commit failure in one of the steps below.
                historyEventBatch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, historyEntity));

                // Keep track of the byte count to ensure we don't hit the 4 MB per-batch maximum
                estimatedBytes += GetEstimatedByteCount(historyEntity);

                // Monitor for orchestration instance events 
                switch (historyEvent.EventType)
                {
                    case EventType.ExecutionStarted:
                        runtimeStatus = OrchestrationStatus.Running;
                        ExecutionStartedEvent executionStartedEvent = (ExecutionStartedEvent)historyEvent;
                        instanceEntity["Name"] = executionStartedEvent.Name;
                        instanceEntity["Version"] = executionStartedEvent.Version;
                        instanceEntity["CreatedTime"] = executionStartedEvent.Timestamp;
                        instanceEntity["RuntimeStatus"] = OrchestrationStatus.Running.ToString();
                        if (executionStartedEvent.ScheduledStartTime.HasValue)
                        {
                            instanceEntity["ScheduledStartTime"] = executionStartedEvent.ScheduledStartTime;
                        }

                        this.SetInstancesTablePropertyFromHistoryProperty(
                            historyEntity,
                            instanceEntity,
                            historyPropertyName: nameof(executionStartedEvent.Input),
                            instancePropertyName: InputProperty,
                            data: executionStartedEvent.Input);
                        break;
                    case EventType.ExecutionCompleted:
                        ExecutionCompletedEvent executionCompleted = (ExecutionCompletedEvent)historyEvent;
                        runtimeStatus = executionCompleted.OrchestrationStatus;
                        instanceEntity["RuntimeStatus"] = executionCompleted.OrchestrationStatus.ToString();
                        instanceEntity["CompletedTime"] = DateTime.UtcNow;
                        this.SetInstancesTablePropertyFromHistoryProperty(
                            historyEntity,
                            instanceEntity,
                            historyPropertyName: nameof(executionCompleted.Result),
                            instancePropertyName: OutputProperty,
                            data: executionCompleted.FailureDetails?.ToString() ?? executionCompleted.Result);
                        break;
                    case EventType.ExecutionTerminated:
                        runtimeStatus = OrchestrationStatus.Terminated;
                        ExecutionTerminatedEvent executionTerminatedEvent = (ExecutionTerminatedEvent)historyEvent;
                        instanceEntity["RuntimeStatus"] = OrchestrationStatus.Terminated.ToString();
                        instanceEntity["CompletedTime"] = DateTime.UtcNow;
                        this.SetInstancesTablePropertyFromHistoryProperty(
                            historyEntity,
                            instanceEntity,
                            historyPropertyName: nameof(executionTerminatedEvent.Input),
                            instancePropertyName: OutputProperty,
                            data: executionTerminatedEvent.Input);
                        break;
                    case EventType.ContinueAsNew:
                        runtimeStatus = OrchestrationStatus.ContinuedAsNew;
                        ExecutionCompletedEvent executionCompletedEvent = (ExecutionCompletedEvent)historyEvent;
                        instanceEntity["RuntimeStatus"] = OrchestrationStatus.ContinuedAsNew.ToString();
                        this.SetInstancesTablePropertyFromHistoryProperty(
                            historyEntity,
                            instanceEntity,
                            historyPropertyName: nameof(executionCompletedEvent.Result),
                            instancePropertyName: OutputProperty,
                            data: executionCompletedEvent.Result);
                        break;
                }

                // Table storage only supports inserts of up to 100 entities at a time or 4 MB at a time.
                if (historyEventBatch.Count == 99 || estimatedBytes > 3 * 1024 * 1024 /* 3 MB */)
                {
                    eTagValue = await this.UploadHistoryBatch(
                        instanceId,
                        sanitizedInstanceId,
                        executionId,
                        historyEventBatch,
                        newEventListBuffer,
                        allEvents.Count,
                        episodeNumber,
                        estimatedBytes,
                        eTagValue,
                        isFinalBatch: isFinalEvent,
                        cancellationToken: cancellationToken);

                    // Reset local state for the next batch
                    newEventListBuffer.Clear();
                    historyEventBatch.Clear();
                    estimatedBytes = 0;
                }
            }

            // First persistence step is to commit history to the history table. Messages must come after.
            if (historyEventBatch.Count > 0)
            {
                eTagValue = await this.UploadHistoryBatch(
                    instanceId,
                    sanitizedInstanceId,
                    executionId,
                    historyEventBatch,
                    newEventListBuffer,
                    allEvents.Count,
                    episodeNumber,
                    estimatedBytes,
                    eTagValue,
                    isFinalBatch: true,
                    cancellationToken: cancellationToken);
            }

            Stopwatch orchestrationInstanceUpdateStopwatch = Stopwatch.StartNew();
            await this.InstancesTable.InsertOrMergeAsync(instanceEntity);

            this.settings.Logger.InstanceStatusUpdate(
                this.storageAccountName,
                this.taskHubName,
                instanceId,
                executionId,
                runtimeStatus,
                episodeNumber,
                orchestrationInstanceUpdateStopwatch.ElapsedMilliseconds);

            return eTagValue;
        }

        static int GetEstimatedByteCount(TableEntity entity)
        {
            // Assume at least 1 KB of data per entity to account for static-length properties
            int estimatedByteCount = 1024;

            // Count the bytes for variable-length properties, which are assumed to always be strings
            foreach (string propertyName in VariableSizeEntityProperties)
            {
                if (entity.TryGetValue(propertyName, out object property) && property is string stringProperty && stringProperty != "")
                {
                    estimatedByteCount += Encoding.Unicode.GetByteCount(stringProperty);
                }
            }

            return estimatedByteCount;
        }

        Type GetTypeForTableEntity(TableEntity tableEntity)
        {
            string propertyName = nameof(HistoryEvent.EventType);

            if (!tableEntity.TryGetValue(propertyName, out object eventTypeProperty))
            {
                throw new ArgumentException($"The TableEntity did not contain a '{propertyName}' property.");
            }

            if (eventTypeProperty is not string stringProperty)
            {
                throw new ArgumentException($"The TableEntity's {propertyName} property type must a String.");
            }

            if (!Enum.TryParse(stringProperty, out EventType eventType))
            {
                throw new ArgumentException($"{stringProperty} is not a valid EventType value.");
            }

            return this.eventTypeMap[eventType];
        }

        // Assigns the target table entity property. Any large message for type 'Input, or 'Output' would have been compressed earlier as part of the 'entity' object,
        // so, we only need to assign the 'entity' object's blobName to the target table entity blob name property.
        void SetInstancesTablePropertyFromHistoryProperty(
            TableEntity TableEntity,
            TableEntity instanceEntity,
            string historyPropertyName,
            string instancePropertyName,
            string data)
        {
            string blobPropertyName = GetBlobPropertyName(historyPropertyName);
            if (TableEntity.TryGetValue(blobPropertyName, out object blobProperty) && blobProperty is string blobName)
            {
                // This is a large message
                string blobUrl = this.messageManager.GetBlobUrl(blobName);
                instanceEntity[instancePropertyName] = blobUrl;
            }
            else
            {
                // This is a normal-sized message and can be stored inline
                instanceEntity[instancePropertyName] = data;
            }
        }

        async Task CompressLargeMessageAsync(TableEntity entity, CancellationToken cancellationToken)
        {
            foreach (string propertyName in VariableSizeEntityProperties)
            {
                if (entity.TryGetValue(propertyName, out object property) &&
                    property is string stringProperty &&
                    this.ExceedsMaxTablePropertySize(stringProperty))
                {
                    // Upload the large property as a blob in Blob Storage since it won't fit in table storage.
                    string blobName = GetBlobName(entity, propertyName);
                    byte[] messageBytes = Encoding.UTF8.GetBytes(stringProperty);
                    await this.messageManager.CompressAndUploadAsBytesAsync(messageBytes, blobName, cancellationToken);

                    // Clear out the original property value and create a new "*BlobName"-suffixed property.
                    // The runtime will look for the new "*BlobName"-suffixed column to know if a property is stored in a blob.
                    string blobPropertyName = GetBlobPropertyName(propertyName);
                    entity.Add(blobPropertyName, blobName);
                    entity[propertyName] = string.Empty;
                }
            }
        }

        async Task DecompressLargeEntityProperties(TableEntity entity, CancellationToken cancellationToken)
        {
            // Check for entity properties stored in blob storage
            foreach (string propertyName in VariableSizeEntityProperties)
            {
                string blobPropertyName = GetBlobPropertyName(propertyName);
                if (entity.TryGetValue(blobPropertyName, out object property) && property is string blobName)
                {
                    string decompressedMessage = await this.messageManager.DownloadAndDecompressAsBytesAsync(blobName, cancellationToken);
                    entity[propertyName] = decompressedMessage;
                    entity.Remove(blobPropertyName);
                }
            }
        }

        static string GetBlobPropertyName(string originalPropertyName)
        {
            // WARNING: Changing this is a breaking change!
            return originalPropertyName + "BlobName";
        }

        static string GetBlobName(TableEntity entity, string property)
        {
            string sanitizedInstanceId = entity.PartitionKey;
            string sequenceNumber = entity.RowKey;

            string eventType;
            if (entity.TryGetValue("EventType", out object obj) && obj is string value)
            {
                eventType = value;
            }
            else if (property == "Input")
            {
                // This message is just to start the orchestration, so it does not have a corresponding
                // EventType. Use a hardcoded value to record the orchestration input.
                eventType = "Input";
            }
            else
            {
                throw new InvalidOperationException($"Could not compute the blob name for property {property}");
            }

            return $"{sanitizedInstanceId}/history-{sequenceNumber}-{eventType}-{property}.json.gz";
        }

        async Task<ETag?> UploadHistoryBatch(
            string instanceId,
            string sanitizedInstanceId,
            string executionId,
            IList<TableTransactionAction> historyEventBatch,
            StringBuilder historyEventNamesBuffer,
            int numberOfTotalEvents,
            int episodeNumber,
            int estimatedBatchSizeInBytes,
            ETag? eTagValue,
            bool isFinalBatch,
            CancellationToken cancellationToken)
        {
            // Adding / updating sentinel entity
            TableEntity sentinelEntity = new TableEntity(sanitizedInstanceId, SentinelRowKey)
            {
                ["ExecutionId"] = executionId,
                [IsCheckpointCompleteProperty] = isFinalBatch,
            };

            if (isFinalBatch)
            {
                sentinelEntity[CheckpointCompletedTimestampProperty] = DateTime.UtcNow;
            }

            if (eTagValue != null)
            {
                historyEventBatch.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, sentinelEntity, eTagValue.GetValueOrDefault()));
            }
            else
            {
                historyEventBatch.Add(new TableTransactionAction(TableTransactionActionType.Add, sentinelEntity));
            }

            TableTransactionResults resultInfo;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                resultInfo = await this.HistoryTable.ExecuteBatchAsync(historyEventBatch, cancellationToken);
            }
            catch (DurableTaskStorageException ex)
            {
                if (ex.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed)
                {
                    this.settings.Logger.SplitBrainDetected(
                        this.storageAccountName,
                        this.taskHubName,
                        instanceId,
                        executionId,
                        historyEventBatch.Count - 1, // exclude sentinel from count
                        numberOfTotalEvents,
                        historyEventNamesBuffer.ToString(0, historyEventNamesBuffer.Length - 1), // remove trailing comma
                        stopwatch.ElapsedMilliseconds,
                        eTagValue?.ToString());
                }

                throw;
            }

            IReadOnlyList<Response> responses = resultInfo.Responses;
            ETag? newETagValue = null;
            for (int i = responses.Count - 1; i >= 0; i--)
            {
                if (historyEventBatch[i].Entity.RowKey == SentinelRowKey)
                {
                    newETagValue = responses[i].Headers.ETag;
                    break;
                }
            }

            this.settings.Logger.AppendedInstanceHistory(
                this.storageAccountName,
                this.taskHubName,
                instanceId,
                executionId,
                historyEventBatch.Count - 1, // exclude sentinel from count
                numberOfTotalEvents,
                historyEventNamesBuffer.ToString(0, historyEventNamesBuffer.Length - 1), // remove trailing comma
                episodeNumber,
                resultInfo.ElapsedMilliseconds,
                estimatedBatchSizeInBytes,
                string.Concat(eTagValue?.ToString() ?? "(null)", " --> ", newETagValue?.ToString() ?? "(null)"),
                isFinalBatch);

            return newETagValue;
        }

        bool ExceedsMaxTablePropertySize(string data)
        {
            if (!string.IsNullOrEmpty(data) && Encoding.Unicode.GetByteCount(data) > MaxTablePropertySizeInBytes)
            {
                return true;
            }

            return false;
        }
    }
}
