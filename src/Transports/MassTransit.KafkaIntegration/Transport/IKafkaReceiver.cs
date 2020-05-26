﻿namespace MassTransit.KafkaIntegration.Transport
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Confluent.Kafka;
    using GreenPipes;
    using Pipeline;
    using Transports;


    public interface IKafkaReceiver<TKey, TValue> :
        IReceiveObserverConnector,
        IPublishObserverConnector,
        ISendObserverConnector,
        IConsumeMessageObserverConnector,
        IConsumeObserverConnector,
        IProbeSite
        where TValue : class
    {
        /// <summary>
        ///     Handles the <paramref name="message" />
        /// </summary>
        /// <param name="message"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="contextCallback">Callback to adjust the context</param>
        /// <returns></returns>
        Task Handle(ConsumeResult<TKey, TValue> message, CancellationToken cancellationToken, Action<ReceiveContext> contextCallback = null);
    }
}