namespace FEB.Cqrs;


public interface ICommand;

public interface ICommand<TResult> : ICommand;