using System;
using System.Reflection;
using ContainerExtension;

class Program
{
    static void Main()
    {
        var asm = Assembly.LoadFrom("../../src/ContainerExtension/bin/Debug/net10.0/FEntwumS.ContainerExtension.dll");
        var type = asm.GetType("ContainerExtension.ContainerExtensionModule");
        var baseType = type.BaseType;
        Console.WriteLine($"Base Type: {baseType.FullName}");
        foreach (var method in baseType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (method.IsVirtual)
            {
                Console.WriteLine($"Virtual Method: {method.Name}");
            }
        }
    }
}
