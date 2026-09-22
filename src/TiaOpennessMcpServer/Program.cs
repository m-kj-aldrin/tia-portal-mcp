using TiaOpennessMcpServer.Host;

// Register before the composition root references any Siemens types.
AssemblyResolver.Register();
await ServerApplication.RunAsync();
