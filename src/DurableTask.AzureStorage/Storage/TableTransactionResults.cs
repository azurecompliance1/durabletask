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
#nullable enable
namespace DurableTask.AzureStorage.Storage
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Azure;

    sealed class TableTransactionResults
    {
        public TableTransactionResults(IReadOnlyList<Response> responses, TimeSpan elapsed, int requestCount = 1)
        {
            this.Responses = responses ?? throw new ArgumentNullException(nameof(responses));
            this.Elapsed = elapsed;
            this.RequestCount = requestCount;
        }

        public IReadOnlyList<Response> Responses { get; }

        public TimeSpan Elapsed { get; }

        public int ElapsedMilliseconds => (int)this.Elapsed.TotalMilliseconds;

        public int RequestCount { get; }

        public TableTransactionResults Add(TableTransactionResults other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            return new TableTransactionResults(
                this.Responses.Concat(other.Responses).ToList(),
                this.Elapsed + other.Elapsed,
                this.RequestCount + other.RequestCount);
        }
    }
}
