using System;
using System.Reflection;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;

class Program {
    static void Main() {
        Console.WriteLine("Methods of OneWareModuleBase:");
        foreach (var m in typeof(OneWareModuleBase).GetMethods(BindingFlags.Public | BindingFlags.Instance)) {
            Console.WriteLine(m.Name);
        }
        Console.WriteLine("Events of IToolService:");
        foreach (var e in typeof(IToolService).GetEvents(BindingFlags.Public | BindingFlags.Instance)) {
            Console.WriteLine(e.Name);
        }
    }
}
