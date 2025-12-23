namespace PersonDetection.Application.Services
{
    using PersonDetection.Application.Common;

    public interface ICommandDispatcher
    {
        Task<TResponse> Dispatch<TResponse>(ICommand<TResponse> command, CancellationToken ct = default);
    }

    public interface IQueryDispatcher
    {
        Task<TResponse> Dispatch<TResponse>(IQuery<TResponse> query, CancellationToken ct = default);
    }

    public class CommandDispatcher : ICommandDispatcher
    {
        private readonly IServiceProvider _serviceProvider;

        public CommandDispatcher(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public Task<TResponse> Dispatch<TResponse>(ICommand<TResponse> command, CancellationToken ct = default)
        {
            var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResponse));
            dynamic handler = _serviceProvider.GetRequiredService(handlerType);
            return handler.Handle((dynamic)command, ct);
        }
    }

    public class QueryDispatcher : IQueryDispatcher
    {
        private readonly IServiceProvider _serviceProvider;

        public QueryDispatcher(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public Task<TResponse> Dispatch<TResponse>(IQuery<TResponse> query, CancellationToken ct = default)
        {
            var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResponse));
            dynamic handler = _serviceProvider.GetRequiredService(handlerType);
            return handler.Handle((dynamic)query, ct);
        }
    }
}