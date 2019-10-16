namespace Carter
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using Carter.OpenApi;
    using FluentValidation;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Routing;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using static OpenApi.CarterOpenApi;

    public static class CarterExtensions
    {
        /// <summary>
        /// Adds Carter to the specified <see cref="IApplicationBuilder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IApplicationBuilder"/> to configure.</param>
        /// <param name="options">A <see cref="CarterOptions"/> instance.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IEndpointConventionBuilder MapCarter(this IEndpointRouteBuilder builder, CarterOptions options = null)
        {
            var carterConfigurator = builder.ServiceProvider.GetService<CarterConfigurator>();

            var loggerFactory = builder.ServiceProvider.GetService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger(typeof(CarterConfigurator));

            carterConfigurator.LogDiscoveredCarterTypes(logger);

            var builders = new List<IEndpointConventionBuilder>();

            var routeMetaData = new Dictionary<(string verb, string path), RouteMetaData>();

            //Create a "startup scope" to resolve modules from
            using (var scope = builder.ServiceProvider.CreateScope())
            {
                var statusCodeHandlers = scope.ServiceProvider.GetServices<IStatusCodeHandler>().ToList();

                //Get all instances of CarterModule to fetch and register declared routes
                foreach (var module in scope.ServiceProvider.GetServices<CarterModule>())
                {
                    var moduleLogger = scope.ServiceProvider
                        .GetService<ILoggerFactory>()
                        .CreateLogger(module.GetType());

                    routeMetaData = routeMetaData.Concat(module.RouteMetaData).ToDictionary(x => x.Key, x => x.Value);

                    foreach (var route in module.Routes)
                    {
                        var conventionBuilder = builder.MapMethods(route.Key.path, new[] { route.Key.verb },
                            CreateRouteHandler(route.Key.path, module.GetType(), statusCodeHandlers, moduleLogger));

                        if (module.AuthPolicies.Any())
                        {
                            conventionBuilder.RequireAuthorization(module.AuthPolicies);
                        }
                        else if (module.RequiresAuth)
                        {
                            conventionBuilder.RequireAuthorization();
                        }

                        route.Value.conventions.Apply(conventionBuilder);
                        builders.Add(conventionBuilder);
                    }
                }
            }

            builders.Add(builder.MapGet("openapi", BuildOpenApiResponse(options, routeMetaData)));

            return new CompositeConventionBuilder(builders);
        }

        private static RequestDelegate CreateRouteHandler(
            string path, Type moduleType, IEnumerable<IStatusCodeHandler> statusCodeHandlers, ILogger logger)
        {
            return async ctx =>
            {
                // Now in per-request scope
                var module = ctx.RequestServices.GetRequiredService(moduleType) as CarterModule;

                if (!module.Routes.TryGetValue((ctx.Request.Method, path), out var routeHandler))
                {
                    // if the path was registered but a handler matching the
                    // current method was not found, return MethodNotFound
                    // ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    throw new Exception();
                }

                // run the module handlers
                bool shouldContinue = true;

                if (module.Before != null)
                {
                    foreach (var beforeDelegate in module.Before.GetInvocationList())
                    {
                        var beforeTask = (Task<bool>)beforeDelegate.DynamicInvoke(ctx);
                        shouldContinue = await beforeTask;
                        if (!shouldContinue)
                        {
                            break;
                        }
                    }
                }

                if (shouldContinue)
                {
                    // run the route handler
                    logger.LogDebug("Executing module route handler for {Method} /{Path}", ctx.Request.Method, path);
                    await routeHandler.handler(ctx);

                    // run after handler
                    if (module.After != null)
                    {
                        await module.After(ctx);
                    }
                }

                // run status code handler
                var scHandler = statusCodeHandlers.FirstOrDefault(x => x.CanHandle(ctx.Response.StatusCode));
                if (scHandler != null)
                {
                    await scHandler.Handle(ctx);
                }
            };
        }

        /// <summary>
        /// Adds Carter to the specified <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add Carter to.</param>
        /// <param name="assemblyCatalog">Optional <see cref="DependencyContextAssemblyCatalog"/> containing assemblies to add to the services collection. If not provided, the default catalog of assemblies is added, which includes Assembly.GetEntryAssembly.</param>
        /// <param name="configurator">Optional <see cref="CarterConfigurator"/> to enable registration of specific types within Carter</param>
        public static void AddCarter(this IServiceCollection services, DependencyContextAssemblyCatalog assemblyCatalog = null, Action<CarterConfigurator> configurator = null)
        {
            assemblyCatalog = assemblyCatalog ?? new DependencyContextAssemblyCatalog();

            var config = new CarterConfigurator();
            configurator?.Invoke(config);

            WireupCarter(services, assemblyCatalog, config);
        }

        private static void WireupCarter(this IServiceCollection services, DependencyContextAssemblyCatalog assemblyCatalog, CarterConfigurator carterConfigurator)
        {
            var assemblies = assemblyCatalog.GetAssemblies();

            var validators = GetValidators(carterConfigurator, assemblies);

            var modules = GetModules(carterConfigurator, assemblies);

            var statusCodeHandlers = GetStatusCodeHandlers(carterConfigurator, assemblies);

            var responseNegotiators = GetResponseNegotiators(carterConfigurator, assemblies);

            services.AddSingleton(carterConfigurator);

            foreach (var validator in validators)
            {
                services.AddSingleton(typeof(IValidator), validator);
            }

            services.AddSingleton<IValidatorLocator, DefaultValidatorLocator>();

            services.AddRouting();

            foreach (var module in modules)
            {
                services.AddScoped(module);
                services.AddScoped(typeof(CarterModule), module);
            }

            foreach (var sch in statusCodeHandlers)
            {
                services.AddScoped(typeof(IStatusCodeHandler), sch);
            }

            foreach (var negotiator in responseNegotiators)
            {
                services.AddSingleton(typeof(IResponseNegotiator), negotiator);
            }

            services.AddSingleton<IResponseNegotiator, DefaultJsonResponseNegotiator>();
        }

        private static IEnumerable<Type> GetResponseNegotiators(CarterConfigurator carterConfigurator, IReadOnlyCollection<Assembly> assemblies)
        {
            IEnumerable<Type> responseNegotiators;
            if (!carterConfigurator.ResponseNegotiatorTypes.Any())
            {
                responseNegotiators = assemblies.SelectMany(x => x.GetTypes()
                    .Where(t =>
                        !t.IsAbstract &&
                        typeof(IResponseNegotiator).IsAssignableFrom(t) &&
                        t != typeof(IResponseNegotiator) &&
                        t != typeof(DefaultJsonResponseNegotiator)
                    ));

                carterConfigurator.ResponseNegotiatorTypes.AddRange(responseNegotiators);
            }
            else
            {
                responseNegotiators = carterConfigurator.ResponseNegotiatorTypes;
            }

            return responseNegotiators;
        }

        private static IEnumerable<Type> GetStatusCodeHandlers(CarterConfigurator carterConfigurator, IReadOnlyCollection<Assembly> assemblies)
        {
            IEnumerable<Type> statusCodeHandlers;
            if (!carterConfigurator.StatusCodeHandlerTypes.Any())
            {
                statusCodeHandlers = assemblies.SelectMany(x =>
                    x.GetTypes().Where(t =>
                        typeof(IStatusCodeHandler).IsAssignableFrom(t) &&
                        t != typeof(IStatusCodeHandler)));

                carterConfigurator.StatusCodeHandlerTypes.AddRange(statusCodeHandlers);
            }
            else
            {
                statusCodeHandlers = carterConfigurator.StatusCodeHandlerTypes;
            }

            return statusCodeHandlers;
        }

        private static IEnumerable<Type> GetModules(CarterConfigurator carterConfigurator, IReadOnlyCollection<Assembly> assemblies)
        {
            IEnumerable<Type> modules;
            if (!carterConfigurator.ModuleTypes.Any())
            {
                modules = assemblies.SelectMany(x => x.GetTypes()
                    .Where(t =>
                        !t.IsAbstract &&
                        typeof(CarterModule).IsAssignableFrom(t) &&
                        t != typeof(CarterModule) &&
                        t.IsPublic
                    ));

                carterConfigurator.ModuleTypes.AddRange(modules);
            }
            else
            {
                modules = carterConfigurator.ModuleTypes;
            }

            return modules;
        }

        private static IEnumerable<Type> GetValidators(CarterConfigurator carterConfigurator, IReadOnlyCollection<Assembly> assemblies)
        {
            IEnumerable<Type> validators;
            if (!carterConfigurator.ValidatorTypes.Any())
            {
                validators = assemblies.SelectMany(ass => ass.GetTypes())
                    .Where(typeof(IValidator).IsAssignableFrom)
                    .Where(t => !t.GetTypeInfo().IsAbstract);

                carterConfigurator.ValidatorTypes.AddRange(validators);
            }
            else
            {
                validators = carterConfigurator.ValidatorTypes;
            }

            return validators;
        }

        private class CompositeConventionBuilder : IEndpointConventionBuilder
        {
            private readonly List<IEndpointConventionBuilder> _builders;

            public CompositeConventionBuilder(List<IEndpointConventionBuilder> builders)
            {
                _builders = builders;
            }

            public void Add(Action<EndpointBuilder> convention)
            {
                foreach (var builder in _builders)
                {
                    builder.Add(convention);
                }
            }
        }
    }
}
