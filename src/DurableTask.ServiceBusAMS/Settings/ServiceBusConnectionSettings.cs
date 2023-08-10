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

namespace DurableTask.ServiceBus.Settings
{
    using Azure.Core;
    using Azure.Messaging.ServiceBus;
    using System;
    using System.Net;

    /// <summary>
    /// Service Bus connection settings
    /// </summary>
    public class ServiceBusConnectionSettings
    {
        /// <summary>
        /// Creates an instance of <see cref="ServiceBusConnectionSettings"/>
        /// </summary>
        /// <param name="connectionString">Service Bus connection string</param>
        /// <returns></returns>
        public static ServiceBusConnectionSettings Create(string connectionString)
        {
            return new ServiceBusConnectionSettings
            {
                ConnectionString = connectionString
            };
        }

        /// <summary>
        /// Creates an instance of <see cref="ServiceBusConnectionSettings"/>
        /// </summary>
        /// <param name="namespaceHostName">Service Bus namespace host name</param>
        /// <param name="tokenCredential">Service Bus authentication token credential</param>
        /// <param name="transportType">Service Bus messaging protocol</param>
        /// <returns></returns>
        public static ServiceBusConnectionSettings Create(string namespaceHostName, TokenCredential tokenCredential, ServiceBusTransportType transportType = ServiceBusTransportType.AmqpTcp)
        {
            return new ServiceBusConnectionSettings
            {
                FullyQualifiedNamespace = namespaceHostName,
                TokenCredential = tokenCredential,
                TransportType = transportType
            };
        }

        /// <summary>
        /// Creates an instance of <see cref="ServiceBusConnectionSettings"/>
        /// </summary>
        /// <param name="serviceBusEndpoint">Service Bus endpoint</param>
        /// <param name="tokenCredential">Service Bus authentication token credential</param>
        /// <param name="transportType">Service Bus messaging protocol</param>
        /// <returns></returns>
        public static ServiceBusConnectionSettings Create(Uri serviceBusEndpoint, TokenCredential tokenCredential, ServiceBusTransportType transportType = ServiceBusTransportType.AmqpTcp)
        {
            return new ServiceBusConnectionSettings
            {
                FullyQualifiedNamespace = serviceBusEndpoint.Host,
                TokenCredential = tokenCredential,
                TransportType = transportType
            };
        }

        private ServiceBusConnectionSettings()
        {
        }

        /// <summary>
        /// Service Bus connection string
        /// </summary>
        public string ConnectionString { get; private set; }

        public string FullyQualifiedNamespace { get; private set; }

        /// <summary>
        /// Service Bus endpoint
        /// </summary>
        public Uri Endpoint {
            get
            {
                return new Uri($"sb:/{this.FullyQualifiedNamespace}/");
            }
        }


        /// <summary>
        /// Service Bus authentication token provider
        /// </summary>
        public TokenCredential TokenCredential { get; private set; }

        /// <summary>
        /// Service Bus messaging protocol
        /// </summary>
        public ServiceBusTransportType TransportType { get; private set; }


    }
}
