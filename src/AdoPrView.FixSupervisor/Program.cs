using AdoPrView;

if (args is ["--native-fix-supervisor", var specification])
{
    var owned = NativeFixSupervisor.Read<NativeFixSupervisor.Specification>(specification);
    NativeFixSupervisor.Verify(owned);
    var log = NativeFixSupervisor.FilePath(owned, "supervisor.stderr.log");
    NativeFixSupervisor.SafePath(log);
    using var error = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        { AutoFlush = true };
    Console.SetError(error);
    return NativeFixSupervisor.Run(specification);
}
Console.Error.WriteLine("This executable supervises only an explicitly owned ADO PR View Fix run.");
return 64;
