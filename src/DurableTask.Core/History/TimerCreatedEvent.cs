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

namespace DurableTask.Core.History
{
    using System;
    using System.Runtime.Serialization;

    /// <summary>
    /// A history event for a new timer creation
    /// </summary>
    [DataContract]
    public class TimerCreatedEvent : HistoryEvent
    {
        // Private ctor for JSON deserialization (required by some storage providers and out-of-proc executors)
        private TimerCreatedEvent() : base(-1) { }

        /// <summary>
        /// Creates a new <see cref="TimerCreatedEvent"/> with the supplied event ID
        /// and <paramref name="fireAt"/> timestamp.
        /// </summary>
        /// <param name="eventId">The ID of the timer event.</param>
        /// <param name="fireAt">The time at which the timer should fire.</param>
        public TimerCreatedEvent(int eventId, DateTime fireAt) : base(eventId) => this.FireAt = fireAt;

        /// <summary>
        /// Creates a new TimerCreatedEvent with the supplied event id
        /// </summary>
        /// <param name="eventId"></param>
        public TimerCreatedEvent(int eventId) : base(eventId) { }

        /// <summary>
        /// Gets the event type
        /// </summary>
        public override EventType EventType => EventType.TimerCreated;

        /// <summary>
        /// Gets or sets the desired datetime to fire
        /// </summary>
        [DataMember]
        public DateTime FireAt { get; set; }
    }
}
