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

namespace DurableTask.Test.Orchestrations.Stress;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DurableTask.Core;

public class TestOrchestration : TaskOrchestration<int, TestOrchestrationData>
{
    public override async Task<int> RunTask(OrchestrationContext context, TestOrchestrationData data)
    {
        var results = new List<Task<int>>();
        var i = 0;
        var j = 0;
        for (; i < data.NumberOfParallelTasks; i++)
        {
            results.Add(context.ScheduleTask<int>(typeof(TestTask), new TestTaskData
            {
                TaskId = "ParallelTask: " + i.ToString(),
                MaxDelayInMinutes = data.MaxDelayInMinutes,
            }));
        }

        int[] counters = await Task.WhenAll(results.ToArray());
        int result = counters.Max();

        for (; j < data.NumberOfSerialTasks; j++)
        {
            int c = await context.ScheduleTask<int>(typeof(TestTask), new TestTaskData
            {
                TaskId = "SerialTask" + (i + j).ToString(),
                MaxDelayInMinutes = data.MaxDelayInMinutes,
            });
            result = Math.Max(result, c);
        }

        return result;
    }
}
