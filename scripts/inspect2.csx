using System;
using System.Reflection;
var asm = Assembly.LoadFrom("tests/ContainerExtension.UnitTests/bin/Release/net10.0/OneWare.Essentials.dll");
var type = asm.GetType("OneWare.Essentials.Services.IMainDockService");
if (type != null) {
    Console.WriteLine("Methods of IMainDockService:");
    foreach(var m in type.GetMethods()) {
        Console.WriteLine($"{m.ReturnType.Name} {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");
    }
}
