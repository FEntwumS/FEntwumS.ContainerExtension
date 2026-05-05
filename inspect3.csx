using System;
using System.Reflection;
using System.Linq;

string path = "./.nuget_packages/oneware.essentials/1.0.0/lib/net10.0/OneWare.Essentials.dll";

var asm = Assembly.LoadFrom(path);
var type = asm.GetType("OneWare.Essentials.ViewModels.ExtendedTool");
if (type != null) {
    Console.WriteLine("Properties of ExtendedTool:");
    foreach(var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
        Console.WriteLine(prop.Name + " (" + prop.PropertyType.Name + ")");
    }
} else {
    Console.WriteLine("Type not found");
}
