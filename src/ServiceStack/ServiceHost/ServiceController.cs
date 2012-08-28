using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using ServiceStack.Configuration;
using ServiceStack.Logging;
using ServiceStack.ServiceModel.Serialization;
using ServiceStack.Text;
using ServiceStack.WebHost.Endpoints;
using ServiceStack.WebHost.Endpoints.Support;

namespace ServiceStack.ServiceHost
{
	public class ServiceController
		: IServiceController
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof(ServiceController));
		private const string ResponseDtoSuffix = "Response";

		public ServiceController()
		{
			this.RequestServiceTypeMap = new Dictionary<Type, Type>();
			this.ResponseServiceTypeMap = new Dictionary<Type, Type>();
			this.OperationTypes = new List<Type>();
            this.RequestTypes = new List<Type>();
			this.RequestTypeFactoryMap = new Dictionary<Type, Func<IHttpRequest, object>>();
            this.ResponseBinders = new Dictionary<Type, ResponseBinderFn>();
            this.AsyncResponseBinders = new List<AsyncResponseBinderFn>();
			this.ServiceTypes = new HashSet<Type>();
		}

		readonly Dictionary<Type, ExecuteServiceFn> requestExecMap =new Dictionary<Type, ExecuteServiceFn>();

        readonly Dictionary<Type, ExecuteOneWayServiceFn> requestExecOneWayMap = new Dictionary<Type, ExecuteOneWayServiceFn>();

		public Dictionary<Type, Type> ResponseServiceTypeMap { get; set; }

		public Dictionary<Type, Type> RequestServiceTypeMap { get; set; }

        public IList<Type> RequestTypes { get; protected set; }

		public IList<Type> OperationTypes { get; protected set; }

		public Dictionary<Type, Func<IHttpRequest, object>> RequestTypeFactoryMap { get; set; }

        public Dictionary<Type, ResponseBinderFn> ResponseBinders { get; set; }

        public List<AsyncResponseBinderFn> AsyncResponseBinders { get; set; }

		public HashSet<Type> ServiceTypes { get; protected set; }

		public void RegisterServices(ITypeFactory serviceFactory, IEnumerable<Type> serviceTypes)
		{
			foreach (var serviceType in serviceTypes)
				RegisterService(serviceFactory, serviceType);
		}

		public void RegisterService(ITypeFactory serviceFactory, Type serviceType)
		{
			if (serviceType.IsAbstract || serviceType.ContainsGenericParameters) return;

            var genericInterfaces = serviceType.GetInterfaces()
                .Where(x => x.IsGenericType);

            var services = genericInterfaces
                .Where(x => IsValidService(x.GetGenericTypeDefinition()))
                .GroupBy(x => x.GetGenericArguments()[0]);

            var oneWayServiceRequestTypes = genericInterfaces
                .Where(x => x.GetGenericTypeDefinition() == typeof(IOneWayService<>))
                .Select(x => x.GetGenericArguments()[0])
                .ToList();

            foreach (var service in services)
            {
                var requestType = service.Key;
                var executeServiceFn = GetExecuteServiceFn(requestType, serviceType, serviceFactory);

                ExecuteOneWayServiceFn executeOneWayServiceFn = null;
                var oneWayService = oneWayServiceRequestTypes.FirstOrDefault(x => x == requestType);
                if (oneWayService != null)
                {
                    executeOneWayServiceFn = GetExecuteOneWayServiceFn(requestType, serviceType, serviceFactory);
                    oneWayServiceRequestTypes.Remove(oneWayService);
                }

                RegisterService(requestType, serviceType, executeServiceFn, executeOneWayServiceFn);
            }

            foreach (var oneWayServiceRequestType in oneWayServiceRequestTypes)
            {
                var executeOneWayServiceFn = GetExecuteOneWayServiceFn(oneWayServiceRequestType, serviceType, serviceFactory);
                RegisterService(oneWayServiceRequestType, serviceType, null, executeOneWayServiceFn); 
            }
		}

        private static bool IsValidService(Type genericDefinition)
        {
            return genericDefinition == typeof(IService<>)
                || genericDefinition == typeof(IRestGetService<>)
                || genericDefinition == typeof(IRestPostService<>)
                || genericDefinition == typeof(IRestPutService<>)
                || genericDefinition == typeof(IRestDeleteService<>)
                || genericDefinition == typeof(IRestPatchService<>);
        }

		private ExecuteServiceFn GetExecuteServiceFn(Type requestType, Type serviceType, ITypeFactory serviceFactory)
		{
            var callServiceFn = GetCallServiceFn(requestType, serviceType);

            ExecuteServiceFn handlerFn = (requestContext, request, callback) =>
            {
                var service = serviceFactory.CreateInstance(serviceType);
                using (service as IDisposable) // using is happy if this expression evals to null
                {
                    InjectRequestContext(service, requestContext);

                    var endpointAttrs = requestContext != null
                        ? requestContext.EndpointAttributes
                        : EndpointAttributes.None;

                    //Executes the service and returns the result
                    var response = callServiceFn(request, service, endpointAttrs);
                    response = ExecuteResponseBinders(service, request, response);

                    //Wrap the original callback into another callback to release the service instance _after_ the service has executed
                    Action<IServiceResult> cb = r =>
                    {
                        if (EndpointHost.AppHost != null) //tests
                            EndpointHost.AppHost.Release(service);

                        callback(r);
                    };

                    IServiceResult serviceResult = this.ExecuteAsyncResponseBinders(service, request, response, cb);
                    if (serviceResult != null)
                        return serviceResult;

                    serviceResult = new SyncServiceResult(response);
                    cb(serviceResult);
                    return serviceResult;
                }
            };

            return handlerFn;
		}

        private ExecuteOneWayServiceFn GetExecuteOneWayServiceFn(Type requestType, Type serviceType, ITypeFactory serviceFactory)
        {
            var callOneWayServiceFn = GetCallOneWayServiceFn(requestType);

            ExecuteOneWayServiceFn oneWayHandlerFn = (requestContext, request) =>
            {
                var service = serviceFactory.CreateInstance(serviceType);
                using (service as IDisposable)
                {
                    InjectRequestContext(service, requestContext);
                    callOneWayServiceFn(service, request);
                }
            };

            return oneWayHandlerFn;
        }

		private object ExecuteResponseBinders(object service, object request, object response)
		{
			if (response != null)
			{
                var responseType = response.GetType();

                ResponseBinderFn responseBinder;
                if (ResponseBinders.TryGetValue(responseType, out responseBinder))
                    return responseBinder(service, request, response);
			}
			return response;
		}

        private IServiceResult ExecuteAsyncResponseBinders(object service, object request, object response, Action<IServiceResult> callback)
        {
            if (response != null)
            {
                var responseType = response.GetType();
                foreach (var asyncResponseBinder in AsyncResponseBinders)
                {
                    var result = asyncResponseBinder(service, request, response, callback);
                    if (result != null)
                        return result;
                }
            }
            return null;
        }

		private void RegisterService(Type requestType, Type serviceType, ExecuteServiceFn handlerFn, ExecuteOneWayServiceFn oneWayHandlerFn)
		{
			try
			{
                if(handlerFn != null)
                    requestExecMap.Add(requestType, handlerFn);

                if(oneWayHandlerFn != null)
                    requestExecOneWayMap.Add(requestType, oneWayHandlerFn);
			}
			catch (ArgumentException)
			{
				throw new AmbiguousMatchException(
					string.Format("Could not register the service '{0}' as another service with the definition of type 'IService<{1}>' already exists.",
					serviceType.FullName, requestType.Name));
			}

			EndpointHost.RestController.RegisterRestPaths(requestType);
			this.ServiceTypes.Add(serviceType);

			this.RequestServiceTypeMap[requestType] = serviceType;
            this.RequestTypes.Add(requestType);
			this.OperationTypes.Add(requestType);

			var responseTypeName = requestType.FullName + ResponseDtoSuffix;
			var responseType = AssemblyUtils.FindType(responseTypeName);
			if (responseType != null)
			{
				this.ResponseServiceTypeMap[responseType] = serviceType;
				this.OperationTypes.Add(responseType);
			}

			if (typeof(IRequiresRequestStream).IsAssignableFrom(requestType))
			{
				this.RequestTypeFactoryMap[requestType] = httpReq =>
				{
					var rawReq = (IRequiresRequestStream)requestType.CreateInstance();
					rawReq.RequestStream = httpReq.InputStream;
					return rawReq;
				};
			}
		}

		private static void InjectRequestContext(object service, IRequestContext requestContext)
		{
			if (requestContext == null) return;

			var serviceRequiresContext = service as IRequiresRequestContext;
			if (serviceRequiresContext != null)
			{
				serviceRequiresContext.RequestContext = requestContext;
			}

			var servicesRequiresHttpRequest = service as IRequiresHttpRequest;
			if (servicesRequiresHttpRequest != null)
				servicesRequiresHttpRequest.HttpRequest = requestContext.Get<IHttpRequest>();
		}

		private static Func<object, object, EndpointAttributes, object> GetCallServiceFn(
			Type requestType, Type serviceType)
		{
			try
			{
				var requestDtoParam = Expression.Parameter(typeof(object), "requestDto");
				var requestDtoStrong = Expression.Convert(requestDtoParam, requestType);

				var serviceParam = Expression.Parameter(typeof(object), "serviceObj");

				var attrsParam = Expression.Parameter(typeof(EndpointAttributes), "attrs");

				var mi = ServiceExec.GetExecMethodInfo(serviceType, requestType);

				Expression callExecute = Expression.Call(
					mi, new Expression[] { serviceParam, requestDtoStrong, attrsParam });

				var executeFunc = Expression.Lambda<Func<object, object, EndpointAttributes, object>>
					(callExecute, requestDtoParam, serviceParam, attrsParam).Compile();

				return executeFunc;

			}
			catch (Exception)
			{
				//problems with MONO, using reflection for temp fix
				return delegate(object request, object service, EndpointAttributes attrs)
				{
					var mi = ServiceExec.GetExecMethodInfo(serviceType, requestType);
					return mi.Invoke(null, new[] { service, request, attrs });
				};
			}
		}

        private static Action<object, object> GetCallOneWayServiceFn(Type requestType)
        {
            var requestDtoParam = Expression.Parameter(typeof(object), "requestDto");
            var requestDtoStrong = Expression.Convert(requestDtoParam, requestType);

            var serviceParam = Expression.Parameter(typeof(object), "serviceObj");

            var oneWayServiceType = typeof(IOneWayService<>).MakeGenericType(requestType);
            var serviceStrong = Expression.Convert(serviceParam, oneWayServiceType);

            var executeOneWayMethod = oneWayServiceType.GetMethod("ExecuteOneWay");
            var executeExpression = Expression.Call(serviceStrong, executeOneWayMethod, requestDtoStrong);

            return Expression.Lambda<Action<object, object>>(executeExpression, serviceParam, requestDtoParam).Compile(); ;
        }

		public object Execute(object request, IRequestContext requestContext = null)
		{
			var serviceResult = this.ExecuteAsync(request, r => { }, requestContext);
			return serviceResult.Result; //Blocking call
		}

		public IServiceResult ExecuteAsync(object request, Action<IServiceResult> callback, IRequestContext requestContext = null)
		{
			var requestType = request.GetType();
			var handlerFn = GetService(requestType);
			return handlerFn(requestContext, request, callback);
		}

		public ExecuteServiceFn GetService(Type requestType)
		{
			ExecuteServiceFn handlerFn;
			if (!requestExecMap.TryGetValue(requestType, out handlerFn))
			{
				throw new NotImplementedException(
						string.Format("Unable to resolve service '{0}'", requestType.Name));
			}

			return handlerFn;
		}

        public void ExecuteOneWay(object request, IRequestContext requestContext = null)
        {
            var requestType = request.GetType();
            var oneWayHandlerFn = GetOneWayService(requestType);

            oneWayHandlerFn(requestContext, request);
        }

        public ExecuteOneWayServiceFn GetOneWayService(Type requestType)
        {
            ExecuteOneWayServiceFn oneWayHandlerFn;
            if (!requestExecOneWayMap.TryGetValue(requestType, out oneWayHandlerFn))
                throw new NotImplementedException(string.Format("Unable to resolve '{0}' IOneWayService", requestType.Name));

            return oneWayHandlerFn;
        }
    }

}