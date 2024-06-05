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

namespace DurableTask.ServiceBus.Tracking
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.Serialization;
    using Azure.Data.Tables;
    using DurableTask.Core;
    using DurableTask.Core.Common;
    using DurableTask.Core.Serializing;

    /// <summary>
    /// History Tracking Entity for an orchestration's state
    /// </summary>
    public class AzureTableOrchestrationStateEntity : AzureTableCompositeTableEntity
    {
        readonly DataConverter dataConverter;

        /// <summary>
        /// Creates a new AzureTableOrchestrationStateEntity
        /// </summary>
        public AzureTableOrchestrationStateEntity()
        {
            this.dataConverter = JsonDataConverter.Default;
        }

        /// <summary>
        /// Creates a new AzureTableOrchestrationStateEntity with the supplied orchestration state
        /// </summary>
        /// <param name="state">The orchestration state</param>
        public AzureTableOrchestrationStateEntity(OrchestrationState state)
            : this()
        {
            State = state;
            TaskTimeStamp = state.CompletedTime;
        }

        /// <summary>
        /// Gets or sets the orchestration state for the entity
        /// </summary>
        [IgnoreDataMember]
        public OrchestrationState State { get; set; }

#pragma warning disable 1591
        // In order to maintain table schema with the new Azure SDK, we need the following accessors.
        // As a result, public accessors with the "State" prefix do not need to be documented.
        [DataMember(Name = "InstanceId")]
        public string StateInstanceId
        {
            get => State.OrchestrationInstance.InstanceId;
            set => State.OrchestrationInstance.InstanceId = value;
        }

        [DataMember(Name = "ExecutionId")]
        public string StateExecutionId
        {
            get => State.OrchestrationInstance.ExecutionId;
            set => State.OrchestrationInstance.ExecutionId = value;
        }

        [DataMember(Name = "ParentInstanceId")]
        public string StateParentInstanceId
        {
            get => State.ParentInstance?.OrchestrationInstance.InstanceId;
            set
            {
                if (State.ParentInstance != null)
                {
                    State.ParentInstance.OrchestrationInstance.InstanceId = value;
                }
            }
        }

        [DataMember(Name = "ParentExecutionId")]
        public string StateParentExecutionId
        {
            get => State.ParentInstance?.OrchestrationInstance.ExecutionId;
            set
            {
                if (State.ParentInstance != null)
                {
                    State.ParentInstance.OrchestrationInstance.ExecutionId = value;
                }
            }
        }

        [DataMember(Name = "Name")]
        public string StateName
        {
            get => State.Name;
            set => State.Name = value;
        }

        [DataMember(Name = "Version")]
        public string StateVersion
        {
            get => State.Version;
            set => State.Version = value;
        }

        [DataMember(Name = "Status")]
        public string StateStatus
        {
            get => State.Status;
            set => State.Status = value;
        }

        [DataMember(Name = "Tags")]
        public string StateTags
        {
            get => State.Tags != null ? this.dataConverter.Serialize(State.Tags) : null;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    State.Tags = null;
                }

                State.Tags = this.dataConverter.Deserialize<IDictionary<string, string>>(value);
            }
        }

        [DataMember(Name = "OrchestrationStatus")]
        public string StateOrchestrationStatus
        {
            get => State.OrchestrationStatus.ToString();
            set
            {
                if (!Enum.TryParse(value, out State.OrchestrationStatus))
                {
                    throw new InvalidOperationException("Invalid status string in state " + value);
                }
            }
        }

        [DataMember(Name = "CreatedTime")]
        public DateTime StateCreatedTime
        {
            get => State.CreatedTime;
            set => State.CreatedTime = value;
        }

        [DataMember(Name = "CompletedTime")]
        public DateTime StateCompletedTime
        {
            get => State.CompletedTime;
            set => State.CompletedTime = value;
        }

        [DataMember(Name = "LastUpdatedTime")]
        public DateTime StateLastUpdatedTime
        {
            get => State.LastUpdatedTime;
            set => State.LastUpdatedTime = value;
        }

        [DataMember(Name = "Size")]
        public long StateSize
        {
            get => State.Size;
            set => State.Size = value;
        }

        [DataMember(Name = "CompressedSize")]
        public long StateCompressedSize
        {
            get => State.CompressedSize;
            set => State.CompressedSize = value;
        }

        [DataMember(Name = "Input")]
        public string StateInput
        {
            get => State.Input.Truncate(ServiceBusConstants.MaxStringLengthForAzureTableColumn);
            set => State.Input = value;

        }

        [DataMember(Name = "Output")]
        public string StateOutput
        {
            get => State.Output.Truncate(ServiceBusConstants.MaxStringLengthForAzureTableColumn);
            set => State.Output = value;
        }

        [DataMember(Name = "ScheduledStartTime")]
        public DateTime? StateScheduledStartTime
        {
            get => State.ScheduledStartTime;
            set => State.ScheduledStartTime = value;
        }
#pragma warning restore 1591

        internal override IEnumerable<ITableEntity> BuildDenormalizedEntities()
        {
            var entity1 = new AzureTableOrchestrationStateEntity(State);

            entity1.PartitionKey = AzureTableConstants.InstanceStatePrefix;
            entity1.RowKey = AzureTableConstants.InstanceStateExactRowPrefix +
                             AzureTableConstants.JoinDelimiter + State.OrchestrationInstance.InstanceId +
                             AzureTableConstants.JoinDelimiter + State.OrchestrationInstance.ExecutionId;

            return new [] { entity1 };
            // TODO : additional indexes for efficient querying in the future
        }

        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        /// <returns>
        /// A string that represents the current object.
        /// </returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            // ReSharper disable once UseStringInterpolation
            return string.Format(
                "Instance Id: {0} Execution Id: {1} Name: {2} Version: {3} CreatedTime: {4} CompletedTime: {5} LastUpdated: {6} Status: {7} User Status: {8} Input: {9} Output: {10} Size: {11} CompressedSize: {12}",
                State.OrchestrationInstance.InstanceId, State.OrchestrationInstance.ExecutionId, State.Name,
                State.Version, State.CreatedTime, State.CompletedTime,
                State.LastUpdatedTime, State.OrchestrationStatus, State.Status, State.Input, State.Output, State.Size,
                State.CompressedSize);
        }
    }
}