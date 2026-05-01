using System;
using System.Reflection;
using System.Runtime.Loader;

var alc = new AssemblyLoadContext("MyPlugin", isCollectible: true);
var asm = alc.LoadFromAssemblyPath(System.IO.Path.GetFullPath("tests/ContainerExtension.UnitTests/bin/Release/net10.0/ContainerExtension.dll"));

var typeName = "ContainerExtension.ViewModels.DockerDiagnosticsViewModel, ContainerExtension";
var t1 = Type.GetType(typeName);
Console.WriteLine($"Before hook: {t1 != null}");

AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
    if (new AssemblyName(e.Name).Name == "ContainerExtension") return asm;
    return null;
};

var t2 = Type.GetType(typeName);
Console.WriteLine($"After hook: {t2 != null}");
