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

namespace DurableTask.ServiceFabric
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System.Web.Http;
    using System.Web.Http.Results;

    using DurableTask.Core;
    using DurableTask.Core.Exceptions;
    using DurableTask.ServiceFabric.Models;
    using DurableTask.ServiceFabric.Tracing;

    /// <summary>
    /// A Web Api controller that provides TaskHubClient operations.
    /// </summary>
    public class FabricOrchestrationServiceController : ApiController
    {
        private readonly IOrchestrationServiceClient orchestrationServiceClient;

        /// <summary>
        /// Creates an instance of FabricOrchestrationServiceController for given OrchestrationServiceClient
        /// </summary>
        /// <param name="orchestrationServiceClient">IOrchestrationServiceClient instance</param>
        public FabricOrchestrationServiceController(IOrchestrationServiceClient orchestrationServiceClient)
        {
            this.orchestrationServiceClient = orchestrationServiceClient;
        }

        /// <summary>
        /// Creates a task orchestration.
        /// </summary>
        /// <param name="parameters">Parameters for creating task orchestration.</param>
        /// <exception cref="OrchestrationAlreadyExistsException">Will throw an OrchestrationAlreadyExistsException exception
        /// If any orchestration with the same instance Id exists in the instance store and it has a status specified in dedupeStatuses.</exception>
        /// <returns> <see cref="IHttpActionResult"/> object. </returns>
        [HttpPut]
        public async Task<IHttpActionResult> CreateTaskOrchestration([FromBody] CreateTaskOrchestrationParameters parameters)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(CreateTaskOrchestration));
            try
            {
                if (parameters.DedupeStatuses == null)
                {
                    await this.orchestrationServiceClient.CreateTaskOrchestrationAsync(parameters.TaskMessage);
                }
                else
                {
                    await this.orchestrationServiceClient.CreateTaskOrchestrationAsync(parameters.TaskMessage, parameters.DedupeStatuses);
                }

                return new OkResult(this);
            }
            catch (OrchestrationAlreadyExistsException exception)
            {
                return new ExceptionResult(exception, this);
            }
        }

        /// <summary>
        /// Sends an orchestration message to TaskHubClient.
        /// </summary>
        /// <param name="message">Message to send</param>
        /// <returns> <see cref="IHttpActionResult"/> object. </returns>
        [HttpPost]
        public async Task<IHttpActionResult> SendTaskOrchestrationMessage([FromBody] TaskMessage message)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(SendTaskOrchestrationMessage));
            await this.orchestrationServiceClient.SendTaskOrchestrationMessageAsync(message);
            return new OkResult(this);
        }

        /// <summary>
        /// Sends an array of orchestration messages to TaskHubClient.
        /// </summary>
        /// <param name="messages">Message to send</param>
        /// <returns> <see cref="IHttpActionResult"/> object. </returns>
        [HttpPost]
        public async Task<IHttpActionResult> SendTaskOrchestrationMessageBatch([FromBody] TaskMessage[] messages)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(SendTaskOrchestrationMessageBatch));
            await this.orchestrationServiceClient.SendTaskOrchestrationMessageBatchAsync(messages);
            return new OkResult(this);
        }

        /// <summary>
        /// Gets the state of orchestration.
        /// </summary>
        /// <param name="instanceId">Instance id of the orchestration</param>
        /// <param name="executionId">Execution id of the orchestration</param>
        /// <returns> <see cref="OrchestrationState" /> object. </returns>
        [HttpGet]
        public async Task<OrchestrationState> GetOrchestrationState(string instanceId, string executionId)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(GetOrchestrationState));
            var state = await this.orchestrationServiceClient.GetOrchestrationStateAsync(instanceId, executionId);
            return state;
        }

        /// <summary>
        /// Gets the state of orchestration.
        /// </summary>
        /// <param name="instanceId">Instance id of the orchestration</param>
        /// <param name="allExecutions">True if method should fetch all executions of the instance, false if the method should only fetch the most recent execution</param>
        /// <returns>List of <see cref="OrchestrationState"/>. </returns>
        [HttpGet]
        public async Task<IList<OrchestrationState>> GetOrchestrationState(string instanceId, bool allExecutions)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(GetOrchestrationState));
            var state = await this.orchestrationServiceClient.GetOrchestrationStateAsync(instanceId, allExecutions);
            return state;
        }

        /// <summary>
        /// Terminates an orchestration.
        /// </summary>
        /// <param name="instanceId">Instance id of the orchestration</param>
        /// <param name="reason">Execution id of the orchestration</param>
        /// <returns> <see cref="IHttpActionResult"/> object. </returns>
        [HttpDelete]
        public async Task<IHttpActionResult> ForceTerminateTaskOrchestration(string instanceId, string reason)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(ForceTerminateTaskOrchestration));
            await this.orchestrationServiceClient.ForceTerminateTaskOrchestrationAsync(instanceId, reason);
            return new OkResult(this);
        }

        /// <summary>
        /// Gets the history of orchestration.
        /// </summary>
        /// <param name="instanceId">Instance id of the orchestration</param>
        /// <param name="executionId">Execution id of the orchestration</param>
        /// <returns>Orchestration history</returns>
        [HttpGet]
        public async Task<string> GetOrchestrationHistory(string instanceId, string executionId)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(GetOrchestrationHistory));
            var result = await this.orchestrationServiceClient.GetOrchestrationHistoryAsync(instanceId, executionId);
            return result;
        }

        /// <summary>
        /// Purges orchestration instance state and history for orchestrations older than the specified threshold time.
        /// </summary>
        /// <param name="purgeParameters">Purge history parameters</param>
        /// <returns> <see cref="IHttpActionResult"/> object. </returns>
        [HttpPost]
        public async Task<IHttpActionResult> PurgeOrchestrationHistory(PurgeOrchestrationHistoryParameters purgeParameters)
        {
            ProviderEventSource.Log.LogServingNetworkAction(nameof(PurgeOrchestrationHistory));
            await this.orchestrationServiceClient.PurgeOrchestrationHistoryAsync(purgeParameters.ThresholdDateTimeUtc, purgeParameters.TimeRangeFilterType);
            return new OkResult(this);
        }
    }
}
