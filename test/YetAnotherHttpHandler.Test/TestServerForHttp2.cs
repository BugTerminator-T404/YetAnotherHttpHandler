using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TestWebApp;

namespace _YetAnotherHttpHandler.Test;

class TestServerForHttp2 : ITestServerBuilder
{
    public static WebApplication BuildApplication(WebApplicationBuilder builder)
    {
        var app = builder.Build();

        app.MapGet("/", () => Results.Content("__OK__"));
        app.MapGet("/not-found", () => Results.Content("__Not_Found__", statusCode: 404));
        app.MapGet("/response-headers", (HttpContext httpContext) =>
        {
            httpContext.Response.Headers["x-test"] = "foo";
            return Results.Content("__OK__");
        });
        app.MapPost("/post-echo", async (HttpContext httpContext, Stream bodyStream) =>
        {
            httpContext.Response.Headers["x-request-content-type"] = httpContext.Request.ContentType;

            var memStream = new MemoryStream();
            await bodyStream.CopyToAsync(memStream);

            return Results.Bytes(memStream.ToArray(), "application/octet-stream");
        });
        app.MapPost("/post-streaming", async (HttpContext httpContext, PipeReader reader) =>
        {
            // Send status code and response headers.
            httpContext.Response.Headers["x-header-1"] = "foo";
            httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            await httpContext.Response.BodyWriter.FlushAsync();

            var readLen = 0L;
            while (true)
            {
                var readResult = await reader.ReadAsync();
                readLen += readResult.Buffer.Length;
                reader.AdvanceTo(readResult.Buffer.End);
                if (readResult.IsCompleted || readResult.IsCanceled) break;
            }

            await httpContext.Response.WriteAsync(readLen.ToString());

            return Results.Empty;
        });
        app.MapPost("/post-response-trailers", (HttpContext httpContext) =>
        {
            httpContext.Response.AppendTrailer("x-trailer-1", "foo");
            httpContext.Response.AppendTrailer("x-trailer-2", "bar");

            return Results.Ok("__OK__");
        });
        app.MapPost("/post-response-headers-immediately", async (HttpContext httpContext, PipeReader reader) =>
        {
            // Send status code and response headers.
            httpContext.Response.Headers["x-header-1"] = "foo";
            httpContext.Response.StatusCode = (int)HttpStatusCode.Accepted;
            await httpContext.Response.BodyWriter.FlushAsync();

            await Task.Delay(100000);
            await httpContext.Response.WriteAsync("__OK__");
            return Results.Empty;
        });
        app.MapPost("/post-abort-while-reading", async (HttpContext httpContext, PipeReader reader) =>
        {
            var readResult = await reader.ReadAsync();
            reader.AdvanceTo(readResult.Buffer.End);
            httpContext.Abort();

            return Results.Empty;
        });
        app.MapPost("/post-null", async (HttpContext httpContext, PipeReader reader) =>
        {
            while (!httpContext.RequestAborted.IsCancellationRequested)
            {
                var readResult = await reader.ReadAsync();
                reader.AdvanceTo(readResult.Buffer.End);
                if (readResult.IsCompleted || readResult.IsCanceled) break;
            }
            return Results.Empty;
        });
        app.MapPost("/post-null-duplex", async (HttpContext httpContext, PipeReader reader) =>
        {
            // Send status code and response headers.
            httpContext.Response.Headers["x-header-1"] = "foo";
            httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            await httpContext.Response.BodyWriter.FlushAsync();

            while (!httpContext.RequestAborted.IsCancellationRequested)
            {
                var readResult = await reader.ReadAsync();
                reader.AdvanceTo(readResult.Buffer.End);
                if (readResult.IsCompleted || readResult.IsCanceled) break;
            }
            return Results.Empty;
        });

        // HTTP/2
        app.MapGet("/error-reset", (HttpContext httpContext) =>
        {
            // https://learn.microsoft.com/ja-jp/aspnet/core/fundamentals/servers/kestrel/http2?view=aspnetcore-7.0#reset-1
            var resetFeature = httpContext.Features.Get<IHttpResetFeature>();
            resetFeature!.Reset(errorCode: 2); // INTERNAL_ERROR
            return Results.Empty;
        });

        // gRPC
        app.MapGrpcService<GreeterService>();

        return app;
    }

    class GreeterService : Greeter.GreeterBase
    {
#pragma warning disable CS1998
        public override async Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        {
            return new HelloReply { Message = $"Hello {request.Name}" };
        }

        public override async Task SayHelloDuplex(IAsyncStreamReader<HelloRequest> requestStream, IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(new HelloReply { Message = $"Hello {request.Name}" });
            }
        }

        public override async Task SayHelloDuplexCompleteRandomly(IAsyncStreamReader<HelloRequest> requestStream, IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(new HelloReply { Message = request.Name });
                if (Random.Shared.Next(0, 9) == 0)
                {
                    return;
                }
            }
        }

        public override async Task SayHelloDuplexAbortRandomly(IAsyncStreamReader<HelloRequest> requestStream, IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(new HelloReply { Message = request.Name });
                if (Random.Shared.Next(0, 9) == 0)
                {
                    context.GetHttpContext().Abort();
                    return;
                }
            }
        }

        public override async Task<ResetReply> ResetByServer(ResetRequest request, ServerCallContext context)
        {
            context.GetHttpContext().Features.GetRequiredFeature<IHttpResetFeature>().Reset(errorCode: request.ErrorCode); 
            return new ResetReply {  };
        }

        public override async Task EchoDuplex(IAsyncStreamReader<EchoRequest> requestStream, IServerStreamWriter<EchoReply> responseStream, ServerCallContext context)
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(new EchoReply() { Message = request.Message });
            }
        }
#pragma warning restore CS1998
    }

}