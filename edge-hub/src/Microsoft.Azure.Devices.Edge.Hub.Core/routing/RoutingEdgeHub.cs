// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Hub.Core.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Cloud;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Device;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Routing.Core;
    using Microsoft.Extensions.Logging;
    using static System.FormattableString;
    using IIdentity = Microsoft.Azure.Devices.Edge.Hub.Core.Identity.IIdentity;
    using IMessage = Microsoft.Azure.Devices.Edge.Hub.Core.IMessage;
    using IRoutingMessage = Microsoft.Azure.Devices.Routing.Core.IMessage;

    public class RoutingEdgeHub : IEdgeHub
    {
        readonly Router router;
        readonly Core.IMessageConverter<IRoutingMessage> messageConverter;
        readonly IConnectionManager connectionManager;
        readonly ITwinManager twinManager;
        readonly string edgeDeviceId;
        const long MaxMessageSize = 256 * 1024; // matches IoTHub

        public RoutingEdgeHub(Router router, Core.IMessageConverter<IRoutingMessage> messageConverter,
            IConnectionManager connectionManager, ITwinManager twinManager, string edgeDeviceId)
        {
            this.router = Preconditions.CheckNotNull(router, nameof(router));
            this.messageConverter = Preconditions.CheckNotNull(messageConverter, nameof(messageConverter));
            this.connectionManager = Preconditions.CheckNotNull(connectionManager, nameof(connectionManager));
            this.twinManager = Preconditions.CheckNotNull(twinManager, nameof(twinManager));
            this.edgeDeviceId = Preconditions.CheckNonWhiteSpace(edgeDeviceId, nameof(edgeDeviceId));
            this.connectionManager.CloudConnectionEstablished += this.CloudConnectionEstablished;
        }

        public Task ProcessDeviceMessage(IIdentity identity, IMessage message)
        {
            Preconditions.CheckNotNull(message, nameof(message));
            Preconditions.CheckNotNull(identity, nameof(identity));
            Events.MessageReceived(identity);
            IRoutingMessage routingMessage = this.ProcessMessageInternal(message, true);
            return this.router.RouteAsync(routingMessage);
        }

        public Task ProcessDeviceMessageBatch(IIdentity identity, IEnumerable<IMessage> messages)
        {
            IEnumerable<IRoutingMessage> routingMessages = Preconditions.CheckNotNull(messages)
                .Select(m => this.ProcessMessageInternal(m, true));
            return this.router.RouteAsync(routingMessages);
        }

        public Task<DirectMethodResponse> InvokeMethodAsync(string id, DirectMethodRequest methodRequest)
        {
            Preconditions.CheckNotNull(methodRequest, nameof(methodRequest));

            Events.MethodCallReceived(id, methodRequest.Id, methodRequest.CorrelationId);
            Option<IDeviceProxy> deviceProxy = this.connectionManager.GetDeviceConnection(methodRequest.Id);
            return deviceProxy.Match(
                dp =>
                {
                    if (this.connectionManager.GetSubscriptions(methodRequest.Id)
                        .Filter(s => s.TryGetValue(DeviceSubscription.Methods, out bool isActive) && isActive)
                        .HasValue)
                    {
                        Events.InvokingMethod(methodRequest);
                        return dp.InvokeMethodAsync(methodRequest);
                    }
                    else
                    {
                        Events.NoSubscriptionForMethodInvocation(methodRequest);
                        return Task.FromResult(new DirectMethodResponse(null, null, (int)HttpStatusCode.NotFound));
                    }
                },
                () =>
                {
                    Events.NoDeviceProxyForMethodInvocation(methodRequest);
                    return Task.FromResult(new DirectMethodResponse(null, null, (int)HttpStatusCode.NotFound));
                });
        }

        public Task UpdateReportedPropertiesAsync(IIdentity identity, IMessage reportedPropertiesMessage)
        {
            Preconditions.CheckNotNull(identity, nameof(identity));
            Preconditions.CheckNotNull(reportedPropertiesMessage, nameof(reportedPropertiesMessage));
            Events.UpdateReportedPropertiesReceived(identity);
            Task cloudSendMessageTask = this.twinManager.UpdateReportedPropertiesAsync(identity.Id, reportedPropertiesMessage);

            IRoutingMessage routingMessage = this.ProcessMessageInternal(reportedPropertiesMessage, false);
            Task routingSendMessageTask = this.router.RouteAsync(routingMessage);

            return Task.WhenAll(cloudSendMessageTask, routingSendMessageTask);
        }

        public Task SendC2DMessageAsync(string id, IMessage message)
        {
            Preconditions.CheckNonWhiteSpace(id, nameof(id));
            Preconditions.CheckNotNull(message, nameof(message));

            Option<IDeviceProxy> deviceProxy = this.connectionManager.GetDeviceConnection(id);
            if (!deviceProxy.HasValue)
            {
                Events.UnableToSendC2DMessageNoDeviceConnection(id);
            }

            return deviceProxy.ForEachAsync(d => d.SendC2DMessageAsync(message));
        }

        static void ValidateMessageSize(IRoutingMessage messageToBeValidated)
        {
            long messageSize = messageToBeValidated.Size();
            if (messageSize > MaxMessageSize)
            {
                throw new InvalidOperationException($"Message size exceeds maximum allowed size: got {messageSize}, limit {MaxMessageSize}");
            }
        }

        IRoutingMessage ProcessMessageInternal(IMessage message, bool validateSize)
        {
            this.AddEdgeSystemProperties(message);
            IRoutingMessage routingMessage = this.messageConverter.FromMessage(Preconditions.CheckNotNull(message, nameof(message)));

            // Validate message size
            if (validateSize)
            {
                ValidateMessageSize(routingMessage);
            }

            return routingMessage;
        }

        internal void AddEdgeSystemProperties(IMessage message)
        {
            message.SystemProperties[Core.SystemProperties.EdgeMessageId] = Guid.NewGuid().ToString();
            if (message.SystemProperties.TryGetValue(Core.SystemProperties.ConnectionDeviceId, out string deviceId))
            {
                string edgeHubOriginInterface = deviceId == this.edgeDeviceId
                    ? Core.Constants.InternalOriginInterface
                    : Core.Constants.DownstreamOriginInterface;
                message.SystemProperties[Core.SystemProperties.EdgeHubOriginInterface] = edgeHubOriginInterface;
            }
        }

        public Task<IMessage> GetTwinAsync(string id)
        {
            Events.GetTwinCallReceived(id);
            return this.twinManager.GetTwinAsync(id);
        }

        public Task UpdateDesiredPropertiesAsync(string id, IMessage twinCollection)
        {
            Events.UpdateDesiredPropertiesCallReceived(id);
            return this.twinManager.UpdateDesiredPropertiesAsync(id, twinCollection);
        }

        public async Task AddSubscription(string id, DeviceSubscription deviceSubscription)
        {
            Events.AddingSubscription(id, deviceSubscription);
            this.connectionManager.AddSubscription(id, deviceSubscription);
            try
            {
                Option<ICloudProxy> cloudProxy = this.connectionManager.GetCloudConnection(id);
                await cloudProxy.ForEachAsync(c => this.ProcessSubscription(c, deviceSubscription, true));
            }
            catch (Exception e)
            {
                Events.ErrorAddingSubscription(e, id, deviceSubscription);
                if (!e.HasTimeoutException())
                {
                    throw;
                }
            }
        }

        public async Task RemoveSubscription(string id, DeviceSubscription deviceSubscription)
        {
            Events.RemovingSubscription(id, deviceSubscription);
            this.connectionManager.RemoveSubscription(id, deviceSubscription);
            try
            {
                Option<ICloudProxy> cloudProxy = this.connectionManager.GetCloudConnection(id);
                await cloudProxy.ForEachAsync(c => this.ProcessSubscription(c, deviceSubscription, false));
            }
            catch (Exception e)
            {
                Events.ErrorRemovingSubscription(e, id, deviceSubscription);
                if (!(e is TimeoutException))
                {
                    throw;
                }
            }
        }

        internal async Task ProcessSubscription(ICloudProxy cloudProxy, DeviceSubscription deviceSubscription, bool addSubscription)
        {
            switch (deviceSubscription)
            {
                case DeviceSubscription.C2D:
                    if (addSubscription)
                    {
                        cloudProxy.StartListening();
                    }
                    break;

                case DeviceSubscription.DesiredPropertyUpdates:
                    await (addSubscription ? cloudProxy.SetupDesiredPropertyUpdatesAsync() : cloudProxy.RemoveDesiredPropertyUpdatesAsync());
                    break;

                case DeviceSubscription.Methods:
                    await (addSubscription ? cloudProxy.SetupCallMethodAsync() : cloudProxy.RemoveCallMethodAsync());
                    break;

                case DeviceSubscription.ModuleMessages:
                case DeviceSubscription.TwinResponse:
                case DeviceSubscription.Unknown:
                    // No Action required
                    break;
            }
        }

        async void CloudConnectionEstablished(object sender, IIdentity identity)
        {
            try
            {
                await this.ProcessSubscriptions(identity.Id);
            }
            catch (Exception e)
            {
                Events.ErrorProcessingSubscriptions(e, identity);
            }
        }

        async Task ProcessSubscriptions(string id)
        {
            Option<ICloudProxy> cloudProxy = this.connectionManager.GetCloudConnection(id);
            await cloudProxy.ForEachAsync(
                c =>
                {
                    Option<IReadOnlyDictionary<DeviceSubscription, bool>> subscriptions = this.connectionManager.GetSubscriptions(id);
                    return subscriptions.ForEachAsync(
                        async s =>
                        {
                            foreach (KeyValuePair<DeviceSubscription, bool> subscription in s)
                            {
                                await this.ProcessSubscription(c, subscription.Key, subscription.Value);
                            }
                        });

                });
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.router?.Dispose();
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        static class Events
        {
            static readonly ILogger Log = Logger.Factory.CreateLogger<RoutingEdgeHub>();
            const int IdStart = HubCoreEventIds.RoutingEdgeHub;

            enum EventIds
            {
                MethodReceived = IdStart,
                MessageReceived = 1501,
                ReportedPropertiesUpdateReceived = 1502,
                DesiredPropertiesUpdateReceived = 1503,
                DeviceConnectionNotFound,
                ErrorProcessingSubscriptions,
                ErrorRemovingSubscription,
                ErrorAddingSubscription,
                AddingSubscription,
                RemovingSubscription,
                InvokingMethod,
                NoSubscription,
                ClientNotFound
            }

            public static void MethodCallReceived(string fromId, string toId, string correlationId)
            {
                Log.LogDebug((int)EventIds.MethodReceived, Invariant($"Received method invoke call from {fromId} for {toId} with correlation ID {correlationId}"));
            }

            internal static void MessageReceived(IIdentity identity)
            {
                Log.LogDebug((int)EventIds.MessageReceived, Invariant($"Received message from {identity.Id}"));
            }

            internal static void UpdateReportedPropertiesReceived(IIdentity identity)
            {
                Log.LogDebug((int)EventIds.ReportedPropertiesUpdateReceived, Invariant($"Reported properties update message received from {identity.Id}"));
            }

            internal static void GetTwinCallReceived(string id)
            {
                Log.LogDebug((int)EventIds.MessageReceived, Invariant($"GetTwin call received from {id ?? string.Empty}"));
            }

            internal static void UpdateDesiredPropertiesCallReceived(string id)
            {
                Log.LogDebug((int)EventIds.DesiredPropertiesUpdateReceived, Invariant($"Desired properties update message received for {id ?? string.Empty}"));
            }

            public static void UnableToSendC2DMessageNoDeviceConnection(string id)
            {
                Log.LogWarning((int)EventIds.DeviceConnectionNotFound, Invariant($"Unable to send C2D message to device {id} as an active device connection was not found."));
            }

            public static void ErrorProcessingSubscriptions(Exception ex, IIdentity identity)
            {
                Log.LogWarning((int)EventIds.ErrorProcessingSubscriptions, ex, Invariant($"Error processing subscriptions for client {identity.Id}."));
            }

            public static void ErrorRemovingSubscription(Exception ex, string id, DeviceSubscription subscription)
            {
                Log.LogWarning((int)EventIds.ErrorRemovingSubscription, ex, Invariant($"Error removing subscription {subscription} for client {id}."));
            }

            public static void ErrorAddingSubscription(Exception ex, string id, DeviceSubscription subscription)
            {
                Log.LogWarning((int)EventIds.ErrorAddingSubscription, ex, Invariant($"Error adding subscription {subscription} for client {id}."));
            }

            public static void AddingSubscription(string id, DeviceSubscription subscription)
            {
                Log.LogDebug((int)EventIds.AddingSubscription, Invariant($"Adding subscription {subscription} for client {id}."));
            }

            public static void RemovingSubscription(string id, DeviceSubscription subscription)
            {
                Log.LogDebug((int)EventIds.RemovingSubscription, Invariant($"Removing subscription {subscription} for client {id}."));
            }

            public static void InvokingMethod(DirectMethodRequest methodRequest)
            {
                Log.LogDebug((int)EventIds.InvokingMethod, Invariant($"Invoking method {methodRequest.Name} on client {methodRequest.Id}."));
            }

            public static void NoSubscriptionForMethodInvocation(DirectMethodRequest methodRequest)
            {
                Log.LogWarning((int)EventIds.NoSubscription, Invariant($"Unable to invoke method {methodRequest.Name} on client {methodRequest.Id} because no subscription for methods for found."));
            }

            public static void NoDeviceProxyForMethodInvocation(DirectMethodRequest methodRequest)
            {
                Log.LogWarning((int)EventIds.ClientNotFound, Invariant($"Unable to invoke method {methodRequest.Name} as client {methodRequest.Id} is not connected."));
            }
        }
    }
}
