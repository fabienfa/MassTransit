﻿// Copyright 2007-2015 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace Automatonymous
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using CorrelationConfigurators;
    using MassTransit;
    using MassTransit.Internals.Extensions;
    using Requests;


    /// <summary>
    /// A MassTransit state machine adds functionality on top of Automatonymous supporting
    /// things like request/response, and correlating events to the state machine, as well
    /// as retry and policy configuration.
    /// </summary>
    /// <typeparam name="TInstance">The state instance type</typeparam>
    public class MassTransitStateMachine<TInstance> :
        AutomatonymousStateMachine<TInstance>,
        SagaStateMachine<TInstance>
        where TInstance : class, SagaStateMachineInstance
    {
        readonly Dictionary<Event, EventCorrelation<TInstance>> _eventCorrelations;
        readonly Lazy<StateMachineRegistration[]> _registrations;
        Func<TInstance, bool> _isCompleted;

        protected MassTransitStateMachine()
        {
            _registrations = new Lazy<StateMachineRegistration[]>(GetRegistrations);

            _eventCorrelations = new Dictionary<Event, EventCorrelation<TInstance>>();
            _isCompleted = NotCompletedByDefault;

            RegisterImplicit();
        }

        public IEnumerable<EventCorrelation<TInstance>> Correlations
        {
            get
            {
                foreach (Event @event in Events)
                {
                    EventCorrelation<TInstance> correlation;
                    if (_eventCorrelations.TryGetValue(@event, out correlation))
                        yield return correlation;
                }
            }
        }

        bool SagaStateMachine<TInstance>.IsCompleted(TInstance instance)
        {
            return _isCompleted(instance);
        }

        /// <summary>
        /// Sets the method used to determine if a state machine instance is completed. A completed 
        /// state machine instance is removed from the saga repository.
        /// </summary>
        /// <param name="completed"></param>
        protected void SetCompleted(Func<TInstance, bool> completed)
        {
            _isCompleted = completed ?? NotCompletedByDefault;
        }

        /// <summary>
        /// Sets the state machine instance to Completed when in the final state. A completed
        /// state machine instance is removed from the saga repository.
        /// </summary>
        protected void SetCompletedWhenFinalized()
        {
            _isCompleted = IsFinalized;
        }

        bool IsFinalized(TInstance instance)
        {
            return Final.Equals(((StateMachine<TInstance>)this).InstanceStateAccessor.GetState(instance));
        }

        /// <summary>
        /// Declares an Event on the state machine with the specified data type, and allows the correlation of the event
        /// to be configured.
        /// </summary>
        /// <typeparam name="T">The event data type</typeparam>
        /// <param name="propertyExpression">The event property</param>
        /// <param name="configureEventCorrelation">Configuration callback for the event</param>
        protected void Event<T>(Expression<Func<Event<T>>> propertyExpression, Action<EventCorrelationConfigurator<TInstance, T>> configureEventCorrelation)
            where T : class
        {
            base.Event(propertyExpression);

            PropertyInfo propertyInfo = propertyExpression.GetPropertyInfo();

            var @event = (Event<T>)propertyInfo.GetValue(this);

            var configurator = new MassTransitEventCorrelationConfigurator<TInstance, T>(this, @event);

            configureEventCorrelation(configurator);

            _eventCorrelations[@event] = configurator.Build();
        }

        /// <summary>
        /// Declares an Event on the state machine with the specified data type, and allows the correlation of the event
        /// to be configured.
        /// </summary>
        /// <typeparam name="T">The event data type</typeparam>
        /// <typeparam name="TProperty">The property type</typeparam>
        /// <param name="propertyExpression">The containing property</param>
        /// <param name="eventPropertyExpression">The event property expression</param>
        /// <param name="configureEventCorrelation">Configuration callback for the event</param>
        protected void Event<TProperty, T>(Expression<Func<TProperty>> propertyExpression, Expression<Func<TProperty, Event<T>>> eventPropertyExpression,
            Action<EventCorrelationConfigurator<TInstance, T>> configureEventCorrelation)
            where TProperty : class
            where T : class
        {
            base.Event(propertyExpression, eventPropertyExpression);

            PropertyInfo propertyInfo = propertyExpression.GetPropertyInfo();
            var property = (TProperty)propertyInfo.GetValue(this);

            PropertyInfo eventPropertyInfo = eventPropertyExpression.GetPropertyInfo();
            var @event = (Event<T>)eventPropertyInfo.GetValue(property);

            var configurator = new MassTransitEventCorrelationConfigurator<TInstance, T>(this, @event);

            configureEventCorrelation(configurator);

            _eventCorrelations[@event] = configurator.Build();
        }

        /// <summary>
        /// Declares an event on the state machine with the specified data type, where the data type contains the
        /// CorrelatedBy(Guid) interface. The correlation by CorrelationId is automatically configured to the saga
        /// instance.
        /// </summary>
        /// <typeparam name="T">The event data type</typeparam>
        /// <param name="propertyExpression">The property to initialize</param>
        protected override void Event<T>(Expression<Func<Event<T>>> propertyExpression)
        {
            base.Event(propertyExpression);

            if (typeof(T).HasInterface<CorrelatedBy<Guid>>())
            {
                PropertyInfo propertyInfo = propertyExpression.GetPropertyInfo();

                var @event = (Event<T>)propertyInfo.GetValue(this);

                Type builderType = typeof(CorrelatedByEventCorrelationBuilder<,>).MakeGenericType(typeof(TInstance), typeof(T));
                var builder = (EventCorrelationBuilder<TInstance>)Activator.CreateInstance(builderType, this, @event);

                _eventCorrelations[@event] = builder.Build();
            }
        }

        void DefaultCorrelatedByConfigurator<T>(EventCorrelationConfigurator<TInstance, T> configurator)
            where T : class, CorrelatedBy<Guid>
        {
            configurator.CorrelateById(context => context.Message.CorrelationId);
        }

        /// <summary>
        /// Declares a request that is sent by the state machine to a service, and the associated response, fault, and
        /// timeout handling. The property is initialized with the fully built Request. The request must be declared before
        /// it is used in the state/event declaration statements.
        /// </summary>
        /// <typeparam name="TRequest">The request type</typeparam>
        /// <typeparam name="TResponse">The response type</typeparam>
        /// <param name="propertyExpression">The request property on the state machine</param>
        /// <param name="requestIdExpression">The property where the requestId is stored</param>
        /// <param name="configureRequest">Allow the request settings to be specified inline</param>
        protected void Request<TRequest, TResponse>(Expression<Func<Request<TInstance, TRequest, TResponse>>> propertyExpression,
            Expression<Func<TInstance, Guid?>> requestIdExpression,
            Action<RequestConfigurator<TInstance, TRequest, TResponse>> configureRequest)
            where TRequest : class
            where TResponse : class
        {
            var configurator = new StateMachineRequestConfigurator<TInstance, TRequest, TResponse>();

            configureRequest(configurator);

            Request(propertyExpression, requestIdExpression, configurator.Settings);
        }

        /// <summary>
        /// Declares a request that is sent by the state machine to a service, and the associated response, fault, and
        /// timeout handling. The property is initialized with the fully built Request. The request must be declared before
        /// it is used in the state/event declaration statements.
        /// </summary>
        /// <typeparam name="TRequest">The request type</typeparam>
        /// <typeparam name="TResponse">The response type</typeparam>
        /// <param name="propertyExpression">The request property on the state machine</param>
        /// <param name="requestIdExpression">The property where the requestId is stored</param>
        /// <param name="settings">The request settings (which can be read from configuration, etc.)</param>
        protected void Request<TRequest, TResponse>(Expression<Func<Request<TInstance, TRequest, TResponse>>> propertyExpression,
            Expression<Func<TInstance, Guid?>> requestIdExpression,
            RequestSettings settings)
            where TRequest : class
            where TResponse : class
        {
            PropertyInfo property = propertyExpression.GetPropertyInfo();

            string requestName = property.Name;

            var request = new StateMachineRequest<TInstance, TRequest, TResponse>(requestName, requestIdExpression, settings);

            property.SetValue(this, request);

            Event(propertyExpression, x => x.Completed, x => x.CorrelateBy(requestIdExpression, context => context.RequestId));
            Event(propertyExpression, x => x.Faulted, x => x.CorrelateBy(requestIdExpression, context => context.RequestId));
            Event(propertyExpression, x => x.TimeoutExpired, x => x.CorrelateBy<Guid>(requestIdExpression, context => context.Message.RequestId));

            State(propertyExpression, x => x.Pending);
        }

        static bool NotCompletedByDefault(TInstance instance)
        {
            return false;
        }

        /// <summary>
        /// Register all remaining events and states that have not been explicitly declared.
        /// </summary>
        void RegisterImplicit()
        {
            foreach (StateMachineRegistration declaration in _registrations.Value)
                declaration.Declare(this);
        }

        static IEnumerable<PropertyInfo> GetStateMachineProperties(TypeInfo typeInfo)
        {
            if (typeInfo.IsInterface)
                yield break;

            if (typeInfo.BaseType != null)
            {
                foreach (PropertyInfo propertyInfo in GetStateMachineProperties(typeInfo.BaseType.GetTypeInfo()))
                    yield return propertyInfo;
            }

            IEnumerable<PropertyInfo> properties = typeInfo.DeclaredMethods
                .Where(x => x.IsSpecialName && x.Name.StartsWith("get_") && !x.IsStatic)
                .Select(x => typeInfo.GetDeclaredProperty(x.Name.Substring("get_".Length)))
                .Where(x => x.CanRead && x.CanWrite);

            foreach (PropertyInfo propertyInfo in properties)
                yield return propertyInfo;
        }

        StateMachineRegistration[] GetRegistrations()
        {
            var events = new List<StateMachineRegistration>();

            Type machineType = GetType();

            IEnumerable<PropertyInfo> properties = GetStateMachineProperties(machineType.GetTypeInfo());

            foreach (PropertyInfo propertyInfo in properties)
            {
                if (propertyInfo.PropertyType.IsGenericType)
                {
                    if (propertyInfo.PropertyType.GetGenericTypeDefinition() == typeof(Event<>))
                    {
                        Type messageType = propertyInfo.PropertyType.GetGenericArguments().First();
                        if (messageType.HasInterface<CorrelatedBy<Guid>>())
                        {
                            Type declarationType = typeof(CorrelatedEventRegistration<,>).MakeGenericType(typeof(TInstance), machineType,
                                messageType);
                            object declaration = Activator.CreateInstance(declarationType, propertyInfo);
                            events.Add((StateMachineRegistration)declaration);
                        }
                    }
                }
            }

            return events.ToArray();
        }


        class CorrelatedEventRegistration<TStateMachine, TData> :
            StateMachineRegistration
            where TStateMachine : MassTransitStateMachine<TInstance>
            where TData : CorrelatedBy<Guid>
        {
            readonly PropertyInfo _propertyInfo;

            public CorrelatedEventRegistration(PropertyInfo propertyInfo)
            {
                _propertyInfo = propertyInfo;
            }

            public void Declare(object stateMachine)
            {
                var machine = ((TStateMachine)stateMachine);
                var @event = (Event<TData>)_propertyInfo.GetValue(machine);
                if (@event != null)
                {
                    Type builderType = typeof(CorrelatedByEventCorrelationBuilder<,>).MakeGenericType(typeof(TInstance), typeof(TData));
                    var builder = (EventCorrelationBuilder<TInstance>)Activator.CreateInstance(builderType, machine, @event);

                    machine._eventCorrelations[@event] = builder.Build();
                }
            }
        }


        interface StateMachineRegistration
        {
            void Declare(object stateMachine);
        }
    }
}