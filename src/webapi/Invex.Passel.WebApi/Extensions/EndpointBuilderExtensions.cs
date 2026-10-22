namespace Invex.Passel.WebApi.Extensions;

internal static class EndpointBuilderExtensions
{
    private const string JsonContentType = "application/json";
    private const string ProblemJsonContentType = "application/problem+json";

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    extension(RouteHandlerBuilder builder)
    {
        [PublicAPI]
        public RouteHandlerBuilder Accepts<TRequest>(string contentType, TRequest example)
            where TRequest : notnull =>
            builder.Accepts(contentType, "example", example);

        [PublicAPI]
        public RouteHandlerBuilder Accepts<TRequest>(string contentType, string exampleName, TRequest example)
            where TRequest : notnull =>
            builder
                .Accepts<TRequest>(contentType)
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    if (operation.RequestBody?.Content?.TryGetValue(contentType, out var mediaType) is true)
                        (mediaType.Examples ??= new Dictionary<string, IOpenApiExample>())[exampleName] =
                            new OpenApiExample
                            {
                                Value = JsonSerializer.SerializeToNode(example, JsonSerializerOptions),
                            };

                    return Task.CompletedTask;
                });

        [PublicAPI]
        public RouteHandlerBuilder AcceptsJson<TRequest>(TRequest example)
            where TRequest : notnull =>
            builder.Accepts(JsonContentType, example);

        [PublicAPI]
        public RouteHandlerBuilder AcceptsJson<TRequest>(string exampleName, TRequest example)
            where TRequest : notnull =>
            builder.Accepts(JsonContentType, exampleName, example);

        [PublicAPI]
        public RouteHandlerBuilder Produces<TResponse>(int statusCode, string contentType, TResponse example)
            where TResponse : notnull =>
            builder.Produces(statusCode, contentType, "example", example);

        [PublicAPI]
        public RouteHandlerBuilder Produces<TResponse>(
            int statusCode,
            string contentType,
            string exampleName,
            TResponse example)
            where TResponse : notnull =>
            builder
                .Produces<TResponse>(statusCode, contentType)
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    if (operation.Responses?.TryGetValue(statusCode.ToString(), out var response) is true &&
                        response.Content?.TryGetValue(contentType, out var mediaType) is true)
                        (mediaType.Examples ??= new Dictionary<string, IOpenApiExample>())[exampleName] =
                            new OpenApiExample
                            {
                                Value = JsonSerializer.SerializeToNode(example, JsonSerializerOptions),
                            };

                    return Task.CompletedTask;
                });

        [PublicAPI]
        public RouteHandlerBuilder ProducesOkJson<TResponse>(TResponse example)
            where TResponse : notnull =>
            builder.Produces(StatusCodes.Status200OK, JsonContentType, example);

        [PublicAPI]
        public RouteHandlerBuilder ProducesOkJson<TResponse>(string exampleName, TResponse example)
            where TResponse : notnull =>
            builder.Produces(StatusCodes.Status200OK, JsonContentType, exampleName, example);

        [PublicAPI]
        public RouteHandlerBuilder ProducesCreatedJson<TResponse>(TResponse example)
            where TResponse : notnull =>
            builder.Produces(StatusCodes.Status201Created, JsonContentType, example);

        [PublicAPI]
        public RouteHandlerBuilder ProducesCreatedJson<TResponse>(string exampleName, TResponse example)
            where TResponse : notnull =>
            builder.Produces(StatusCodes.Status201Created, JsonContentType, exampleName, example);

        [PublicAPI]
        public RouteHandlerBuilder ProducesOk() =>
            builder.Produces(StatusCodes.Status200OK);

        [PublicAPI]
        public RouteHandlerBuilder ProducesNoContent() =>
            builder.Produces(StatusCodes.Status204NoContent);

        [PublicAPI]
        public RouteHandlerBuilder ProducesProblem(int statusCode, string title, string detail) =>
            builder.ProducesProblem(statusCode, "example", title, detail);

        [PublicAPI]
        public RouteHandlerBuilder ProducesProblem(int statusCode, string exampleName, string title, string detail) =>
            builder
                .ProducesProblem(statusCode)
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    if (operation.Responses?.TryGetValue(statusCode.ToString(), out var response) is true &&
                        response.Content?.TryGetValue(ProblemJsonContentType, out var mediaType) is true)
                        (mediaType.Examples ??= new Dictionary<string, IOpenApiExample>())[exampleName] =
                            new OpenApiExample
                            {
                                Value = JsonSerializer.SerializeToNode(new ProblemDetails
                                    {
                                        Title = title,
                                        Status = statusCode,
                                        Detail = detail,
                                    },
                                    JsonSerializerOptions),
                            };

                    return Task.CompletedTask;
                });

        [PublicAPI]
        public RouteHandlerBuilder ProducesBadRequest() =>
            builder.ProducesProblem(StatusCodes.Status400BadRequest, "Bad Request", "The request is invalid.");

        [PublicAPI]
        public RouteHandlerBuilder ProducesNotFound() =>
            builder.ProducesProblem(StatusCodes.Status404NotFound,
                "Not Found",
                "The requested resource was not found.");

        [PublicAPI]
        public RouteHandlerBuilder ProducesValidationProblem(
            IDictionary<string, string[]> errors,
            string title = "One or more validation errors occurred.",
            string detail = "The request contains validation errors.")
        {
            const int statusCode = StatusCodes.Status400BadRequest;

            return builder
                .ProducesValidationProblem()
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    if (operation.Responses?.TryGetValue(statusCode.ToString(), out var response) is true &&
                        response.Content?.TryGetValue(ProblemJsonContentType, out var mediaType) is true)
                        (mediaType.Examples ??= new Dictionary<string, IOpenApiExample>())["example"] =
                            new OpenApiExample
                            {
                                Value = JsonSerializer.SerializeToNode(new HttpValidationProblemDetails(errors)
                                    {
                                        Title = title,
                                        Status = statusCode,
                                        Detail = detail,
                                    },
                                    JsonSerializerOptions),
                            };

                    return Task.CompletedTask;
                });
        }

        [PublicAPI]
        public RouteHandlerBuilder ProducesUnauthorized() =>
            builder.Produces(StatusCodes.Status401Unauthorized);

        [PublicAPI]
        public RouteHandlerBuilder ProducesForbidden() =>
            builder.Produces(StatusCodes.Status403Forbidden);

        [PublicAPI]
        public RouteHandlerBuilder ProducesInternalServerError() =>
            builder.ProducesProblem(StatusCodes.Status500InternalServerError,
                "Internal Server Error",
                "An unexpected error occurred while processing the request");
    }

    extension<TBuilder>(TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        [PublicAPI]
        public TBuilder AllowAnonymous(bool allow) =>
            allow
                ? builder.AllowAnonymous()
                : builder;
    }
}
