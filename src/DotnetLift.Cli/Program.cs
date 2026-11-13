using DotnetLift.Cli;

return await new App(Console.In, Console.Out, interactive: !Console.IsInputRedirected).RunAsync(args);
