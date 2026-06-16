using Olve.Pipelines.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    // ReSharper disable once AccessToDisposedClosure
    cts.Cancel();
};

var dispatcher = new CliDispatcher(new ProcessRunner());
return await dispatcher.RunAsync(args, cts.Token);
