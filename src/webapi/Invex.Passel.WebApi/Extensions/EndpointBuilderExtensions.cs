namespace Invex.Passel.WebApi.Extensions;

internal static class EndpointBuilderExtensions
{
    private const string JsonContentType = "application/json";

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    extension(RouteHandlerBuilder builder)
    {
        /// <summary>
        ///     Declares a JSON request body of <typeparamref name="TRequest" /> and adds <paramref name="example" /> to
        ///     the OpenAPI document under <paramref name="exampleName" />.
        /// </summary>
        public RouteHandlerBuilder AcceptsJson<TRequest>(string exampleName, TRequest example)
            where TRequest : notnull =>
            builder
                .Accepts<TRequest>(JsonContentType)
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    if (operation.RequestBody?.Content?.TryGetValue(JsonContentType, out var mediaType) is true)
                        (mediaType.Examples ??= new Dictionary<string, IOpenApiExample>())[exampleName] =
                            new OpenApiExample
                            {
                                Value = JsonSerializer.SerializeToNode(example, JsonSerializerOptions),
                            };

                    return Task.CompletedTask;
                });
    }
}
