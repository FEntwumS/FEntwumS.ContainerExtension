using System;
using System.Reflection;
var asm = Assembly.LoadFrom("tests/ContainerExtension.UnitTests/bin/Release/net10.0/OneWare.Essentials.dll");
var type = asm.GetType("OneWare.Essentials.ViewModels.ExtendedTool");
if (type != null) {
    Console.WriteLine("Properties of ExtendedTool:");
    foreach(var prop in type.GetProperties()) {
        Console.WriteLine($"{prop.PropertyType.Name} {prop.Name}");
    }
}
