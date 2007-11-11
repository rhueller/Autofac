﻿// This software is part of the Autofac IoC container
// Copyright (c) 2007 Nicholas Blumhardt
// nicholas.blumhardt@gmail.com
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Reflection;
using System.Linq;

namespace Autofac
{
    /// <summary>
    /// Standard container implementation.
    /// </summary>
	public class Container : Disposable, IContainer, IContext
    {
        #region Fields

        /// <summary>
        /// Protects instance variables from concurrent access.
        /// </summary>
        object _synchRoot = new object();

        /// <summary>
        /// Tracks all registrations by name.
        /// </summary>
        IDictionary<string, IComponentRegistration> _allServiceRegistrations = new Dictionary<string, IComponentRegistration>();

		/// <summary>
		/// Supports nested containers.
		/// </summary>
		Container _outerContainer;

		/// <summary>
		/// External registration sources.
		/// </summary>
		IList<IRegistrationSource> _registrationSources = new List<IRegistrationSource>();

        /// <summary>
        /// Disposer that handles disposal of instances attached to the container.
        /// </summary>
        IDisposer _disposer = new Disposer();

        #endregion

        #region Initialisation

        /// <summary>
		/// Create a new container.
		/// </summary>
		public Container() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Container"/> class.
        /// </summary>
        /// <param name="outerScope">The outer scope.</param>
		protected Container(Container outerScope)
		: this()
		{
            Enforce.ArgumentNotNull(outerScope, "outerScope");

			_outerContainer = outerScope;
		}

        /// <summary>
        /// Begin a new sub-context. Contextual and transient instances created inside
        /// the subcontext will be disposed along with it.
        /// </summary>
        /// <returns>A new subcontext.</returns>
        public Container CreateInnerContainer()
        {
            var result = new Container(this);
            result.Activating += ComponentActivating;
            result.Activated += ComponentActivated;
            return result;
        }

		#endregion

        #region Registration

        /// <summary>
		/// Register a component.
		/// </summary>
		/// <param name="registration">A component registration.</param>
		public void RegisterComponent(IComponentRegistration registration)
		{
            Enforce.ArgumentNotNull(registration, "registration");

            lock (_synchRoot)
            {
                if (_allServiceRegistrations.ContainsKey(registration.Name))
                    throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                        ContainerResources.RegistrationNameNotUnique, registration.Name));

                _allServiceRegistrations.Add(registration.Name, registration);

                IList<Type> typesSeen = new List<Type>();
                foreach (Type serviceType in registration.Services)
                {
                    if (serviceType == null)
                        throw new ArgumentException(ContainerResources.ServiceTypesCannotBeNull);

                    if (typesSeen.Contains(serviceType))
                        throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                            ContainerResources.ComponentExposesServiceMultipleTimes, serviceType));

                    typesSeen.Add(serviceType);

                    AddServiceRegistration(registration, serviceType);
                }

                registration.Activating += ComponentActivating;
                registration.Activated += ComponentActivated;
            }
		}

        /// <summary>
        /// Associate a registration with a service that it provides.
        /// </summary>
        /// <param name="registration">The registration.</param>
        /// <param name="serviceType">Type of the service.</param>
        void AddServiceRegistration(IComponentRegistration registration, Type serviceType)
        {
            Enforce.ArgumentNotNull(registration, "registration");
            Enforce.ArgumentNotNull(serviceType, "serviceType");

            lock (_synchRoot)
            {
                CheckNotDisposed();

                _allServiceRegistrations[ServiceKeyGenerator.GenerateKey(serviceType)] = registration;
            }
        }

		/// <summary>
		/// Add a source from which registrations may be retrieved in the case that they
		/// are not available in the container.
		/// </summary>
		/// <param name="registrationSource"></param>
		internal void AddRegistrationSource(IRegistrationSource registrationSource)
		{
            Enforce.ArgumentNotNull(registrationSource, "registrationSource");
			_registrationSources.Add(registrationSource);
		}

        #endregion

        #region IContainer Support

        bool IContainer.TryGetRegistration(string key, out IComponentRegistration registration, out IDisposer disposer, out IContext context)
        {
            Enforce.ArgumentNotNullOrEmpty(key, "key");
            context = null;

            lock (_synchRoot)
            {
                CheckNotDisposed();

                if (_allServiceRegistrations.TryGetValue(key, out registration) ||
                    TryGetRegistrationFromSources(key, out registration))
                {
                    disposer = _disposer;
                    return true;
                }

                if (_outerContainer == null)
                {
                    disposer = null;
                    return false;
                }

                // Parent is non-null otherwise the registration check would have failed above
                if (_outerContainer.TryExportToNewContext(key, out registration))
                {
                    disposer = _disposer;
                    RegisterComponent(registration);
                    return true;
                }

                if (((IContainer)_outerContainer).TryGetRegistration(key, out registration, out disposer, out context))
                {
                    if (context == null)
                        context = _outerContainer;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Searches for an IRegistrationSource that can provide a registration for an
        /// unregistered service, and completes the registration process if possible.
        /// </summary>
        /// <param name="service">The requested service.</param>
        /// <returns>True if a registration was provided, otherwise, false.</returns>
        bool TryGetRegistrationFromSources(string key, out IComponentRegistration registration)
        {
            Enforce.ArgumentNotNull(key, "key");

            registration = null;

            Type service;
            if (!ServiceKeyGenerator.TryGetService(key, out service))
                return false;

            foreach (IRegistrationSource registrationSource in _registrationSources)
            {
                if (registrationSource.TryGetRegistration(service, out registration))
                {
                    bool supported = false;
                    foreach (Type provided in registration.Services)
                    {
                        if (provided == service)
                        {
                            supported = true;
                            break;
                        }
                    }

                    if (!supported)
                    {
                        registration.Dispose();
                        throw new ArgumentException(ContainerResources.RequiredServiceNotSupported);
                    }

                    RegisterComponent(registration);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Create an IComponentRegistration for a new subcontext if available.
        /// </summary>
        /// <param name="key">The service that was requested. Note that any
        /// additional services provided by the component will also be exported.</param>
        /// <param name="registration">The new registration.</param>
        /// <returns>True if the new context could be supported.</returns>
        bool TryExportToNewContext(string key, out IComponentRegistration registration)
        {
            Enforce.ArgumentNotNullOrEmpty(key, "key");

            lock (_synchRoot)
            {
                IComponentRegistration localRegistration;
                if (_allServiceRegistrations.TryGetValue(key, out localRegistration))
                {
                    return localRegistration.TryDuplicateForNewContext(out registration);
                }
                else if (_outerContainer != null)
                {
                    return _outerContainer.TryExportToNewContext(key, out registration);
                }
                else
                {
                    registration = null;
                    return false;
                }
            }
        }

        #endregion

        #region Event Support

        /// <summary>
		/// Fired when a new instance is being activated. The instance can be
		/// wrapped or switched at this time by setting the Instance property in
		/// the provided event arguments.
		/// </summary>
		public event EventHandler<ActivatingEventArgs> Activating = (sender, e) => { };

		/// <summary>
		/// Fired when the activation process for a new instance is complete.
		/// </summary>
		public event EventHandler<ActivatedEventArgs> Activated = (sender, e) => { };

        void ComponentActivating(object sender, ActivatingEventArgs e)
        {
            // The context will always be locked at this point.
            Activating(this, e);
        }

        void ComponentActivated(object sender, ActivatedEventArgs e)
        {
            // The context will always be locked at this point.
            Activated(this, e);
        }

		#endregion

        #region IDisposable Support

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                lock (_synchRoot)
                {
                    foreach (IComponentRegistration registration in _allServiceRegistrations.Values.Distinct())
                        registration.Dispose();

                    _disposer.Dispose();
                }
        }

        #endregion

        #region IContext Support

        IContext CreateResolutionContext()
        {
            return new Context(this);
        }

        /// <summary>
        /// Retrieve a service registered with the container.
        /// </summary>
        /// <typeparam name="TService">The service to retrieve.</typeparam>
        /// <param name="parameters"></param>
        /// <returns>
        /// The component instance that provides the service.
        /// </returns>
        /// <exception cref="ComponentNotRegisteredException"/>
        /// <exception cref="DependencyResolutionException"/>
        public TService Resolve<TService>(params Parameter[] parameters)
        {
            return CreateResolutionContext().Resolve<TService>(parameters);
        }

        /// <summary>
        /// Retrieve a service registered with the container.
        /// </summary>
        /// <param name="serviceType">The service to retrieve.</param>
        /// <param name="parameters"></param>
        /// <returns>
        /// The component instance that provides the service.
        /// </returns>
        /// <exception cref="ComponentNotRegisteredException"/>
        /// <exception cref="DependencyResolutionException"/>
        public object Resolve(Type serviceType, params Parameter[] parameters)
        {
            return CreateResolutionContext().Resolve(serviceType, parameters);
        }

        /// <summary>
        /// Retrieve a service registered with the container.
        /// </summary>
        /// <typeparam name="TService">The service to retrieve.</typeparam>
        /// <param name="instance">The component instance that provides the service.</param>
        /// <param name="parameters"></param>
        /// <returns>
        /// True if the service was registered and its instance created;
        /// false otherwise.
        /// </returns>
        /// <exception cref="DependencyResolutionException"/>
        public bool TryResolve<TService>(out TService instance, params Parameter[] parameters)
        {
            return CreateResolutionContext().TryResolve<TService>(out instance, parameters);
        }

        /// <summary>
        /// Retrieve a service registered with the container.
        /// </summary>
        /// <param name="serviceType">The service to retrieve.</param>
        /// <param name="instance">The component instance that provides the service.</param>
        /// <param name="parameters"></param>
        /// <returns>
        /// True if the service was registered and its instance created;
        /// false otherwise.
        /// </returns>
        /// <exception cref="DependencyResolutionException"/>
        public bool TryResolve(Type serviceType, out object instance, params Parameter[] parameters)
        {
            return CreateResolutionContext().TryResolve(serviceType, out instance, parameters);
        }

        /// <summary>
        /// Retrieve a service registered with the container.
        /// </summary>
        /// <typeparam name="TService">The service to retrieve.</typeparam>
        /// <param name="parameters"></param>
        /// <returns>
        /// The component instance that provides the service, or null if
        /// none is available.
        /// </returns>
        /// <remarks>Useful with the C#3 initialiser syntax.</remarks>
        /// <example>
        /// container.Register&lt;ISomething&gt;(c =&gt; new Something(){ AProperty = c.ResolveOptional&lt;IOptional&gt;() });
        /// </example>
        public TService ResolveOptional<TService>(params Parameter[] parameters)
        {
            return CreateResolutionContext().ResolveOptional<TService>(parameters);
        }

        /// <summary>
        /// Determines whether the specified service is registered.
        /// </summary>
        /// <param name="service">The service.</param>
        /// <returns>
        /// 	<c>true</c> if the specified service is registered; otherwise, <c>false</c>.
        /// </returns>
        public bool IsRegistered(Type service)
        {
            return CreateResolutionContext().IsRegistered(service);
        }

        /// <summary>
        /// Determine whether or not a service has been registered.
        /// </summary>
        /// <typeparam name="TService">The service to test for the registration of.</typeparam>
        /// <returns>True if the service is registered.</returns>
        public bool IsRegistered<TService>()
        {
            return CreateResolutionContext().IsRegistered<TService>();
        }

        /// <summary>
        /// Set any properties on <paramref name="instance"/> that can be
        /// resolved by the container. (Generally use <see cref="InjectUnsetProperties"/>
        /// unless you're using the Null Object pattern for unset dependencies.)
        /// </summary>
        /// <typeparam name="T">Type of instance. Used only to provide method chaining.</typeparam>
        /// <param name="instance">The instance to inject properties into.</param>
        /// <returns><paramref name="instance"/>.</returns>
        public T InjectProperties<T>(T instance)
        {
            return CreateResolutionContext().InjectProperties<T>(instance);
        }

        /// <summary>
        /// Set any null-valued properties on <paramref name="instance"/> that can be
        /// resolved by the container.
        /// </summary>
        /// <typeparam name="T">Type of instance. Used only to provide method chaining.</typeparam>
        /// <param name="instance">The instance to inject properties into.</param>
        /// <returns><paramref name="instance"/>.</returns>
        public T InjectUnsetProperties<T>(T instance)
        {
            return CreateResolutionContext().InjectUnsetProperties<T>(instance);
        }

        /// <summary>
        /// Retrieve a service registered with the container.
        /// </summary>
        /// <param name="componentName">The name of the component to retrieve.</param>
        /// <param name="instance">The component instance that provides the service.</param>
        /// <param name="parameters"></param>
        /// <returns>
        /// True if the service was registered and its instance created;
        /// false otherwise.
        /// </returns>
        /// <exception cref="DependencyResolutionException"/>
        public bool TryResolve(string componentName, out object instance, params Parameter[] parameters)
        {
            return CreateResolutionContext().TryResolve(componentName, out instance, parameters);
        }

        #endregion
    }
}
