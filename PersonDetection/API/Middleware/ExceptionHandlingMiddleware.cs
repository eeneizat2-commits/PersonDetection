namespace PersonDetection.API.Middleware
{
    using System.Net;
    using System.Text.Json;

    public class ExceptionHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionHandlingMiddleware> _logger;

        public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client disconnected/navigated away — not an error
                _logger.LogDebug("Request cancelled by client: {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 499;
                }
            }
            catch (OperationCanceledException)
            {
                // App shutting down or internal cancellation
                // Also catches TaskCanceledException (subclass)
                _logger.LogWarning("Operation cancelled: {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        status = 503,
                        message = "Service temporarily unavailable. Please retry."
                    }));
                }
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == -2)
            {
                // SQL Timeout
                _logger.LogWarning("SQL timeout on {Method} {Path}: {Message}",
                    context.Request.Method, context.Request.Path, ex.Message);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.GatewayTimeout;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        status = 504,
                        message = "Database operation timed out. Please retry."
                    }));
                }
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Database update error on {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        status = 409,
                        message = "Database conflict. Please retry.",
                        details = ex.InnerException?.Message ?? ex.Message
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                if (!context.Response.HasStarted)
                {
                    await HandleExceptionAsync(context, ex);
                }
            }
        }

        private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            var response = new
            {
                status = context.Response.StatusCode,
                message = "An error occurred processing your request.",
                details = exception.Message
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
        }
    }
}