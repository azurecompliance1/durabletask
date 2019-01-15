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

namespace DurableTask.ServiceFabric.Models
{
    using System;
    using DurableTask.Core;

    /// <summary>
    /// Purging orchestration history parameters.
    /// </summary>
    public class PurgeOrchestrationHistoryParameters
    {
        /// <summary>
        /// Starting date time for purging orchestrations.
        /// </summary>
        public DateTime ThresholdDateTimeUtc { get; set; }

        /// <summary>
        /// Orchestration start time range filter type.
        /// </summary>
        public OrchestrationStateTimeRangeFilterType TimeRangeFilterType { get; set; }
    }
}
