using System;
using System.Reflection;

string path = "./.nuget_packages/oneware.essentials/1.0.0/lib/net10.0/OneWare.Essentials.dll";
var asm = Assembly.LoadFrom(path);
foreach(var type in asm.GetTypes()) {
    if (type.Name.Contains("Tool")) {
        Console.WriteLine(type.FullName);
    }
}
