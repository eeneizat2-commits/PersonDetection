namespace PersonDetection.Application.Common
{
    public interface ICommand<out TResponse>
    {
    }

    public interface ICommandHandler<in TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        Task<TResponse> Handle(TCommand command, CancellationToken ct);
    }

    public interface IQuery<out TResponse>
    {
    }

    public interface IQueryHandler<in TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        Task<TResponse> Handle(TQuery query, CancellationToken ct);
    }
}