using AdoPrView;

if (args is ["--native-fix-supervisor", var specification])
    return NativeFixSupervisor.Run(specification);
Console.Error.WriteLine("This executable supervises only an explicitly owned ADO PR View Fix run.");
return 64;
