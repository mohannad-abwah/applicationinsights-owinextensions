using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Owin;
using System.Diagnostics.Tracing;

namespace ApplicationInsights.OwinExtensions
{
    public class HttpRequestTrackingMiddleware : OwinMiddleware
    {
        private readonly TelemetryClient _client;
        private readonly RequestTrackingConfiguration _configuration;

        [Obsolete("Use the overload accepting RequestTrackingConfiguration")]
        public HttpRequestTrackingMiddleware(
            OwinMiddleware next, 
            TelemetryConfiguration configuration = null, 
            Func<IOwinRequest, IOwinResponse, bool> shouldTraceRequest = null, 
            Func<IOwinRequest, IOwinResponse, KeyValuePair<string,string>[]> getContextProperties = null) : 
            this(next, new RequestTrackingConfiguration
            {
                TelemetryConfiguration = configuration,
                ShouldTrackRequest = shouldTraceRequest != null ? (IOwinContext cts) => shouldTraceRequest(cts.Request, cts.Response) : (Func<IOwinContext, bool>) null,
                GetAdditionalContextProperties = getContextProperties != null ? (IOwinContext ctx) => getContextProperties(ctx.Request, ctx.Response).AsEnumerable()
                    : (Func<IOwinContext, IEnumerable<KeyValuePair<string, string>>>) null,
            })
        {
        }

        public HttpRequestTrackingMiddleware(
            OwinMiddleware next,
            RequestTrackingConfiguration configuration = null) : base(next)
        {
            _configuration = configuration ?? new RequestTrackingConfiguration();

            _configuration.ShouldTrackRequest = _configuration.ShouldTrackRequest ?? (ctx => true);

            _configuration.GetAdditionalContextProperties = _configuration.GetAdditionalContextProperties ?? 
                (ctx => Enumerable.Empty<KeyValuePair<string, string>>());

            _client = _configuration.TelemetryConfiguration != null 
                ? new TelemetryClient(_configuration.TelemetryConfiguration) 
                : new TelemetryClient();
        }

        public override async Task Invoke(IOwinContext context)
        {
            if (!_configuration.ShouldTrackRequest(context))
            {
                await Next.Invoke(context);
                return;
            }

            // following request properties have to be accessed before other middlewares run
            // otherwise access could result in ObjectDisposedException
            var requestTelemetry = TrackRequest(context);
            IOperationHolder<RequestTelemetry> operation = null;

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            operation = _client.StartOperation(requestTelemetry);
            try
            {
                await Next.Invoke(context);
            }
            catch (Exception e)
            {
                TraceException(e);

                operation.Telemetry.ResponseCode = HttpStatusCode.InternalServerError.ToString();

                throw;
            }
            finally
            {
                stopWatch.Stop();

                operation.Telemetry.Duration = stopWatch.Elapsed;
                operation.Telemetry.Success = context.Response.StatusCode < 400;
                operation.Telemetry.ResponseCode = operation.Telemetry.ResponseCode ?? context.Response.StatusCode.ToString();

                _client.StopOperation(operation);
                operation.Dispose();
            }
        }

        private RequestTelemetry TrackRequest(IOwinContext context)
        {
            var method = context.Request.Method;
            var path = context.Request.Path.ToString();
            var uri = context.Request.Uri;

            var requestStartDate = DateTimeOffset.Now;

            var name = $"{method} {path}";

            var telemetry = new RequestTelemetry()
            {
                Id = OperationIdContext.Get(),
                HttpMethod = method,
                Url = uri
            };
            telemetry.Name = name;
            telemetry.Timestamp = DateTimeOffset.Now;

            telemetry.Context.Operation.Name = name;

            foreach (var kvp in _configuration.GetAdditionalContextProperties(context))
                telemetry.Context.Properties.Add(kvp);

            return telemetry;
        }

        private void TraceException(Exception e)
        {
            var telemetry = new ExceptionTelemetry(e);
            telemetry.Context.Operation.Id = OperationIdContext.Get();

            _client.TrackException(telemetry);
        }
    }

}
